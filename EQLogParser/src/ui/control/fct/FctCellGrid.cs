using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Fixed cells for text that does not travel.
   *
   * Free-floating positions work for numbers that move, because each one is only in a spot for a moment. Text that stays
   * still has the opposite requirement: two of them in the same place is unreadable mush, which is exactly what pulse mode
   * produced while it chose its own x from lane jitter. Every combat log UI settles on the same answer — slot allocation:
   * WoW's anti-stagger number mode and the scrolling-text addons spread simultaneous numbers across fixed positions with a
   * row limit, FFXIV stacks a capped number of rows in one place, GW2 groups into fixed areas — so pulse mode allocates a
   * cell per hit, holds it for the hit's life, and takes the oldest one when the block is full.
   *
   * Cells belong to a band, not to a lane: the scarce resource is space inside a band, and separate pools per lane would
   * have damage and healing blocks sitting on top of each other, which is the bug being fixed. Colour already says what a
   * number is; position only has to say "not on top of another number".
   *
   * The outer row of each band belongs to procs — the smaller cells they asked for, kept out of the block the player is
   * reading so item spam can never push into it. "Outer" means away from the protected middle strip on both sides, which
   * also keeps the strip's neighbour free of the most frequent text on screen.
   *
   * Rows fill nearest-the-strip first, and centre-out within a row: the first number lands where the eye already is and the
   * block grows away from it, so a burst reads as one event expanding rather than scattered appearances. Row index 0 is
   * therefore the row closest to the strip.
   *
   * One rule does all the work in this file: geometry depends only on the band and the canvas size, never on the hit being
   * placed. A per-hit reserve looks harmless — a bigger number wants a bigger slot — but then two numbers in the same row
   * disagree about where the row is, and they land on each other. Row slots are shared; only the centring inside a slot is
   * per-hit. A crit's pop can therefore crowd its neighbour slightly, which is the trade every grid layout makes: rows
   * sized for the loudest possible number would halve how many numbers fit.
   */
  internal static class FctCellGrid
  {
    /* Rows of direct hits per band, plus the single proc row sitting outside them. */
    public const int MainRows = 2;
    public const int ProcRows = 1;

    /* Procs are smaller, so more of them fit in a row than the big numbers they accompany. */
    public const int MainColumns = 4;
    public const int ProcColumns = 6;

    /* Side margin before the first cell column, as a multiple of the layout's edge pad. */
    public const int SideMarginPads = 2;

    /*
     * Breathing room between the block of cells and the protected strip. The band already keeps every number off the strip;
     * this is about not crowding it — the row nearest the middle is the one read while looking at your own cast bar, and
     * text sitting on that border is the complaint that started all this. Share of band depth.
     */
    public const double StripMarginFrac = 0.12;

    /* How long the slide from the spawn point into the assigned cell takes. Zero means appear straight in the cell. */
    public const double SlideMs = FctMotion.PulseSlideMs;

    /*
     * Give `hit` a cell and set its travel from the band's single spawn point to that cell. Returns false when the pool has
     * no cell this hit may take, which happens only when every cell is held by a crit and the newcomer is not one — a crit
     * must not erase the biggest number on screen. `evicted` reports whose cell was taken over; the caller releases whatever
     * its backend kept for that hit, and the hit is already gone from the list by then.
     */
    internal static bool Assign(FctHitState hit, List<FctHitState> hits, double w, double h, double now, out FctHitState evicted)
    {
      evicted = null;

      var count = CellCount(hit.Incoming, hit.Proc, h);
      if (count <= 0)
      {
        return false;
      }

      var taken = Claimed(hits, hit.Incoming, hit.Proc, h);
      var index = -1;
      for (var i = 0; i < count; i++)
      {
        if (!taken[i])
        {
          index = i;
          break;
        }
      }

      if (index < 0)
      {
        /* Full: take the oldest cell, but never a crit's for anything smaller. */
        var oldest = OldestOf(hits, hit.Incoming, hit.Proc, hit.Lane is FctLane.Crit);
        if (oldest is null)
        {
          return false;
        }

        evicted = oldest;
        index = oldest.Cell;
        hits.Remove(oldest);
      }

      hit.Cell = index;

      /*
       * One spawn point per band: the middle of the block's strip-facing edge. Every number starts there and slides out, so
       * a burst visibly comes from one place even though it ends up in several. Y0/Rise use the convention FctMotion already
       * has — the drawn y is Y0 - Rise*ease — and RaisedY clamps the path to the band, so no cell can pull text into the
       * protected strip or off the window.
       */
      hit.X0 = w * 0.5;
      hit.Y0 = hit.Incoming ? BlockTop(hit.Incoming, h) : BlockBottom(hit.Incoming, h);

      Position(hit.Incoming, hit.Proc, index, w, h, FctLayout.TextReserve(hit), out var cellX, out var cellY);
      hit.Arc = cellX - hit.X0;
      hit.Rise = hit.Y0 - cellY;
      hit.FallDist = 0;
      hit.MotionMs = SlideMs;

      return true;
    }

    /*
     * Whether the band has room for cells at all. A very small overlay cannot hold a row of text plus its margins, and the
     * caller should then leave the hit where FctLayout put it rather than drop real information over furniture. Overload —
     * every cell held by a crit — is a different answer: see Assign.
     */
    internal static bool HasRoom(FctHitState hit, double h) => CellCount(hit.Incoming, hit.Proc, h) > 0;

    /*
     * Re-seat a hit that already holds a cell after the canvas changed size. The index survives — that is what an index is for —
     * but everything derived from it does not: cell centres, the block's margins and the band's single spawn point all come out of
     * the current width and height, so a number that was mid-slide finishes its slide at where its cell is now rather than at
     * where it was when the window was bigger. If the grid shrank under it, the index comes inward to the last cell that still
     * exists: two numbers sharing a cell beats one drawn outside the overlay, and the cap is what stops that happening often.
     */
    internal static void Reseat(FctHitState hit, double w, double h)
    {
      var count = CellCount(hit.Incoming, hit.Proc, h);
      if (count <= 0)
      {
        return; // no grid at this size any more: FctResize has already given it sane bands, so leave it where it is
      }

      hit.Cell = Math.Min(hit.Cell, count - 1);
      hit.X0 = w * 0.5;
      hit.Y0 = hit.Incoming ? BlockTop(hit.Incoming, h) : BlockBottom(hit.Incoming, h);

      Position(hit.Incoming, hit.Proc, hit.Cell, w, h, FctLayout.TextReserve(hit), out var cellX, out var cellY);
      hit.Arc = cellX - hit.X0;
      hit.Rise = hit.Y0 - cellY;
    }

    /* Rows of direct hits that actually fit, giving up the outer row first when the band is too shallow for two. */
    internal static int MainRowCount(bool incoming, double h)
    {
      var reserve = MainReserve(incoming);
      var block = BlockHeight(incoming, h);
      var rows = MainRows;

      while (rows > 1 && block / (rows + ProcRows) < reserve)
      {
        rows--;
      }

      return block < reserve ? 0 : rows;
    }

    internal static int CellCount(bool incoming, bool proc, double h)
    {
      var mainRows = MainRowCount(incoming, h);
      if (proc)
      {
        /* The proc row exists only if a row of small text fits in its slice of the band. */
        return Pitch(incoming, h, mainRows) >= ProcReserve() ? ProcColumns : 0;
      }

      return mainRows * MainColumns;
    }

    /* Where a cell is: x is its horizontal centre, y the top of the text placed in it. */
    internal static void Position(bool incoming, bool proc, int index, double w, double h, double reserve, out double x, out double y)
    {
      var columns = proc ? ProcColumns : MainColumns;
      var pitch = Pitch(incoming, h, MainRowCount(incoming, h));

      /* Row 0 is nearest the strip for both bands; procs continue outward from the last direct row. */
      var row = proc ? MainRowCount(incoming, h) + (index / columns) : index / columns;
      var column = ColumnAt(index % columns, columns);

      var side = FctLayout.EdgePad * SideMarginPads;
      x = side + ((column + 0.5) * (w - (side * 2))) / columns;

      /* Text is centred in its slot: the same slot reads as a big cell or a small one depending on what is in it. */
      var slotTop = incoming ? BlockTop(incoming, h) + (row * pitch) : BlockBottom(incoming, h) - ((row + 1) * pitch);
      y = slotTop + Math.Max(0, (pitch - reserve) * 0.5);
    }

    /*
     * The block of cells inside a band, sized by the lane's normal text rather than by whatever hit is being placed — see
     * the class comment. NominalBandMax is the flush position of that nominal text: for my band it sits just above the
     * strip, for the incoming band just above the bottom edge.
     */
    private static double NominalBandMin(bool incoming, double h) => incoming ? h * FctLayout.GapBottomFrac : FctLayout.EdgePad;

    private static double NominalBandMax(bool incoming, double h) =>
      incoming ? h - FctLayout.EdgePad - MainReserve(incoming) : (h * FctLayout.GapTopFrac) - MainReserve(incoming);

    private static double BlockHeight(bool incoming, double h) => (NominalBandMax(incoming, h) - NominalBandMin(incoming, h)) * (1 - StripMarginFrac);

    /* The strip-facing margin comes off the near end: cells never crowd the protected middle. */
    private static double BlockTop(bool incoming, double h) =>
      incoming ? NominalBandMin(incoming, h) + ((NominalBandMax(incoming, h) - NominalBandMin(incoming, h)) * StripMarginFrac) : NominalBandMin(incoming, h);

    private static double BlockBottom(bool incoming, double h) =>
      incoming ? NominalBandMax(incoming, h) : NominalBandMax(incoming, h) - ((NominalBandMax(incoming, h) - NominalBandMin(incoming, h)) * StripMarginFrac);

    private static double Pitch(bool incoming, double h, int mainRows) => BlockHeight(incoming, h) / (mainRows + ProcRows);

    /*
     * The column sitting in a slot of the centre-out ordering: slot 0 is the middle column, then outwards, ties going left
     * so an even count grows evenly on both sides. Expressed as a rank rather than a table because the two pools have
     * different column counts and a table would be one more thing to keep in sync.
     */
    internal static int ColumnAt(int orderSlot, int columns)
    {
      var centre = (columns - 1) / 2.0;
      for (var c = 0; c < columns; c++)
      {
        if (Rank(c, centre, columns) == orderSlot)
        {
          return c;
        }
      }

      return 0;
    }

    private static int Rank(int column, double centre, int columns)
    {
      var d = Math.Abs(column - centre);
      var rank = 0;
      for (var c = 0; c < columns; c++)
      {
        var dc = Math.Abs(c - centre);
        if (dc < d || (dc == d && c < column))
        {
          rank++;
        }
      }

      return rank;
    }

    /* Which cells this pool has live claims on. Derived from the hits themselves, so nothing can drift out of sync when a
     * hit expires, is absorbed into a total, or is dropped — the list is the only bookkeeping there is. */
    private static bool[] Claimed(List<FctHitState> hits, bool incoming, bool proc, double h)
    {
      var claimed = new bool[Math.Max(1, CellCount(incoming, proc, h))];
      for (var i = 0; i < hits.Count; i++)
      {
        var other = hits[i];
        if (other.Cell >= 0 && other.Incoming == incoming && other.Proc == proc && other.Cell < claimed.Length)
        {
          claimed[other.Cell] = true;
        }
      }

      return claimed;
    }

    private static FctHitState OldestOf(List<FctHitState> hits, bool incoming, bool proc, bool newcomerIsCrit)
    {
      FctHitState oldest = null;
      for (var i = 0; i < hits.Count; i++)
      {
        var other = hits[i];
        if (other.Cell < 0 || other.Incoming != incoming || other.Proc != proc)
        {
          continue;
        }

        if (other.Lane is FctLane.Crit && !newcomerIsCrit)
        {
          continue; // a crit holds its ground against anything smaller
        }

        if (oldest is null || other.SpawnMs < oldest.SpawnMs)
        {
          oldest = other;
        }
      }

      return oldest;
    }

    /* Row height comes from the lane's normal size with a source line so every number in the band agrees on its rows. A
     * crit pop can crowd a neighbour by a few pixels; sizing rows for crits instead would cost half the cells. */
    private static double MainReserve(bool incoming) => Reserve(incoming ? FctLane.DamageTaken : FctLane.DamageDealt);

    private static double Reserve(FctLane lane)
    {
      var size = lane is FctLane.DamageTaken ? FctStyle.DamageTakenFontSize : FctStyle.DamageDealtFontSize;
      return (size * FctLayout.TextHeightFactor) + (FctStyle.SourceSize(size) * FctLayout.SourceLineFactor);
    }

    private static double ProcReserve()
    {
      var size = FctStyle.DamageDealtFontSize * FctStyle.ProcSizeFrac;
      return (size * FctLayout.TextHeightFactor) + (FctStyle.SourceSize(size) * FctLayout.SourceLineFactor);
    }
  }
}

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
   * Cells belong to a region, not to a lane: the scarce resource is space inside the side's territory, and separate pools
   * per lane would have damage and healing blocks sitting on top of each other, which is the bug being fixed. Colour already
   * says what a number is; position only has to say "not on top of another number". In halves that means one block per half,
   * measured against the half's own width — which is also what fixed the original halves mode, whose grid counted its
   * columns against the canvas and collapsed several "distinct" cells onto one place (4 overlapping numbers in 8).
   *
   * The outer row of each block belongs to procs — the smaller cells they asked for, kept out of the block the player is
   * reading so item spam can never push into it. "Outer" means away from whatever the side spawns against: the protected
   * middle strip in bands, the far edge of the half in halves, which also keeps that neighbour free of the most frequent
   * text on screen.
   *
   * Rows fill nearest-the-spawn-edge first, and centre-out within a row: the first number lands where the eye already is
   * and the block grows away from it, so a burst reads as one event expanding rather than scattered appearances. Row index
   * 0 is therefore the row closest to the spawn edge (the strip-facing one in bands).
   *
   * One rule does all the work in this file: geometry depends only on the region and the canvas size, never on the hit
   * being placed. A per-hit reserve looks harmless — a bigger number wants a bigger slot — but then two numbers in the same
   * row disagree about where the row is, and they land on each other. Row slots are shared; only the centring inside a
   * slot is per-hit. A crit's pop can therefore crowd its neighbour slightly, which is the trade every grid layout makes:
   * rows sized for the loudest possible number would halve how many numbers fit.
   */
  internal static class FctCellGrid
  {
    /* Rows of direct hits per region, plus the single proc row sitting outside them. */
    public const int MainRows = 2;
    public const int ProcRows = 1;

    /* Procs are smaller, so more of them fit in a row than the big numbers they accompany. */
    public const int MainColumns = 4;
    public const int ProcColumns = 6;

    /* Side margin before the first cell column, as a multiple of the layout's edge pad. */
    public const int SideMarginPads = 2;

    /*
     * Breathing room between the block of cells and whatever the side spawns against — in bands the protected strip: the row
     * nearest the middle is the one read while looking at your own cast bar, and text sitting on that border is the complaint
     * that started all this. Halves has nothing equivalent inside the half, so its blocks run edge to edge (the seam between
     * the halves is already two edge pads wide). Share of block depth.
     */
    public const double StripMarginFrac = 0.12;

    /* How long the slide from the spawn point into the assigned cell takes. Zero means appear straight in the cell. */
    public const double SlideMs = FctMotion.PulseSlideMs;

    /*
     * Everything a block of cells depends on: its rect, which end the side spawns against, how much of that end belongs to
     * something else (the strip, in bands) and the row height its nominal text needs. Geometry is a pure function of this,
     * which keeps rows shared and per-hit disagreement impossible — see the class comment.
     */
    private readonly struct Area
    {
      public readonly double X;
      public readonly double Width;
      public readonly double Top;
      public readonly double Bottom;
      public readonly double StripFrac;
      public readonly bool SpawnAtTop;
      public readonly double Reserve;

      internal Area(double x, double width, double top, double bottom, double stripFrac, bool spawnAtTop, double reserve)
      {
        X = x;
        Width = width;
        Top = top;
        Bottom = bottom;
        StripFrac = stripFrac;
        SpawnAtTop = spawnAtTop;
        Reserve = reserve;
      }
    }

    /*
     * The cell block for a side, out of its stage. Bands: the side's own band across the canvas, strip margin on the
     * strip-facing end. Halves: the whole half, no strip to keep off — the spawn edge is whichever end the side travels
     * away from, and the block grows from there.
     */
    private static Area AreaOf(FctStage stage, bool incoming, bool heal = false)
    {
      var reserve = MainReserve(incoming);
      if (stage.Mode is FctLayoutMode.Bands)
      {
        return new Area(0, stage.W,
          incoming ? stage.H * FctLayout.GapBottomFrac : FctLayout.EdgePad,
          incoming ? stage.H - FctLayout.EdgePad - reserve : (stage.H * FctLayout.GapTopFrac) - reserve,
          StripMarginFrac, incoming, reserve);
      }

      var region = stage.RegionFor(incoming, heal);
      var top = region.Y + FctLayout.EdgePad;
      return new Area(region.X, region.Width, top, region.Y + region.Height - FctLayout.EdgePad - reserve,
        0.0, stage.UpFor(incoming) < 0, reserve);
    }

    /*
     * Give `hit` a cell and set its travel from the region's single spawn point to that cell. Returns false when the pool
     * has no cell this hit may take, which happens only when every cell is held by a crit and the newcomer is not one — a
     * crit must not erase the biggest number on screen. `evicted` reports whose cell was taken over; the caller releases
     * whatever its backend kept for that hit, and the hit is already gone from the list by then.
     */
    internal static bool Assign(FctHitState hit, List<FctHitState> hits, double w, double h, double now, out FctHitState evicted)
      => Assign(hit, hits, FctStage.Bands(w, h), now, out evicted);

    internal static bool Assign(FctHitState hit, List<FctHitState> hits, FctStage stage, double now, out FctHitState evicted)
    {
      evicted = null;

      var area = AreaOf(stage, hit.Incoming, hit.Heal);
      var count = CellCount(area, hit.Proc);
      if (count <= 0)
      {
        return false;
      }

      var taken = Claimed(hits, PoolKey(stage, hit), hit.Proc, count);
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
        var oldest = OldestOf(hits, PoolKey(stage, hit), hit.Proc, hit.Lane is FctLane.Crit);
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
       * One spawn point per region: the middle of the block's spawn-facing edge. Every number starts there and slides out,
       * so a burst visibly comes from one place even though it ends up in several. Y0/Rise use the convention FctMotion
       * already has — the drawn y is Y0 - Rise*ease — and RaisedY clamps the path to the band, so no cell can pull text
       * into the protected strip or off the window.
       */
      hit.X0 = area.X + (area.Width / 2);
      hit.Y0 = area.SpawnAtTop ? BlockTop(area) : BlockBottom(area);

      Position(hit.Proc, index, area, FctLayout.TextReserve(hit), out var cellX, out var cellY);
      hit.Arc = cellX - hit.X0;
      hit.Rise = hit.Y0 - cellY;
      hit.FallDist = 0;
      hit.MotionMs = SlideMs;

      return true;
    }

    /*
     * Whether the region has room for cells at all. A very small overlay cannot hold a row of text plus its margins, and
     * the caller should then leave the hit where FctLayout put it rather than drop real information over furniture.
     * Overload — every cell held by a crit — is a different answer: see Assign.
     */
    internal static bool HasRoom(FctHitState hit, double h) => HasRoom(hit, FctStage.Bands(0, h));

    internal static bool HasRoom(FctHitState hit, FctStage stage) => CellCount(AreaOf(stage, hit.Incoming, hit.Heal), hit.Proc) > 0;

    /*
     * Re-seat a hit that already holds a cell after the canvas changed size. The index survives — that is what an index is
     * for — but everything derived from it does not: cell centres, the block's margins and the region's single spawn point
     * all come out of the current size, so a number that was mid-slide finishes its slide at where its cell is now rather
     * than at where it was when the window was bigger. If the grid shrank under it, the index comes inward to the last cell
     * that still exists: two numbers sharing a cell beats one drawn outside the overlay, and the cap is what stops that
     * happening often.
     */
    internal static void Reseat(FctHitState hit, double w, double h) => Reseat(hit, FctStage.Bands(w, h));

    internal static void Reseat(FctHitState hit, FctStage stage)
    {
      var area = AreaOf(stage, hit.Incoming, hit.Heal);
      var count = CellCount(area, hit.Proc);
      if (count <= 0)
      {
        return; // no grid at this size any more: FctResize has already given it sane bands, so leave it where it is
      }

      hit.Cell = Math.Min(hit.Cell, count - 1);
      hit.X0 = area.X + (area.Width / 2);
      hit.Y0 = area.SpawnAtTop ? BlockTop(area) : BlockBottom(area);

      Position(hit.Proc, hit.Cell, area, FctLayout.TextReserve(hit), out var cellX, out var cellY);
      hit.Arc = cellX - hit.X0;
      hit.Rise = hit.Y0 - cellY;
    }

    /* Rows of direct hits that actually fit, giving up the outer row first when the block is too shallow for two. */
    internal static int MainRowCount(bool incoming, double h) => MainRowCount(AreaOf(FctStage.Bands(0, h), incoming));

    private static int MainRowCount(Area area)
    {
      var block = BlockHeight(area);
      var rows = MainRows;

      while (rows > 1 && block / (rows + ProcRows) < area.Reserve)
      {
        rows--;
      }

      return block < area.Reserve ? 0 : rows;
    }

    internal static int CellCount(bool incoming, bool proc, double h) => CellCount(AreaOf(FctStage.Bands(0, h), incoming), proc);

    private static int CellCount(Area area, bool proc)
    {
      var mainRows = MainRowCount(area);
      if (proc)
      {
        /* The proc row exists only if a row of small text fits in its slice of the block. */
        return Pitch(area, mainRows) >= ProcReserve() ? ProcColumns : 0;
      }

      return mainRows * MainColumns;
    }

    /* Where a cell is: x is its horizontal centre, y the top of the text placed in it. */
    internal static void Position(bool incoming, bool proc, int index, double w, double h, double reserve, out double x, out double y)
      => Position(proc, index, AreaOf(FctStage.Bands(w, h), incoming), reserve, out x, out y);

    private static void Position(bool proc, int index, Area area, double reserve, out double x, out double y)
    {
      var columns = proc ? ProcColumns : MainColumns;
      var pitch = Pitch(area, MainRowCount(area));

      /* Row 0 is nearest the spawn edge for both bands and both halves' directions; procs continue outward from the last
       * direct row. */
      var row = proc ? MainRowCount(area) + (index / columns) : index / columns;
      var column = ColumnAt(index % columns, columns);

      var side = FctLayout.EdgePad * SideMarginPads;
      x = area.X + side + ((column + 0.5) * (area.Width - (side * 2))) / columns;

      /* Text is centred in its slot: the same slot reads as a big cell or a small one depending on what is in it. */
      var blockTop = BlockTop(area);
      var blockBottom = BlockBottom(area);
      var slotTop = area.SpawnAtTop ? blockTop + (row * pitch) : blockBottom - ((row + 1) * pitch);
      y = slotTop + Math.Max(0, (pitch - reserve) * 0.5);
    }

    /*
     * The block of cells inside a region, sized by the lane's normal text rather than by whatever hit is being placed — see
     * the class comment.
     */
    private static double BlockHeight(Area area) => (area.Bottom - area.Top) * (1 - area.StripFrac);

    /* The spawn-facing margin comes off the near end: cells never crowd whatever the side spawns against. */
    private static double BlockTop(Area area) =>
      area.SpawnAtTop ? area.Top + ((area.Bottom - area.Top) * area.StripFrac) : area.Top;

    private static double BlockBottom(Area area) =>
      area.SpawnAtTop ? area.Bottom : area.Bottom - ((area.Bottom - area.Top) * area.StripFrac);

    private static double Pitch(Area area, int mainRows) => BlockHeight(area) / (mainRows + ProcRows);

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
     * hit expires, is absorbed into a total, or is dropped — the list is the only bookkeeping there is. The (pool, proc)
     * pair is the region's identity — and which question the pool answers follows the scheme (PoolKey): in halves each half
     * owns its own grid, and a hit can never claim across the seam. */
    private static bool[] Claimed(List<FctHitState> hits, bool incoming, bool proc, int count)
    {
      var claimed = new bool[Math.Max(1, count)];
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

    /* The grid lives in a side's region; the pool that shares it is whichever half of ownership the scheme draws — by
     * direction in halves and bands, by category in by type, where healing's column holds its own grid whatever the
     * direction its numbers travel (FctStage). */
    private static bool PoolKey(FctStage stage, FctHitState hit) =>
      stage.Mode is FctLayoutMode.ByType ? hit.Heal : hit.Incoming;

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

    /* Row height comes from the lane's normal size with a source line so every number in the block agrees on its rows. A
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

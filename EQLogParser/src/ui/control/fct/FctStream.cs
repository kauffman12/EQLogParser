using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Row discipline for the parabola in halves — what MSBT actually is: not scattered flying numbers but a stream of
   * rows running down one centre column, each new line taking its place behind the last. The constants are theirs
   * (minimum spacing between lines and between columns); everything else is reuse rather than a second engine:
   * launch points go through FctLayout.Spawn like any placement candidate, and FctPlacement's flight-scored search
   * picks among them with its pad raised so "free" means MSBT's gap apart, not merely not touching.
   *
   * Most spacing costs nothing to enforce because every row shares the rail AND its speed: no travel jitter (FctLayout
   * .Spawn gives the style a slack-free far end), one scroll rate for crits and procs alike (FctIngest.AssignLifetime),
   * and no park — rows that rested at end of travel would all rest at the same place, and a queue that shares a parking
   * space is not a stream. Two rows born half a second apart therefore stay half a second of travel apart, on the same
   * curve, for their whole lives: MSBT's chain, exactly. Only bursts need room made for them, and room is made in two escalating
   * steps (see ComfortableRows): the crowded rail speeds its newborn rows up, and only when even an accelerated rail can land a row
   * on top of another does a number go — the smallest plain value in the crowd first, every loss counted. What never happens is
   * silent overlap by default or silent loss: the flood reads fast but legible, protected rows (crits, marks, heals, words) keep
   * their pixels even at the margin, and DroppedCount says exactly what congestion cost.
   */
  internal static class FctStream
  {
    /* MSBT's MIN_VERTICAL_SPACING / MIN_HORIZONTAL_SPACING (MSBTData.lua): lines keep 8 px between blocks and
     * columns 10 px. Fixed pixels, not window fractions — the gap is about what reads as two separate rows. */
    public const double MinRowGap = 8;
    public const double ColumnGap = 10;

    /*
     * Congestion control, two escalating steps (the pulse grid never reaches either; it has its own cells).
     *
     * 1. SPEED. A rail at the player's configured tempo can keep `ComfortableRows` rows legibly apart — the mouth, the depth stack,
     *    and the two braid columns. Text arriving faster than that cannot be separated at the dial's pace no matter where each row
     *    enters, so a row born
     *    into a crowded rail travels proportionally faster (FctHitState.RailPress), down to PressFloor: the backlog flushes while the
     *    flood lasts, and the moment the rail empties newborn rows are back at exactly the configured speed. The floor keeps
     *    accelerated numbers readable and guarantees a fight that stops stops typing inside about a second and a half — congestion
     *    must never leave the overlay still spamming seconds after the last swing.
     *
     * 2. VALUES. If even an accelerated rail can only land this row on top of another, something goes: the weakest ordinary damage
     *    number in the crowded region (weaker, or equal and older — the fresher news wins ties) leaves to make room, or this row
     *    refuses itself if it is the weakest arrival there. Never the protected — crits, marked specials, heals, and words outrank
     *    tidiness and take their marginal overlap instead. Every loss, eviction or refusal, counts in FctIngest.DroppedCount;
     *    nothing goes missing quietly.
     */
    internal const int ComfortableRows = 8;
    internal const double PressFloor = 0.45;

    /* What counts as "landed on top of" for the values valve: a fraction of the smaller block. The scorer already separates
     * everything it can and lets the residue graze — a tight column with kissing edges reads fine, and the compression tests
     * pin that even a deliberately over-capacity squeeze stays under a third of a block. The valve sits above that ceiling on
     * purpose: if it fired anywhere inside the tolerated graze, every busy-but-legal fight would start paying losses the
     * geometry never actually required. Only real smearing — a third or more of one number painted over another — is worth
     * losing a number to prevent. */
    internal const double ValveOverlap = 0.35;


    /* True when this freshly-placed row still covers part of a live neighbour at some shared moment — the accelerated
     * rail found no clean landing, and only the sacrifice rule can settle it now (FctIngest's rail branch). */
    internal static bool OverlapsAny(FctHitState hit, List<FctHitState> hits)
    {
      for (var i = 0; i < hits.Count; i++)
      {
        if (FctPlacement.WorstOverlap(hit, hits[i], 0) > ValveOverlap)
        {
          return true;
        }
      }

      return false;
    }

    /* Whether labels are drawn under the value. The canvas owns the choice and stamps this when settings load, so the
     * engine prices the shape the player will actually see: with labels below, a following number that lands on the word
     * band is a collision even when the two VALUES clear each other — which is exactly what "labels underneath" asks for.
     * Shipped default is Below, so that protection is on unless the player moved the words beside the number. */
    internal static bool LabelBelow = true;

    /* A band bite is vertical, and vertical is what reads: a side-kiss between two crowded columns is geometry a player
     * accepts, while a number plunging through the word under the one above it welds two rows into one unusable blob.
     * So the tripwire is depth-based — how far the value box dips into the band — not an overlap fraction, and it needs
     * enough shared width to matter before counting at all. */
    internal const double LabelBitePx = 8;
    internal const double LabelBiteWide = 16;

    /* True when this row's label band is bitten by a neighbour's value box, or its own value would bite a neighbour's. */
    internal static bool LabelBitten(FctHitState hit, List<FctHitState> hits)
    {
      if (!LabelBelow)
      {
        return false;
      }

      for (var i = 0; i < hits.Count; i++)
      {
        if (BandBite(hit, hits[i]) || BandBite(hits[i], hit))
        {
          return true;
        }
      }

      return false;
    }


    /* Sacrifice rule. Protected rows — crits and marks (the big class), heals, words — are never sacrificed and never
     * refused: they outrank tidiness. For an ordinary number: the weakest strictly-weaker ordinary damage row still live
     * in this region gives up its pixels; if this row is itself the weakest there, it goes instead. Ties on value evict
     * the older neighbour, which is closer to leaving on its own anyway. */
    internal static bool Sacrificable(FctHitState hit) =>
      hit.FixedText is null && !hit.Blowout && !hit.Heal;

    /* The sacrifice is chosen among the rows THIS one actually conflicts with — smearing or biting — not the whole region:
     * evicting a bystander to make room that the scorer then used somewhere else entirely was collateral nonsense, and it
     * kept ordinary bites alive behind unrelated funerals. Same weakest-first rule (equal counts, ties evicting the older,
     * which is nearer leaving anyway), now aimed at the actual obstruction. */
    internal static FctHitState WeakestNeighbour(FctHitState hit, List<FctHitState> hits, FctStage stage)
    {
      var region = stage.RegionFor(hit);
      FctHitState weakest = null;
      for (var i = 0; i < hits.Count; i++)
      {
        var other = hits[i];
        if (!Sacrificable(other) ||
            other.SideMax <= region.X || (region.X + region.Width) <= other.SideMin ||
            other.SpawnMs + other.LifetimeMs <= hit.SpawnMs ||
            other.Value > hit.Value ||
            !(FctPlacement.WorstOverlap(hit, other, 0) > ValveOverlap || BandBite(hit, other) || BandBite(other, hit)))
        {
          continue;
        }

        if (weakest is null || other.Value < weakest.Value ||
            (other.Value == weakest.Value && other.SpawnMs < weakest.SpawnMs))
        {
          weakest = other;
        }
      }

      return weakest;
    }

    /* Place one row of the stream: score this side's three columns at its spawn edge and return whichever reads
     * cleanest, always one of them — a parabola number never arrives off its column. keepPress exists for the congestion
     * valve's escalation retry (FctIngest): the caller has already floored this row's accelerator on purpose, and the
     * crowd-average stamp must not undo a decision that was made about this row specifically. */
    internal static FctHitState Place(FctHitState hit, List<FctHitState> hits, FctStage stage, Random rand,
      bool keepPress = false, double? forcePress = null)
    {
      /* One beat for the whole rail, set before any candidate is tried: the scoring below measures whole flights, and a
       * trial cloned without a lifetime has already ended. The beat depends only on the region, so applying it here
       * costs nothing per column and cannot disagree with itself (FctIngest.ApplyRailTempo). */
      FctIngest.ApplyRailTempo(hit, stage);
      if (forcePress is double forced)
      {
        hit.RailPress = forced;
      }
      else if (!keepPress)
      {
        hit.RailPress = Pressure(hit, hits, stage);
      }

      var region = stage.RegionFor(hit);
      var cx = region.X + (region.Width / 2);
      var edgeY = stage.UpFor(hit) > 0 ? hit.BandMaxY : hit.BandMinY;

      /* The line is a line, and lines do not braid sideways: crowding a straight row into an emergency column parks it
       * tens of pixels beside the stream forever — which is exactly why misses and parries drifted in their own offset
       * mini-column while resists, rare enough to always find the mouth clear, sat on the centre. A crowded line row
       * steps INTO its travel instead: entering one text line further along the same column. The shared scroll rate
       * locks whatever gap existed at spawn for the whole flight, so a same-frame pair passes as two cleanly separated
       * rows one line ahead of the other, and the sideways columns drop to what they should be: a valve for bursts
       * past the line's throughput, not where an ordinary fight parks its words (PlaceLine). */
      if (hit.Style is FctMotionStyle.Straight)
      {
        return PlaceLine(hit, hits, stage, rand, cx, edgeY);
      }

      var price = DrawnHalf(hit);
      for (var i = 0; i < hits.Count; i++)
      {
        // only this side's numbers can crowd this side's stream; the other half is a different region entirely
        /* Neighbours are counted by territory, not by side-of-the-stream ownership: in halves the two directions' ranges
         * are disjoint so this skips exactly what the old direction check skipped, and in by type — where a column can
         * carry both an outgoing and an incoming train at once — it counts the crossings that a direction check would
         * have waved straight through. */
        if (hits[i].SideMax < hit.SideMin || hits[i].SideMin > hit.SideMax)
        {
          continue;
        }

        price = Math.Max(price, DrawnHalf(hits[i]));
      }

      /* One column step is the widest a row in this region can be DRAWN — a crit swells past its raw width, and rows
       * that share a rail share their phase too, so a neighbour's peak size is permanent overlap rather than a moment
       * something dephased out of the way — plus the horizontal gap. Two rows one step apart then cannot touch however
       * they are drawn, which is what makes the discipline free most of the time. */
      var step = (price * 2) + ColumnGap;

      /*
       * Centre first — a tie breaks toward it because the scoring keeps the first equal cost, which is what makes an
       * empty stream one column rather than a coin flip — then two emergency columns braided UPSTREAM of the arc.
       * Every row bows out to its vertex at half height; a column placed downstream of that bow swings through the
       * space the centre row is sweeping and pins against the wall beside it — same shape, same tempo, so they stay
       * pinned for the whole arc: a same-frame burst measured 28% block overlap before the braid turned upstream.
       * Against the arc the extra columns swing inside what the stream already covers and touch nothing.
       *
       * The row y is the edge itself with no depth jitter: along-travel placement belongs to birth time and scroll
       * speed, not to dice. Column distance is the widest live text in this region plus the horizontal gap, so two
       * rows one step apart cannot touch however wide they are drawn, and a resize mid-stream needs no special case
       * because every later spawn measures the current widths anyway.
       */
      var braid = hit.Bow >= 0 ? -1.0 : 1.0;

      /*
       * Two steps must fit between the centre and that side's clamp wall, priced at the widest any row here can be
       * drawn (crits blow out; a column parked half off-screen is not a column). Where the territory is narrower than
       * the text demands — ordinary fight, ordinary font, somebody's 420 px overlay — the columns compress evenly:
       * adjacent rows graze a little and the scorer still separates them as far as geometry allows. Compression is
       * visible; pinning every emergency column onto the same wall clamp is worse.
       */
      /* Right-aligned: the box hangs left of the rail, so the sideways room a braided column needs is the FULL drawn
         width going left and nothing at all going right (the rail itself only has to stay inside the territory). */
      var span = braid < 0 ? cx - (hit.SideMin + (price * 2)) : hit.SideMax - cx;
      if (span < 2 * step)
      {
        step = Math.Max(0, span) / 2;
      }

      var origins = new (double X, double Y)[]
      {
        (cx, edgeY),
        (cx + (braid * step), edgeY),
        (cx + (2 * braid * step), edgeY),
      };

      // the anchor is the stream's own centre, so the drift preference pulls rows back to the middle lane rather
      // than to wherever this hit's free throw happened to land
      return FctPlacement.PlaceOrigins(hit, hits, stage, rand, origins, cx, edgeY, MinRowGap);
    }

    /* Worst plunge of b's value box into a's label band across their shared life, sampled in drawn coordinates so the
     * pop carries band and intruder together. Band geometry mirrors the canvas: top 1.25 value-fonts under the row's own
     * top, one source-font deep — which is the same real estate FctLayout.TextHeight already charges for; this is the
     * fence around it. */
    private static bool BandBite(FctHitState a, FctHitState b)
    {
      if (a.SourceLabel is null || a.SpawnMs + a.LifetimeMs <= b.SpawnMs || b.SpawnMs + b.LifetimeMs <= a.SpawnMs)
      {
        return false;
      }

      var first = Math.Max(a.SpawnMs, b.SpawnMs);
      var last = Math.Min(a.SpawnMs + a.LifetimeMs, b.SpawnMs + b.LifetimeMs);
      for (var s = 0; s <= 12; s++)
      {
        var now = first + ((last - first) * s / 12.0);
        var ta = FctMotion.Progress(a, now - a.SpawnMs);
        var tb = FctMotion.Progress(b, now - b.SpawnMs);
        var sa = FctMotion.ScaleOf(a, now - a.SpawnMs);
        var sb = FctMotion.ScaleOf(b, now - b.SpawnMs);

        var bandTop = FctMotion.RaisedY(a, ta) + (a.ValueFontSize * 1.25 * sa);
        var bandBottom = bandTop + (a.SourceFontSize * sa);
        var valueTop = FctMotion.RaisedY(b, tb);
        var valueBottom = valueTop + (b.ValueFontSize * 1.35 * sb);
        var bite = Math.Min(bandBottom, valueBottom) - Math.Max(bandTop, valueTop);
        if (bite < LabelBitePx)
        {
          continue;
        }

        /* The band is as wide as the WORD, not the number — "Kromdek's Favor" reaches far past "412", and a value
         * landing on the word's tail is exactly the complaint even though it misses the value's column. Live rows carry
         * their measured SourceWidth (the canvas stamped it at birth); before any canvas exists an estimate from the
         * label's own length keeps the guard honest in tests and headless replay. */
        var bandHalf = Math.Max(a.ValueWidth, a.SourceWidth > 0 ? a.SourceWidth : a.SourceLabel.Length * a.SourceFontSize * 0.52) * sa / 2.0;
        var ax = FctMotion.ArcedX(a, ta);
        var bx = FctMotion.ArcedX(b, tb);
        var wide = Math.Min(ax + bandHalf, bx + (b.ValueWidth * sb / 2.0)) - Math.Max(ax - bandHalf, bx - (b.ValueWidth * sb / 2.0));
        if (wide >= LabelBiteWide)
        {
          return true;
        }
      }

      return false;
    }

    /* How much this row's rail is overdriven at its birth: 1.0 while the region holds ComfortableRows or fewer live rows,
     * shrinking with the crowd beyond that, floored at PressFloor so an emergency still clears the region inside about a
     * second and a half.
     *
     * Counting everything alive — not just what sits near the entry — is the point: this is Little's law spent on purpose.
     * A rail's throughput at the dial's tempo is its capacity divided by a row's flight time, so the live count IS the ratio
     * of arrival rate to service rate. Steady fights sit at or under ComfortableRows and never feel an override; floods push
     * it over, the override shortens each flight, shorter flights drain the live count back to comfort, and the rail settles
     * exactly as fast as the traffic needs and no faster. Rows in the emergency columns count too, and so do opposing
     * trains, because those share these pixels either way. */
    private static double Pressure(FctHitState hit, List<FctHitState> hits, FctStage stage)
    {
      var region = stage.RegionFor(hit);
      var live = 0;
      for (var i = 0; i < hits.Count; i++)
      {
        var other = hits[i];
        if (other.SideMax <= region.X || (region.X + region.Width) <= other.SideMin ||
            other.SpawnMs + other.LifetimeMs <= hit.SpawnMs)
        {
          continue;
        }

        live++;
      }

      var room = ComfortableRows / (live + 1.0);
      return room >= 1.0 ? 1.0 : Math.Max(room, PressFloor);
    }
    /* Half-width a row can occupy at its widest draw. Nothing is multiplied on here any more: stream styles never scale a number past the
     * font it was measured at (the big class carries its size in that font, and its pop swells up from below), so the measured width plus
     * the reserved glyph IS the widest this row ever gets — which is also what makes the odometer's right edge exact.
     *
     * Vertical step: a line has to clear the tallest row this stream can draw, not just this one's. */
    private static double DrawnHalf(FctHitState h) => (h.ValueWidth + h.IconAllowance) / 2.0;

    private static double DrawnHeight(FctHitState h) => FctLayout.TextHeight(h);

    /*
     * Straight's candidates, cheapest-first: the mouth, then one text line deeper along the column up to five deep,
     * and only after those the sideways emergency columns the parabola uses. The scorer is time-aware (shared-life
     * sampling, FctPlacement.Cost), so a depth step is scored as what it is — the same flight starting further along,
     * colliding only with rows actually there — and at any traffic a line can carry the vertical slots are free or
     * nearly so, which keeps whole categories from being pushed permanently beside their stream. Sideways stays as
     * overflow because past throughput there is no room anywhere on the line: rows spawning faster than they scroll
     * must overlap somewhere, and a clean second column reads better than two numbers printed on each other. Real
     * fights sit far below that (a swing every second against ~5 rows/s of lane throughput); the columns are the
     * burst valve, not the commute. Nothing is ever dropped for want of room.
     */
    private static FctHitState PlaceLine(FctHitState hit, List<FctHitState> hits, FctStage stage, Random rand, double cx, double edgeY)
    {
      var vPrice = DrawnHeight(hit);
      var price = DrawnHalf(hit);
      for (var i = 0; i < hits.Count; i++)
      {
        if (hits[i].SideMax < hit.SideMin || hits[i].SideMin > hit.SideMax)
        {
          continue;
        }

        vPrice = Math.Max(vPrice, DrawnHeight(hits[i]));
        price = Math.Max(price, DrawnHalf(hits[i]));
      }

      // deeper along the travel = further from the spawn edge, toward the end the row scrolls out of
      var into = stage.UpFor(hit) > 0 ? -1.0 : 1.0;
      var stepY = (2.0 * vPrice) + MinRowGap;

      const int DepthSteps = 5;
      var step = (price * 2) + ColumnGap;

      // right-aligned: the whole box hangs LEFT of the rail, so sideways room is measured from the full drawn width
      var span = cx - (hit.SideMin + (price * 2));
      if (span < 2 * step)
      {
        step = Math.Max(0, span) / 2;
      }

      var origins = new (double X, double Y)[DepthSteps + 2];
      for (var s = 0; s < DepthSteps; s++)
      {
        origins[s] = (cx, edgeY + (into * s * stepY));
      }

      // the burst valve: one step to each side at the mouth, priced like the parabola's emergency columns
      origins[DepthSteps] = (cx - step, edgeY);
      origins[DepthSteps + 1] = (cx + step, edgeY);

      return FctPlacement.PlaceOrigins(hit, hits, stage, rand, origins, cx, edgeY, MinRowGap);
    }
  }
}
using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Where to put a number so it does not land on one that is already there.
   *
   * FctLayout picks a lane slot plus a random jitter, which suits text that moves: each number is only in a spot for a
   * moment. In practice that was not enough — three live fountains can and did draw nearly the same origin, and two numbers
   * climbing almost the same path are unreadable for their whole flight while most of the band around them sits empty. Measured
   * on a 980x640 overlay at 700 ms intervals, three fountains had 58% of their pairs overlap at some point in their shared
   * life; six numbers held in one band collided 71% of pairs. The grid is the right answer for text that stays still
   * (FctCellGrid) and the wrong one here: thrown text has to look thrown, and filing it into slots is a different style's job.
   *
   * So: ask where a number would actually go from a lattice of legal launch points across its band, and keep the one that gets
   * its own space. Every candidate comes out of `FctLayout.Spawn`, which is what keeps this honest — no candidate can sit in the
   * protected strip, outside the window or on a band the layout would not have chosen, because it is the layout's own geometry
   * asked for a specific origin rather than a dice roll. Nothing is dropped for want of room: if every option crowds, the best of
   * them is still placed, exactly as before this file existed.
   *
   * Two mistakes are recorded here because both were made while building it, and both cost more than the bug they were fixing.
   * Widening the throw on both axes sent damage numbers to the left screen border — see LateralSearchTexts. Probing with random
   * throws instead of a lattice found far less room than walking the band does, which is what made the first version look broken
   * once the sideways freedom was taken back away from it.
   *
   * Cost is measured along the flight rather than at the origin, because that is what the player watches. Fountain travels a
   * band and falls back through it, so two spawns that start apart can still collide on the way down — sampling positions at
   * one instant would have missed the case this exists for.
   */
  internal static class FctPlacement
  {
    /*
     * The search grid: how many launch points across the column's sideways reach, and how many down the depth of its band.
     * Probing is systematic on purpose. Twelve random throws at a crowded band found much less room than walking it — a third of
     * held pairs still collided once sideways freedom was restricted — while this finds the gaps that are there instead of
     * hoping a dice roll lands in one. Three columns because a column offers about three places to sit beside somebody; six rows
     * because that is roughly how many rows of text the band depth allows, measured rather than derived: 3x6 and 5x8 came out
     * within a couple of points of each other on overlap, so the grid is sized to the space, not tuned for luck.
     */
    public const int LateralSteps = 3;
    public const int DepthSteps = 6;

    /* How far a candidate drifts off its lattice point, as a share of the lattice spacing. Enough that no two numbers land in
     * the same pixel twice, small enough that the search still covers the band instead of clumping. */
    public const double LatticeJitter = 0.35;

    /* Total candidates considered, including the layout's own throw. Reported in diagnostics. */
    public static int Candidates => LateralSteps * DepthSteps + 1;

    /*
     * How far sideways the search may reach from the column's centre, in widths of the number's own text — the question "could a
     * neighbour sit beside this one?" is about how wide the text is, not how wide the window is: at 34 pt on a 980 overlay one
     * text is ~0.16 of the width and on a 560 overlay ~0.28, so a fixed canvas fraction would be too tight on the small window,
     * where crowding hurts most, and too loose on the big one.
     *
     * Depth is free and sideways is not, which is the whole reason this is a separate number. The first version of this file used
     * the same widening on both axes — five times the layout's jitter — and that is how a hit came to start at the far left border
     * of the overlay and sway inland on the way up: the damage column sits at 0.42 of the width, so a ±0.45 throw went off the left
     * edge, the clamp pinned it to the wall, and the search scored that wall as empty space. Measured afterwards: numbers averaged
     * 22% of the overlay away from their own column, with 7% starting flush against an edge. The column is what tells damage and
     * healing apart without reading a word, so sideways reach is priced in text widths and capped by LateralSearchMaxFrac.
     */
    public const double LateralSearchTexts = 1.9;

    /* Never further than this share of the overlay width from the column's centre, whatever the text says: a small window, a
     * wide crit or an eleven-character total must not be able to buy its way across the screen with room. */
    public const double LateralSearchMaxFrac = 0.22;

    /* Samples per pair along their shared flight. Enough to catch a crossing without weighting any one instant. */
    public const int TimeSamples = 6;

    /*
     * What choosing a wide draw costs, against overlap that costs up to 1.0 per neighbour. Small enough that an empty overlay
     * keeps the layout's own spot every time (the first candidate is in the set and costs no penalty), large enough that a
     * candidate has to actually clear space rather than merely be somewhere else.
     */
    public const double WideSearchCost = 0.05;

    /*
     * Graded preference for where the layout would have put the number anyway, as a share of canvas height per unit of
     * vertical drift. Without it, ten equally empty gaps are equal and the lane stops reading as a lane: numbers appear at
     * whatever depth the search happened to like. With it, crowding decides when there is crowding and the usual edge wins
     * when there is not.
     */
    public const double DriftWeight = 0.02;

    /*
     * The same preference sideways, and deliberately several times stronger: drifting a number three rows down its band costs it
     * nothing in meaning, while drifting it across the overlay quietly moves it into another column's territory.
     */
    public const double LateralDriftWeight = 0.12;

    /*
     * Returns the placed hit, which is not necessarily the one passed in: candidates are trials of the same hit, and the best
     * is returned for the caller to add to its list. `hit` is returned untouched when there is nothing to dodge.
     */
    internal static FctHitState Place(FctHitState hit, List<FctHitState> hits, double w, double h, Random rand)
      => Place(hit, hits, FctStage.Bands(w, h), rand);

    /*
     * The search walks the candidate's own band and column territory: in halves both are one side's half, so a candidate can
     * never be proposed across the seam, and the overlap pass below never spends a sample on a number that cannot reach it.
     */
    internal static FctHitState Place(FctHitState hit, List<FctHitState> hits, FctStage stage, Random rand)
    {
      if (hits.Count == 0 || stage.W <= 0 || stage.H <= 0)
      {
        return hit;   // the first number of a pull gets the lane's own spot, and costs nothing to find
      }

      var region = stage.RegionFor(hit);

      /* Sideways room in pixels, from the text: see LateralSearchTexts. Zero width (nothing measured yet) leaves the layout's
       * own jitter in place rather than inventing a reach. The column centre is the lane slot in bands and the half's own
       * centre in halves — there are no columns there to drift back toward, and the canonical-x preference below does the
       * job of keeping a stream centred on its side instead. */
      var slot = stage.Mode is not FctLayoutMode.Bands ? region.X + (region.Width / 2) : FctLayout.LaneSlot(hit.Lane, stage.W);
      var reach = Math.Min(stage.TerritoryFor(hit) * LateralSearchMaxFrac, hit.ValueWidth * LateralSearchTexts);
      var canonicalX = hit.X0;
      var canonicalY = hit.Y0;

      var best = hit;
      var bestCost = Cost(hit, canonicalX, canonicalY, stage.W, stage.H, hits, 0);

      /* The band this hit was given, walked from edge to edge. Band edges come from the layout's own reserve maths, so every
       * candidate below is inside a legal band before it is scored. */
      var depth = hit.BandMaxY - hit.BandMinY;
      var xStep = LateralSteps > 1 ? (2 * reach) / (LateralSteps - 1) : 0;

      for (var row = 0; row < DepthSteps; row++)
      {
        var y = hit.BandMinY + (depth * ((row + 0.5) / DepthSteps));

        for (var col = 0; col < LateralSteps; col++)
        {
          var x = slot + ((col - ((LateralSteps - 1) / 2.0)) * xStep);

          var trial = Trial(hit, stage, rand,
            x + (xStep * LatticeJitter * (rand.NextDouble() * 2 - 1)),
            y + ((depth / DepthSteps) * LatticeJitter * (rand.NextDouble() * 2 - 1)));

          var cost = Cost(trial, canonicalX, canonicalY, stage.W, stage.H, hits, WideSearchCost);
          if (cost < bestCost)
          {
            best = trial;
            bestCost = cost;
          }
        }
      }

      return best;
    }

    /*
     * A search over a caller-supplied list of launch points instead of the lattice — this is what FctStream scores
     * its three row positions with, and why "stream vs scatter" is one difference of candidate sets rather than two
     * scoring engines. Unlike Place it has no nothing-to-dodge shortcut: the trial IS the placement even against an
     * empty overlay, because the caller's origin (the stream's centre) outranks wherever the free throw landed.
     * `pad` inflates every pair test; see Cost.
     */
    internal static FctHitState PlaceOrigins(FctHitState hit, List<FctHitState> hits, FctStage stage, Random rand,
      (double X, double Y)[] origins, double anchorX, double anchorY, double pad)
    {
      var best = Trial(hit, stage, rand, origins[0].X, origins[0].Y);
      var bestCost = Cost(best, anchorX, anchorY, stage.W, stage.H, hits, 0, pad);

      for (var i = 1; i < origins.Length; i++)
      {
        var trial = Trial(hit, stage, rand, origins[i].X, origins[i].Y);
        var cost = Cost(trial, anchorX, anchorY, stage.W, stage.H, hits, 0, pad);
        if (cost < bestCost)
        {
          best = trial;
          bestCost = cost;
        }
      }

      return best;
    }

    /* One scored candidate: the layout's own spawn at a requested origin, with its fall recomputed because the
     * fall belongs to the travel and a new origin is new travel. */
    private static FctHitState Trial(FctHitState hit, FctStage stage, Random rand, double x, double y)
    {
      var trial = hit.Clone();
      FctLayout.Spawn(trial, stage, rand, origin: (x, y));
      FctLayout.ApplyFall(trial, stage);
      return trial;
    }

    /*
     * Crowding cost: for every live number in flight with this one, the worst fraction of the smaller block that the two
     * cover at any sampled moment of their shared life, plus the candidate's own wide-search penalty.
     *
     * Opacity is not part of it. Weighting by fade would make a collision at spawn look cheap — every hit fades in — and
     * spawn collisions are exactly what reads as one blob of unreadable text.
     *
     * `pad` grows every pair test by that many pixels: with padding, "costs nothing" means *this far apart*, not merely
     * *not touching* — which is how FctStream keeps MSBT's minimum line gap without changing what the lattice search
     * (pad 0) scores. Bit-identical for the old call sites because 0 inflation is arithmetic nobody can feel.
     */
    private static double Cost(FctHitState candidate, double canonicalX, double canonicalY, double w, double h, List<FctHitState> hits, double penalty, double pad = 0)
    {
      var cost = penalty
        + (DriftWeight * Math.Abs(candidate.Y0 - canonicalY) / h)
        + (LateralDriftWeight * Math.Abs(candidate.X0 - canonicalX) / w);

      for (var i = 0; i < hits.Count; i++)
      {
        cost += WorstOverlap(candidate, hits[i], pad);
      }

      return cost;
    }

    private static double WorstOverlap(FctHitState candidate, FctHitState other, double pad)
    {
      /* A drawn block never leaves its own [SideMin, SideMax]: ArcedX clamps the centre to that range with the half-width
       * priced at peak scale, and the block is never wider than it was priced. Two numbers whose side ranges do not overlap
       * therefore cannot touch at any sampled instant — in halves that answers every cross-region pair before a single sample
       * is spent (the two halves' ranges are disjoint by construction), while in bands both sides carry the canvas range and
       * no pair is ever skipped. Bit-identical output, because the sweep these pairs would run always scored zero.
       */
      if (candidate.SideMax <= other.SideMin || other.SideMax <= candidate.SideMin)
      {
        return 0;
      }

      // wall-clock alignment: the other hit was spawned earlier, so it is that much further along at any candidate age
      var ageOffset = candidate.SpawnMs - other.SpawnMs;
      if (ageOffset >= other.LifetimeMs)
      {
        return 0;   // gone before this number appears
      }

      var shared = Math.Min(candidate.LifetimeMs, other.LifetimeMs - ageOffset);
      if (shared <= 0)
      {
        return 0;
      }

      var worst = 0.0;
      for (var s = 0; s <= TimeSamples; s++)
      {
        var age = shared * s / TimeSamples;
        var overlap = Overlap(Block(candidate, age, pad), Block(other, age + ageOffset, pad));
        if (overlap > worst)
        {
          worst = overlap;
        }
      }

      return worst;
    }

    /* Fraction of the smaller block covered by the other one: 0 apart, 1 on top of each other. */
    private static double Overlap((double Left, double Top, double Right, double Bottom) a, (double Left, double Top, double Right, double Bottom) b)
    {
      var ix = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
      var iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
      if (ix <= 0 || iy <= 0)
      {
        return 0;
      }

      var areaA = (a.Right - a.Left) * (a.Bottom - a.Top);
      var areaB = (b.Right - b.Left) * (b.Bottom - b.Top);
      var smaller = Math.Min(areaA, areaB);

      return smaller <= 0 ? 0 : (ix * iy) / smaller;
    }

    /* Where the drawn block actually is at a given age: the same maths the canvases draw with, centre-anchored like
     * the text, then grown by pad on every side — "within padding of" is what a stream counts as a blocked row. */
    private static (double Left, double Top, double Right, double Bottom) Block(FctHitState hit, double ageMs, double pad)
    {
      var t = FctMotion.Progress(hit, ageMs);
      var scale = FctMotion.ScaleOf(hit, ageMs);
      var half = (hit.ValueWidth * scale) / 2.0 + pad;

      // no fade-out gate here: a number that is on screen at all is one the player could be trying to read
      var x = FctMotion.ArcedX(hit, t);
      var y = FctMotion.RaisedY(hit, t);

      return (x - half, y - pad, x + half, y + (FctLayout.TextHeight(hit) * scale) + pad);
    }
  }
}

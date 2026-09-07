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
   * So: throw several legal spawns, watch where each one would actually go against the hits already in flight, and keep the
   * one that gets its own space. Every candidate comes out of FctLayout.Spawn, which is what keeps this honest — no
   * candidate can sit in the protected strip, outside the window or on a band the layout would not have chosen, because it is
   * the layout's own geometry with a wider dice cup. Nothing is dropped for want of room: if every option crowds, the best
   * of them is still placed, exactly as before this file existed.
   *
   * Cost is measured along the flight rather than at the origin, because that is what the player watches. Fountain travels a
   * band and falls back through it, so two spawns that start apart can still collide on the way down — sampling positions at
   * one instant would have missed the case this exists for.
   */
  internal static class FctPlacement
  {
    /*
     * How many draws to try. The first keeps the layout's own gentle jitter, so an uncrowded overlay looks exactly as it did
     * before; the rest search wider. Twelve takes the three-fountain case above to 3% of pairs and a crowded hold band from
     * 71% to 6%, and beyond it the curve is flat enough that the extra measuring buys nothing — a whole lane of twelve live
     * numbers costs about 1.3 us per placement, which is why spending more of it is cheap but pointless.
     */
    public const int Candidates = 12;

    /*
     * How much wider the later draws may reach, as a multiple of the layout's own origin jitter — sideways and away from the
     * protected strip at once. This is the "there was plenty of room" the layout was not using: its jitter spends about an
     * eighth of a band, so with one number already in flight there was barely anywhere else to go. Reach matters more than
     * candidate count here (five wide draws beat twelve narrow ones), and past this the mean origin moves visibly away from
     * where the lane puts things, which stops looking like one lane throwing numbers and starts looking like a scatter.
     */
    public const double SearchFrac = 5.0;

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
     * Returns the placed hit, which is not necessarily the one passed in: candidates are trials of the same hit, and the best
     * is returned for the caller to add to its list. `hit` is returned untouched when there is nothing to dodge.
     */
    internal static FctHitState Place(FctHitState hit, List<FctHitState> hits, double w, double h, Random rand)
    {
      if (hits.Count == 0 || w <= 0 || h <= 0)
      {
        return hit;   // the first number of a pull gets the lane's own spot, and costs nothing to find
      }

      var canonicalY = hit.Y0;
      var best = hit;
      var bestCost = Cost(hit, canonicalY, h, hits, 0);

      for (var i = 1; i < Candidates; i++)
      {
        var trial = hit.Clone();
        FctLayout.Spawn(trial, w, h, rand, SearchFrac);

        // the fall belongs to the travel, so a candidate with a new origin needs it recomputed before it can be watched
        FctLayout.ApplyFall(trial, h);

        var cost = Cost(trial, canonicalY, h, hits, WideSearchCost);
        if (cost < bestCost)
        {
          best = trial;
          bestCost = cost;
        }
      }

      return best;
    }

    /*
     * Crowding cost: for every live number in flight with this one, the worst fraction of the smaller block that the two
     * cover at any sampled moment of their shared life, plus the candidate's own wide-search penalty.
     *
     * Opacity is not part of it. Weighting by fade would make a collision at spawn look cheap — every hit fades in — and
     * spawn collisions are exactly what reads as one blob of unreadable text.
     */
    private static double Cost(FctHitState candidate, double canonicalY, double h, List<FctHitState> hits, double penalty)
    {
      var cost = penalty + (DriftWeight * Math.Abs(candidate.Y0 - canonicalY) / h);

      for (var i = 0; i < hits.Count; i++)
      {
        cost += WorstOverlap(candidate, hits[i]);
      }

      return cost;
    }

    private static double WorstOverlap(FctHitState candidate, FctHitState other)
    {
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
        var overlap = Overlap(Block(candidate, age), Block(other, age + ageOffset));
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

    /* Where the drawn block actually is at a given age: the same maths the canvases draw with, centre-anchored like the text. */
    private static (double Left, double Top, double Right, double Bottom) Block(FctHitState hit, double ageMs)
    {
      var t = FctMotion.Progress(hit, ageMs);
      var scale = FctMotion.ScaleOf(hit, ageMs);
      var half = (hit.ValueWidth * scale) / 2.0;

      // no fade-out gate here: a number that is on screen at all is one the player could be trying to read
      var x = FctMotion.ArcedX(hit, t);
      var y = FctMotion.RaisedY(hit, t);

      return (x - half, y, x + half, y + (FctLayout.TextHeight(hit) * scale));
    }
  }
}

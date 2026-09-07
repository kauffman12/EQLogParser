using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * The stream (FctStream): row discipline for the parabola in halves — numbers run down a centre column at the spawn
   * edge instead of being scattered, and bursts braid to one side and then the other rather than stacking. What these
   * pin down: that the column is the region's exact centre and the edge exactly (no depth dice), that rows spaced in
   * time reuse that centre, that a burst separates without touching, that more rows than columns still never drop a
   * number, that each half streams on its own, and that the scatter styles keep their lattice — the stream is the
   * parabola's discipline, not everybody's.
   */
  [TestClass]
  public sealed class FctStreamTest
  {
    private const double Width = 980;
    private const double Height = 640;

    /* An accept through the shipped scheme with the style that streams; values vary so identity folding never enters
     * the picture, and sources vary for belt-and-braces. */
    private static readonly string[] Sources = ["Slash", "Crush", "Pierce"];

    private static FctHitState Accept(FctIngest ingest, List<FctHitState> hits, bool incoming, double value, double nowMs,
      double w = Width, double h = Height)
      => ingest.Accept(hits, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, value,
        Sources[(int)(value % 3)], false, false, false, null, w, h, nowMs);

    private static FctIngest Streaming() => new(new Random(7))
    {
      Style = FctMotionStyle.Parabola,
      Layout = FctLayoutChoice.Shipped,
    };

    /* The drawn block again, unpadded: what the player's eye measures. */
    private static (double L, double T, double R, double B) BlockOf(FctHitState hit, double ageMs)
    {
      var t = FctMotion.Progress(hit, ageMs);
      var scale = FctMotion.ScaleOf(hit, ageMs);
      var half = (hit.ValueWidth * scale) / 2.0;
      var x = FctMotion.ArcedX(hit, t);
      var y = FctMotion.RaisedY(hit, t);
      return (x - half, y, x + half, y + (FctLayout.TextHeight(hit) * scale));
    }

    /* Worst shared fraction of the smaller block over the window both are actually drawn — each aged from its own birth. */
    private static double WorstPairOverlap(FctHitState a, FctHitState b)
    {
      var first = Math.Max(a.SpawnMs, b.SpawnMs);
      var last = Math.Min(a.SpawnMs + a.LifetimeMs, b.SpawnMs + b.LifetimeMs);
      if (last <= first)
      {
        return 0;
      }

      var worst = 0.0;
      for (var s = 0; s <= 40; s++)
      {
        var now = first + ((last - first) * s / 40.0);
        var ba = BlockOf(a, now - a.SpawnMs);
        var bb = BlockOf(b, now - b.SpawnMs);

        var ix = Math.Min(ba.R, bb.R) - Math.Max(ba.L, bb.L);
        var iy = Math.Min(ba.B, bb.B) - Math.Max(ba.T, bb.T);
        if (ix <= 0 || iy <= 0)
        {
          continue;
        }

        worst = Math.Max(worst, (ix * iy) / Math.Min((ba.R - ba.L) * (ba.B - ba.T), (bb.R - bb.L) * (bb.B - bb.T)));
      }

      return worst;
    }

    /* Empty overlay, one number: it goes to the exact centre of its half at the exact spawn edge. The free throw's
     * ±9 % jitter and depth dice are gone by design — along-travel placement belongs to birth time, not luck. */
    [TestMethod]
    public void AStreamNumberSpawnsAtTheCentreOfItsColumnOnTheEdge()
    {
      var ingest = Streaming();
      var hits = new List<FctHitState>();
      var hit = Accept(ingest, hits, incoming: false, 1001, 0);

      Assert.IsNotNull(hit);
      var stage = FctStage.Halves(FctRegionSide.Left, false, false, Width, Height);
      var region = stage.RegionFor(false);
      Assert.AreEqual(region.X + (region.Width / 2), hit.X0, 1e-9, "the stream's column is its region's centre");

      var edge = stage.UpFor(false) > 0 ? hit.BandMaxY : hit.BandMinY;
      Assert.AreEqual(edge, hit.Y0, 1e-9, "rows launch from the edge itself, with no depth jitter");
    }

    /*
     * The centre is reusable, not occupied: rows born far enough apart that constant-speed travel has separated them
     * go back to the same column — which is what makes a steady fight one clean stream instead of a widening fan.
     */
    [TestMethod]
    public void RowsBornFarApartReuseTheCentreColumn()
    {
      var ingest = Streaming();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 3; i++)
      {
        var hit = Accept(ingest, hits, incoming: false, 1001 + i, i * 800);
        Assert.IsNotNull(hit, $"row {i} was not spawned");
        var stage = FctStage.Halves(FctRegionSide.Left, false, false, Width, Height);
        var region = stage.RegionFor(false);
        Assert.AreEqual(region.X + (region.Width / 2), hit.X0, 1e-9, $"row {i} abandoned the centre column it had coming");
      }
    }

    /*
     * Three simultaneous rows in a territory wide enough to hold its three columns: centre, then two braided upstream
     * of the drift — and no two of them ever touch across their whole shared life. Parallel sweeps at equal spacing
     * never converge; this is the stream's promise when the geometry can afford it.
     */
    [TestMethod]
    public void AThreeRowBurstBraidsApartWithoutTouching()
    {
      const double wide = 1600;
      const double tall = 900;

      var ingest = Streaming();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 3; i++)
      {
        Assert.IsNotNull(Accept(ingest, hits, incoming: false, 1001 + i, 0, wide, tall), $"burst row {i} was not spawned");
      }

      var columns = hits.Select(h => Math.Round(h.X0, 6)).Distinct().Count();
      Assert.AreEqual(3, columns, "a burst of three must occupy three columns");

      for (var i = 0; i < hits.Count; i++)
      {
        for (var j = i + 1; j < hits.Count; j++)
        {
          Assert.AreEqual(0.0, WorstPairOverlap(hits[i], hits[j]), 1e-9,
            "rows placed in different columns of a territory that fits them must never overlap while both are drawn");
        }
      }

      var stage = FctStage.Halves(FctRegionSide.Left, false, false, wide, tall);
      var region = stage.RegionFor(false);
      Assert.IsTrue(hits.All(h => h.X0 >= region.X && h.X0 <= region.X + region.Width), "columns stay inside their half");
    }

    /*
     * Where the half is narrower than three full text widths, the columns compress instead of disappearing: every row
     * still spawns, stays inside its half, and the worst overlap between any two is a graze bounded by the compression
     * — not the wall-pinning collision an unpriced braid produced, where an emergency column parked at the clamp
     * directly in the centre row's sweep.
     */
    [TestMethod]
    public void NarrowTerritoryCompressesTheColumnsInsteadOfLosingRows()
    {
      var ingest = Streaming();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 3; i++)
      {
        Assert.IsNotNull(Accept(ingest, hits, incoming: false, 1001 + i, 0), $"row {i} was lost to compression");
      }

      var stage = FctStage.Halves(FctRegionSide.Left, false, false, Width, Height);
      var region = stage.RegionFor(false);
      Assert.IsTrue(hits.All(h => h.X0 >= region.X && h.X0 <= region.X + region.Width), "compressed columns stay inside their half");

      for (var i = 0; i < hits.Count; i++)
      {
        for (var j = i + 1; j < hits.Count; j++)
        {
          var overlap = WorstPairOverlap(hits[i], hits[j]);
          Assert.IsTrue(overlap < 0.25, $"compressed rows may graze, not cover: {overlap:0.##} of a block");
        }
      }
    }

    /*
     * The genre's overlays get away with dropping rows because their input was throttled upstream; a parser overlay
     * may not. Past the three columns the least-bad spot wins — crowded, contained, and counted where counting happens.
     */
    [TestMethod]
    public void MoreRowsThanColumnsStillNeverDropANumber()
    {
      var ingest = Streaming();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 6; i++)
      {
        Assert.IsNotNull(Accept(ingest, hits, incoming: false, 1001 + i, 0), $"row {i} was dropped by the stream");
      }

      Assert.AreEqual(0, ingest.DroppedCount, "a crowded stream is not a full overlay");
    }

    /* Each half streams down its own centre column; what fills one says nothing about the other. */
    [TestMethod]
    public void EachHalfStreamsOnItsOwn()
    {
      var ingest = Streaming();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 3; i++)
      {
        Assert.IsNotNull(Accept(ingest, hits, incoming: true, 1001 + i, 0));
      }

      var outgoing = Accept(ingest, hits, incoming: false, 2001, 0);
      Assert.IsNotNull(outgoing);

      var stage = FctStage.Halves(FctRegionSide.Left, false, false, Width, Height);
      var region = stage.RegionFor(false);
      Assert.AreEqual(region.X + (region.Width / 2), outgoing.X0, 1e-9,
        "a full incoming half may not push the lone outgoing number off its own centre");
    }

    /*
     * Column discipline belongs to the parabola: twelve numbers in a row stream through at most the three columns
     * (same-length values keep the column step constant), while the styles that kept the lattice still scatter.
     */
    [TestMethod]
    public void TheDisciplineIsTheParabolasAndNotEverybodys()
    {
      var ingest = Streaming();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 12; i++)
      {
        ingest.PruneExpired(hits, i * 400);
        Accept(ingest, hits, incoming: false, 1001 + i, i * 400);
      }

      Assert.IsTrue(hits.Count > 0);
      Assert.IsTrue(hits.Select(h => Math.Round(h.X0, 6)).Distinct().Count() <= 3,
        "the stream is three columns; anything wider means the lattice leaked back in");

      var spray = new FctIngest(new Random(9)) { Style = FctMotionStyle.Spray, Layout = FctLayoutChoice.Shipped };
      var scattered = new List<FctHitState>();
      for (var i = 0; i < 12; i++)
      {
        spray.PruneExpired(scattered, i * 400);
        Accept(spray, scattered, incoming: false, 1001 + i, i * 400);
      }

      Assert.IsTrue(scattered.Select(h => Math.Round(h.X0, 6)).Distinct().Count() > 3,
        "spray must keep the free lattice search; the stream is not a global placement");
    }
  }
}

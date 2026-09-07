using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The halves region scheme (FctStage): two side-by-side streams with per-side direction, and the promise that both
   * schemes share one code path. The load-bearing test in here is the bands one — a refactor of shared geometry that
   * changes a single bit of what bands mode draws has broken the overlay for every player who never touches halves — so
   * bands is pinned against its old entry points draw for draw, and halves gets its own containment guarantees: a number
   * stays in its half, out of the window, and at the spawn edge its direction says it starts from, for its whole life.
   */
  [TestClass]
  public sealed class FctHalvesTest
  {
    private const double Width = 980;
    private const double Height = 640;

    private static FctStage Shipped(double w = Width, double h = Height) => FctLayoutChoice.Shipped.Stage(w, h);

    private static FctStage Halves(FctRegionSide side, bool incomingUp, bool outgoingUp, double w = Width, double h = Height)
      => FctStage.Halves(side, incomingUp, outgoingUp, w, h);

    // ---- the stage itself: whose region is this, which way does it travel ----

    [TestMethod]
    public void TheShippedDefaultIsHalvesIncomingLeftBothSidesDown()
    {
      var stage = Shipped();

      Assert.AreEqual(FctLayoutMode.Halves, stage.Mode);
      Assert.AreEqual(0.0, stage.RegionFor(true).X, "shipped incoming sits in the left half");
      Assert.AreEqual(Width / 2, stage.RegionFor(true).Width);
      Assert.AreEqual(Width / 2, stage.RegionFor(false).X, "outgoing takes the other half");
      Assert.AreEqual(-1.0, stage.UpFor(true), "shipped incoming travels down");
      Assert.AreEqual(-1.0, stage.UpFor(false), "shipped outgoing travels down");
    }

    [TestMethod]
    public void BandsSharesTheCanvasAndKeepsOutUpInDown()
    {
      var stage = FctStage.Bands(Width, Height);

      Assert.AreEqual(1.0, stage.UpFor(false));
      Assert.AreEqual(-1.0, stage.UpFor(true));
      Assert.AreEqual(Width, stage.TerritoryFor(true));

      var region = stage.RegionFor(true);
      Assert.AreEqual(0.0, region.X);
      Assert.AreEqual(Width, region.Width, "bands: both sides share the canvas; the strip is FctLayout's business");
    }

    [TestMethod]
    public void MirroringTheIncomingSideSwapsTheRegions()
    {
      var mirrored = Halves(FctRegionSide.Right, false, false);

      Assert.AreEqual(Width / 2, mirrored.RegionFor(true).X, "incoming right starts in the right half");
      Assert.AreEqual(0.0, mirrored.RegionFor(false).X);
      Assert.AreEqual(Width / 2, mirrored.TerritoryFor(true));
    }

    // ---- bands regression: the refactor must not move a single pixel of the old scheme ----

    [TestMethod]
    public void BandsSpawnIsIdenticalThroughItsOldEntryPoints()
    {
      var randOld = new Random(77);
      var randNew = new Random(77);
      var lanes = new[] { FctLane.DamageDealt, FctLane.DamageTaken, FctLane.HealingDealt, FctLane.HealingReceived, FctLane.Missed };
      var styles = new[] { FctMotionStyle.Hold, FctMotionStyle.Fountain, FctMotionStyle.Pulse, FctMotionStyle.Spray };

      for (var i = 0; i < 200; i++)
      {
        var incoming = i % 3 == 0;
        var lane = incoming ? lanes[i % lanes.Length] : lanes[(i + 1) % lanes.Length];
        var style = styles[i % styles.Length];
        var proc = i % 5 == 0;

        var oldWay = Build(lane, incoming, style, proc, crit: i % 7 == 0);
        FctLayout.Spawn(oldWay, Width, Height, randOld);
        FctLayout.ApplyFall(oldWay, Height);

        var newWay = Build(lane, incoming, style, proc, crit: i % 7 == 0);
        FctLayout.Spawn(newWay, FctStage.Bands(Width, Height), randNew);
        FctLayout.ApplyFall(newWay, FctStage.Bands(Width, Height));

        Assert.AreEqual(oldWay.X0, newWay.X0, $"x moved on iteration {i}");
        Assert.AreEqual(oldWay.Y0, newWay.Y0, $"y moved on iteration {i}");
        Assert.AreEqual(oldWay.Rise, newWay.Rise, $"rise moved on iteration {i}");
        Assert.AreEqual(oldWay.Arc, newWay.Arc, $"arc moved on iteration {i}");
        Assert.AreEqual(oldWay.FallDist, newWay.FallDist, $"fall moved on iteration {i}");
        Assert.AreEqual(oldWay.BandMinY, newWay.BandMinY, $"band moved on iteration {i}");
        Assert.AreEqual(oldWay.BandMaxY, newWay.BandMaxY, $"band moved on iteration {i}");
        Assert.AreEqual(oldWay.SideMin, newWay.SideMin);
        Assert.AreEqual(oldWay.SideMax, newWay.SideMax);
      }
    }

    // ---- halves containment: the number stays in its half for its whole life ----

    [TestMethod]
    public void HalvesNumbersStayInsideTheirOwnHalfForTheirWholeLife()
    {
      var rand = new Random(4_242);
      var styles = new[] { FctMotionStyle.Hold, FctMotionStyle.Fountain, FctMotionStyle.Pulse, FctMotionStyle.Spray };

      foreach (var style in styles)
      {
        foreach (var incomingSide in new[] { FctRegionSide.Left, FctRegionSide.Right })
        {
          foreach (var incomingUp in new[] { false, true })
          {
            foreach (var outgoingUp in new[] { false, true })
            {
              var stage = Halves(incomingSide, incomingUp, outgoingUp);

              for (var i = 0; i < 40; i++)
              {
                foreach (var incoming in new[] { true, false })
                {
                  var hit = Spawn(stage, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, style);
                  var region = stage.RegionFor(incoming);

                  for (var t = 0.0; t <= 1.0; t += 0.05)
                  {
                    var x = FctMotion.ArcedX(hit, t);
                    var y = FctMotion.RaisedY(hit, t);

                    // the value's centre: ArcedX clamps it to the side bounds, and a centre inside the half is what "its own stream" means
                    Assert.IsTrue(x >= region.X + FctLayout.EdgePad - 0.5 && x <= region.X + region.Width - FctLayout.EdgePad + 0.5,
                      $"text crossed the seam at t={t:0.00} (x {x}, half {region.X}-{region.X + region.Width})");

                    // the drawn block must not leave the window on its own side
                    Assert.IsTrue(y >= -0.5 && y + FctLayout.TextReserve(hit) <= Height + 0.5,
                      $"text left the window at t={t:0.00} (y {y})");
                  }
                }
              }
            }
          }
        }
      }
    }

    [TestMethod]
    public void TheSpawnEdgeFollowsTheSideDirection()
    {
      // a down-travelling side starts its numbers at the top of the half; a rising one at the bottom
      var down = Spawn(Halves(FctRegionSide.Left, false, false), FctLane.DamageTaken, incoming: true, new Random(3));
      Assert.IsTrue(down.Rise < 0, "the down side must sink");
      Assert.IsTrue(down.Y0 < Height * 0.3, $"down-travelling spawn at {down.Y0} is not near the top of its half");

      var up = Spawn(Halves(FctRegionSide.Left, true, false), FctLane.DamageTaken, incoming: true, new Random(3));
      Assert.IsTrue(up.Rise > 0, "the up side must rise");
      Assert.IsTrue(up.Y0 > Height * 0.7, $"rising spawn at {up.Y0} is not near the bottom of its half");
    }

    [TestMethod]
    public void HalvesFountainBouncesOffItsOwnFarEnd()
    {
      // no strip anywhere in halves: whatever the side's direction, the fall runs back toward the spawn edge, so a number
      // can never park against the far wall the way the old incoming-band fountain did
      var stage = Halves(FctRegionSide.Left, true, false);

      var sinking = Spawn(stage, FctLane.DamageDealt, incoming: false, new Random(5), FctMotionStyle.Fountain);
      FctLayout.ApplyFall(sinking, stage);
      Assert.IsTrue(sinking.Rise < 0, "outgoing on this stage sinks");
      Assert.IsTrue(sinking.FallDist < 0, "a sinking fountain must fall back up, toward where it came from");

      var rising = Spawn(stage, FctLane.DamageTaken, incoming: true, new Random(5), FctMotionStyle.Fountain);
      FctLayout.ApplyFall(rising, stage);
      Assert.IsTrue(rising.Rise > 0, "incoming on this stage rises");
      Assert.IsTrue(rising.FallDist > 0, "a rising fountain must fall back down, toward where it came from");

      // the bounce is a share of the travel already spent: it cannot cross back through the spawn edge
      var depth = Math.Min(Math.Abs(sinking.FallDist), Math.Abs(rising.FallDist));
      Assert.IsTrue(depth > 0 && depth <= FctMotion.IncomingFallsBackFrac * Height, $"bounce depth {depth}");
    }

    // ---- pulse cells: one block per half, measured against the half ----

    [TestMethod]
    public void PulseCellsStayInsideTheirHalf()
    {
      var stage = Halves(FctRegionSide.Left, false, false);
      var hits = new List<FctHitState>();
      var rand = new Random(9);

      for (var i = 0; i < 16; i++)
      {
        var incoming = i % 2 == 0;
        var hit = Spawn(stage, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, FctMotionStyle.Pulse);

        Assert.IsTrue(FctCellGrid.HasRoom(hit, stage), "a full-size overlay must have room for pulse cells in a half");
        Assert.IsTrue(FctCellGrid.Assign(hit, hits, stage, now: i * 100.0, out _), "cells are the point of pulse mode");
        hits.Add(hit);

        var region = stage.RegionFor(incoming);
        var x = FctMotion.ArcedX(hit, 1.0); // where the slide finishes
        Assert.IsTrue(x >= region.X + FctLayout.EdgePad - 0.5 && x <= region.X + region.Width - FctLayout.EdgePad + 0.5,
          $"a cell landed across the seam (x {x}, half {region.X}-{region.X + region.Width})");
      }
    }

    [TestMethod]
    public void TheTwoHalfsNeverShareACell()
    {
      // the old halves bug was cells measured against canvas width collapsing onto one place: with per-half grids, every cell
      // in one half has its own spot and none of them intersect a cell in the other
      var stage = Halves(FctRegionSide.Left, false, false);
      var hits = new List<FctHitState>();
      var rand = new Random(13);

      // eight main cells per half, plus a proc row each: filling both halves is what puts the seam under test
      for (var i = 0; i < (FctCellGrid.MainColumns * FctCellGrid.MainRows + FctCellGrid.ProcColumns) * 2; i++)
      {
        var incoming = i % 2 == 0;
        var hit = Spawn(stage, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, FctMotionStyle.Pulse,
          proc: i >= FctCellGrid.MainColumns * FctCellGrid.MainRows);
        Assert.IsTrue(FctCellGrid.Assign(hit, hits, stage, now: i * 100.0, out _));
        hits.Add(hit);
      }

      for (var a = 0; a < hits.Count; a++)
      {
        for (var b = a + 1; b < hits.Count; b++)
        {
          var aHit = hits[a];
          var bHit = hits[b];
          if (aHit.Incoming == bHit.Incoming)
          {
            continue;   // same half, different cells by construction of the grid; this test is about the seam
          }

          Assert.IsFalse(Intersects(BlockOf(aHit), BlockOf(bHit)), "a block from each half shares a spot");
        }
      }
    }

    [TestMethod]
    public void ReseatingKeepsCellsInsideTheirNewHalf()
    {
      var oldStage = Halves(FctRegionSide.Left, false, false, w: 980, h: 640);
      var newStage = Halves(FctRegionSide.Left, false, false, w: 720, h: 520);
      var hits = new List<FctHitState>();
      var rand = new Random(17);

      for (var i = 0; i < 8; i++)
      {
        var incoming = i % 2 == 0;
        var hit = Spawn(oldStage, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, FctMotionStyle.Pulse);
        Assert.IsTrue(FctCellGrid.Assign(hit, hits, oldStage, now: i * 100.0, out _));
        hits.Add(hit);
      }

      FctResize.Rescale(hits, 980, 640, newStage);

      for (var i = 0; i < hits.Count; i++)
      {
        var hit = hits[i];
        var region = newStage.RegionFor(hit.Incoming);
        var x = FctMotion.ArcedX(hit, 1.0);
        Assert.IsTrue(x >= region.X + FctLayout.EdgePad - 0.5 && x <= region.X + region.Width - FctLayout.EdgePad + 0.5,
          $"a re-seated cell crossed the seam (x {x})");
      }
    }

    // ---- placement: the search walks the half, not the canvas ----

    [TestMethod]
    public void PlacementSearchStaysInsideTheHalf()
    {
      var stage = Halves(FctRegionSide.Left, true, false);
      var rand = new Random(23);
      var live = new List<FctHitState>();

      // a crowd in both halves so the search actually has to dodge something
      for (var i = 0; i < 24; i++)
      {
        var incoming = i % 2 == 0;
        var hit = Spawn(stage, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand,
          i % 3 == 0 ? FctMotionStyle.Fountain : FctMotionStyle.Hold);
        live.Add(FctPlacement.Place(hit, live, stage, rand));
      }

      var region = stage.RegionFor(true);
      for (var i = 0; i < 20; i++)
      {
        var newcomer = Spawn(stage, FctLane.DamageTaken, incoming: true, rand, FctMotionStyle.Hold);
        var placed = FctPlacement.Place(newcomer, live, stage, new Random(i));

        Assert.IsTrue(placed.X0 >= region.X + FctLayout.EdgePad - 0.5 && placed.X0 <= region.X + region.Width - FctLayout.EdgePad + 0.5,
          $"a candidate was proposed across the seam (x {placed.X0})");
      }
    }

    // ---- resize: the halves map onto themselves ----

    [TestMethod]
    public void AResizeMapsEachHalfOntoItsNewSelf()
    {
      var oldStage = Halves(FctRegionSide.Left, false, false, w: 800, h: 560);
      var newStage = Halves(FctRegionSide.Left, false, false, w: 980, h: 640);
      var hits = new List<FctHitState>();
      var rand = new Random(29);

      for (var i = 0; i < 10; i++)
      {
        var incoming = i % 2 == 0;
        var hit = Spawn(oldStage, incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand,
          i % 3 == 0 ? FctMotionStyle.Fountain : FctMotionStyle.Hold);
        FctLayout.ApplyFall(hit, oldStage);
        hits.Add(hit);
      }

      FctResize.Rescale(hits, 800, 560, newStage);

      for (var i = 0; i < hits.Count; i++)
      {
        var hit = hits[i];
        var region = newStage.RegionFor(hit.Incoming);

        Assert.IsTrue(hit.X0 >= region.X + FctLayout.EdgePad - 0.5 && hit.X0 <= region.X + region.Width - FctLayout.EdgePad + 0.5,
          $"a number ended up in the other half after a resize (x {hit.X0})");

        // the band is re-derived for the new size: asking twice answers the same thing
        var probe = hit.Clone();
        FctLayout.Refit(probe, newStage);
        Assert.AreEqual(probe.BandMinY, hit.BandMinY, $"band not re-fitted (hit {i})");
        Assert.AreEqual(probe.BandMaxY, hit.BandMaxY, $"band not re-fitted (hit {i})");
      }
    }

    // ---- helpers ----

    private static FctHitState Build(FctLane lane, bool incoming, FctMotionStyle style, bool proc, bool crit)
    {
      var hit = new FctHitState
      {
        Lane = crit ? FctLane.Crit : lane,
        Incoming = incoming,
        Style = style,
        Proc = proc,
        Source = "Spinning Attack",
        Value = 1234,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      return hit;
    }

    private static FctHitState Spawn(FctStage stage, FctLane lane, bool incoming, Random rand,
      FctMotionStyle style = FctMotionStyle.Hold, bool proc = false)
    {
      var hit = Build(lane, incoming, style, proc, crit: false);
      FctLayout.Spawn(hit, stage, rand);
      return hit;
    }

    /* The drawn block at rest, the same way the canvases measure it: what "sharing a spot" means for two cells. */
    private static (double Left, double Top, double Right, double Bottom) BlockOf(FctHitState hit)
    {
      var scale = FctMotion.ScaleOf(hit, hit.LifetimeMs / 2);
      var half = (hit.ValueWidth * scale) / 2.0;
      var x = FctMotion.ArcedX(hit, 1.0);
      var y = FctMotion.RaisedY(hit, 1.0);

      return (x - half, y, x + half, y + (FctLayout.TextHeight(hit) * scale));
    }

    private static bool Intersects((double Left, double Top, double Right, double Bottom) a,
      (double Left, double Top, double Right, double Bottom) b) =>
      a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
  }
}

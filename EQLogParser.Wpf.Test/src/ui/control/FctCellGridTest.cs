using System;
using System.Collections.Generic;

namespace EQLogParser
{

  /*
   * Pulse mode puts a number in a cell and leaves it there, which is the only way static combat text stays readable - two
   * numbers in one place is mush. These tests drive the real ingest rather than hand-building hits, because allocation is
   * the part that can go wrong: geometry alone can look perfect while two streams quietly claim the same slot.
   *
   * The promises, in order: cells fill from where the eye already is and grow outward; nothing overlaps; nothing reaches the
   * protected middle strip; procs get their own block outside the direct hits; a full block takes its oldest cell but never
   * a crit's for anything smaller; and an overlay too small for a grid keeps its numbers instead of dropping them.
   */
  [TestClass]
  public class FctCellGridTest
  {
    private const double Width = 980;
    private const double Height = 640;
    private const string Ability = "Crushing Blow";

    [TestMethod]
    public void CellsGrowOutwardFromTheMiddleOfTheBand()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();

      var first = Add(ingest, hits, FctLane.DamageDealt, 0);
      var count = FctCellGrid.CellCount(false, false, Height);
      Assert.IsTrue(count >= FctCellGrid.MainColumns * 2, $"the default overlay should hold at least two rows ({count} cells)");

      for (var i = 1; i < count; i++)
      {
        Add(ingest, hits, FctLane.DamageDealt, i * 10.0);
      }

      var near = Rest(hits[0]);
      var second = Rest(hits[1]);

      // centre-out: the first two numbers share a row and land on either side of the middle
      Assert.AreEqual(near.Y, second.Y, 0.001, "the first two cells should be in the same row");
      Assert.IsTrue((near.X - (Width / 2)) * (second.X - (Width / 2)) < 0, $"the block should grow outwards from the middle ({near.X:0.#}, {second.X:0.#})");

      // and it grows away from the strip, not sideways forever: the last row sits further out than the first
      var furthest = Outward(hits[^1]);
      Assert.IsTrue(furthest > Outward(hits[0]) + 10, $"later cells should sit further out ({furthest:0.#} vs {Outward(hits[0]):0.#})");

      DistinctPlaces(hits);
    }

    /* The whole reason for cells. Damage, healing and procs share a band's space and must still not touch. */
    [TestMethod]
    public void NoTwoCellsOverlap()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();
      var now = 0.0;

      foreach (var lane in new[] { FctLane.DamageDealt, FctLane.HealingDealt, FctLane.DamageTaken })
      {
        for (var i = 0; i < FctCellGrid.MainColumns; i++)
        {
          Add(ingest, hits, lane, now);
          Add(ingest, hits, lane, now + 1, proc: true);
          now += 20;
        }
      }

      Assert.IsTrue(hits.Count >= 20, $"expected a busy overlay to be laid out (got {hits.Count} numbers)");

      for (var a = 0; a < hits.Count; a++)
      {
        for (var b = a + 1; b < hits.Count; b++)
        {
          Assert.IsFalse(Touches(hits[a], hits[b]), $"two numbers overlap: {Label(hits[a])} and {Label(hits[b])}");
        }
      }
    }

    [TestMethod]
    public void EveryCellStaysInsideItsBandAndOffTheProtectedStrip()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();
      var gapTop = Height * FctLayout.GapTopFrac;
      var gapBottom = Height * FctLayout.GapBottomFrac;
      var now = 0.0;

      foreach (var lane in new[] { FctLane.DamageDealt, FctLane.HealingDealt, FctLane.DamageTaken })
      {
        for (var i = 0; i < 8; i++)
        {
          Add(ingest, hits, lane, now, proc: i % 2 == 0);
          now += 20;
        }
      }

      foreach (var hit in hits)
      {
        var rest = Rest(hit);
        var bottom = rest.Y + FctLayout.TextReserve(hit);

        Assert.IsTrue(rest.Y >= FctLayout.EdgePad - 0.001, $"cell above the window ({rest.Y:0.#})");
        Assert.IsTrue(bottom <= Height - FctLayout.EdgePad + 0.001, $"cell below the window ({bottom:0.#} of {Height})");

        if (hit.Incoming)
        {
          Assert.IsTrue(rest.Y >= gapBottom - 0.001, $"incoming cell encroached on the strip from below ({rest.Y:0.#} < {gapBottom:0.#})");
        }
        else
        {
          Assert.IsTrue(bottom <= gapTop + 0.001, $"outgoing cell encroached on the strip ({bottom:0.#} > {gapTop:0.#})");
        }
      }
    }

    /* Procs are the frequent, unaimed text, so they live in their own block outside the row the player is reading. */
    [TestMethod]
    public void ProcCellsSitOutsideTheDirectHits()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();
      var now = 0.0;

      foreach (var lane in new[] { FctLane.DamageDealt, FctLane.DamageTaken })
      {
        for (var i = 0; i < FctCellGrid.MainColumns; i++)
        {
          Add(ingest, hits, lane, now);
          Add(ingest, hits, lane, now + 1, proc: true);
          now += 20;
        }
      }

      foreach (var incoming in new[] { false, true })
      {
        double direct = 0;
        var procs = double.MaxValue;
        var sawProc = false;

        foreach (var hit in hits)
        {
          if (hit.Incoming != incoming)
          {
            continue;
          }

          if (hit.Proc)
          {
            sawProc = true;
            procs = Math.Min(procs, Outward(hit));
          }
          else
          {
            direct = Math.Max(direct, Outward(hit));
          }
        }

        Assert.IsTrue(sawProc, "the default overlay should have room for proc cells");
        Assert.IsTrue(procs > direct, $"proc cells should sit outside the direct hits ({procs:0.#} vs {direct:0.#})");
      }
    }

    [TestMethod]
    public void AFullBlockTakesTheOldestCell()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();

      var first = Add(ingest, hits, FctLane.DamageDealt, 0);
      var count = FctCellGrid.CellCount(false, false, Height);
      for (var i = 1; i < count; i++)
      {
        Add(ingest, hits, FctLane.DamageDealt, i * 10.0);
      }

      Assert.AreEqual(count, hits.Count, "the block should be exactly full");

      FctHitState? bumped = null;
      var fresh = Add(ingest, hits, FctLane.DamageDealt, count * 10.0, evicting: h => bumped = h);

      Assert.IsNotNull(fresh, "a full block must still show the newest number");
      Assert.IsNotNull(bumped, "and say which one it replaced");
      Assert.AreEqual(0, bumped.SpawnMs, 0.001, "the oldest number goes first");
      Assert.AreEqual(bumped.Cell, fresh.Cell, "the newcomer takes the cell it just freed");
      Assert.AreEqual(count, hits.Count, "taking a cell is a swap, not growth");
    }

    /* The one exception: a crit is the biggest number on screen and does not move for anything smaller. */
    [TestMethod]
    public void ACritHoldsItsCellAgainstSmallerNumbers()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();

      var first = Add(ingest, hits, FctLane.DamageDealt, 0, crit: true);
      var count = FctCellGrid.CellCount(false, false, Height);
      for (var i = 1; i < count; i++)
      {
        Add(ingest, hits, FctLane.DamageDealt, i * 10.0);
      }

      FctHitState? bumped = null;
      Add(ingest, hits, FctLane.DamageDealt, count * 10.0, evicting: h => bumped = h);

      Assert.IsNotNull(bumped, "the block was full, so something had to give");
      Assert.AreEqual(10, bumped.SpawnMs, 0.001, "the crit was there first and stays");
      Assert.IsFalse(bumped.Lane is FctLane.Crit, "a crit's cell is not up for grabs by a normal hit");

      // every cell now held by a crit: a smaller number is refused outright rather than erasing one
      var wall = Pulse();
      var crits = new List<FctHitState>();
      Add(wall, crits, FctLane.DamageDealt, 0, crit: true);
      var room = FctCellGrid.CellCount(false, false, Height);
      for (var i = 1; i < room; i++)
      {
        Add(wall, crits, FctLane.DamageDealt, i * 10.0, crit: true);
      }

      Assert.IsNull(Add(wall, crits, FctLane.DamageDealt, room * 10.0), "nothing is free, and nothing may be erased");
      Assert.AreEqual(1, wall.DroppedCount, "but the refusal is counted, not silent");
      Assert.IsNotNull(Add(wall, crits, FctLane.DamageDealt, (room * 10.0) + 5, crit: true), "a crit may take one");
    }

    /* One spawn point per band, so a burst reads as one event spreading out instead of several unrelated appearances. */
    [TestMethod]
    public void NumbersStartFromASinglePoint()
    {
      var ingest = Pulse();
      var hits = new List<FctHitState>();
      var now = 0.0;

      foreach (var lane in new[] { FctLane.DamageDealt, FctLane.DamageTaken })
      {
        for (var i = 0; i < 4; i++)
        {
          Add(ingest, hits, lane, now);
          now += 20;
        }
      }

      foreach (var incoming in new[] { false, true })
      {
        var originX = double.NaN;
        var originY = double.NaN;

        foreach (var hit in hits)
        {
          if (hit.Incoming != incoming)
          {
            continue;
          }

          originX = double.IsNaN(originX) ? hit.X0 : originX;
          originY = double.IsNaN(originY) ? hit.Y0 : originY;

          Assert.AreEqual(originX, hit.X0, 0.001, "every number in a band should start from the same x");
          Assert.AreEqual(originY, hit.Y0, 0.001, "and the same y");
          Assert.IsTrue(hit.Rise != 0 || hit.Arc != 0, "and then move out to its cell");
        }
      }

      // the two bands start apart from each other: direction is still the first thing a number says
      Assert.AreNotEqual(Outward(hits[0]), Outward(hits[^1]), 0.001);
    }

    /*
     * A tiny overlay has no room for rows plus margins, and the answer has to be the old static placement rather than
     * silence: dropping real numbers because the furniture did not fit is the worst possible trade.
     */
    [TestMethod]
    public void AnOverlayTooSmallForAGridKeepsItsNumbers()
    {
      const double ThinW = 420;
      const double ThinH = 300;

      var ingest = Pulse();
      var hits = new List<FctHitState>();
      var now = 0.0;

      foreach (var lane in new[] { FctLane.DamageDealt, FctLane.DamageTaken })
      {
        for (var i = 0; i < 4; i++)
        {
          var hit = Add(ingest, hits, lane, now, ThinW, ThinH);
          Assert.IsNotNull(hit, "a cramped overlay may lose the grid, not the number");
          Assert.AreEqual(-1, hit.Cell, "no cells fit here, so nothing is claimed");

          if (hit != null)
          {
            var rest = Rest(hit);
            Assert.IsTrue(rest.Y >= FctLayout.EdgePad - 0.001 && rest.Y + FctLayout.TextReserve(hit) <= ThinH - FctLayout.EdgePad + 0.001,
              $"a fallback placement ran off the window ({rest.Y:0.#})");
          }

          now += 20;
        }
      }

      Assert.AreEqual(0, ingest.DroppedCount, "nothing here is overload");
    }

    private static FctIngest Pulse() => new() { Style = FctMotionStyle.Pulse };

    private static FctHitState Add(FctIngest ingest, List<FctHitState> hits, FctLane lane, double now, double w = Width, double h = Height,
      bool proc = false, bool crit = false, Action<FctHitState>? evicting = null)
    {
      return ingest.Accept(hits, lane, 500 + (now % 97), Ability, crit, false, false, null, w, h, now, proc, evicting);
    }

    private static (double X, double Y) Rest(FctHitState hit) => (FctMotion.ArcedX(hit, 1), FctMotion.RaisedY(hit, 1));

    /* Distance from the strip the hit's band starts at, so one helper works for both directions. */
    private static double Outward(FctHitState hit) => Outward(hit, Rest(hit).Y);

    private static double Outward(FctHitState hit, double y) => hit.Incoming ? y - hit.BandMinY : hit.BandMaxY - y;

    private static void DistinctPlaces(List<FctHitState> hits)
    {
      var seen = new List<(double X, double Y)>();
      foreach (var hit in hits)
      {
        var rest = Rest(hit);
        Assert.IsFalse(seen.Contains(rest), $"two numbers rest in the same place ({rest.X:0.#}, {rest.Y:0.#})");
        seen.Add(rest);
      }
    }

    /* Drawn boxes touching means unreadable text, which is the bug this whole file exists to keep fixed. */
    private static bool Touches(FctHitState a, FctHitState b)
    {
      var ar = Rect(a);
      var br = Rect(b);
      return ar.Left < br.Right && br.Left < ar.Right && ar.Top < br.Bottom && br.Top < ar.Bottom;
    }

    private static (double Left, double Top, double Right, double Bottom) Rect(FctHitState hit)
    {
      var rest = Rest(hit);
      var half = hit.ValueWidth / 2.0;
      return (rest.X - half, rest.Y, rest.X + half, rest.Y + FctLayout.TextReserve(hit));
    }

    private static string Label(FctHitState hit) =>
      $"{(hit.Proc ? "proc " : string.Empty)}{hit.Lane} at ({Rest(hit).X:0.#}, {Rest(hit).Y:0.#})";
  }
}

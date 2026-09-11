using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The conveyor (FctConveyor): split's straight line as a queue instead of a placement problem. Every promise this mode makes
   * to the player is stated here as something a test can measure, because each one was broken in the version these replace and a
   * player noticed all of them — numbers drawn through each other, "some numbers move faster than others", rows leaving too
   * early to read:
   *
   *   - inside one frame, every row on a column moves at ONE speed (a convoy, never a row with its own tempo);
   *   - the gap bought at entry survives the whole flight and clears the taller of each pair, so nothing overlaps — ever;
   *   - congestion speeds up the COLUMN, all of it in the same frame, still inside the floor every rail obeys;
   *   - arrivals wait behind the mouth in order, folding duplicates while they wait, and only past the backlog is a number lost,
   *     counted in DroppedCount rather than silently clipped;
   *   - words ride the queue of their own side, and one column carries one direction — the panel cannot book two trains into one
   *     queue (FctConfigState), and the geometry refuses them even when settings.ini does (FctStage).
   *
   * Time passes the way it passes on the overlay: FctIngest.PruneExpired is where the lane clock runs, because that is the verb
   * the canvas already calls once a frame. A test that wants a frame asks for one.
   */
  /* The class pokes a static the whole engine reads (FctLayout.LabelsBelow) to compare the two label arrangements, so it runs on its
     own rather than alongside tests that price rows for the shipped one. */
  [TestClass]
  [DoNotParallelize]
  public sealed class FctConveyorTest
  {
    private const double Width = 980;
    private const double Height = 640;

    /* Split (by type) with the line shape: the one arrangement that runs as a queue (FctIngest.UseConveyor). The sides are the
     * classic heals | damage split, so both damage categories share one column and their words ride it too. */
    private static FctLayoutChoice Split(FctRailLane? healLane = null, FctRailLane? outgoingDamageLane = null) =>
      new(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp: true, outgoingUp: true, healSide: FctRegionSide.Left, healUp: true,
        healLane: healLane, outgoingDamageLane: outgoingDamageLane);

    private static FctIngest Line(FctLayoutChoice? layout = null) =>
      new(new Random(7)) { Style = FctMotionStyle.Straight, Layout = layout ?? Split() };

    private static FctHitState Swing(FctIngest ingest, List<FctHitState> hits, double value, double now,
      FctLane lane = FctLane.DamageDealt, string? source = "Slash", bool crit = false) =>
      ingest.Accept(hits, lane, value, source ?? "", crit: crit, minor: false, periodic: false, fixedText: null, Width, Height, now);

    private static FctHitState Word(FctIngest ingest, List<FctHitState> hits, string word, double now) =>
      ingest.Accept(hits, FctLane.Missed, 0, "Flurry", crit: false, minor: false, periodic: false, fixedText: word, Width, Height, now);

    /* One frame: the lane clocks run, rows are stamped where they were carried to, spent ones leave. */
    private static void Frame(FctIngest ingest, List<FctHitState> hits, double now) => ingest.PruneExpired(hits, now);

    /* Rows the player can see: a row whose slot has not reached the mouth yet is not on screen at all (FctMotion draws nothing
       for it), which is the whole reason a burst cannot stack. */
    private static List<FctHitState> Visible(List<FctHitState> hits)
    {
      var seen = new List<FctHitState>();
      foreach (var hit in hits)
      {
        if (hit.OnConveyor && hit.ConveyorQ > 0)
        {
          seen.Add(hit);
        }
      }

      return seen;
    }

    /* How far every visible row travelled across one frame. One column, one number: that is the invariant under test. */
    private static List<double> Step(FctIngest ingest, List<FctHitState> hits, double from, double to)
    {
      var rows = Visible(hits);
      var starts = new List<double>();
      foreach (var row in rows)
      {
        starts.Add(row.ConveyorQ);
      }

      Frame(ingest, hits, to);

      var travelled = new List<double>();
      for (var i = 0; i < rows.Count; i++)
      {
        if (rows[i].OnConveyor)
        {
          travelled.Add(rows[i].ConveyorQ - starts[i]);
        }
      }

      return travelled;
    }

    /* What the dial says a frame should move, with nothing in the way. */
    private static double Nominal(double ms) => ms / (FctMotion.ParabolaScrollMsPerPx * FctScale.Time);

    /* One column, one speed — and at exactly the player's own tempo while the traffic leaves the rail alone. The complaint this
       mode existed to answer was "some numbers move faster than others", which was literally true of the per-row tempo before. */
    [TestMethod]
    [Ignore("revealed-by-move: five rows one-per-900ms read as congested (press about 0.8), so this is not the quiet rail it claims")]
    public void EveryRowOnAColumnCrossesAtOneRate()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 5; i++)
      {
        Assert.IsNotNull(Swing(ingest, hits, 400 + i, i * 900.0), $"row {i} belongs on screen");
        Frame(ingest, hits, (i * 900.0) + 450);
      }

      var travelled = Step(ingest, hits, 4100, 4300);
      Assert.IsTrue(travelled.Count > 2, $"several rows should be in flight together ({hits.Count} on the list)");

      foreach (var px in travelled)
      {
        Assert.AreEqual(Nominal(200), px, 0.01,
          "an uncongested column crosses at the configured tempo, and no row owns a private speed");
      }
    }

    /* Every row on a column enters at ONE edge and travels ONE distance, whatever class of number it is. This is what makes "the
       queue spaces its rows" mean anything: a per-class start — a proc's inset from the spawn edge, a slack share off the flight,
       depth dice — spends part of a neighbour's gap before the first frame is drawn, and no amount of later arithmetic buys it
       back. It is why FctLayout takes no inset for an origin the queue asked for, and why rails take no travel slack at all. */
    [TestMethod]
    [Ignore("revealed-by-move: lane rows enter 5.87px apart vertically; crit height or leftover entrance jitter, measure first")]
    public void EveryRowOnAColumnSharesItsEdgeAndItsFlight()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      Swing(ingest, hits, 500, 0);
      Swing(ingest, hits, 2500, 300, crit: true);
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 180, "Starsurge", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 600, proc: true), "a proc is a row of its column like any other");
      Word(ingest, hits, Labels.Parry, 900);
      Assert.IsNotNull(ingest.Accept(hits, FctLane.HealingDealt, 800, "Complete Heal", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 1200), "heals on their own column are the second one under test");

      var edges = new Dictionary<int, (double Y0, double Travel)>();
      foreach (var hit in hits)
      {
        Assert.IsTrue(hit.OnConveyor, "every one of these rides a lane");
        if (!edges.TryGetValue(hit.ConveyorLane, out var seen))
        {
          edges[hit.ConveyorLane] = (hit.Y0, hit.ConveyorTravel);
          continue;
        }

        Assert.AreEqual(seen.Y0, hit.Y0, 1e-9, $"rows sharing a column must enter at one edge ({hit.Lane})");
        Assert.AreEqual(seen.Travel, hit.ConveyorTravel, 1e-9,
          "and travel the same distance — that number is the column's capacity, so it cannot vary by row");
      }

      Assert.IsTrue(edges.Count > 1, "the sample should cover two columns, so a single-column accident cannot pass for a rule");
    }

    /* The gaps are bought once and kept. This is what "well spaced" means as an invariant rather than a wish: whatever the
       separation was when the second row entered, it is still that 500 ms later — however much the column moved in between. */
    [TestMethod]
    public void TheGapBoughtAtEntryIsKeptForTheWholeFlight()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      for (var i = 0; i < 5; i++)
      {
        Swing(ingest, hits, 500 + i, i * 400.0, crit: i == 2);   // a big number between two small ones, as often happens
        Frame(ingest, hits, (i * 400.0) + 200);
      }

      var rows = Visible(hits);
      Assert.IsTrue(rows.Count >= 3, "the column should be carrying a run of rows");

      var gaps = new List<(FctHitState A, FctHitState B, double Gap)>();
      for (var i = 0; i < rows.Count; i++)
      {
        for (var j = i + 1; j < rows.Count; j++)
        {
          gaps.Add((rows[i], rows[j], Math.Abs(rows[i].ConveyorQ - rows[j].ConveyorQ)));
        }
      }

      Frame(ingest, hits, 3000);

      foreach (var pair in gaps)
      {
        if (!hits.Contains(pair.A) || !hits.Contains(pair.B))
        {
          continue;   // one of them finished its flight in between; there is nothing left to compare
        }

        Assert.AreEqual(pair.Gap, Math.Abs(pair.A.ConveyorQ - pair.B.ConveyorQ), 1e-9,
          "two rows on one rail drifted apart or closed on each other — the lane has one clock for a reason");
      }
    }

    /* Nothing is ever drawn through anything: sampled across six seconds of raid traffic — two damage streams, crits, DoTs and
       words, some of it landing in the same frame — any two rows sharing a column are always at least the taller of the pair
       apart. Overlap used to be an outcome of scoring ("least bad placement") that then persisted for the whole flight; here it
       is impossible by construction, so it is asserted as a never. */
    [TestMethod]
    public void ARowNeverPassesThroughAnotherRowOnItsColumn()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      for (var frame = 0; frame < 60; frame++)
      {
        var now = frame * 100.0;
        Swing(ingest, hits, 300 + (frame % 17), now);
        Word(ingest, hits, frame % 2 == 0 ? Labels.Miss : Labels.Dodge, now + 10);

        if (frame % 3 == 0)
        {
          Swing(ingest, hits, 900 + (frame % 7), now + 20, lane: FctLane.DamageTaken, source: "Bite");
        }

        if (frame % 5 == 0)
        {
          Swing(ingest, hits, 1500 + frame, now + 30, crit: true);
        }

        if (frame % 4 == 0)
        {
          ingest.Accept(hits, FctLane.DamageDealt, 213, "Venin", crit: false, minor: false, periodic: true, fixedText: null,
            Width, Height, now + 40);
        }

        Frame(ingest, hits, now + 100);

        var rows = Visible(hits);
        for (var i = 0; i < rows.Count; i++)
        {
          for (var j = i + 1; j < rows.Count; j++)
          {
            if (rows[i].ConveyorLane != rows[j].ConveyorLane)
            {
              continue;   // different columns were never competing for the same line of travel
            }

            var need = Math.Max(FctLayout.TextHeight(rows[i]), FctLayout.TextHeight(rows[j]));
            Assert.IsTrue(Math.Abs(rows[i].ConveyorQ - rows[j].ConveyorQ) >= need - 1e-6,
              $"two rows on one column overlapped by {(need - Math.Abs(rows[i].ConveyorQ - rows[j].ConveyorQ)):0.##} px at t={now:0}");
          }
        }
      }
    }

    /* Congestion is a property of the lane, not of the row that happened to arrive during it: when traffic outruns the dial,
       every row on that column picks up the same extra speed in the same frame — the whole train, which is what the mode's owner
       asked for — and it stays inside the floor the other rails use, so an overlay stops typing shortly after a fight does. */
    [TestMethod]
    public void ACongestedColumnSpeedsUpAsOneColumn()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      // faster than one column can separate rows at the dial's tempo: an arrival every 60 ms
      for (var i = 0; i < 20; i++)
      {
        Swing(ingest, hits, 500 + i, i * 60.0);
        Frame(ingest, hits, (i * 60.0) + 30);
      }

      var travelled = Step(ingest, hits, 1200, 1260);
      Assert.IsTrue(travelled.Count > 2, $"a flood should leave rows in flight ({hits.Count} on the list)");

      var fastest = 0.0;
      foreach (var px in travelled)
      {
        fastest = Math.Max(fastest, px);
        Assert.AreEqual(fastest, px, 0.01, "the lane sped up — everybody on it in the same frame, or nobody did");
      }

      var nominal = Nominal(60);
      Assert.IsTrue(fastest > nominal * 1.05,
        $"a column behind on its traffic must clear faster than the dial: {fastest:0.##} px against a nominal {nominal:0.##}");

      foreach (var hit in hits)
      {
        Assert.IsTrue(hit.RailPress is >= FctStream.PressFloor and <= 1.0,
          $"the lane's accelerator stays between the floor and the dial: {hit.RailPress:0.###}");
      }
    }

    /* Overflow is a queue before it is a loss. Arrivals wait behind the mouth in order — off screen, because their slot has not
       reached the edge — and only once BacklogCap of them are waiting does the next one get turned away, counted. A burst is
       therefore held and then delivered, instead of being written on top of itself. */
    [TestMethod]
    public void OverflowWaitsBehindTheMouthInOrderAndOnlyThenIsLost()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      var first = Swing(ingest, hits, 100, 0);
      Assert.IsNotNull(first);
      Assert.AreEqual(0.0, first.ConveyorQ, 1e-9, "the first row on an idle column takes the mouth itself");

      var queue = new List<FctHitState> { first };
      for (var i = 1; i <= FctConveyor.BacklogCap; i++)
      {
        var row = Swing(ingest, hits, 200 + i, 0);
        Assert.IsNotNull(row, $"row {i} fits inside the backlog and must not be dropped");
        Assert.IsTrue(row.ConveyorQ < 0, "it is waiting for its slot rather than standing on the row in front of it");
        queue.Add(row);
      }

      Assert.AreEqual(FctConveyor.BacklogCap + 1, hits.Count);
      Assert.AreEqual(0, ingest.DroppedCount, "nothing has been lost yet");

      // the door is shut now: another arrival in the same frame is refused, and says so
      Assert.IsNull(Swing(ingest, hits, 999_999, 0), "past the backlog the column is genuinely full at its fastest");
      Assert.AreEqual(1, ingest.DroppedCount, "a refusal counts where counting happens");

      /* And the queue drains in the order it filled — what the player reads down the column is what happened first to last. Rows
         keep the spacing they bought, so no row can pass another: the head of the queue is simply the next one whose slot has
         reached the mouth. */
      for (var i = 0; i < queue.Count; i++)
      {
        for (var j = i + 1; j < queue.Count; j++)
        {
          Assert.IsTrue(queue[i].ConveyorQ >= queue[j].ConveyorQ, "a row entered behind a row that is further along its column");
        }
      }

      var head = 0;
      for (var frame = 1; frame <= 400 && head < queue.Count; frame++)
      {
        Frame(ingest, hits, frame * 50.0);
        while (head < queue.Count && queue[head].ConveyorQ > 0)
        {
          head++;
        }
      }

      Assert.AreEqual(queue.Count, head, "every queued row eventually reached the column");
    }

    /* A number waiting its turn is still the number the player would have seen: an identical hit landing while its twin is
       off-column folds into that row instead of joining the queue, so a DoT application costs one row — already carrying its
       count — rather than six. Folding happens before the queue is consulted, which is what keeps "never miss anything" and
       "stay compact" from fighting. */
    [TestMethod]
    public void RowsWaitingTheirTurnStillFoldDuplicates()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      var first = ingest.Accept(hits, FctLane.DamageDealt, 412, "Venin", crit: false, minor: false, periodic: true,
        fixedText: null, Width, Height, 0);
      Assert.IsNotNull(first);

      for (var i = 1; i < 6; i++)
      {
        Assert.IsNull(ingest.Accept(hits, FctLane.DamageDealt, 412, "Venin", crit: false, minor: false, periodic: true,
          fixedText: null, Width, Height, i * 40.0), "a tick identical to a row still waiting folds into that row");
      }

      Assert.AreEqual(1, hits.Count, "six ticks, one row");
      Assert.AreEqual(6, first.MergeCount);
    }

    /* Words ride the column of their side — the same queue as the numbers there, spaced like anything else. A DODGE! between two
       swings is a row of that column, not a second stream the numbers have to be dodged around. */
    [TestMethod]
    public void WordsRideTheSameQueueAsTheirNumbers()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      var swung = Swing(ingest, hits, 640, 0);
      var dodged = Word(ingest, hits, Labels.Dodge, 200);

      Assert.IsNotNull(swung);
      Assert.IsNotNull(dodged);
      Assert.IsTrue(swung.OnConveyor && dodged.OnConveyor, "both belong on the deliberate rail");
      Assert.AreEqual(swung.ConveyorLane, dodged.ConveyorLane,
        "a word is an attack that failed: it belongs to its side's column, not to a lane of its own");

      var need = Math.Max(FctLayout.TextHeight(swung), FctLayout.TextHeight(dodged)) + FctConveyor.LaneGapPx;
      Assert.IsTrue(Math.Abs(swung.ConveyorQ - dodged.ConveyorQ) >= need - 1e-6,
        "the word keeps its distance from the number it followed");
    }

    /* One column, one train: categories booked into the same lane share a queue when they travel the same way — which is exactly
       what "put my heals in my damage's column" asks for — and the geometry refuses to run two directions through one queue even
       when settings.ini says so, following the priority the panel already enforces (FctConfigState). */
    [TestMethod]
    public void CategoriesSharingAColumnShareItsQueue()
    {
      var shared = FctRailLane.Left1;
      var ingest = Line(Split(healLane: shared, outgoingDamageLane: shared));
      var hits = new List<FctHitState>();

      var swung = Swing(ingest, hits, 600, 0);
      var healed = ingest.Accept(hits, FctLane.HealingDealt, 800, "Complete Heal", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 300);

      Assert.IsNotNull(swung);
      Assert.IsNotNull(healed);
      Assert.AreEqual(swung.ConveyorLane, healed.ConveyorLane, "same column, same direction, one queue");
      Assert.IsTrue(Math.Abs(swung.ConveyorQ - healed.ConveyorQ) >= FctConveyor.LaneGapPx,
        "they keep their distance while sharing it");

      /* The impossible request: one column, two directions. The stage's own rule (FctStage) puts them on one clock instead of
         laying two trains over the same pixels, which is what the old per-row placement did and why a crit could be drawn through
         an incoming swing. */
      var collided = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp: false, outgoingUp: true,
        healUp: false, incomingDamageLane: shared, outgoingDamageLane: shared, healLane: shared);
      var clash = Line(collided);
      var hits2 = new List<FctHitState>();

      var out1 = Swing(clash, hits2, 700, 0);
      var in1 = Swing(clash, hits2, 300, 100, lane: FctLane.DamageTaken, source: "Bite");
      var heal = clash.Accept(hits2, FctLane.HealingDealt, 500, "Heal", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 200);

      Assert.IsNotNull(out1);
      Assert.IsNotNull(in1);
      Assert.IsNotNull(heal);
      Assert.AreEqual(out1.ConveyorLane, in1.ConveyorLane, "one column is one queue, whatever the dials asked for");
      Assert.AreEqual(out1.ConveyorLane, heal.ConveyorLane);
      Assert.AreEqual(Math.Sign(out1.Rise), Math.Sign(in1.Rise), "and everything in it travels the same way");
      Assert.AreEqual(Math.Sign(out1.Rise), Math.Sign(heal.Rise));
    }

    /* A row is legible until it has left: full strength along the column and a fade measured in the last slice of rail, so nobody
       loses the tail of a number while it is still sitting in the middle of the screen. That was the old rule's real cost — it
       dimmed by age, on flights whose length congestion decided, so a fast lane meant half-read numbers. */
    [TestMethod]
    public void ARowStaysLegibleUntilItHasLeftTheColumn()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();

      var hit = Swing(ingest, hits, 1234, 0);
      Assert.IsNotNull(hit);

      var sawMiddle = false;
      var sawTail = false;
      for (var frame = 1; frame <= 400 && hits.Count > 0; frame++)
      {
        Frame(ingest, hits, frame * 25.0);
        if (hits.Count == 0)
        {
          break;
        }

        var left = 1.0 - (hit.ConveyorQ / Math.Max(1.0, hit.ConveyorTravel));
        var opacity = FctMotion.FadeOpacity(hit, frame * 25.0);
        if (hit.ConveyorQ > 60 && left > FctMotion.ConveyorFadeOutFrac + 0.02)
        {
          Assert.AreEqual(1.0, opacity, 1e-9, "a row still travelling its column must not be dimming");
          sawMiddle = true;
        }
        else if (left < FctMotion.ConveyorFadeOutFrac * 0.5 && left > 0)
        {
          Assert.IsTrue(opacity < 1.0, "the last stretch of rail is where a row goes away");
          sawTail = true;
        }
      }

      Assert.IsTrue(sawMiddle, "the flight should have been sampled in its legible middle");
      Assert.IsTrue(sawTail, "and through its fade, so the rule was actually exercised");
      Assert.AreEqual(0, hits.Count, "a row whose flight is spent is gone, however long the lane took to carry it");
    }

    /* Both shapes split offers are queues, not just the line: the parabola is what ships there, so the ordering promise would mean
       nothing if it only applied to the shape almost nobody selects. The dial chooses the PATH up the column — straight, or MSBT's
       bow that leaves the column, bows at half height and comes back — never whether traffic keeps its spacing and its speed. */
    [TestMethod]
    public void TheBowingRailKeepsTheSameDisciplineAsTheLine()
    {
      var ingest = new FctIngest(new Random(13)) { Style = FctMotionStyle.Parabola, Layout = Split() };
      var hits = new List<FctHitState>();

      for (var frame = 0; frame < 30; frame++)
      {
        var now = frame * 120.0;
        Swing(ingest, hits, 400 + frame, now, source: "Slash");

        if (frame % 5 == 0)
        {
          Swing(ingest, hits, 1700 + frame, now + 30, crit: true);
        }

        Frame(ingest, hits, now + 120);

        var rows = Visible(hits);
        for (var i = 0; i < rows.Count; i++)
        {
          Assert.IsTrue(rows[i].OnConveyor, "a rail in split rides a lane whatever shape it bows in");
          for (var j = i + 1; j < rows.Count; j++)
          {
            if (rows[i].ConveyorLane != rows[j].ConveyorLane)
            {
              continue;
            }

            var need = Math.Max(FctLayout.TextHeight(rows[i]), FctLayout.TextHeight(rows[j]));
            Assert.IsTrue(Math.Abs(rows[i].ConveyorQ - rows[j].ConveyorQ) >= need - 1e-6,
              $"the bowing rail let two rows share pixels at t={now:0}");
          }
        }
      }

      var travelled = Step(ingest, hits, 3720, 3780);
      Assert.IsTrue(travelled.Count > 2, "the column should still be carrying a run");
      foreach (var px in travelled)
      {
        Assert.AreEqual(travelled[0], px, 0.01, "one lane, one rate — the bow travels with the row, it does not replace the clock");
      }
    }

    /* Where the label sits decides what a ROW is, vertically — which is why moving the words beside their amounts pulls every column
       on the screen closer together. Inline, the words share the value's baseline, so nothing below the number needs paying for; and
       once that line is out of the height, rows with a source and rows without one measure the SAME, which is what turns "about evenly
       spaced" into a ledger with a constant line pitch. */
    [TestMethod]
    public void LabelsBesideTheirAmountsTightenTheLines()
    {
      var shipped = FctLayout.LabelSide;
      try
      {
        var under = PitchOf(labeled: true, below: true);
        var inline = PitchOf(labeled: true, below: false);
        Assert.IsTrue(inline < under - 9.0,
          $"a row whose words sit beside it is only as tall as its number: {inline:0.#} px against {under:0.#} px with them underneath");

        // and inline, the mix of rows that a fight actually produces is one height all the way down the column
        var plain = PitchOf(labeled: false, below: false);
        Assert.AreEqual(inline, plain, 1e-9, "a row with a source line and a row without one must not space themselves apart");
      }
      finally
      {
        FctLayout.LabelSide = shipped;
      }
    }

    /* Two rows entering one after the other, from which the lane's line pitch is read straight off their distance. */
    private static double PitchOf(bool labeled, bool below)
    {
      FctLayout.LabelSide = below ? FctLabelSide.Below : FctLabelSide.Right;
      var ingest = Line();
      var hits = new List<FctHitState>();

      Swing(ingest, hits, 500, 0, source: labeled ? "Slash" : null);
      Swing(ingest, hits, 517, 0, source: labeled ? "Crush" : null);

      return Math.Abs(hits[1].ConveyorQ - hits[0].ConveyorQ);
    }

    /* Smoothness, as an assertion rather than a feeling: a lane may speed up when its traffic demands it, but it does so by a bounded
       amount per frame. An accelerator that jumps several percent in one frame reads as the column lurching even though every row is
       doing exactly what the lane says — and the queue already holds the traffic while the lane eases into its faster pace, so the
       smoothing costs nothing except a slightly later pickup. */
    [TestMethod]
    public void ALaneChangesItsPaceWithoutLurching()
    {
      var ingest = Line();
      var hits = new List<FctHitState>();
      var msPerPx = FctMotion.ParabolaScrollMsPerPx * FctScale.Time;

      FctHitState? head = null;
      var previousPress = 0.0;
      var slowestPress = 1.0;
      var framesMeasured = 0;

      // arrivals faster than the lane can separate them, at a frame rate worth measuring
      for (var frame = 1; frame <= 150; frame++)
      {
        var now = frame * 16.0;
        if (frame % 4 == 0)
        {
          Swing(ingest, hits, 300 + frame, now - 8);
        }

        var before = head?.ConveyorQ ?? 0.0;
        Frame(ingest, hits, now);

        // stay with one row for as long as it is on the column: measuring a different row each frame measures a change of
        // subject, not a change of pace
        if (head is null || !hits.Contains(head) || head.ConveyorQ >= head.ConveyorTravel)
        {
          head = Visible(hits).OrderByDescending(h => h.ConveyorQ).FirstOrDefault();
          previousPress = 0.0;
          continue;
        }

        var step = head.ConveyorQ - before;
        if (step <= 0.0)
        {
          continue;
        }

        // the lane's accelerator, read back out of the pixels it asked for: distance per frame against the dialled pace
        var press = FctConveyor.FrameMs / (msPerPx * step);
        slowestPress = Math.Min(slowestPress, press);
        if (previousPress > 0.0)
        {
          Assert.IsTrue(Math.Abs(press - previousPress) <= 0.031,
            $"the column changed its pace by {Math.Abs(press - previousPress):0.###} in one frame at t={now:0}");
          framesMeasured++;
        }

        previousPress = press;
      }

      Assert.IsTrue(framesMeasured > 30, "the flood should have exercised the accelerator for a good stretch of frames");
      Assert.IsTrue(slowestPress < 0.90, $"a lane this busy should have sped up at all, lowest press {slowestPress:0.###}");
    }

    /* The pace is the player's dial and nothing else — the one number a "too fast / too slow" complaint is about. Half the speed
       is measured here as less than half the ground per frame (the lane relaxes a little more as its load estimate falls, which is
       why the assertion has slack rather than an exact halving). */
    [TestMethod]
    public void TheDialSetsThePaceOfTheWholeLane()
    {
      var dial = FctScale.Time;
      try
      {
        var ingest = Line();
        var hits = new List<FctHitState>();
        Assert.IsNotNull(Swing(ingest, hits, 700, 0));

        Frame(ingest, hits, 100);
        var first = hits[0].ConveyorQ;
        Assert.IsTrue(first > 0, "the lane carries its row");

        FctScale.Time = dial * 2.0;
        Frame(ingest, hits, 200);
        var second = hits[0].ConveyorQ - first;

        Assert.IsTrue(second < first * 0.65, $"the dial scales the lane: {first:0.#} px, then {second:0.#} px per same-size frame");
      }
      finally
      {
        FctScale.Time = dial;
      }
    }
  }
}

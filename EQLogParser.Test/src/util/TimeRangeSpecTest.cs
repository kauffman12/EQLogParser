using EQLogParser;

namespace EQLogParserTest
{
  /*
   * TimeRange against the thing it is meant to mean: "this player was active during these seconds", where all we ever know for certain is that
   * something happened at an exact second. The existing TimeRangeTest covers the API's happy paths; this file tests meaning and invariants, then
   * documents four sharp edges the class actually has (see the "sharp edge" tests at the bottom: they assert current behavior on purpose, so a fix
   * makes them fail and get read rather than silently changing numbers somewhere).
   *
   * The reference model is written the slow, obvious way on purpose - a set of active seconds, then spans joined across short silences - so it shares
   * no code with the class under test.
   */
  [TestClass]
  public class TimeRangeSpecTest
  {
    // GetTotal() treats silence this long as "still in the fight". Duplicated here rather than referenced so the test states the rule, not inherits it.
    private const int Offset = 6;

    /* Every distinct second covered by the added spans. Inclusive on both ends, which is why Total() adds one and why a lone attack counts as a second. */
    private static HashSet<long> ActiveSeconds(IEnumerable<TimeSegment> added)
    {
      var seconds = new HashSet<long>();

      foreach (var segment in added)
      {
        for (var t = (long)segment.BeginTime; t <= (long)segment.EndTime; t++)
        {
          seconds.Add(t);
        }
      }

      return seconds;
    }

    /*
     * The spec for GetTotal(): collapse the added spans into runs (spans that overlap or touch are one run), then join runs separated by at most
     * Offset seconds and count the joined silence as activity. Repeating the join is what "at most six seconds of silence" implies transitively, and
     * it is worth noting that TimeRange's single bridging pass agrees with this closure - that is asserted below, because it is not obvious.
     */
    private static double BridgedTotal(IEnumerable<TimeSegment> added)
    {
      var runs = new List<(double Begin, double End)>();

      foreach (var span in added.Where(a => a.BeginTime <= a.EndTime).OrderBy(a => a.BeginTime))
      {
        if (runs.Count > 0 && span.BeginTime - runs[^1].End <= Offset)
        {
          runs[^1] = (runs[^1].Begin, Math.Max(runs[^1].End, span.EndTime));
        }
        else
        {
          runs.Add((span.BeginTime, span.EndTime));
        }
      }

      var merged = true;

      while (merged)
      {
        merged = false;

        for (var i = 0; i + 1 < runs.Count; i++)
        {
          if (runs[i + 1].Begin - runs[i].End <= Offset)
          {
            runs[i] = (runs[i].Begin, Math.Max(runs[i].End, runs[i + 1].End));
            runs.RemoveAt(i + 1);
            merged = true;
            i--;
          }
        }
      }

      return runs.Sum(run => run.End - run.Begin + 1);
    }

    private static void AssertSortedAndDisjoint(TimeRange range, string because)
    {
      for (var i = 0; i < range.TimeSegments.Count; i++)
      {
        var segment = range.TimeSegments[i];
        Assert.IsTrue(segment.BeginTime <= segment.EndTime, $"{because}: span [{segment.BeginTime},{segment.EndTime}] runs backwards");

        if (i == 0)
        {
          continue;
        }

        var previous = range.TimeSegments[i - 1];
        Assert.IsTrue(previous.BeginTime <= segment.BeginTime, $"{because}: list is not sorted by BeginTime");
        Assert.IsTrue(previous.EndTime < segment.BeginTime, $"{because}: spans [{previous.BeginTime},{previous.EndTime}] and [{segment.BeginTime},{segment.EndTime}] overlap but were not merged");

        /*
         * And more than a tick apart: since the bridging rule lives in Add rather than in GetTotal(), "no two runs sit within Offset seconds of each other"
         * is a property of the list at all times, not only after somebody asked for a number. Checked here so every sweep in this file proves it.
         */
        Assert.IsTrue(segment.BeginTime - previous.EndTime > Offset, $"{because}: runs [{previous.BeginTime},{previous.EndTime}] and [{segment.BeginTime},{segment.EndTime}] are {(segment.BeginTime - previous.EndTime):0.###} seconds apart, inside the tick, so they were never meant to be two runs");
      }
    }

    private static void AssertAgainstModel(TimeRange range, List<TimeSegment> added, string because)
    {
      Assert.AreEqual(BridgedTotal(added), range.GetTotal(), 1e-9, $"{because}: added {Describe(added)}");
      AssertSortedAndDisjoint(range, because);
    }

    private static string Describe(IEnumerable<TimeSegment> segments) =>
      string.Join(" ", segments.Select(s => $"[{s.BeginTime:0.###},{s.EndTime:0.###}]"));

    /* The premise of the whole class: an attack at one exact second is a second of activity, not zero. */
    [TestMethod]
    public void AnAttackAtOneExactSecondIsASecondOfActivity()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(1000, 1000));
      Assert.AreEqual(1, range.GetTotal());
    }

    [TestMethod]
    public void Total_CountsEachActiveSecondOnce()
    {
      var added = new List<TimeSegment>
      {
        new(100, 110),
        new(105, 120),
        new(108, 109),
      };
      var range = new TimeRange();
      added.ForEach(range.Add);

      // 100..120 inclusive; no second counted twice even though three spans cover the middle
      Assert.AreEqual(21, range.GetTotal());
      AssertAgainstModel(range, added, "overlapping spans");
    }

    [TestMethod]
    public void SilenceOfSixSecondsOrLessStillCountsAsActive()
    {
      var added = new List<TimeSegment> { new(0, 0), new(6, 6) };
      var range = new TimeRange();
      added.ForEach(range.Add);

      // Two attacks six seconds apart are read as one continuous 7 second engagement: the silent middle counts.
      Assert.AreEqual(7, range.GetTotal());
      AssertAgainstModel(range, added, "bridged at the boundary");
    }

    [TestMethod]
    public void SilenceOfSevenSecondsEndsTheActivity()
    {
      var added = new List<TimeSegment> { new(0, 0), new(7, 7) };
      var range = new TimeRange();
      added.ForEach(range.Add);

      Assert.AreEqual(2, range.GetTotal());
      AssertAgainstModel(range, added, "just past the bridge");
    }

    [TestMethod]
    public void BridgingChainsAcrossSeveralShortSilences()
    {
      var added = new List<TimeSegment> { new(0, 10), new(16, 20), new(24, 30) };
      var range = new TimeRange();
      added.ForEach(range.Add);

      // Each silence is within Offset, so one run 0..30 - which needs the rule to apply transitively.
      Assert.AreEqual(31, range.GetTotal());
      AssertAgainstModel(range, added, "chained bridging");
    }

    /*
     * Adds do not arrive in time order. Stats builders feed fight spans in from several places (TankStats merges per-tank ranges into the raid range,
     * FilterTimeRange rebuilds copies), so an older span landing in the middle of the list has to slot into the right place rather than append.
     */
    [TestMethod]
    public void AddsArrivingOutOfOrderStillLandInTheRightPlace()
    {
      var added = new List<TimeSegment>
      {
        new(100, 110), new(500, 510), new(10, 20), new(300, 310), new(200, 205), new(400, 401),
      };
      var range = new TimeRange();
      added.ForEach(range.Add);

      Assert.AreEqual(6, range.TimeSegments.Count);
      AssertAgainstModel(range, added, "out of order adds");
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(510, range.TimeSegments[^1].EndTime);
    }

    [TestMethod]
    public void AOlderSpanInTheMiddleOfTheRunExtendsIt()
    {
      var added = new List<TimeSegment> { new(1000, 1100), new(2000, 2100), new(1500, 1500) };
      var range = new TimeRange();
      added.ForEach(range.Add);

      AssertAgainstModel(range, added, "span between two others");
      Assert.AreEqual(3, range.TimeSegments.Count);
    }

    [TestMethod]
    public void ASpanThatStraddlesSeveralOthersAbsorbsAllOfThem()
    {
      var range = new TimeRange();
      foreach (var (b, e) in new[] { (10d, 20d), (30d, 40d), (50d, 60d), (70d, 80d) })
      {
        range.Add(new TimeSegment(b, e));
      }

      range.Add(new TimeSegment(15, 65));

      // Merged to [10,65]; the 5 second silence to [70,80] is inside Offset so GetTotal() sees one run of 71.
      Assert.AreEqual(71, range.GetTotal());
      Assert.AreEqual("[10,80]", Describe(range.TimeSegments));
    }

    /*
     * The union itself, computed the obvious way, at the size where Add used to cost the most. Added by halving search rather than by walking the list
     * from zero, so the order spans arrive in must not matter one bit - which is exactly what an insert-position off-by-one would show up as, and this
     * is the guard for it (the side-by-side harness that proved the change is not part of the build).
     */
    [TestMethod]
    public void ManyOutOfOrderAddsProduceExactlyTheBruteForceRuns()
    {
      var rng = new Random(90210);

      for (var trial = 0; trial < 25; trial++)
      {
        var added = new List<TimeSegment>();
        var range = new TimeRange();

        for (var i = 0; i < 400; i++)
        {
          var begin = rng.Next(0, 20_000);
          var end = begin + rng.Next(0, 30);
          added.Add(new TimeSegment(begin, end));
          range.Add(new TimeSegment(begin, end));
        }

        /*
         * The brute force run list, and it joins across short silences because Add does now - the tick rule moved from GetTotal() to Add, so the list is
         * kept in the shape a bridging read used to put it into. Before that move this model collapsed only true overlaps and matched the class exactly;
         * the two failures when the rule moved were these tests and GetTotalBridgesTheSegmentListAsASideEffectOfBeingRead, both of which pinned the old
         * meaning rather than finding a bug. See docs/DesignNotes.md -> "TimeRange: the tick rule lives in Add".
         */
        var want = new List<(double Begin, double End)>();

        foreach (var span in added.OrderBy(a => a.BeginTime))
        {
          if (want.Count > 0 && span.BeginTime - want[^1].End <= Offset)
          {
            want[^1] = (want[^1].Begin, Math.Max(want[^1].End, span.EndTime));
          }
          else
          {
            want.Add((span.BeginTime, span.EndTime));
          }
        }

        Assert.AreEqual(want.Count, range.TimeSegments.Count, $"trial {trial}: span count after {added.Count} adds");

        for (var i = 0; i < want.Count; i++)
        {
          Assert.AreEqual(want[i].Begin, range.TimeSegments[i].BeginTime, $"trial {trial}, span {i} begins wrong: {Describe(added)}");
          Assert.AreEqual(want[i].End, range.TimeSegments[i].EndTime, $"trial {trial}, span {i} ends wrong: {Describe(added)}");
        }

        AssertAgainstModel(range, added, $"trial {trial}: {added.Count} adds in random order");
      }
    }

    /* The random sweep: meaning and invariants after every single add, against a model that shares no code with the class. */
    [TestMethod]
    public void RandomAddsMatchTheModelAfterEverySingleAdd()
    {
      var rng = new Random(20260922);

      for (var trial = 0; trial < 400; trial++)
      {
        var range = new TimeRange();
        var added = new List<TimeSegment>();
        var previousTotal = double.MinValue;

        for (var i = 0; i < 15; i++)
        {
          // Absolute positions, so adds land in the past, in the future and inside existing runs.
          var begin = rng.Next(0, 200);
          var end = begin + rng.Next(0, 12);
          var segment = new TimeSegment(begin, end);

          added.Add(segment);
          range.Add(new TimeSegment(begin, end));

          AssertAgainstModel(range, added, $"trial {trial}, add {i}");

          /* Doing something can never make you look like you were active for less time. */
          var total = range.GetTotal();
          Assert.IsTrue(total >= previousTotal, $"total fell from {previousTotal} to {total} after adding [{begin},{end}]");
          previousTotal = total;
        }
      }
    }

    /*
     * Used to read: "reading the answer rewrites the question" - GetTotal() closed short silences by ADDING spans to its own list, so the list you walked
     * depended on whether anybody had asked for a number yet. LineChart's UpdateRemaining reads TimeSegments.Last().BeginTime and feeds it straight back
     * into Add, which is how a read reached a later write. That test was replaced, not deleted: the bridging now happens in Add, so the run is welded at
     * insert and the read has nothing left to do.
     */
    [TestMethod]
    public void AddingWithinATickWeldsTheRunAtInsert()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(0, 10));
      range.Add(new TimeSegment(14, 20));

      // welded before anyone asks: four seconds of silence is inside the tick, so this was never two runs
      Assert.AreEqual("[0,20]", Describe(range.TimeSegments));
      Assert.AreEqual(21, range.GetTotal());

      // and a silence wider than the tick stays a silence
      range.Add(new TimeSegment(27, 30));
      Assert.AreEqual("[0,20] [27,30]", Describe(range.TimeSegments));
      Assert.AreEqual(25, range.GetTotal());
    }

    /* The tick boundary, pinned: six seconds of silence is inside the rule, seven is outside it. Same edge GetTotal() used to apply when read. */
    [TestMethod]
    public void ExactlyATickOfSilenceWeldsAndOneSecondMoreDoesNot()
    {
      var inside = new TimeRange();
      inside.Add(new TimeSegment(0, 0));
      inside.Add(new TimeSegment(6, 6));
      Assert.AreEqual("[0,6]", Describe(inside.TimeSegments));
      Assert.AreEqual(7, inside.GetTotal());

      var outside = new TimeRange();
      outside.Add(new TimeSegment(0, 0));
      outside.Add(new TimeSegment(7, 7));
      Assert.AreEqual("[0,0] [7,7]", Describe(outside.TimeSegments));
      Assert.AreEqual(2, outside.GetTotal());
    }

    /* The read is a read. Nothing about the list moves, so walking it after a total is the same walk as before one. */
    [TestMethod]
    public void ReadingTheTotalLeavesTheListAlone()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(0, 10));
      range.Add(new TimeSegment(30, 40));
      range.Add(new TimeSegment(90, 95));

      var before = Describe(range.TimeSegments);
      var spans = range.TimeSegments.ToArray();

      for (var i = 0; i < 3; i++)
      {
        Assert.AreEqual(28, range.GetTotal(), $"read {i} changed the answer");
        Assert.AreEqual(before, Describe(range.TimeSegments), $"read {i} changed the list");

        for (var s = 0; s < spans.Length; s++)
        {
          Assert.AreEqual(spans[s].BeginTime, range.TimeSegments[s].BeginTime, $"read {i} moved span {s}");
          Assert.AreEqual(spans[s].EndTime, range.TimeSegments[s].EndTime, $"read {i} widened span {s}");
        }
      }
    }

    /* Two reads must not disagree, or keep growing the answer. */
    [TestMethod]
    public void GetTotalIsIdempotent()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(0, 0));
      range.Add(new TimeSegment(5, 5));
      range.Add(new TimeSegment(11, 11));

      var first = range.GetTotal();
      var second = range.GetTotal();
      var third = range.GetTotal();

      Assert.AreEqual(first, second);
      Assert.AreEqual(second, third);
    }

    /*
     * I expected this to show that asking changes the answer. It does not - and the reason is worth having written down: closing a silence merges a
     * run's OUTER bounds (the merged span begins where its leftmost member began and ends where its rightmost member ended), so materialising a
     * bridge early moves no endpoint any later Add could have reached. The total is therefore independent of how often it was read. That is what makes
     * a non-mutating GetTotal() safe for the number, even though the list shape leaks (see the test above).
     */
    [TestMethod]
    public void ABridgingReadDoesNotChangeWhatALaterAddTotals()
    {
      var withReads = new TimeRange();
      withReads.Add(new TimeSegment(171, 176));
      withReads.GetTotal();
      withReads.Add(new TimeSegment(156, 165));
      withReads.GetTotal();
      withReads.Add(new TimeSegment(149, 160));

      var withoutReads = new TimeRange();
      withoutReads.Add(new TimeSegment(171, 176));
      withoutReads.Add(new TimeSegment(156, 165));
      withoutReads.Add(new TimeSegment(149, 160));

      Assert.AreEqual(withoutReads.GetTotal(), withReads.GetTotal());
    }

    /* The same property as a sweep: reads between adds, at random points, never move the answer away from the model. */
    [TestMethod]
    public void ReadingMoreOftenDoesNotChangeTheAnswer()
    {
      var rng = new Random(31337);

      for (var trial = 0; trial < 200; trial++)
      {
        var added = new List<TimeSegment>();
        var quiet = new TimeRange();
        var nosy = new TimeRange();

        for (var i = 0; i < 14; i++)
        {
          var begin = rng.Next(0, 150);
          var end = begin + rng.Next(0, 10);
          added.Add(new TimeSegment(begin, end));

          quiet.Add(new TimeSegment(begin, end));
          nosy.Add(new TimeSegment(begin, end));

          var reads = rng.Next(0, 3);

          for (var read = 0; read <= reads; read++)
          {
            nosy.GetTotal();
          }
        }

        Assert.AreEqual(BridgedTotal(added), quiet.GetTotal(), $"trial {trial}: never read: {Describe(added)}");
        Assert.AreEqual(BridgedTotal(added), nosy.GetTotal(), $"trial {trial}: read between every add: {Describe(added)}");
      }
    }

    [TestMethod]
    public void InvertedAndNullAddsAreIgnored()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(30, 20));
      range.Add((TimeSegment?)null);
      Assert.AreEqual(0, range.TimeSegments.Count);
      Assert.AreEqual(0, range.GetTotal());
    }

    /* ---- sharp edges: asserted as they behave today, so a change of behaviour is a decision and not a surprise ---- */

    /*
     * The constructor bypasses both guards Add() has: an inverted span gets in, and so does null. An inverted span makes GetTotal() NEGATIVE, which
     * then flows out as TotalSeconds into every per-second number that divides by it. Nothing in the tree constructs one this way today (callers pass
     * BeginTime/UpdateTime they have just read off a record), which is why it stays latent rather than visible.
     */
    [TestMethod]
    public void ConstructorWithSegmentSkipsTheGuardsAddHas()
    {
      Assert.AreEqual(1, new TimeRange(new TimeSegment(30, 20)).TimeSegments.Count);
      Assert.AreEqual(-9, new TimeRange(new TimeSegment(30, 20)).GetTotal());

      var withNull = new TimeRange((TimeSegment)null!);
      Assert.AreEqual(1, withNull.TimeSegments.Count);
      Assert.Throws<NullReferenceException>(() => withNull.GetTotal());
      Assert.Throws<NullReferenceException>(() => new TimeRange((List<TimeSegment>)null!));
    }

    /*
     * The single-segment constructor stores the caller's object; the list constructor copies each segment. So a caller that keeps its own reference
     * and moves it has moved the range too - here an 11 second span becomes a 891 second one without the range being touched.
     */
    [TestMethod]
    public void ConstructorWithSegmentKeepsTheCallersInstanceWhileTheListConstructorCopies()
    {
      var shared = new TimeSegment(10, 20);
      var shares = new TimeRange(shared);
      shared.EndTime = 900;
      Assert.AreEqual(891, shares.GetTotal());
      Assert.IsTrue(ReferenceEquals(shared, shares.TimeSegments[0]));

      var copied = new TimeSegment(10, 20);
      var owns = new TimeRange([copied]);
      copied.EndTime = 900;
      Assert.AreEqual(11, owns.GetTotal());
    }

    /*
     * An empty range totals zero, and the app divides by totals in about ten places. Damage over no seconds does not throw: it saturates to
     * long.MaxValue, which on a chart is a spike tall enough to flatten everything else. Callers have to rule out the zero themselves.
     */
    [TestMethod]
    public void DividingByAnEmptyRangeSaturatesInsteadOfThrowing()
    {
      var range = new TimeRange();
      Assert.AreEqual(0, range.GetTotal());

      var perSecond = (long)Math.Round(5000d / range.GetTotal());
      Assert.AreEqual(long.MaxValue, perSecond);
    }

    /* The one caller of this overload (MainActions' save-selected-fights) checks TimeSegments.Count > 0 first; the check is load bearing. */
    [TestMethod]
    public void TimeCheckOnAnEmptyRangeThrowsAndItsCallerGuardsFirst()
    {
      var line = "[Mon Jul 28 09:00:10 2025] Gharai hits you.";
      Assert.Throws<InvalidOperationException>(
        () => TimeRange.TimeCheck(line, 1, new TimeRange(), out _));

      // With a segment present it reads Last().EndTime to set `exceeds`, which is only meaningful because the list stays sorted.
      var at = DateUtil.StandardDateToDotNetSeconds(line);
      var range = new TimeRange(new TimeSegment(at, at));
      Assert.IsTrue(TimeRange.TimeCheck(line, at, range, out var insideLastEnd));
      Assert.IsFalse(insideLastEnd, "a line inside the last segment has not gone past the end");

      // start == 0 means "any time", which short-circuits before the range is consulted at all.
      Assert.IsTrue(TimeRange.TimeCheck(line, 0, range, out _));
    }

    [TestMethod]
    public void TimeCheckAcceptsOnlyLinesInsideASegment()
    {
      var inside = "[Mon Jul 28 09:00:10 2025] Gharai hits you.";
      var outside = "[Mon Jul 28 09:00:40 2025] Gharai hits you.";
      var start = DateUtil.StandardDateToDotNetSeconds(inside);
      var range = new TimeRange(new TimeSegment(start, start + 5));

      Assert.IsTrue(TimeRange.TimeCheck(inside, start, range, out _));
      Assert.IsFalse(TimeRange.TimeCheck(outside, start, range, out var exceeds));
      Assert.IsTrue(exceeds);
    }
  }
}

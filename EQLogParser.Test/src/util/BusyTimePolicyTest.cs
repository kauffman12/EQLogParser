namespace EQLogParser;

/*
 * The arithmetic behind the Busy column: seconds come from marks, not from a stored span; the welding boundary is the DISTANCE between
 * two marks (which is why {0,10} joins into 11 while {0,11} stays 2); and every run is cut inside a covered span, because Busy removes
 * idle time from the seconds Sec already counts rather than inventing its own. The coverage tests below are the difference between a
 * column that subtracts from the row's own range and one that quietly reports activity the capture never recorded.
 * Everything here runs at FixtureTime.BaseL magnitudes too: .NET-epoch seconds are ~64 billion, so a 32-bit mark truncates silently
 * while still producing plausible numbers.
 */
[TestClass]
public class BusyTimePolicyTest
{
  private static HashSet<long> MarksOf(params long[] marks) => [.. marks];

  /* One covered run from the first mark to the last: nothing the arithmetic could reach is outside the row's own data. */
  private static TimeRange CoveredAcross(long[] marks) =>
    marks.Length == 0 ? new TimeRange() : new TimeRange(new TimeSegment(marks.Min(), marks.Max()));

  private static long Welded(params long[] marks) => BusyTimePolicy.Weld(MarksOf(marks), CoveredAcross(marks));

  [TestMethod]
  [DataRow(new long[] { }, 0L)]
  [DataRow(new long[] { 120 }, 1L)]
  [DataRow(new long[] { 0, 0, 1 }, 2L)]
  [DataRow(new long[] { 0, 10 }, 11L)]
  [DataRow(new long[] { 0, 11 }, 2L)]
  [DataRow(new long[] { 0, 4, 8, 12 }, 13L)]
  [DataRow(new long[] { 0, 14, 28 }, 3L)]
  [DataRow(new long[] { 0, 2, 4, 50, 52 }, 8L)]
  public void WeldJoinsRunsAndSplitsLongGaps(long[] marks, long expected)
  {
    Assert.AreEqual(expected, Welded(marks));
  }

  [TestMethod]
  public void WeldIgnoresInsertionOrderBecauseItSortsBeforeWalking()
  {
    var sorted = MarksOf(0, 4, 8, 12, 40, 44);
    var shuffled = MarksOf(44, 8, 0, 40, 12, 4);
    var covered = new TimeRange(new TimeSegment(0, 44));

    Assert.AreEqual(BusyTimePolicy.Weld(sorted, covered), BusyTimePolicy.Weld(shuffled, covered));
    Assert.AreEqual(18L, BusyTimePolicy.Weld(shuffled, covered), "runs [0,12] (a four second cadence) and [40,44], split by the 28 second silence");
  }

  [TestMethod]
  public void WeldMeasuresTheSameAtRealClockMagnitudes()
  {
    foreach (var marks in new[] { new long[] { 120 }, new long[] { 0, 10 }, new long[] { 0, 11 }, new long[] { 0, 2, 4, 50, 52 } })
    {
      var small = Welded(marks);
      var shifted = marks.Select(m => FixtureTime.BaseL + m).ToArray();
      var real = BusyTimePolicy.Weld(MarksOf(shifted), new TimeRange(new TimeSegment(FixtureTime.BaseL, FixtureTime.BaseL + shifted.Max())));

      Assert.AreEqual(small, real, $"marks shifted by {FixtureTime.BaseL} must not lose seconds to a narrow clock");
    }
  }

  [TestMethod]
  public void TheWeldIsCutInsideTheSpanSecCountsRatherThanAddedToIt()
  {
    var marks = MarksOf(0, 10);

    // One covered run across the pause: the row's own range claims the seconds in between, so welding them is cutting nothing.
    Assert.AreEqual(11L, BusyTimePolicy.Weld(marks, new TimeRange(new TimeSegment(0, 10))));

    /* Two runs — the shape two selected fights leave when Sec refuses to bridge ten seconds: those eight seconds belong to neither column. */
    var split = new TimeRange();
    split.Add(new TimeSegment(0, 0));
    split.Add(new TimeSegment(10, 10));
    Assert.AreEqual(2L, BusyTimePolicy.Weld(marks, split), "a pause with no data cannot buy Busy time");
    Assert.AreEqual(split.GetTotal(), BusyTimePolicy.Weld(marks, split), "with nothing idle to remove, Busy is Sec");
  }

  [TestMethod]
  public void ALongSilenceIsStillCutInsideOneCoveredSpan()
  {
    var covered = new TimeRange(new TimeSegment(0, 20));

    Assert.AreEqual(2L, BusyTimePolicy.Weld(MarksOf(0, 20), covered), "the fight's own span covers the silence; Busy is what removes it");
    Assert.AreEqual(21L, (long)covered.GetTotal(), "...and Sec keeps counting all of it, which is the point of the column");
  }

  [TestMethod]
  public void AMarkOutsideEveryCoveredSpanCountsNothing()
  {
    var covered = new TimeRange(new TimeSegment(0, 1));

    Assert.AreEqual(2L, BusyTimePolicy.Weld(MarksOf(0, 1), covered));
    Assert.AreEqual(2L, BusyTimePolicy.Weld(MarksOf(0, 1, 50), covered), "a mark the row's range does not contain is not evidence of anything");
  }

  [TestMethod]
  public void NoCoverageMeansNoMeasurement()
  {
    Assert.AreEqual(0L, BusyTimePolicy.Weld(MarksOf(0, 1), new TimeRange()));
    Assert.AreEqual(0L, BusyTimePolicy.Weld(MarksOf(0, 1), null));
  }

  [TestMethod]
  public void AWeldedTotalNeverOutgrowsTheSpanItWasCutFrom()
  {
    var cases = new[]
    {
      new long[] { 7 },
      new long[] { 0, 1, 2, 3 },
      new long[] { 0, 9, 18, 27, 100 },
      new long[] { 0, 11, 22, 33 },
      new long[] { FixtureTime.BaseL, FixtureTime.BaseL + 5, FixtureTime.BaseL + 6, FixtureTime.BaseL + 40 },
    };

    foreach (var raw in cases)
    {
      var marks = MarksOf(raw);
      foreach (var covered in new[] { CoveredAcross(raw), SplitEveryOther(raw) })
      {
        var welded = BusyTimePolicy.Weld(marks, covered);

        Assert.IsTrue(welded >= 0, $"a cut cannot go negative: {welded}");
        Assert.IsTrue(welded >= marks.Count - UncoveredCount(raw, covered), $"{raw.Length} marks cannot measure less than the ones the span holds: {welded}");
        Assert.IsTrue(welded <= covered.GetTotal(), $"Busy may only subtract from its coverage: {welded} over {covered.GetTotal()}");
      }
    }
  }

  /* Coverage with a hole in it, so the same marks are tested against a row whose range does not reach everywhere. */
  private static TimeRange SplitEveryOther(long[] marks)
  {
    var range = new TimeRange();
    for (var i = 0; i < marks.Length; i++)
      range.Add(new TimeSegment(marks[i], marks[i] + (i % 2 == 0 ? 1 : 0)));

    return range;
  }

  private static int UncoveredCount(long[] marks, TimeRange covered) => marks.Count(m => !covered.Contains(m));

  [TestMethod]
  public void AnAggregateOfOwnerAndPetMarksIsAUnionNotASum()
  {
    var owner = MarksOf(0, 1, 2);
    var pet = MarksOf(1, 2, 3);
    var overlap = new HashSet<long>(owner);
    overlap.UnionWith(pet);

    Assert.AreEqual(3L, Welded([.. owner]));
    Assert.AreEqual(3L, Welded([.. pet]));
    Assert.AreEqual(4L, Welded([.. overlap]), "seconds both of them worked in are counted once, not twice");

    /*
     * A parent can still beat the sum of its children: one second each at 0 and 3 is one four second run for the pair, because their own
     * spans sit close enough to be one range. That bridge is Sec's doing, which is what the next assertion refuses.
     */
    Assert.AreEqual(4L, Welded(0, 3));

    var far = MarksOf(0, 30);
    var twoSpans = new TimeRange();
    twoSpans.Add(new TimeSegment(0, 0));
    twoSpans.Add(new TimeSegment(30, 30));
    Assert.AreEqual(2L, BusyTimePolicy.Weld(far, twoSpans), "the union of marks may not bridge further than the row's ranges do");
  }

  [TestMethod]
  [DataRow(0)]
  [DataRow(-1)]
  public void AGapBelowOneIsRefusedRatherThanWeldingEverything(int gap)
  {
    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BusyTimePolicy.Weld(MarksOf(0, 5), new TimeRange(new TimeSegment(0, 5)), gap));
  }

  [TestMethod]
  public void NoteMarksASecondOnceEvenWhenTheWalkComesBackToIt()
  {
    var row = StatsUtil.CreatePlayerStats("Busyrow");
    var covered = new TimeRange(new TimeSegment(FixtureTime.Base, FixtureTime.Base + 6));

    BusyTimePolicy.Note(row, FixtureTime.Base + 5);
    BusyTimePolicy.Note(row, FixtureTime.Base + 5);
    Assert.AreEqual(1, row.ActivityMarks!.Count, "the consecutive-second gate catches the repeat");

    // A different second in between moves the gate on; the SET is what stops the revisited second counting twice.
    BusyTimePolicy.Note(row, FixtureTime.Base + 6);
    BusyTimePolicy.Note(row, FixtureTime.Base + 5);
    Assert.AreEqual(2, row.ActivityMarks.Count);

    BusyTimePolicy.Apply(row, covered);
    Assert.AreEqual(2d, row.BusySeconds);
  }

  [TestMethod]
  public void ApplyPublishesTheScalarAndReleasesTheScratch()
  {
    var row = StatsUtil.CreatePlayerStats("Busyrow");
    BusyTimePolicy.Note(row, FixtureTime.Base);
    BusyTimePolicy.Note(row, FixtureTime.Base + 2);

    BusyTimePolicy.Apply(row, new TimeRange(new TimeSegment(FixtureTime.Base, FixtureTime.Base + 2)));

    Assert.AreEqual(3d, row.BusySeconds, "marks 0 and 2 weld into the run [0,2]");
    Assert.IsNull(row.ActivityMarks, "rows are retained by CombinedStats, so scratch may not survive publication");
    Assert.AreEqual(long.MinValue, row.LastMarkSec, "the gate belongs to the build that set it");
  }

  [TestMethod]
  public void AnUnmarkedRowMeasuresZeroLikeAnyOtherTotal()
  {
    var row = StatsUtil.CreatePlayerStats("Busyrow");
    var covered = new TimeRange(new TimeSegment(FixtureTime.Base, FixtureTime.Base + 9));

    Assert.AreEqual(0d, row.BusySeconds);
    BusyTimePolicy.Apply(row, covered);
    Assert.AreEqual(0d, row.BusySeconds, "a row nothing was credited to measures zero, the way TotalSeconds does");

    // And a stale scalar from an earlier build cannot outlive the marks that earned it.
    BusyTimePolicy.Note(row, FixtureTime.Base);
    BusyTimePolicy.Apply(row, covered);
    Assert.AreEqual(1d, row.BusySeconds);
    BusyTimePolicy.Apply(row, covered);
    Assert.AreEqual(0d, row.BusySeconds);

    // Marks with no coverage at all measure nothing either - the same row reports zero seconds to Sec.
    BusyTimePolicy.Note(row, FixtureTime.Base);
    BusyTimePolicy.Apply(row, new TimeRange());
    Assert.AreEqual(0d, row.BusySeconds);
  }
}

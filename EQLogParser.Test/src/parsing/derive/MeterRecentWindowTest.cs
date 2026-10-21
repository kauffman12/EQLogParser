using System.Collections.Generic;
using System.Linq;

namespace EQLogParser;

/*
 * The six-second window on its own, without a projection in the way: labels, division and the two ways a modulo
 * collision can go wrong. The integration half lives beside DamageMeterCalculationTest, where the same amounts go
 * through the meter's incremental walk.
 *
 * Every call here names the window's right edge explicitly, because that edge is the whole point: it is a LOG second —
 * the newest second the meter has read — and never the instant a build happened to run.
 */
[TestClass]
public class MeterRecentWindowTest
{
  // A labelled second inside the fixture epoch; every test below counts forward from it.
  private const long S = FixtureTime.BaseL + 1000;

  [TestMethod]
  public void ABurstCountsSixLabelsThenLeavesTheWindow()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S, 60_000, S);

    Assert.AreEqual(10_000L, Sum(window, S) / MeterRecentWindow.WindowSeconds);
    for (var second = S; second <= S + 5; second++)
    {
      window.Advance(second);
      Assert.AreEqual(60_000L, Sum(window, second), $"second {second - S} still holds its own label");
    }

    window.Advance(S + 6);
    Assert.AreEqual(0L, Sum(window, S + 6), "the sixth second after the hit drops it whole, not in a fade");
  }

  [TestMethod]
  public void AStampOutsideTheWindowIsRefusedBeforeItCanTouchACollidingBucket()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S, 500, S);

    // S - 6 lands in the same slot as S and is out of the window: taking it for the newest second would erase the burst.
    window.Add("Illuminai", string.Empty, S - 6, 999, S);
    Assert.AreEqual(500L, Sum(window, S), "an expired stamp may not rotate a live bucket out");

    // The future shares slot behaviour too: it is not in the current second merely because it is nearby.
    window.Add("Illuminai", string.Empty, S + 1, 777, S);
    Assert.AreEqual(500L, Sum(window, S));
  }

  [TestMethod]
  public void AStaleStampSharingASlotIsReplacedRatherThanMerged()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S - 6, 900, S - 6);
    Assert.AreEqual(900L, Sum(window, S - 6));

    // Bucket for S-6 is also the bucket for S. The new label wins outright; nothing of the old one may survive.
    window.Add("Illuminai", string.Empty, S, 1200, S);
    Assert.AreEqual(1200L, Sum(window, S));
    Assert.AreEqual(1, EntryCount(window, S));
  }

  [TestMethod]
  public void ALateArrivalInsideTheWindowKeepsItsOwnLabel()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S + 1, 200, S + 3);
    window.Add("Bithika", string.Empty, S - 1, 1000, S + 3);

    Assert.AreEqual(1200L, Sum(window, S + 3), "out-of-order within the window adds to its own second");

    window.Advance(S + 3);
    Assert.AreEqual(1200L, Sum(window, S + 3), "S-1 is still one of the six labels at S+3");
    window.Advance(S + 4);
    Assert.AreEqual(1200L, Sum(window, S + 4), "and still one at S+4");
    window.Advance(S + 5);
    Assert.AreEqual(200L, Sum(window, S + 5), "the older label expires on its own schedule, not the arrival order");
  }

  [TestMethod]
  public void ASumIgnoresLabelsTheEdgeHasLeftBehindEvenWithoutAnAdvance()
  {
    var window = new MeterRecentWindow();
    for (var offset = 0; offset <= 5; offset++) window.Add("Illuminai", string.Empty, S + offset, 100, S + offset);

    Assert.AreEqual(600L, Sum(window, S + 5), "all six labels are inside while the edge is second 5");

    // Aging and summing live in different classes, and nothing here advanced or overwrote anything: asked at second 6, the
    // sum has to notice label S is outside its own window instead of serving it as current damage.
    Assert.AreEqual(500L, Sum(window, S + 6), "label S left the window the edge just moved out from under");
    Assert.AreEqual(100L, Sum(window, S + 10), "and asking much later answers nothing");
  }

  [TestMethod]
  public void AdvanceKeepsExactlySixLabels()
  {
    var window = new MeterRecentWindow();
    for (var offset = 0; offset <= 7; offset++) window.Add("Illuminai", string.Empty, S + offset, 100, S + offset);

    window.Advance(S + 7);
    Assert.AreEqual(600L, Sum(window, S + 7), "labels S+0 and S+1 are gone, S+2 through S+7 remain");
  }

  [TestMethod]
  public void AClockJumpOfSixOrMoreClearsEveryBucket()
  {
    var window = new MeterRecentWindow();
    for (var offset = 0; offset <= 5; offset++) window.Add("Illuminai", string.Empty, S + offset, 100, S + offset);

    Assert.AreEqual(600L, Sum(window, S + 5));
    window.Advance(S + 400);
    Assert.AreEqual(0L, Sum(window, S + 400));
    Assert.AreEqual(0, EntryCount(window, S + 400));
  }

  [TestMethod]
  public void FractionalTimesShareTheirSecondLabelAndLeaveTogether()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S + 0.2, 300, S);
    window.Add("Illuminai", string.Empty, S + 0.9, 400, S);

    Assert.AreEqual(700L, Sum(window, S));
    window.Advance(S + 6);
    Assert.AreEqual(0L, Sum(window, S + 6), "one label, one expiry — a partly elapsed second is still six seconds wide");
  }

  [TestMethod]
  public void AnOwnerKeyIsKeptApartFromItsActor()
  {
    var window = new MeterRecentWindow();
    window.Add("Fluffy", "Illuminai", S, 100, S);
    window.Add("Illuminai", string.Empty, S, 200, S);

    var totals = NewTotals();
    window.SumInto(totals, S);
    Assert.AreEqual(2, totals.Count, "the row resolver, not the bucket, decides who owns what");
    Assert.AreEqual(300L, Sum(window, S));
  }

  [TestMethod]
  public void ANonPositiveAmountIsNotBucketed()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S, 0, S);
    window.Add("Illuminai", string.Empty, S + 1, -5, S + 1);

    Assert.AreEqual(0, EntryCount(window, S + 1), "a miss costs nothing and allocates nothing");
  }

  [TestMethod]
  public void AmountsStayWideAcrossTheWindow()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S, 3_000_000_000L, S);
    window.Add("Illuminai", string.Empty, S + 1, 3_000_000_000L, S + 1);

    Assert.AreEqual(6_000_000_000L, Sum(window, S + 1), "the rolling lane carries the same widths as the encounter total");
    Assert.AreEqual(1_000_000_000L, Sum(window, S + 1) / MeterRecentWindow.WindowSeconds);
  }

  [TestMethod]
  public void AWindowSecondIsTheCapturedTimeFlooredToItsLabel()
  {
    Assert.AreEqual(S, MeterRecentWindow.SecondOf(S + 0.9));
    Assert.AreEqual(MeterRecentWindow.NoSecond, MeterRecentWindow.SecondOf(double.PositiveInfinity));
    Assert.AreEqual(MeterRecentWindow.NoSecond, MeterRecentWindow.SecondOf(double.NaN));
  }

  [TestMethod]
  public void AWindowWithNoEdgeHoldsNothingAndSumsNothing()
  {
    var window = new MeterRecentWindow();
    window.Add("Illuminai", string.Empty, S, 600, S);
    Assert.AreEqual(600L, Sum(window, S));

    // No edge means no window to be inside: an add is refused rather than landed on somebody else's label.
    window.Add("Bithika", string.Empty, S, 600, MeterRecentWindow.NoSecond);
    Assert.AreEqual(600L, Sum(window, S));

    window.Advance(MeterRecentWindow.NoSecond);
    Assert.AreEqual(0L, Sum(window, S), "and an edge that disappears takes the state with it");
  }

  [TestMethod]
  public void TheRateIsAnIntegerAfterTheCompleteSumNotAPerBucketRounding()
  {
    var window = new MeterRecentWindow();
    for (var offset = 0; offset <= 5; offset++) window.Add("Illuminai", string.Empty, S + offset, 101, S + 5);

    Assert.AreEqual(606L, Sum(window, S + 5));
    Assert.AreEqual(101L, Sum(window, S + 5) / MeterRecentWindow.WindowSeconds);
  }

  // The whole point of the type: one clock, and it is the data's. Nothing here takes a build instant, so there is no wall
  // second to compare against — which is why a log eight seconds behind its fight gets its own six seconds of labels.
  [TestMethod]
  public void TheWindowTakesNoBuildInstantAnywhere()
  {
    var window = new MeterRecentWindow();
    for (var second = 95; second <= 100; second++) window.Add("Illuminai", string.Empty, second, 600, 100);

    // Repeating the same data edge cannot age the window.
    for (var repeat = 0; repeat < 3; repeat++)
      Assert.AreEqual(3600L, Sum(window, 100), $"repeat {repeat} of the same question answers the same");

    // Only a newer data edge ages labels: at 101 the window is [96..101], so second 95 leaves.
    Assert.AreEqual(3000L, Sum(window, 101), "second 95 left when the edge reached 101");
    window.Advance(106);
    Assert.AreEqual(0L, Sum(window, 106), "and an edge that outruns every label leaves an empty window, not a stale one");
  }

  /*
   * The window belongs to the data. Asked at edge 100 it holds labels 95 through 100 and refuses a stamp of 107 outright —
   * the seconds the wall clock has reached but no captured line has written are not evidence, and taking one would both
   * dilute the rate and rotate a real label out of its bucket.
   */
  [TestMethod]
  public void AStampAheadOfTheDataEdgeIsRefused()
  {
    var window = new MeterRecentWindow();
    for (var second = 95; second <= 100; second++) window.Add("Illuminai", string.Empty, second, 600, 100);
    window.Add("Illuminai", string.Empty, 107, 50_000, 100);

    Assert.AreEqual(3600L, Sum(window, 100), "six logged labels, and the future stamp is not in the sum");
    Assert.AreEqual(3000L, Sum(window, 101), "and label 95 leaves on the next second like any other");
  }

  private static Dictionary<(string Name, string Owner), long> NewTotals() => new();

  private static long Sum(MeterRecentWindow window, long edge)
  {
    var totals = NewTotals();
    window.SumInto(totals, edge);
    return totals.Values.Sum();
  }

  private static int EntryCount(MeterRecentWindow window, long edge)
  {
    var totals = NewTotals();
    window.SumInto(totals, edge);
    return totals.Count;
  }
}

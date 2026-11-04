namespace EQLogParser;

[TestClass]
public sealed class MeleeDiEstimatorTest
{
  [TestMethod]
  public void TheMeasuredOutgoingLadderExplainsEveryHitExactly()
  {
    int[] counts = [115, 21, 11, 20, 16, 12, 18, 23, 21, 14, 21, 18, 14, 12, 11, 8, 16, 6, 7, 26];
    var bars = Ladder().Select((bar, index) => bar with { Frequency = counts[index] }).ToArray();
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    Assert.AreEqual(410L, fit.HitCount);
    Assert.AreEqual(20, fit.ObservedLevels);
    Assert.AreEqual(7500m, fit.FirstAmount);
    Assert.AreEqual(450m, fit.Interval);
    Assert.AreEqual(7050m, fit.DamageBonus);
    Assert.AreEqual(0m, fit.MaximumResidual);
    for (var i = 0; i < bars.Length; i++) Assert.AreEqual(i + 1, fit.LevelFor(bars[i].Amount));
    Assert.AreEqual(0, fit.LevelFor(9999), "a model labels observed values, not invented hits on a nearby grid");
  }

  [TestMethod]
  public void TheIndependentCaptureWithAMissingLevelDoesNotRenumberLaterAmounts()
  {
    int[] counts = [15, 2, 2, 7, 1, 5, 3, 2, 4, 3, 5, 4, 3, 5, 0, 3, 3, 1, 1, 6];
    var bars = Ladder().Select((bar, index) => bar with { Frequency = counts[index] })
      .Where(bar => bar.Frequency > 0).ToArray();
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    Assert.AreEqual(75L, fit.HitCount);
    Assert.AreEqual(19, fit.ObservedLevels);
    Assert.AreEqual(450m, fit.Interval);
    Assert.AreEqual(0, fit.LevelFor(13800), "DI15 was not observed");
    Assert.AreEqual(16, fit.LevelFor(14250));
    Assert.AreEqual(20, fit.LevelFor(16050));
  }

  [TestMethod]
  public void TheMeasuredIncomingLadderToleratesIntegerRoundingWithoutUsingTheMinimumGap()
  {
    long[] amounts = [8993, 10458, 11924, 13389, 14854, 16319, 17784, 19250, 20715, 22180,
      23645, 25110, 26576, 28042, 29507, 30971, 32436, 33902, 36833];
    int[] counts = [13, 6, 3, 1, 2, 1, 5, 6, 3, 2, 2, 9, 6, 2, 2, 3, 2, 2, 22];
    var bars = HitFrequencySeries.Prepare(amounts.Select((amount, index) => (amount, count: counts[index]))
      .ToDictionary(pair => pair.amount, pair => pair.count));
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    Assert.AreEqual(92L, fit.HitCount);
    Assert.IsTrue(Math.Abs(fit.Interval - 1465.235746m) < 0.000001m);
    Assert.IsTrue(fit.MaximumResidual > 0.9m && fit.MaximumResidual <= 1m);
    Assert.AreEqual(1, fit.LevelFor(8993));
    Assert.AreEqual(18, fit.LevelFor(33902));
    Assert.AreEqual(20, fit.LevelFor(36833));
    // The chart's full noncritical row also includes one lucky-only hit at this existing amount.
    bars[15] = bars[15] with { Frequency = bars[15].Frequency + 1 };
    var chartFit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(chartFit);
    Assert.AreEqual(93L, chartFit.HitCount);
    Assert.AreEqual(fit.Interval, chartFit.Interval, "frequency skew must not move the distinct-level geometry");
    Assert.AreEqual(fit.MaximumResidual, chartFit.MaximumResidual);
  }

  [TestMethod]
  public void WideAmountsKeepOnePointResidualsMeaningfulAtThe48BitCeiling()
  {
    var first = TextUtils.MaxAmount - (19 * 450L);
    var bars = Ladder(first);
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    Assert.AreEqual((decimal)first, fit.FirstAmount);
    Assert.AreEqual(450m, fit.Interval);
    Assert.AreEqual(0m, fit.MaximumResidual);
    Assert.AreEqual(20, fit.LevelFor(TextUtils.MaxAmount));
    bars[8] = bars[8] with { Amount = bars[8].Amount + 5 };
    Assert.IsNull(MeleeDiEstimator.TryFit(bars), "large magnitudes must not swallow a five-point outlier");
  }

  [TestMethod]
  public void MissingAnEndpointLeavesTheAbsoluteOffsetUnknown()
  {
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder().Skip(1).ToArray()), "these could be DI1–19 or DI2–20");
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder().SkipLast(1).ToArray()));
  }

  [TestMethod]
  public void FourInternalHolesAreAllowedButBothEndsAndEnoughLevelsAreRequired()
  {
    var bars = Ladder(frequency: 4).Where((_, index) => index is not (2 or 6 or 10 or 15)).ToArray();
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    Assert.AreEqual(16, fit.ObservedLevels);
    Assert.AreEqual(4, fit.LevelFor(8850));
    Assert.AreEqual(20, fit.LevelFor(16050));
    Assert.IsNull(MeleeDiEstimator.TryFit(bars.Where(bar => bar.Amount != 9750).ToArray()));
  }

  [TestMethod]
  public void LowSupportDoesNotBecomeConfidenceJustBecauseAmountsAreEvenlySpaced()
  {
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder(frequency: 1)));
    var boundary = Ladder(frequency: 2);
    boundary[0] = boundary[0] with { Frequency = 12 };
    Assert.IsNotNull(MeleeDiEstimator.TryFit(boundary), "50 hits across 20 levels is the support floor");
    boundary[0] = boundary[0] with { Frequency = 11 };
    Assert.IsNull(MeleeDiEstimator.TryFit(boundary));
  }

  [TestMethod]
  public void OutliersAndMultipleLaddersAreNotSilentlyRemovedToMakeAFit()
  {
    var bars = Ladder();
    bars[9] = bars[9] with { Amount = bars[9].Amount + 5 };
    Assert.IsNull(MeleeDiEstimator.TryFit(bars));
    var mixed = Ladder().Concat(Ladder(first: 7600)).OrderBy(bar => bar.Amount).ToArray();
    Assert.IsNull(MeleeDiEstimator.TryFit(mixed));
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder().Append(new HitFrequencyBar(50000, 1, 0)).ToArray()));
  }

  [TestMethod]
  public void SmallIntervalsCannotUseTheRoundingToleranceToInventExtraLevels()
  {
    var fit = MeleeDiEstimator.TryFit(Ladder(first: 1, interval: 1));
    Assert.IsNotNull(fit);
    Assert.AreEqual(20, fit.LevelFor(20));
    Assert.AreEqual(0m, fit.DamageBonus);
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder(first: 1, interval: 1).SkipLast(1).ToArray()));
  }

  [TestMethod]
  public void MalformedOrNonphysicalInputsAreDeclinedWithoutChangingTheInput()
  {
    Assert.IsNull(MeleeDiEstimator.TryFit([]));
    var bars = Ladder();
    bars[5] = bars[5] with { Frequency = 0 };
    var before = bars.ToArray();
    Assert.IsNull(MeleeDiEstimator.TryFit(bars));
    CollectionAssert.AreEqual(before, bars);
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder().Reverse().ToArray()));
    Assert.IsNull(MeleeDiEstimator.TryFit(Ladder(first: 100)),
      "a negative effective bonus is not a clean supported model");
    bars = Ladder();
    bars[0] = bars[0] with { Amount = 0 };
    Assert.IsNull(MeleeDiEstimator.TryFit(bars));
    bars = Ladder(TextUtils.MaxAmount - (19 * 450L));
    bars[^1] = bars[^1] with { Amount = TextUtils.MaxAmount + 1 };
    Assert.IsNull(MeleeDiEstimator.TryFit(bars));
  }

  private static HitFrequencyBar[] Ladder(long first = 7500, long interval = 450, int frequency = 3)
    => Enumerable.Range(0, 20).Select(index => new HitFrequencyBar(first + (index * interval), frequency,
      index == 0 ? 0 : interval)).ToArray();
}

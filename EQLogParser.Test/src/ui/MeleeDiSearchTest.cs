namespace EQLogParser;

[TestClass]
public sealed class MeleeDiSearchTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void FiftyThousandDistinctAmountsDoNotHideADominantLadder(bool critical)
  {
    var frequencies = Enumerable.Range(1, 50000).ToDictionary(value => (long)value, _ => 1);
    for (var level = 1; level <= 20; level++) frequencies[7050 + (450 * level)] = 200000;
    var bars = HitFrequencySeries.Prepare(frequencies);
    var fit = MeleeDiEstimator.TryFit(bars, critical);
    Assert.IsNotNull(fit);
    Assert.AreEqual(20, fit.ObservedLevels);
    Assert.AreEqual(20, fit.LevelFor(16050));
    Assert.AreEqual(1, fit.LevelFor(7500));
    Assert.AreEqual(0, fit.LevelFor(8000));
    Assert.AreEqual(4049980L, fit.TotalHitCount);
    Assert.IsTrue(fit.HitCount >= 4000000);
    var data = BarChartData.Frequency(bars, fit);
    Assert.AreEqual(50000, data.Count);
    Assert.AreEqual("DI 20", data.Column(16049).DiLabel);
    var export = TextUtils.ReadTsv(data.FrequencyTsv());
    Assert.AreEqual(50001, export.Count);
    Assert.AreEqual("16050", export[16050][0]);
    Assert.AreEqual("8000", export[8000][0]);
  }

  [TestMethod]
  public void TheMeasuredHazysongsPetSampleKeepsFiveOutlierAmountsAndLabelsTheOtherTwenty()
  {
    var player = new PlayerStats { Name = "Hazysongs`s pet" };
    var row = player.SubStatOf("Hits", Labels.Melee);
    foreach (var bar in HitFrequencyFixtures.HazysongsPetHits())
      for (var hit = 0; hit < bar.Frequency; hit++) row.RecordHitTotal(bar.Amount, false);
    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    var fit = choice.DiFor(false);
    Assert.IsNotNull(fit);
    Assert.AreEqual(25, choice.NonCritical.Count);
    Assert.AreEqual(929L, fit.HitCount);
    Assert.AreEqual(940L, fit.TotalHitCount);
    Assert.AreEqual(20, fit.ObservedLevels);
    Assert.AreEqual(20, fit.MatchedAmounts);
    Assert.AreEqual(120m, fit.Interval);
    Assert.AreEqual(1200m, fit.DamageBonus);
    Assert.AreEqual(0m, fit.MaximumResidual);
    Assert.AreEqual(1, fit.LevelFor(1320));
    Assert.AreEqual(20, fit.LevelFor(3600));
    foreach (var amount in new long[] { 1557, 1840, 3115, 3398, 3540 }) Assert.AreEqual(0, fit.LevelFor(amount));
    var data = BarChartData.Frequency(choice.NonCritical, fit);
    var export = TextUtils.ReadTsv(data.FrequencyTsv());
    Assert.AreEqual(26, export.Count);
    Assert.AreEqual("DI 20", data.Column(24).DiLabel);
    CollectionAssert.AreEqual(new[] { "3600", "8", "60" }, export[^1]);
    var filtered = BarChartData.Frequency(HitFrequencySeries.Filter(choice.NonCritical, 5), fit);
    var filteredExport = TextUtils.ReadTsv(filtered.FrequencyTsv());
    CollectionAssert.AreEqual(new[] { "3600", "8", "60" }, filteredExport[^1]);
  }

  [TestMethod]
  public void FlatBroadDataIsRejectedForLackOfSupportNotItsLength()
  {
    var bars = HitFrequencySeries.Prepare(Enumerable.Range(1, 50000).ToDictionary(value => (long)value, _ => 1));
    Assert.IsNull(MeleeDiEstimator.TryFit(bars));
    Assert.IsNull(MeleeDiEstimator.TryFit(bars, critical: true));
  }

  [TestMethod]
  public void RoundedCriticalLadderSurvivesHundredsOfDistinctRareOutliers()
  {
    var source = HitFrequencyFixtures.CombinedKeyceCriticals();
    var frequencies = source.ToDictionary(bar => bar.Amount, bar => bar.Frequency * 100);
    for (var value = 1; value <= 500; value++) frequencies[value] = 1;
    var fit = MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies), critical: true);
    Assert.IsNotNull(fit);
    Assert.AreEqual(71800L, fit.HitCount);
    Assert.AreEqual(72400L, fit.TotalHitCount);
    Assert.AreEqual(0, fit.LevelFor(52674));
    Assert.AreEqual(20, fit.LevelFor(68377));
  }

  [TestMethod]
  [DataRow(false, 1)]
  [DataRow(true, 3)]
  public void SeveralRoundedAmountsPerLevelDoNotBecomeSeveralDiLevels(bool critical, int tolerance)
  {
    var frequencies = new Dictionary<long, int>();
    var first = critical ? 30000L : 7500L;
    var interval = critical ? 2000L : 450L;
    for (var level = 0; level < 20; level++)
      for (var error = -tolerance; error <= tolerance; error++) frequencies[first + (interval * level) + error] = 10;
    var fit = MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies), critical);
    Assert.IsNotNull(fit);
    Assert.AreEqual(20, fit.ObservedLevels);
    Assert.AreEqual(frequencies.Count, fit.MatchedAmounts);
    Assert.AreEqual(frequencies.Count * 10L, fit.HitCount);
    Assert.AreEqual(20, fit.LevelFor(first + (19 * interval) - tolerance));
    Assert.AreEqual(20, fit.LevelFor(first + (19 * interval) + tolerance));
  }

  [TestMethod]
  public void RareEndpointsAreSearchedEvenWhenTheyAreNotAmongTheStrongestBins()
  {
    var frequencies = Enumerable.Range(1, 500).ToDictionary(value => (long)value, _ => 1);
    for (var level = 1; level <= 20; level++) frequencies[7050 + (450 * level)] = 10000;
    frequencies[7500] = frequencies[16050] = 1;
    var fit = MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies));
    Assert.IsNotNull(fit);
    Assert.AreEqual(1, fit.LevelFor(7500));
    Assert.AreEqual(20, fit.LevelFor(16050));
  }

  [TestMethod]
  public void SearchingMoreAmountsDoesNotResolveConflictingEndpointOffsets()
  {
    var frequencies = Enumerable.Range(1, 100).ToDictionary(value => (long)value, _ => 1);
    for (var level = 1; level <= 20; level++) frequencies[7050 + (450 * level)] = 10000;
    frequencies[7500] = frequencies[16500] = 1;
    Assert.IsNull(MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies)));
  }

  [TestMethod]
  public void WideAmountSearchKeepsPrecisionAndValidatesTheEntireInput()
  {
    var frequencies = Enumerable.Range(1, 100).ToDictionary(value => (long)value, _ => 1);
    var first = TextUtils.MaxAmount - (19 * 450L);
    for (var level = 0; level < 20; level++) frequencies[first + (450 * level)] = 1000;
    var bars = HitFrequencySeries.Prepare(frequencies);
    var fit = MeleeDiEstimator.TryFit(bars, critical: true);
    Assert.IsNotNull(fit);
    Assert.AreEqual(450m, fit.Interval);
    Assert.AreEqual(0m, fit.MaximumResidual);
    Assert.AreEqual(20, fit.LevelFor(TextUtils.MaxAmount));
    Assert.AreEqual(20100L, fit.TotalHitCount);
    bars[^1] = bars[^1] with { Amount = TextUtils.MaxAmount + 1 };
    Assert.IsNull(MeleeDiEstimator.TryFit(bars, critical: true));
  }

  [TestMethod]
  public void TwoWellPopulatedLaddersCannotBeRelabeledAsOne()
  {
    var frequencies = new Dictionary<long, int>();
    for (var level = 1; level <= 20; level++)
    {
      frequencies[7050 + (450 * level)] = 100;
      frequencies[7150 + (450 * level)] = 100;
    }
    Assert.IsNull(MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies)));
  }
}

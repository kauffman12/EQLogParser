namespace EQLogParser;

[TestClass]
public sealed class MeleeDiCriticalPolicyTest
{
  [TestMethod]
  [DataRow(1)]
  [DataRow(2)]
  public void TheOperatorsWholeSampleFindsItsRepeatedCriticalComponentAtEitherScale(int divisor)
  {
    var bars = CriticalVarianceFixture.Prepare(divisor);
    Assert.IsNull(MeleeDiEstimator.TryFit(bars), "the ordinary policy must not inherit critical lenience");
    var player = new PlayerStats { Name = "Sample pet" };
    var row = player.SubStatOf("Hits", Labels.Melee);
    foreach (var bar in bars)
      for (var hit = 0; hit < bar.Frequency; hit++) row.RecordHitTotal(bar.Amount, true);
    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    var fit = choice.DiFor(true);
    Assert.IsNotNull(fit);
    Assert.IsNull(choice.DiFor(false));
    Assert.AreEqual(20, fit.ObservedLevels);
    Assert.AreEqual(1, fit.LevelFor(7223 / divisor));
    Assert.AreEqual(4, fit.LevelFor(20024 / divisor));
    Assert.AreEqual(20, fit.LevelFor(88289 / divisor));
    Assert.AreEqual(0, fit.LevelFor(92859 / divisor));
    Assert.AreEqual(452L, fit.HitCount);
    Assert.AreEqual(bars.Sum(bar => (long)bar.Frequency), fit.TotalHitCount);
    Assert.IsTrue(fit.HitCount * 100 < fit.TotalHitCount * 98);
    Assert.IsTrue(fit.AllowedResidual >= 8m / divisor && fit.AllowedResidual <= 8.3m / divisor);
    Assert.IsTrue(fit.MaximumResidual <= fit.AllowedResidual);
    var export = TextUtils.ReadTsv(BarChartData.Frequency(bars, fit).FrequencyTsv());
    Assert.AreEqual(bars.Length + 1, export.Count);
    Assert.AreEqual(3, export[0].Length);
    Assert.AreEqual((88289 / divisor).ToString(), export.Single(fields => fields[0] == (88289 / divisor).ToString())[0]);
    Assert.AreEqual((92859 / divisor).ToString(), export.Single(fields => fields[0] == (92859 / divisor).ToString())[0]);
  }

  [TestMethod]
  public void DynamicAllowanceScalesWithTheLadderNotAnUnrelatedExtreme()
  {
    var policy = new MeleeDiPolicy(true);
    Assert.AreEqual(8.10673m, policy.Tolerance(4266.7m));
    Assert.AreEqual(4.053365m, policy.Tolerance(2133.35m));
    Assert.AreEqual(1m, new MeleeDiPolicy(false).Tolerance(4266.7m));
    Assert.AreEqual(0.25m, policy.Tolerance(1m));
    var bars = CriticalVarianceFixture.Prepare().Append(new HitFrequencyBar(TextUtils.MaxAmount, 1, 0)).ToArray();
    var fit = MeleeDiEstimator.TryFit(bars, critical: true);
    Assert.IsNotNull(fit);
    Assert.IsTrue(fit.AllowedResidual < 8.3m);
    Assert.AreEqual(0, fit.LevelFor(TextUtils.MaxAmount));
  }

  [TestMethod]
  public void ARepeatedPeakWithSingletonRungsCannotManufactureAPartialComponent()
  {
    var frequencies = Enumerable.Range(1, 1000).ToDictionary(value => (long)value, _ => 1);
    for (var level = 1; level <= 20; level++) frequencies[7050 + (450 * level)] = 1;
    frequencies[16050] = 1000;
    Assert.IsNull(MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies), critical: true));
  }

  [TestMethod]
  public void EquallySupportedPartialComponentsWithDifferentLevelOffsetsStayUnlabeled()
  {
    var frequencies = Enumerable.Range(0, 21).ToDictionary(index => 7500L + (450 * index), _ => 20);
    frequencies[7500] = frequencies[16050] = frequencies[16500] = 500;
    Assert.IsNull(MeleeDiEstimator.TryFit(HitFrequencySeries.Prepare(frequencies), critical: true));
  }

  [TestMethod]
  public void APartialComponentNeedsARepeatedEndpointAndRealSupport()
  {
    var policy = new MeleeDiPolicy(true);
    Assert.IsTrue(policy.Supports(400, 1000, 40));
    Assert.IsFalse(policy.Supports(399, 1000, 40));
    Assert.IsFalse(policy.Supports(400, 1000, 39));
    Assert.IsFalse(policy.Supports(400, 1000, 40, repeatedLevels: 15));
    Assert.IsFalse(policy.Supports(40, 100, 4));
    Assert.IsFalse(new MeleeDiPolicy(false).Supports(400, 1000, 200));
    Assert.IsTrue(new MeleeDiPolicy(false).Supports(980, 1000, 1));
  }
}

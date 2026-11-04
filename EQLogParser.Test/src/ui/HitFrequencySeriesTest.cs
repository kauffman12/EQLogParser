namespace EQLogParser;

[TestClass]
public sealed class HitFrequencySeriesTest
{
  [TestMethod]
  public void AmountsSortNumericallyAndDifferencesUseThePrecedingDistinctAmount()
  {
    var bars = HitFrequencySeries.Prepare(new Dictionary<long, int>
    {
      [150] = 3, [100] = 5, [9] = 2, [120] = 1
    });

    CollectionAssert.AreEqual(new[]
    {
      new HitFrequencyBar(9, 2, 0),
      new HitFrequencyBar(100, 5, 91),
      new HitFrequencyBar(120, 1, 20),
      new HitFrequencyBar(150, 3, 30)
    }, bars);
  }

  [TestMethod]
  [DataRow(0, 3)]
  [DataRow(1, 2)]
  [DataRow(2, 2)]
  [DataRow(3, 1)]
  [DataRow(4, 1)]
  [DataRow(5, 0)]
  public void MinimumFrequencyIsStrictlyGreaterThan(int threshold, int expectedCount)
  {
    var bars = HitFrequencySeries.Prepare(new Dictionary<long, int> { [100] = 5, [120] = 1, [150] = 3 });
    var filtered = HitFrequencySeries.Filter(bars, threshold);

    Assert.AreEqual(expectedCount, filtered.Count);
    Assert.IsTrue(filtered.All(bar => bar.Frequency > threshold));
  }

  [TestMethod]
  public void FilteringNeverRecomputesDifferencesBetweenSurvivingAmounts()
  {
    var bars = HitFrequencySeries.Prepare(new Dictionary<long, int> { [100] = 5, [120] = 1, [150] = 3 });
    var filtered = HitFrequencySeries.Filter(bars, 1);

    CollectionAssert.AreEqual(new[] { new HitFrequencyBar(100, 5, 0), new HitFrequencyBar(150, 3, 30) }, filtered);
    Assert.AreEqual(30L, filtered.Skip(1).Single().Difference,
      "a later page reads the prepared difference, not a new zero or the gap between filtered neighbors");
    CollectionAssert.AreEqual(new long[] { 0, 20, 30 }, bars.Select(bar => bar.Difference).ToArray());
  }

  [TestMethod]
  public void EvenTheFirstFilteredAmountKeepsItsOriginalDifference()
  {
    var bars = HitFrequencySeries.Prepare(new Dictionary<long, int> { [100] = 1, [120] = 2, [150] = 3 });
    Assert.AreEqual(new HitFrequencyBar(150, 3, 30), HitFrequencySeries.Filter(bars, 2).Single());
  }

  [TestMethod]
  public void WideAmountsAndRepeatedCountsRemainExactInBothLanes()
  {
    var player = new PlayerStats { Name = "Caster" };
    var row = player.SubStatOf("Wide Spell", Labels.Dd);
    row.RecordHitTotal(TextUtils.MaxAmount, false);
    row.RecordHitTotal(uint.MaxValue + 1L, false);
    row.RecordHitTotal(uint.MaxValue, false);
    row.RecordHitTotal(uint.MaxValue + 1L, false);
    row.RecordHitTotal(TextUtils.MaxAmount, true);
    row.RecordHitTotal(TextUtils.MaxAmount, true);
    Assert.IsFalse(row.HitFreqMaterialized);

    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();

    CollectionAssert.AreEqual(new[]
    {
      new HitFrequencyBar(uint.MaxValue, 1, 0),
      new HitFrequencyBar(uint.MaxValue + 1L, 2, 1),
      new HitFrequencyBar(TextUtils.MaxAmount, 1, TextUtils.MaxAmount - (uint.MaxValue + 1L))
    }, choice.NonCritical.ToArray());
    Assert.AreEqual(new HitFrequencyBar(TextUtils.MaxAmount, 2, 0), choice.Critical.Single());
    Assert.AreEqual(4, choice.Values(false).Sum(bar => bar.Frequency));
    Assert.AreEqual(2, choice.Values(true).Sum(bar => bar.Frequency));
    Assert.IsTrue(row.HitFreqMaterialized);
    Assert.IsFalse(row.HoldsRawHitTotals);
  }

  [TestMethod]
  public void DuplicateDisplayNamesKeepTheirBoardKeysAndTheirOwnDistributions()
  {
    var player = new PlayerStats { Name = "Caster" };
    var direct = player.SubStatOf("Shared Spell", Labels.Dd);
    var dot = player.SubStatOf("Shared Spell", Labels.Dot);
    direct.RecordHitTotal(100, false);
    dot.RecordHitTotal(900, false);
    direct.RecordHitTotal(200, true);
    dot.RecordHitTotal(1800, true);

    var choices = HitFrequencySeries.ForPlayers(player, null)[player.Name];
    var selectedDirect = choices.Single(choice => choice.Key == direct.Key);
    var selectedDot = choices.Single(choice => choice.Key == dot.Key);

    Assert.AreEqual("Shared Spell — Direct Damage", selectedDirect.Label);
    Assert.AreEqual("Shared Spell — DoT Tick", selectedDot.Label);
    Assert.AreEqual(100L, selectedDirect.NonCritical.Single().Amount);
    Assert.AreEqual(900L, selectedDot.NonCritical.Single().Amount);
    Assert.AreEqual(200L, selectedDirect.Critical.Single().Amount);
    Assert.AreEqual(1800L, selectedDot.Critical.Single().Amount);
    Assert.AreNotEqual(selectedDirect.Key, selectedDot.Key);
  }

  [TestMethod]
  public void UniqueNamesKeepTheirLabelsAndBoardFoldedPairsStayOneChoice()
  {
    var player = new PlayerStats { Name = "Attacker" };
    var row = player.SubStatOf("Shared", "Spell");
    var folded = player.SubStatOf("Shared", "Melee");
    Assert.AreSame(row, folded, "the chart must not invent a type/subtype grouping different from the board");
    row.RecordHitTotal(100, false);
    folded.RecordHitTotal(100, false);

    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    Assert.AreEqual("Shared", choice.Label);
    Assert.AreEqual(row.Key, choice.Key);
    Assert.AreEqual(new HitFrequencyBar(100, 2, 0), choice.NonCritical.Single());
  }

  [TestMethod]
  public void NullBoardKeysArePreservedRatherThanReplacedByDisplayText()
  {
    var player = new PlayerStats { Name = "Attacker" };
    var row = player.SubStatOf(null, "Melee");
    row.RecordHitTotal(100, false);
    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    Assert.IsNull(choice.Key);
    Assert.AreEqual(100L, choice.NonCritical.Single().Amount);
  }

  [TestMethod]
  public void EmptyDistributionsAndAllFilteredOutSeriesReturnNoBars()
  {
    Assert.AreEqual(0, HitFrequencySeries.Prepare(new Dictionary<long, int>()).Length);
    Assert.AreEqual(0, HitFrequencySeries.Filter([], 0).Count);
    var bars = HitFrequencySeries.Prepare(new Dictionary<long, int> { [100] = 1 });
    Assert.AreEqual(0, HitFrequencySeries.Filter(bars, 1).Count);

    var player = new PlayerStats { Name = "Empty" };
    Assert.AreEqual(0, HitFrequencySeries.ForPlayers(player, null)[player.Name].Count);
    var row = player.SubStatOf("No Hits", Labels.Dd);
    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    Assert.AreEqual(0, choice.Critical.Count);
    Assert.AreEqual(0, choice.NonCritical.Count);
    Assert.AreEqual(0, row.RawHitTotals);
  }

  [TestMethod]
  public void CombinedChildrenDetermineThePlayerAndPetChoicesWithoutOwnerFolding()
  {
    var selected = new PlayerStats { Name = "Owner +Pets" };
    selected.SubStatOf("Combined", Labels.Dd).RecordHitTotal(999, false);
    var owner = new PlayerStats { Name = "Owner" };
    owner.SubStatOf("Owner Spell", Labels.Dd).RecordHitTotal(100, false);
    var pet = new PlayerStats { Name = "Pet" };
    pet.SubStatOf("Bite", "Melee").RecordHitTotal(200, false);
    var combined = new CombinedStats();
    combined.Children[selected.Name] = [owner, pet];

    var players = HitFrequencySeries.ForPlayers(selected, combined);
    CollectionAssert.AreEqual(new[] { "Owner", "Pet" }, players.Keys.ToArray());
    Assert.AreEqual(100L, players[owner.Name].Single().NonCritical.Single().Amount);
    Assert.AreEqual(200L, players[pet.Name].Single().NonCritical.Single().Amount);
    Assert.IsFalse(selected.SubStats.Single().HitFreqMaterialized,
      "the combined row is not the distribution source when Children supplies the individual rows");
  }

  [TestMethod]
  public void MissingChildrenFallsBackToTheSelectedRowOnly()
  {
    var selected = new PlayerStats { Name = "Solo" };
    selected.SubStatOf("Spell", Labels.Dd).RecordHitTotal(100, false);
    var other = new PlayerStats { Name = "Other" };
    var untouched = other.SubStatOf("Other Spell", Labels.Dd);
    untouched.RecordHitTotal(200, false);
    var combined = new CombinedStats();
    combined.StatsList.Add(other);
    combined.Children[other.Name] = [other];

    var players = HitFrequencySeries.ForPlayers(selected, combined);
    CollectionAssert.AreEqual(new[] { "Solo" }, players.Keys.ToArray());
    Assert.IsFalse(untouched.HitFreqMaterialized, "opening one player's chart must not materialize other players' rows");
  }

  [TestMethod]
  public void PreparationOwnsASnapshotAndRefilteringDoesNotReadTheHistogramAgain()
  {
    var player = new PlayerStats { Name = "Caster" };
    var row = player.SubStatOf("Spell", Labels.Dd);
    row.RecordHitTotal(100, false);
    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    row.NonCritFreqValues[100] = 50;

    Assert.AreEqual(1, HitFrequencySeries.Filter(choice.NonCritical, 0).Single().Frequency);
    Assert.AreEqual(0, HitFrequencySeries.Filter(choice.NonCritical, 1).Count);
  }
}

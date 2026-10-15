namespace EQLogParser;

[TestClass]
public class CharmOwnerAttributionTest
{
  private const string Self = "Incogitable";
  private const string Pet = "A scalewrought terrastriker";

  [TestMethod]
  [DataRow("Esoteric Command", "is charmed.")]
  [DataRow("Esoteric Command Rk. II", "is charmed.")]
  [DataRow("Esoteric Command Rk. III", "is charmed.")]
  [DataRow("Edict of Command XL", "has been charmed.")]
  [DataRow("Beguile", "has been charmed.")]
  [DataRow("Allure", "has been charmed.")]
  [DataRow("Dominate Rk. II", "is charmed.")]
  public void ACharmCastAttributesDamageToItsCasterInSummaryAndMeter(string spell, string message)
  {
    var run = Parse(
      $"[Thu Jul 03 15:18:21 2025] You begin casting {spell}.",
      $"[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker {message}",
      "[Thu Jul 03 15:18:38 2025] A scalewrought terrastriker hits a scalewrought craftsman for 400 points of damage.",
      "[Thu Jul 03 15:18:39 2025] A scalewrought craftsman hits a scalewrought terrastriker for 700 points of damage.",
      "[Thu Jul 03 15:18:40 2025] You hit a scalewrought craftsman for 1000 points of damage.",
      $"[Thu Jul 03 15:25:29 2025] Your {spell} spell has worn off of a scalewrought terrastriker.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    var window = outcome.Charms.Single();
    Assert.AreEqual(Self, window.Owner, "the cast must supply ownership even when the wear-off lands past the cap");
    Assert.AreEqual(CharmEndReason.Cap, window.Reason);
    Assert.AreEqual(window.T0 + CharmWindowPolicy.MaxWindowS, window.T1);
    Assert.AreEqual(Self, run.Timeline.OwnerOf(Pet, window.T0 + 1));
    Assert.IsNull(run.Timeline.OwnerOf(Pet, window.T1), "ownership must end with the charm");

    var cache = new FightProjection.FightProjectionCache();
    var rows = cache.Project(run.Facts, run.Timeline);
    Sectionizer.StampGroupIds(rows);
    var summary = DerivedTotals.ForOverlay(rows, cache.Index, run.Facts,
      double.NegativeInfinity, double.PositiveInfinity);
    Assert.IsNotNull(summary?.DamageStats);
    var combined = summary.DamageStats;
    Assert.AreEqual(1400L, combined.StatsList.Single(row => row.Name == Self + " +Pets").Total);
    Assert.IsTrue(combined.Children.TryGetValue(Self + " +Pets", out var children));
    Assert.AreEqual(400L, children.Single(row => row.OrigName == Pet).Total);
    Assert.AreEqual(1000L, children.Single(row => row.OrigName == Self).Total);
    Assert.IsFalse(combined.StatsList.Any(row => row.Name == Pet), "the charm pet must not remain a standalone damage row");
    Assert.IsNotNull(summary.TankStats);
    Assert.AreEqual(700L, summary.TankStats.StatsList.Single(row => row.OrigName == Pet).Total);

    var calculation = new DamageMeterCalculation();
    var input = cache.Index.CaptureMeterInput(rows, run.Facts, double.NegativeInfinity, double.PositiveInfinity);
    DamageMeterAssert.Equal(summary, calculation.Update(input));
    DamageMeterAssert.Equal(summary, calculation.Update(input));
  }

  [TestMethod]
  public void ASharedCharmPetHasSeparateDamageUnderEachOwnersSummaryAndMeter()
  {
    var run = Parse(
      "[Thu Jul 03 15:18:21 2025] You begin casting Charm XVII.",
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker has been charmed.",
      "[Thu Jul 03 15:18:26 2025] A scalewrought terrastriker hits a scalewrought craftsman for 400 points of damage.",
      "[Thu Jul 03 15:18:30 2025] Teferi begins casting Charm XVII.",
      "[Thu Jul 03 15:18:33 2025] A scalewrought terrastriker hits a scalewrought craftsman for 100 points of damage.",
      "[Thu Jul 03 15:18:34 2025] a scalewrought terrastriker has been charmed.",
      "[Thu Jul 03 15:18:34 2025] A scalewrought terrastriker hits a scalewrought craftsman for 800 points of damage.",
      "[Thu Jul 03 15:18:40 2025] You begin casting Charm XVII.",
      "[Thu Jul 03 15:18:41 2025] A scalewrought terrastriker hits a scalewrought craftsman for 200 points of damage.",
      "[Thu Jul 03 15:18:44 2025] a scalewrought terrastriker has been charmed.",
      "[Thu Jul 03 15:18:44 2025] A scalewrought terrastriker hits a scalewrought craftsman for 600 points of damage.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    var window = outcome.Charms.Single();
    Assert.HasCount(3, window.Segments);
    CollectionAssert.AreEqual(new[] { Self, "Teferi", Self }, window.Segments.Select(s => s.Owner).ToArray());

    var cache = new FightProjection.FightProjectionCache();
    var rows = cache.Project(run.Facts, run.Timeline);
    Sectionizer.StampGroupIds(rows);
    var summary = DerivedTotals.ForOverlay(rows, cache.Index, run.Facts,
      double.NegativeInfinity, double.PositiveInfinity);
    Assert.IsNotNull(summary?.DamageStats);
    var damage = summary.DamageStats;
    Assert.AreEqual(2100L, damage.RaidStats.Total, "a hand-off must neither drop nor duplicate damage");
    Assert.AreEqual(1100L, damage.StatsList.Single(row => row.Name == Self + " +Pets").Total);
    Assert.AreEqual(1000L, damage.StatsList.Single(row => row.Name == "Teferi +Pets").Total);
    var firstPet = damage.Children[Self + " +Pets"].Single(row => row.OrigName == Pet);
    var secondPet = damage.Children["Teferi +Pets"].Single(row => row.OrigName == Pet);
    Assert.AreNotSame(firstPet, secondPet);
    Assert.AreEqual(1100L, firstPet.Total);
    Assert.AreEqual(1000L, secondPet.Total);
    Assert.AreEqual(1100L, firstPet.SubStats.Single().Total);
    Assert.AreEqual(1000L, secondPet.SubStats.Single().Total);
    Assert.AreEqual(100f, firstPet.Percent);
    Assert.AreEqual(100f, secondPet.Percent);
    Assert.AreEqual(2100L, damage.ExpandedStatsList.Sum(row => row.Total));
    Assert.IsFalse(damage.StatsList.Any(row => row.OrigName == Pet));

    var meter = new DamageMeterCalculation();
    var input = cache.Index.CaptureMeterInput(rows, run.Facts, double.NegativeInfinity, double.PositiveInfinity);
    DamageMeterAssert.Equal(summary, meter.Update(input));
    foreach (var segment in window.Segments)
    {
      var scoped = DerivedTotals.ForOverlay(rows, cache.Index, run.Facts, segment.FromS, segment.ToS - 1);
      Assert.IsNotNull(scoped?.DamageStats);
      Assert.HasCount(1, scoped.DamageStats.StatsList);
      Assert.AreEqual(segment.Owner + " +Pets", scoped.DamageStats.StatsList.Single().Name);
      var scopedInput = cache.Index.CaptureMeterInput(rows, run.Facts, segment.FromS, segment.ToS - 1);
      DamageMeterAssert.Equal(scoped, meter.Update(scopedInput));
    }
  }

  [TestMethod]
  [DataRow("Servant of Nature XIII", "becomes a servant of nature.")]
  [DataRow("Dire Charm I", "becomes a servant of nature.")]
  [DataRow("Dire Charm I", "has been bound by an unbreakable pact.")]
  public void PactAndNatureMessagesAttributeCharmDamageAndTanking(string spell, string message)
  {
    var run = Parse(
      $"[Thu Jul 03 15:18:21 2025] You begin casting {spell}.",
      $"[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker {message}",
      "[Thu Jul 03 15:18:38 2025] A scalewrought terrastriker hits a scalewrought craftsman for 400 points of damage.",
      "[Thu Jul 03 15:18:39 2025] A scalewrought craftsman hits a scalewrought terrastriker for 700 points of damage.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.HasCount(1, outcome.Charms);
    var window = outcome.Charms.Single();
    Assert.AreEqual(Pet, window.Name);
    Assert.AreEqual(Self, window.Owner);
    Assert.AreEqual(window.T0 + CharmWindowPolicy.MaxWindowS, window.T1, "new messages retain the existing cap");
    Assert.IsNull(run.Timeline.OwnerOf(Pet, window.T1));

    var cache = new FightProjection.FightProjectionCache();
    var rows = cache.Project(run.Facts, run.Timeline);
    Sectionizer.StampGroupIds(rows);
    var summary = DerivedTotals.ForOverlay(rows, cache.Index, run.Facts,
      double.NegativeInfinity, double.PositiveInfinity);
    Assert.IsNotNull(summary?.DamageStats);
    Assert.AreEqual(400L, summary.DamageStats.StatsList.Single(row => row.Name == Self + " +Pets").Total);
    Assert.AreEqual(400L, summary.DamageStats.Children[Self + " +Pets"].Single(row => row.Name == Pet).Total);
    Assert.IsNotNull(summary.TankStats);
    Assert.AreEqual(700L, summary.TankStats.StatsList.Single(row => row.Name == Pet).Total);
    var input = cache.Index.CaptureMeterInput(rows, run.Facts, double.NegativeInfinity, double.PositiveInfinity);
    DamageMeterAssert.Equal(summary, new DamageMeterCalculation().Update(input));
  }

  [TestMethod]
  [DataRow("becomes a servant of nature.")]
  [DataRow("has been bound by an unbreakable pact.")]
  public void ANewCharmMessageWithoutACastCreatesNoWindow(string message)
  {
    var run = Parse($"[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker {message}");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.HasCount(0, outcome.Charms);
  }

  [TestMethod]
  [DataRow("Esoteric Command")]
  [DataRow("Edict of Command XL")]
  public void AWearOffWithoutACorroboratedStartDoesNotInventAWindow(string spell)
  {
    var run = Parse(
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker is charmed.",
      "[Thu Jul 03 15:18:38 2025] A scalewrought terrastriker hits a scalewrought craftsman for 400 points of damage.",
      $"[Thu Jul 03 15:18:45 2025] Your {spell} spell has worn off of a scalewrought terrastriker.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.HasCount(0, outcome.Charms);
    Assert.IsNull(run.Timeline.OwnerOf(Pet, double.PositiveInfinity));
  }

  [TestMethod]
  public void TheLastMatchingCastWinsButALaterUnrelatedSpellDoesNotStealOwnership()
  {
    var run = Parse(
      "[Thu Jul 03 15:18:19 2025] Firstcaster begins casting Allure.",
      "[Thu Jul 03 15:18:21 2025] Incogitable begins casting Esoteric Command.",
      "[Thu Jul 03 15:18:23 2025] Othercaster begins casting Shrink.",
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker is charmed.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.AreEqual(Self, outcome.Charms.Single().Owner);
  }

  [TestMethod]
  [DataRow("Word of Terris", "Lifeshadow", "moans.", false)]
  [DataRow("Lifeshadow", "Word of Terris", "moans.", true)]
  [DataRow("Charm XVII", "Mistmoore Charm", "has been charmed.", false)]
  [DataRow("Mistmoore Charm", "Charm XVII", "has been charmed.", true)]
  public void TheNewestMatchingSpellDecidesAndANonCharmDoesNotFallBack(
    string first, string last, string message, bool expectedCharm)
  {
    var run = Parse(
      $"[Thu Jul 03 15:18:21 2025] Firstcaster begins casting {first}.",
      $"[Thu Jul 03 15:18:23 2025] Lastcaster begins casting {last}.",
      $"[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker {message}");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.HasCount(expectedCharm ? 1 : 0, outcome.Charms);
    if (expectedCharm) Assert.AreEqual("Lastcaster", outcome.Charms.Single().Owner);
    else Assert.IsFalse(run.Facts.Evidence.ToArray().Any(e => e.Kind == EvidenceFact.EvCharmStart));
  }

  [TestMethod]
  public void AnInterruptedCharmAndASameSecondCastAfterTheLandingCannotConfirmIt()
  {
    var run = Parse(
      "[Thu Jul 03 15:18:21 2025] You begin casting Word of Terris.",
      "[Thu Jul 03 15:18:23 2025] Your Word of Terris spell is interrupted.",
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker moans.",
      "[Thu Jul 03 15:18:25 2025] You begin casting Word of Terris.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.HasCount(0, outcome.Charms);
  }

  [TestMethod]
  public void SameSecondRecharmsKeepTheLastConfirmedOwnerInFileOrder()
  {
    var lines = new List<string>();
    for (var i = 0; i < 24; i++)
    {
      lines.Add($"[Thu Jul 03 15:18:21 2025] Caster{i} begins casting Charm XVII.");
      lines.Add("[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker has been charmed.");
    }
    var run = Parse([.. lines]);
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    var window = outcome.Charms.Single();
    Assert.AreEqual(24, window.Starts);
    Assert.AreEqual("Caster23", run.Timeline.OwnerOf(Pet, window.T0 + 1));
  }

  [TestMethod]
  [DataRow(6, true)]
  [DataRow(7, false)]
  public void TheOwnerCastLookbackIsSixSeconds(int seconds, bool qualifies)
  {
    var run = Parse(
      $"[Thu Jul 03 15:18:{25 - seconds:D2} 2025] You begin casting Esoteric Command.",
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker is charmed.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.HasCount(qualifies ? 1 : 0, outcome.Charms);
    if (qualifies) Assert.AreEqual(Self, outcome.Charms.Single().Owner);
  }

  [TestMethod]
  [DataRow("edict of command xl", true)]
  [DataRow("Edict of Command XLIII", true)]
  [DataRow("Esoteric Command Rk. III", true)]
  [DataRow("Esoteric Command Chance", false)]
  [DataRow("Esoteric Command Chance II", false)]
  [DataRow("Esoteric Command Stun", false)]
  [DataRow("Charm IV", false)]
  [DataRow("Compulsion IV", false)]
  [DataRow("Esoteric Command IV", false)]
  [DataRow("Esoteric Command Rk. IV", false)]
  [DataRow("Edict of Command XL Extra", false)]
  [DataRow("Edict of Commanding", false)]
  public void CharmSpellNamesMustExistInTheDataWithACharmMessage(string spell, bool expected)
  {
    PipelineHarness.EnsureDataStore();
    Assert.AreEqual(expected, CharmSpells.IsCharmSpellName(spell));
  }

  private static PipelineHarness.DeriveRunResult Parse(params string[] lines)
  {
    var folder = Path.Combine(Path.GetTempPath(), "charm-owner-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
      var path = Path.Combine(folder, "eqlog_Incogitable_xegony.txt");
      File.WriteAllLines(path, lines);
      return PipelineHarness.RunFileDerived(path);
    }
    finally
    {
      Directory.Delete(folder, true);
    }
  }
}

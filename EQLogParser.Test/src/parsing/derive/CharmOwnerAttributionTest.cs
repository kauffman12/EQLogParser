namespace EQLogParser;

[TestClass]
public class CharmOwnerAttributionTest
{
  private const string Self = "Incogitable";
  private const string Pet = "A scalewrought terrastriker";

  [TestMethod]
  [DataRow("Esoteric Command")]
  [DataRow("Esoteric Command Rk. II")]
  [DataRow("Esoteric Command Rk. III")]
  [DataRow("Edict of Command XL")]
  [DataRow("Beguile")]
  [DataRow("Allure")]
  [DataRow("Dominate Rk. II")]
  public void ACharmCastAttributesDamageToItsCasterInSummaryAndMeter(string spell)
  {
    var run = Parse(
      $"[Thu Jul 03 15:18:21 2025] You begin casting {spell}.",
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker is charmed.",
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
  [DataRow("Esoteric Command")]
  [DataRow("Edict of Command XL")]
  public void ACommandWearOffCanNameTheOwnerWithoutACastLine(string spell)
  {
    var run = Parse(
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker is charmed.",
      "[Thu Jul 03 15:18:38 2025] A scalewrought terrastriker hits a scalewrought craftsman for 400 points of damage.",
      $"[Thu Jul 03 15:18:45 2025] Your {spell} spell has worn off of a scalewrought terrastriker.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    var window = outcome.Charms.Single();
    Assert.AreEqual(CharmEndReason.WearOff, window.Reason);
    Assert.AreEqual(Self, run.Timeline.OwnerOf(Pet, window.T0 + 1));
    Assert.IsNull(run.Timeline.OwnerOf(Pet, window.T1));
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
  [DataRow(6, true)]
  [DataRow(7, false)]
  public void TheOwnerCastLookbackIsSixSeconds(int seconds, bool qualifies)
  {
    var run = Parse(
      $"[Thu Jul 03 15:18:{25 - seconds:D2} 2025] You begin casting Esoteric Command.",
      "[Thu Jul 03 15:18:25 2025] a scalewrought terrastriker is charmed.");
    var outcome = ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
    Assert.AreEqual(qualifies ? Self : null, outcome.Charms.Single().Owner);
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

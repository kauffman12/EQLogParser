#nullable enable annotations

namespace EQLogParser;

// The regression lived beyond parser events: facts rematerialize records before either real builder counts them.
[TestClass]
public class StruckThroughBoardTest
{
  private const string Tank = "Testtank";
  private const string Mob = "A Solusek knight";
  private const string Pierced = "A Solusek knight tries to slash Testtank, but Testtank ripostes! (Strikethrough)";
  private const string Landed = "A Solusek knight slashes Testtank for 56110 points of damage. (Riposte Strikethrough)";

  [TestMethod]
  public void ReportedThreeLinesReachTheRealTankingBuilderAsOneSwing()
  {
    var (run, rows, index) = Parse(
      (18, "A Solusek knight tries to slash YOU, but YOU riposte! (Strikethrough)"),
      (18, "A Solusek knight is pierced by YOUR thorns for 738803 points of non-melee damage."),
      (18, "A Solusek knight slashes YOU for 56110 points of damage. (Riposte Strikethrough)"));
    var result = Build(run, rows, index);
    var tank = result.TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 1, 0, 1, 1, 56110);
    Assert.AreEqual(0L, tank.TotalRiposte);
    Assert.AreEqual(1L, tank.SubStats.Sum(s => (long)s.MeleeAttempts));
    Assert.AreEqual(1L, tank.SubStats.Sum(s => (long)s.StrikethroughHits));
    Assert.AreEqual(0L, tank.SubStats.Sum(s => (long)s.RiposteHits));
    Assert.AreEqual(3, run.Facts.FactCount, "the shield line and both halves of the incoming swing remain captured");
    var again = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(again, 1, 0, 1, 1, 56110);
  }

  [TestMethod]
  public void AProtectedRiposteAndTwoReturnBlowsStillCountOnTheirOwnBoards()
  {
    var (run, rows, index) = Parse(
      (18, "A Solusek knight tries to hit YOU, but YOU riposte!"),
      (18, "You hit a Solusek knight for 51718 points of physical damage by Ferocious Kick XIX."),
      (18, "You kick a Solusek knight for 25159 points of damage. (Riposte Critical)"),
      (18, "You crush a Solusek knight for 329802 points of damage. (Riposte Critical)"));
    var result = Build(run, rows, index);
    AssertRow(result.TankStats!.StatsList.Single(s => s.Name == Tank), 1, 1, 0, 0, 0);
    var outgoing = result.DamageStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(outgoing, 2, 2, 0, 2, 406679);
    Assert.AreEqual(354961L, outgoing.TotalRiposte);
  }

  [TestMethod]
  public void ATagTheSentenceAlreadySpentIsNotCountedAgainOnItsNumberLine()
  {
    var (run, rows, index) = Parse(
      (18, "A Solusek knight tries to slash Testtank, but Testtank ripostes! (Strikethrough Flurry)"),
      (18, "A Solusek knight slashes Testtank for 56110 points of damage. (Riposte Strikethrough Flurry)"));
    var tank = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 1, 0, 1, 1, 56110);
    Assert.AreEqual(1u, tank.FlurryHits, "one swing carries one Flurry");
  }

  [TestMethod]
  public void AnUnmatchedPiercedSentenceKeepsItsAttemptButNotASuccessfulDefense()
  {
    var (run, rows, index) = Parse((18, Pierced), (18, Pierced));
    var tank = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 2, 0, 2, 0, 0);
    Assert.AreEqual(0u, tank.MeleeUndefended);
  }

  [TestMethod]
  public void AHitWithoutASentenceCountsNormally()
  {
    var (run, rows, index) = Parse((18, Landed));
    AssertRow(Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 1, 56110);
  }

  [TestMethod]
  public void BeingHitByTheNpcsReturnBlowIsNotTheTankSuccessfullyRiposting()
  {
    var (run, rows, index) = Parse(
      (18, "Testtank tries to slash A Solusek knight, but A Solusek knight ripostes!"),
      (18, "A Solusek knight slashes Testtank for 56110 points of damage. (Riposte)"));
    var tank = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 1, 0, 0, 1, 56110);
    Assert.AreEqual(56110L, tank.TotalRiposte, "the incoming damage category remains available");
    Assert.AreEqual(0L, tank.SubStats.Sum(s => (long)s.RiposteHits));
  }

  [TestMethod]
  public void APlainBlockFollowedByDamageIsNotOneSwing()
  {
    var (run, rows, index) = Parse(
      (18, "A Solusek knight tries to slash Testtank, but Testtank blocks!"), (18, Landed));
    var tank = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 2, 0, 1, 1, 56110);
    Assert.AreEqual(1u, tank.Blocks);
  }

  [TestMethod]
  public void APlainHitCannotAnswerAPiercedRiposte()
  {
    var (run, rows, index) = Parse((18, Pierced),
      (18, "A Solusek knight slashes Testtank for 56110 points of damage."));
    AssertRow(Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank), 2, 0, 1, 1, 56110);
  }

  [TestMethod]
  public void AOneSecondPairAndEachOfItsTimeSlicesHaveTheirOwnAttempt()
  {
    var (run, rows, index) = Parse((18, Pierced), (19, Landed));
    var start = (double)run.Facts.At(0).TimeS;
    AssertRow(Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 1, 56110);
    var hitOnly = DerivedTotals.ForOverlay(rows, index, run.Facts, start + 1, start + 1)!;
    AssertRow(hitOnly.TankStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 1, 56110);
    var sentenceOnly = DerivedTotals.ForOverlay(rows, index, run.Facts, start, start)!;
    AssertRow(sentenceOnly.TankStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 0, 0);
    AssertRow(Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 1, 56110);
  }

  [TestMethod]
  public void TheSummaryTimeSliderCountsTheHitWhenItExcludesTheEarlierSentence()
  {
    var (run, rows, index) = Parse((18, Pierced), (19, Landed));
    var input = FightSummarySource.Build(rows, index, run.Facts);
    var options = new GenerateStatsOptions
    {
      Source = "riposte time-slider test", AllRanges = input.AllRanges, MinSeconds = 1, CollectHitTotals = false
    };
    options.Npcs.AddRange(input.Fights);
    var builder = new TankingStatsBuilder();
    CombinedStats? result = null;
    builder.EventsGenerationStatus += e => result = e.CombinedStats;
    builder.BuildTotalStats(options);
    Assert.IsNotNull(result);
    AssertRow(result.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 1, 56110);
  }

  [TestMethod]
  public void UndefendedCountsUnstruckHitsEvenWhenUnmatchedSentencesOutnumberThem()
  {
    // Two pierced sentences with no number line, one plain hit, one struck hit: 3 strikethroughs against 2 hits, which a
    // hits-minus-strikethroughs subtraction floors to zero. The plain hit is still one undefended hit.
    var (run, rows, index) = Parse((18, Pierced), (18, Pierced),
      (18, "A Solusek knight slashes Testtank for 1000 points of damage."), (19, Landed));
    var tank = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 4, 0, 3, 2, 57110);
    Assert.AreEqual(1u, tank.MeleeUndefended);
    Assert.AreEqual(1L, tank.SubStats.Sum(s => (long)s.MeleeUndefended), "the breakdown's child rows add up to the row");
  }

  [TestMethod]
  public void OutgoingFailedRiposteUsesTheSameAccountingWithoutChangingSuccessfulReturnHits()
  {
    var (run, rows, index) = Parse(
      (18, "Testtank tries to slash A Solusek knight, but A Solusek knight ripostes! (Strikethrough)"),
      (18, "Testtank slashes A Solusek knight for 56110 points of damage. (Riposte Strikethrough)"));
    AssertRow(Build(run, rows, index).DamageStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 1, 56110);
  }

  private static (PipelineHarness.DeriveRunResult Run, IReadOnlyList<DerivedFight> Rows, FightFactIndex Index)
    Parse(params (int Second, string Action)[] actions)
  {
    var path = Path.Combine(Path.GetTempPath(), $"eqlog_{Tank}_review-{Guid.NewGuid():N}.txt");
    try
    {
      File.WriteAllLines(path, actions.Select(a => $"[Sun Nov 08 02:28:{a.Second:D2} 2026] {a.Action}"));
      var run = PipelineHarness.RunFileDerived(path);
      run.Timeline.SetIdentity(Tank, IdentityKind.Player, RuleStrength.Manual, "manual");
      run.Timeline.SetIdentity(Mob, IdentityKind.Npc, RuleStrength.Manual, "manual");
      ClassificationRules.Apply(run.Facts, run.Timeline);
      var cache = new FightProjection.FightProjectionCache();
      var rows = cache.Project(run.Facts, run.Timeline);
      Sectionizer.StampGroupIds(rows);
      return (run, rows, cache.Index);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static DamageOverlayStats Build(PipelineHarness.DeriveRunResult run,
    IReadOnlyList<DerivedFight> rows, FightFactIndex index) =>
    DerivedTotals.ForOverlay(rows, index, run.Facts, double.NegativeInfinity, double.PositiveInfinity)!;

  private static void AssertRow(PlayerSubStats row, uint attempts, uint ripostes, uint strikes, uint hits, long total)
  {
    Assert.AreEqual(attempts, row.MeleeAttempts, "attempts");
    Assert.AreEqual(ripostes, row.RiposteHits, "ripostes");
    Assert.AreEqual(strikes, row.StrikethroughHits, "strikethroughs");
    Assert.AreEqual(hits, row.MeleeHits, "landed melee hits");
    Assert.AreEqual(total, row.Total, "damage");
  }
}

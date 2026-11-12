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
  private const string Absorbed = "A Solusek knight tries to slash Testtank, but Testtank's magical skin"
    + " absorbs the blow! (Riposte Strikethrough)";

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

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void BeimeithsKeltakunExampleCountsNineteenSwingsOnEitherBoard(bool outgoing)
  {
    // Public hand count, with the missing date expanded in the fixture; names are substituted only to reverse direction.
    // https://forums.everquest.com/index.php?threads/raid-boss-strikethrough-or-not.280822/page-2#post-4104775
    var attacker = outgoing ? Tank : Mob;
    var defender = outgoing ? Mob : Tank;
    var actions = File.ReadAllLines(BoardGolden.FixturePath("keltakun-swings.txt")).Select(line =>
      (int.Parse(line.AsSpan(18, 2)), line[(line.IndexOf(']') + 2)..]
        .Replace("Keltakun, Last Word", attacker, StringComparison.Ordinal)
        .Replace("YOUR", defender + "'s", StringComparison.Ordinal)
        .Replace("YOU", defender, StringComparison.Ordinal)
        .Replace("You ", defender + " ", StringComparison.Ordinal))).ToArray();
    var (run, rows, index) = Parse(actions);
    var result = Build(run, rows, index);
    var stats = outgoing ? result.DamageStats : result.TankStats;
    var row = stats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(row, 19, 3, 14, 13, 456129);
    Assert.AreEqual(1u, row.Parries);
    Assert.AreEqual(1u, row.Misses);
    Assert.AreEqual(1u, row.Absorbs);
    Assert.AreEqual(0u, row.DamageAbsorbs);
    Assert.AreEqual(13u, row.Hits);
    Assert.AreEqual(73.68f, row.MeleeHitRate, "14 connections / 19 swings, including the rune-eaten blow");
    Assert.AreEqual(12, Enumerable.Range(0, run.Facts.FactCount)
      .Count(i => run.Facts.At(i).TypeId == LabelTypes.Riposte));
    Assert.AreEqual(9, Enumerable.Range(0, run.Facts.FactCount).Count(i =>
      run.Facts.At(i).TypeId == LabelTypes.Riposte
      && LineModifiersParser.IsStrikethrough(run.Facts.At(i).ModMask)));
    Assert.AreEqual(14, Enumerable.Range(0, run.Facts.FactCount)
      .Count(i => run.Facts.At(i).TypeId == LabelTypes.Ds));
    Assert.AreEqual(48, run.Facts.FactCount, "all damage/outcome lines remain captured");
    var opposite = outgoing ? result.TankStats : result.DamageStats;
    var returns = opposite!.StatsList.Single(s => s.Name == Tank);
    Assert.AreEqual(6u, returns.MeleeHits);
    Assert.AreEqual(6u, returns.MeleeAttempts, "thorns do not add melee swings");
    Assert.AreEqual(470607L, returns.Total, "return damage plus thorns stays unchanged");
    var again = Build(run, rows, index);
    AssertRow((outgoing ? again.DamageStats : again.TankStats)!.StatsList.Single(s => s.Name == Tank),
      19, 3, 14, 13, 456129);
  }

  [TestMethod]
  public void AnAbsorbedDamageShieldTickSurvivesFactsAndCodecsWithoutBecomingAMeleeAttempt()
  {
    var (run, rows, index) = Parse(
      (18, Pierced),
      (18, "Testtank's magical skin absorbs the damage of A Solusek knight's thorns."),
      (18, Absorbed),
      (19, Landed));
    var tank = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(tank, 2, 0, 2, 1, 56110);
    Assert.AreEqual(1u, tank.Absorbs);
    Assert.AreEqual(1u, tank.DamageAbsorbs);
    Assert.AreEqual(100f, tank.MeleeHitRate);
    Assert.AreEqual(1u, tank.Hits);
    Assert.AreEqual(1L, tank.SubStats.Sum(s => (long)s.DamageAbsorbs));
    var captured = Enumerable.Range(0, run.Facts.FactCount).Select(run.Facts.At).ToArray();
    var decoded = DamageFactChunkCodec.Decode(DamageFactChunkCodec.Encode(captured, captured.Length), captured.Length);
    Assert.AreEqual(LabelTypes.DamageAbsorb, decoded[1].TypeId);
    Assert.AreEqual(Labels.DamageAbsorb, LabelTypes.LabelOf(decoded[1].TypeId));
    Assert.IsFalse(LabelTypes.IsHit(decoded[1].TypeId));
  }

  [TestMethod]
  public void ATaggedAbsorbAndEachSelectedHalfHaveTheirOwnScopedAttempt()
  {
    var (run, rows, index) = Parse((18, Pierced),
      (19, Absorbed));
    var start = (double)run.Facts.At(0).TimeS;
    var both = Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(both, 1, 0, 1, 0, 0);
    Assert.AreEqual(1u, both.Absorbs);
    Assert.AreEqual(100f, both.MeleeHitRate);
    var absorbOnly = DerivedTotals.ForOverlay(rows, index, run.Facts, start + 1, start + 1)!
      .TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(absorbOnly, 1, 0, 1, 0, 0);
    Assert.AreEqual(1u, absorbOnly.Absorbs);
    var sentenceOnly = DerivedTotals.ForOverlay(rows, index, run.Facts, start, start)!
      .TankStats!.StatsList.Single(s => s.Name == Tank);
    AssertRow(sentenceOnly, 1, 0, 1, 0, 0);
    Assert.AreEqual(0u, sentenceOnly.Absorbs);
    Assert.AreEqual(0f, sentenceOnly.MeleeHitRate);
    var input = FightSummarySource.Build(rows, index, run.Facts);
    var options = new GenerateStatsOptions
    {
      Source = "absorb time-slider test", AllRanges = input.AllRanges, MinSeconds = 1, CollectHitTotals = false
    };
    options.Npcs.AddRange(input.Fights);
    CombinedStats? result = null;
    var builder = new TankingStatsBuilder();
    builder.EventsGenerationStatus += e => result = e.CombinedStats;
    builder.BuildTotalStats(options);
    AssertRow(result!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 0, 0);
    Assert.AreEqual(1u, result.StatsList.Single(s => s.Name == Tank).Absorbs);
    AssertRow(Build(run, rows, index).TankStats!.StatsList.Single(s => s.Name == Tank), 1, 0, 1, 0, 0);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AbsorbsStayOutOfDistributionsAndTankingFiltersKeepTheirOwnOutcomes(bool outgoing)
  {
    var attacker = outgoing ? Tank : Mob;
    var defender = outgoing ? Mob : Tank;
    var (run, rows, index) = Parse(
      (18, $"{attacker} tries to slash {defender}, but {defender} ripostes! (Strikethrough)"),
      (18, $"{defender}'s magical skin absorbs the damage of {attacker}'s thorns."),
      (18, $"{attacker} tries to slash {defender}, but {defender}'s magical skin"
        + " absorbs the blow! (Riposte Strikethrough)"),
      (19, $"{attacker} slashes {defender} for 500 points of damage. (Critical)"),
      (20, $"{attacker} tries to slash {defender}, but misses!"));
    var input = FightSummarySource.Build(rows, index, run.Facts);
    foreach (var type in outgoing ? new[] { 0 } : [0, 1, 2])
    {
      var options = new GenerateStatsOptions
      {
        Source = "absorb histogram and filter test", AllRanges = input.AllRanges, DamageType = type
      };
      options.Npcs.AddRange(input.Fights);
      CombinedStats? result = null;
      if (outgoing)
      {
        var builder = new DamageStatsBuilder();
        builder.EventsGenerationStatus += e => result = e.CombinedStats;
        builder.BuildTotalStats(options);
      }
      else
      {
        var builder = new TankingStatsBuilder();
        builder.EventsGenerationStatus += e => result = e.CombinedStats;
        builder.BuildTotalStats(options);
      }
      var row = result!.StatsList.Single(s => s.Name == Tank);
      Assert.AreEqual(type == 1 ? 0u : 1u, row.DamageAbsorbs);
      if (type == 2)
      {
        AssertRow(row, 0, 0, 0, 0, 0);
        Assert.AreEqual(0u, row.Absorbs);
        Assert.AreEqual(0f, row.MeleeHitRate);
        continue;
      }
      AssertRow(row, 3, 0, 1, 1, 500);
      Assert.AreEqual(1u, row.Absorbs);
      Assert.AreEqual(1u, row.Hits);
      Assert.AreEqual(500L, row.Avg);
      Assert.AreEqual(500L, row.Min);
      Assert.AreEqual(500L, row.Max);
      Assert.AreEqual(1u, row.CritHits);
      Assert.AreEqual(100f, row.CritRate);
      Assert.AreEqual(66.67f, row.MeleeHitRate);
      Assert.AreEqual(1, row.SubStats.Sum(s => s.RawHitTotals));
      Assert.AreEqual(1, row.SubStats.Sum(s => s.CritFreqValues.Values.Sum()));
      Assert.AreEqual(0, row.SubStats.Sum(s => s.NonCritFreqValues.Values.Sum()));
      Assert.AreEqual(1, row.SubStats.Single(s => s.CritFreqValues.Count > 0).CritFreqValues[500]);
    }
  }

  [TestMethod]
  [DataRow(false, "parries!")]
  [DataRow(true, "parries!")]
  [DataRow(false, "blocks!")]
  [DataRow(true, "blocks!")]
  [DataRow(false, "dodges!")]
  [DataRow(true, "dodges!")]
  [DataRow(false, "is INVULNERABLE!")]
  [DataRow(true, "is INVULNERABLE!")]
  public void ADefenseAndItsAbsorbExcludeOneSwingFromAccuracy(bool outgoing, string defense)
  {
    // Synthetic supported grammar: prove bookkeeping, not an unmeasured claim about which defenses EQ prints together.
    var attacker = outgoing ? Tank : Mob;
    var defender = outgoing ? Mob : Tank;
    var (run, rows, index) = Parse(
      (18, $"{attacker} tries to slash {defender}, but {defender} {defense} (Strikethrough)"),
      (19, $"{attacker} tries to slash {defender}, but {defender}'s magical skin"
        + " absorbs the blow! (Strikethrough)"),
      (20, $"{attacker} slashes {defender} for 100 points of damage."),
      (21, $"{attacker} slashes {defender} for 100 points of damage."));
    var result = Build(run, rows, index);
    var row = (outgoing ? result.DamageStats : result.TankStats)!.StatsList.Single(s => s.Name == Tank);
    AssertRow(row, 3, 0, 1, 2, 200);
    Assert.AreEqual(1u, row.Absorbs);
    Assert.AreEqual(1u, row.Parries + row.Blocks + row.Dodges + row.Invulnerable);
    Assert.AreEqual(100f, row.MeleeAccRate, "the paired defense and absorb must not exclude two attempts");
    Assert.AreEqual(100f, row.MeleeHitRate);
    Assert.AreEqual(1u, row.AbsorbExclusionOverlaps);
    Assert.AreEqual(100f, row.SubStats.Single().MeleeAccRate);
    Assert.AreEqual(1u, row.SubStats.Single().AbsorbExclusionOverlaps);

    var start = (double)run.Facts.At(0).TimeS;
    var absorbOnly = DerivedTotals.ForOverlay(rows, index, run.Facts, start + 1, start + 1)!;
    var isolated = (outgoing ? absorbOnly.DamageStats : absorbOnly.TankStats)!.StatsList.Single(s => s.Name == Tank);
    AssertRow(isolated, 1, 0, 1, 0, 0);
    Assert.AreEqual(1u, isolated.Absorbs);
    Assert.AreEqual(100f, isolated.MeleeHitRate);
    Assert.AreEqual(0u, isolated.AbsorbExclusionOverlaps);
    var sentenceOnly = DerivedTotals.ForOverlay(rows, index, run.Facts, start, start)!;
    var sentence = (outgoing ? sentenceOnly.DamageStats : sentenceOnly.TankStats)!
      .StatsList.Single(s => s.Name == Tank);
    AssertRow(sentence, 1, 0, 1, 0, 0);
    Assert.AreEqual(0u, sentence.AbsorbExclusionOverlaps);

    var remaining = DerivedTotals.ForOverlay(rows, index, run.Facts, start + 1, start + 3)!;
    var unpaired = (outgoing ? remaining.DamageStats : remaining.TankStats)!.StatsList.Single(s => s.Name == Tank);
    AssertRow(unpaired, 3, 0, 1, 2, 200);
    Assert.AreEqual(100f, unpaired.MeleeAccRate);
    Assert.AreEqual(0u, unpaired.AbsorbExclusionOverlaps);
    var again = Build(run, rows, index);
    Assert.AreEqual(100f, (outgoing ? again.DamageStats : again.TankStats)!
      .StatsList.Single(s => s.Name == Tank).MeleeAccRate);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AnUnmatchedPiercedRiposteStaysInTheLegacyAccuracyDenominator(bool outgoing)
  {
    var attacker = outgoing ? Tank : Mob;
    var defender = outgoing ? Mob : Tank;
    var (run, rows, index) = Parse(
      (18, $"{attacker} tries to slash {defender}, but {defender} ripostes! (Strikethrough)"),
      (19, $"{attacker} slashes {defender} for 100 points of damage."),
      (20, $"{attacker} tries to slash {defender}, but misses!"));
    var result = Build(run, rows, index);
    var row = (outgoing ? result.DamageStats : result.TankStats)!.StatsList.Single(s => s.Name == Tank);
    AssertRow(row, 3, 0, 1, 1, 100);
    Assert.AreEqual(1u, row.Misses);
    Assert.AreEqual(0u, row.AbsorbExclusionOverlaps);
    Assert.AreEqual(33.33f, row.MeleeAccRate, "accuracy is not reconstructed from Hits + Misses + Ripostes");
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

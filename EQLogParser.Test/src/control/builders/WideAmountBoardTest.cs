#nullable enable annotations

namespace EQLogParser;

[TestClass]
public class WideAmountBoardTest
{
  [TestMethod]
  [DataRow(true)]
  [DataRow(false)]
  public void ClassifiedWideHitsReachOnlyTheCorrectBoard(bool incoming)
  {
    var path = Path.Combine(Path.GetTempPath(), $"wide-board-{Guid.NewGuid():N}.txt");
    var previousName = ConfigUtil.PlayerName;
    try
    {
      ConfigUtil.PlayerName = "Kilsef";
      var lines = new List<string>
      {
        "[Sun Oct 11 18:45:17 2026] Kilsef has joined the raid.",
        "[Sun Oct 11 18:45:17 2026] Reisil has joined the raid."
      };
      foreach (var amount in new[] { (long)uint.MaxValue, 5_309_597_698L, TextUtils.MaxAmount })
      {
        lines.Add("[Sun Oct 11 18:45:18 2026] " + (incoming
          ? $"Xanzerok hit Kilsef for {amount} points of unresistable damage by Ironbark Hurl."
          : $"Kilsef hit Xanzerok for {amount} points of magic damage by Vexfire of the Null Spire."));
      }
      lines.Add($"[Sun Oct 11 18:45:18 2026] Reisil healed Kilsef for {TextUtils.MaxAmount - 1} ({TextUtils.MaxAmount}) hit points by Greater Renewal.");
      File.WriteAllLines(path, lines);
      var run = PipelineHarness.RunFileDerived(path);
      ClassificationRules.Apply(run.Facts, run.Timeline, run.HealFacts);
      var index = new FightFactIndex(run.Timeline);
      var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
      Sectionizer.StampGroupIds(rows);
      var input = FightSummarySource.Build(rows, index, run.Facts);
      Assert.AreEqual(3, run.Facts.FactCount);
      Assert.AreEqual(1, input.Fights.Count);
      var options = new GenerateStatsOptions
      {
        Source = "wide amount board test", AllRanges = input.AllRanges,
        Heals = HealSummarySource.Materialize(run.HealFacts, input.AllRanges)
      };
      options.Npcs.AddRange(input.Fights);
      var expected = uint.MaxValue + 5_309_597_698L + TextUtils.MaxAmount;
      if (!incoming)
      {
        var spell = input.Fights.Single().DdDamage.Values.Single();
        Assert.AreEqual(expected, spell.Total);
        Assert.AreEqual(TextUtils.MaxAmount, spell.Max);
        Assert.AreEqual(3u, spell.Count);
      }
      DamageStatsBuilder.Instance.BuildTotalStats(options);
      var tank = BuildTank(options);
      var damage = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
      var board = incoming ? tank : damage;
      var other = incoming ? damage : tank;
      Assert.IsNotNull(board);
      Assert.AreEqual(expected, board.RaidStats.Total);
      Assert.AreEqual(0L, other?.RaidStats.Total ?? 0L);
      var player = board.StatsList.Single();
      Assert.AreEqual("Kilsef", player.Name);
      Assert.AreEqual((long)uint.MaxValue, player.Min);
      Assert.AreEqual(TextUtils.MaxAmount, player.Max);
      var sub = player.SubStats.Single();
      Assert.IsFalse(sub.HitFreqMaterialized);
      Assert.AreEqual(3, sub.RawHitTotals);
      Assert.AreEqual(1, sub.NonCritFreqValues[TextUtils.MaxAmount]);
      Assert.AreEqual(1, sub.NonCritFreqValues[uint.MaxValue]);
      Assert.IsFalse(sub.HoldsRawHitTotals);

      HealingStatsBuilder.Instance.BuildTotalStats(options);
      var healing = HealingStatsBuilder.Instance.GetLastStats()?.CombinedStats;
      Assert.IsNotNull(healing);
      var healer = healing.StatsList.Single();
      Assert.AreEqual(TextUtils.MaxAmount - 1, healer.Total);
      Assert.AreEqual(1L, healer.Extra);
      Assert.AreEqual((2 * TextUtils.MaxAmount) - 1, healer.MaxPotentialHit);
      HitRecord baseRecord = options.Heals.Single().Item2;
      Assert.AreEqual(TextUtils.MaxAmount - 1, baseRecord.Total);

      options.CollectHitTotals = false;
      DamageStatsBuilder.Instance.BuildTotalStats(options);
      var scopedTank = BuildTank(options);
      var scoped = incoming ? scopedTank : DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
      Assert.IsNotNull(scoped);
      Assert.AreEqual(expected, scoped.RaidStats.Total);
      foreach (var row in scoped.StatsList.SelectMany(s => s.SubStats))
      {
        Assert.IsFalse(row.HoldsRawHitTotals);
        Assert.IsFalse(row.HitFreqMaterialized);
      }
    }
    finally
    {
      File.Delete(path);
      ConfigUtil.PlayerName = previousName;
      DamageLineParser.ResetProcessState();
      RecordsStore.Instance.Clear(false);
      PlayerRegistry.Instance.Clear();
      HealTap.Clear();
    }
  }

  private static CombinedStats? BuildTank(GenerateStatsOptions options)
  {
    CombinedStats? result = null;
    void OnStatus(StatsGenerationEvent e) => result = e.CombinedStats ?? result;
    TankingStatsBuilder.Instance.EventsGenerationStatus += OnStatus;
    try
    {
      TankingStatsBuilder.Instance.BuildTotalStats(options);
      return result;
    }
    finally
    {
      TankingStatsBuilder.Instance.EventsGenerationStatus -= OnStatus;
    }
  }
}

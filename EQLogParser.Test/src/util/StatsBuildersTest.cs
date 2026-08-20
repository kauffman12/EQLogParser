namespace EQLogParser
{
  /* Direct tests for the cross-platform stats builders + formatter. Builders are singletons fed
   * by Fight objects (damage/tanking) or the RecordsStore (healing); the output is the
   * StatsGenerationEvent/CombinedStats the UI binds to. Tests use unique player names per case
   * so singleton state (group assignments, etc.) can't leak between cases. */
  [TestClass]
  public sealed class StatsBuildersTest
  {
    private static int _nameSalt;

    private static string Name(string prefix) => $"{prefix}{Interlocked.Increment(ref _nameSalt)}";

    /// <summary>One fight with the given damage blocks over [begin, last] seconds.</summary>
    private static Fight MakeFight(long id, string npcName, double begin, double last, params ActionGroup[] blocks)
    {
      var fight = new Fight
      {
        Id = id,
        Name = npcName,
        BeginDamageTime = begin,
        LastDamageTime = last
      };
      fight.DamageBlocks.AddRange(blocks);
      return fight;
    }

    private static ActionGroup Block(double time, params IAction[] actions)
    {
      var group = new ActionGroup { BeginTime = time };
      group.Actions.AddRange(actions);
      return group;
    }

    private static DamageRecord Hit(double time, string attacker, uint total, string type = "damage", string subType = null, string owner = null) =>
      new()
      {
        Attacker = attacker,
        Defender = "TargetNpc",
        Total = total,
        Type = type,
        SubType = subType,
        AttackerOwner = owner
      };

    private static CombinedStats BuildDamage(Fight fight, GenerateStatsOptions options)
    {
      DamageStatsBuilder.Instance.BuildTotalStats(options);
      return DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
    }

    private static GenerateStatsOptions Options(Fight fight, long maxSeconds = -1, long minSeconds = -1)
    {
      var options = new GenerateStatsOptions { MaxSeconds = maxSeconds, MinSeconds = minSeconds, AllRanges = Ranges((0, fight.LastDamageTime)) };
      options.Npcs.Add(fight);
      return options;
    }

    private static TimeRange Ranges(params (double begin, double end)[] segments)
    {
      var range = new TimeRange();
      foreach (var (begin, end) in segments)
      {
        range.TimeSegments.Add(new TimeSegment(begin, end));
      }

      return range;
    }

    #region DamageStatsBuilder

    [TestMethod]
    public void BuildTotalStats_TwoPlayers_RaidTotalsDpsAndRanks()
    {
      var a = Name("Alpha");
      var b = Name("Beta");
      // note: builders group with a lastTime==0 sentinel, so blocks at t==0 are dropped — real log times never hit 0
      var fight = MakeFight(1, "Gek", 0, 60,
        Block(1, Hit(1, a, 1000), Hit(1, b, 500)),
        Block(31, Hit(31, a, 2000)));

      var combined = BuildDamage(fight, Options(fight));
      Assert.IsNotNull(combined);

      // a single (0,60) segment counts as 61s per TimeSegment.Total — use the same semantics
      var fightSeconds = Ranges((0, 60)).GetTotal();
      Assert.AreEqual(3500L, combined.RaidStats.Total);
      // same formula the builder uses — pins division + rounding behavior
      Assert.AreEqual((long)Math.Round(3500 / fightSeconds, 2), combined.RaidStats.Dps);
      Assert.AreEqual(fightSeconds, combined.RaidStats.TotalSeconds, 0.001);

      Assert.AreEqual(2, combined.StatsList.Count);
      Assert.AreEqual(a, combined.StatsList[0].Name);
      Assert.AreEqual(3000L, combined.StatsList[0].Total);
      Assert.AreEqual(b, combined.StatsList[1].Name);
      Assert.AreEqual(500L, combined.StatsList[1].Total);
      Assert.AreEqual((ushort)1, combined.StatsList[0].Rank);
      Assert.AreEqual((ushort)2, combined.StatsList[1].Rank);
    }

    [TestMethod]
    public void BuildTotalStats_PetDamage_AggregatesUnderOwner()
    {
      var player = Name("Hunter");
      var pet = Name("Wolf");
      var fight = MakeFight(2, "Gek", 0, 60,
        Block(1, Hit(1, player, 1000)),
        Block(11, Hit(11, pet, 400, owner: player)));

      var combined = BuildDamage(fight, Options(fight));
      Assert.IsNotNull(combined);

      // top level shows the aggregate, children show the breakdown
      var aggregate = combined.StatsList.FirstOrDefault(s => s.Name == $"{player} +Pets");
      Assert.IsNotNull(aggregate, "expected an owner +Pets aggregate in the top-level list");
      Assert.AreEqual(1400L, aggregate.Total);

      var playerStats = combined.ExpandedStatsList.FirstOrDefault(s => s.Name == player);
      var petStats = combined.ExpandedStatsList.FirstOrDefault(s => s.Name == pet);
      Assert.IsNotNull(playerStats);
      Assert.IsNotNull(petStats);
      Assert.AreEqual(1000L, playerStats.Total);
      Assert.AreEqual(400L, petStats.Total);
      // percent of the aggregate
      Assert.AreEqual((float)Math.Round(1000.0 / 1400 * 100, 2), playerStats.Percent, 0.01f);
      Assert.AreEqual((float)Math.Round(400.0 / 1400 * 100, 2), petStats.Percent, 0.01f);

      // the child list under the aggregate matches the expanded entries
      Assert.IsTrue(combined.Children.TryGetValue($"{player} +Pets", out var children));
      Assert.AreEqual(2, children.Count);
    }

    [TestMethod]
    public void BuildTotalStats_MaxSeconds_DropsDamageAfterWindow()
    {
      var a = Name("Alpha");
      // fight runs to t=70; the window keeps only the first 30s (from the end: 70-30=40 removed)
      var fight = MakeFight(3, "Gek", 0, 70,
        Block(1, Hit(1, a, 1000)),
        Block(61, Hit(61, a, 99999)));

      var combined = BuildDamage(fight, Options(fight, maxSeconds: 30, minSeconds: 0));
      Assert.IsNotNull(combined);

      Assert.AreEqual(1000L, combined.RaidStats.Total);
      Assert.AreEqual(30.0, combined.RaidStats.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void BuildTotalStats_NoFires_ReportsNonpc()
    {
      // the no-data path only fires the generation event — it never sets the last-stats cache
      StatsGenerationEvent captured = null;
      DamageStatsBuilder.Instance.EventsGenerationStatus += e => captured = e;
      try
      {
        DamageStatsBuilder.Instance.BuildTotalStats(new GenerateStatsOptions());
      }
      finally
      {
        DamageStatsBuilder.Instance.EventsGenerationStatus -= e => captured = e;
      }

      Assert.IsNotNull(captured);
      Assert.AreEqual("NONPC", captured.State);
      Assert.IsNull(captured.CombinedStats);
    }

    #endregion

    #region HealingStatsBuilder

    [TestMethod]
    public void BuildTotalStats_Heals_RaidTotalsPerHealerAndSubStats()
    {
      var healer1 = Name("Cleric");
      var healer2 = Name("Druid");
      var victim = Name("Target");

      RecordsStore.Instance.Clear();
      PlayerRegistry.Instance.AddMerc(victim); // healing stats only count heals landing on known player/pet/merc names
      try
      {
        RecordsStore.Instance.Add(new HealRecord { Healer = healer1, Healed = victim, Total = 800, Type = "healing", SubType = "Holy Light" }, 1);
        RecordsStore.Instance.Add(new HealRecord { Healer = healer1, Healed = victim, Total = 200, Type = "healing", SubType = "Flash Heal" }, 6);
        RecordsStore.Instance.Add(new HealRecord { Healer = healer2, Healed = victim, Total = 300, Type = "healing", SubType = "Rejuvenation" }, 9);

        var options = new GenerateStatsOptions { AllRanges = Ranges((0, 60)) };
        options.Npcs.Add(MakeFight(4, "Gek", 0, 60));
        HealingStatsBuilder.Instance.BuildTotalStats(options);
        var combined = HealingStatsBuilder.Instance.GetLastStats()?.CombinedStats;
        Assert.IsNotNull(combined);

        Assert.AreEqual(1300L, combined.RaidStats.Total);

        var top = combined.StatsList[0];
        Assert.AreEqual(healer1, top.Name);
        Assert.AreEqual(1000L, top.Total);
        Assert.AreEqual(300L, combined.StatsList[1].Total);

        // per-spell breakdown
        var holyLight = top.SubStats.FirstOrDefault(s => s.Name == "Holy Light");
        Assert.IsNotNull(holyLight, "expected a Holy Light sub-stat");
        Assert.AreEqual(800L, holyLight.Total);
      }
      finally
      {
        RecordsStore.Instance.Clear();
      }
    }

    #endregion

    #region TankingStatsBuilder

    [TestMethod]
    public void BuildTotalStats_TankBlocks_TotalsPerDefender()
    {
      var tank1 = Name("Tank");
      var tank2 = Name("Offtank");
      var hit1 = new DamageRecord { Attacker = "Gek", Defender = tank1, Total = 500, Type = "damage" };
      var hit2 = new DamageRecord { Attacker = "Gek", Defender = tank2, Total = 250, Type = "damage" };

      var fight = new Fight { Id = 5, Name = "Gek", BeginTankingTime = 0, LastTankingTime = 60 };
      fight.TankingBlocks.Add(Block(1, hit1));
      fight.TankingBlocks.Add(Block(21, hit2));

      var options = new GenerateStatsOptions { AllRanges = Ranges((0, 60)) };
      options.Npcs.Add(fight);

      // this builder surfaces its results through the generation event (no GetLastStats)
      StatsGenerationEvent captured = null;
      TankingStatsBuilder.Instance.EventsGenerationStatus += e => captured = e;
      try
      {
        TankingStatsBuilder.Instance.BuildTotalStats(options);
      }
      finally
      {
        TankingStatsBuilder.Instance.EventsGenerationStatus -= e => captured = e;
      }

      var combined = captured?.CombinedStats;
      Assert.IsNotNull(combined);

      Assert.AreEqual(750L, combined.RaidStats.Total);
      Assert.AreEqual(tank1, combined.StatsList[0].Name);
      Assert.AreEqual(500L, combined.StatsList[0].Total);
      Assert.AreEqual(250L, combined.StatsList[1].Total);
    }

    #endregion

    #region DamageOverlayStatsBuilder

    [TestMethod]
    public void Build_WithoutOverlayData_ReturnsNull()
    {
      // no overlay feed is wired in the test process — pin the empty-state contract
      var result = new DamageOverlayStatsBuilder().Build(reset: true, mode: 0, maxRows: 10, selectedClass: null);
      Assert.IsNull(result);
    }

    #endregion

    #region StatsFormatter

    private static CombinedStats MakeCombined(List<PlayerStats> players)
    {
      var combined = new CombinedStats
      {
        TargetTitle = "Gek",
        TimeTitle = string.Format(System.Globalization.CultureInfo.CurrentCulture, StatsUtil.TimeFormat, 60),
        TotalTitle = "1K Damage @2",
        RaidStats = StatsUtil.CreatePlayerStats(Labels.RaidTotals)
      };
      combined.StatsList.AddRange(players);
      return combined;
    }

    [TestMethod]
    public void Build_DamageParse_RankedLinesWithDps()
    {
      var p1 = new PlayerStats { Name = "One", Total = 2000, Dps = 50, Rank = 1 };
      var p2 = new PlayerStats { Name = "Two", Total = 1000, Dps = 25, Rank = 2 };
      var combined = MakeCombined([p1, p2]);

      var opts = new SummaryOptions { RankPlayers = true, ShowDps = true, ListView = true, ShowTotals = true, ShowRaidTime = true };
      var summary = StatsFormatter.Build(Labels.DamageParse, combined, [p1, p2], opts, null);

      // each expected line is composed with the same production formatters
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      var line1 = string.Format(inv, StatsUtil.PlayerRankFormat, p1.Rank, p1.Name) +
        string.Format(inv, StatsUtil.TotalFormat, StatsUtil.FormatTotals(p1.Total), "", StatsUtil.FormatTotals(p1.Dps));
      var line2 = string.Format(inv, StatsUtil.PlayerRankFormat, p2.Rank, p2.Name) +
        string.Format(inv, StatsUtil.TotalFormat, StatsUtil.FormatTotals(p2.Total), "", StatsUtil.FormatTotals(p2.Dps));

      // ListView mode prefixes each line with the platform newline
      Assert.AreEqual(Environment.NewLine + line1 + Environment.NewLine + line2, summary.RankedPlayers);
      StringAssert.Contains(summary.Title, "Gek");
      StringAssert.Contains(summary.Title, "in 60s");
    }

    [TestMethod]
    public void Build_DamageParse_HidesPetLabelAndStripsSuffix()
    {
      var withPets = new PlayerStats { Name = "Ranger +Pets", Total = 1500, Rank = 1 };
      var combined = MakeCombined([withPets]);

      var hidden = StatsFormatter.Build(Labels.DamageParse, combined, [withPets],
        new SummaryOptions { RankPlayers = true, ShowDps = false, ListView = false }, null);
      StringAssert.Contains(hidden.RankedPlayers, "Ranger = ");
      Assert.IsFalse(hidden.RankedPlayers.Contains("+Pets"));

      var shown = StatsFormatter.Build(Labels.DamageParse, combined, [withPets],
        new SummaryOptions { RankPlayers = true, ShowDps = false, ListView = false, ShowPetLabel = true }, null);
      StringAssert.Contains(shown.RankedPlayers, "Ranger +Pets");
    }

    [TestMethod]
    public void Build_NullStats_ReturnsEmptySummary()
    {
      var summary = StatsFormatter.Build(Labels.DamageParse, null, [], new SummaryOptions(), null);
      Assert.AreEqual("", summary.Title);
      Assert.AreEqual("", summary.RankedPlayers);
    }

    #endregion
  }
}

namespace EQLogParser
{
  /* Traces an unknown-spell cast through the real spell count pipeline (RecordsStore -> QuerySpells -> GetSpellCounts),
   * the same path SpellCountTable consumes. Unknown spells are what a spin-off client like EverQuest Legends produces:
   * the cast line parses, but the name is not in the bundled spell data, so the record carries an IsUnknown stub.
   * These tests pin where such casts do and do not survive that pipeline.
   *
   * Needs the real EQDataStore (for the unknown-spell registry), whose data files live next to the test binary, so it
   * sets the process-wide CWD and must not run concurrently with other test classes. */
  [DoNotParallelize]
  [TestClass]
  public sealed class SpellCountBuilderTest
  {
    private EQDataStore? _dataStore;

    [TestInitialize]
    public void Setup()
    {
      Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

      _dataStore = new EQDataStore();
      EQDataStore.Instance = _dataStore;

      RecordsStore.Instance.Clear();
      PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
      RecordsStore.Instance.Clear();
      PlayerRegistry.Instance.Clear();
      _dataStore = null;
    }

    /// <summary>One fight over [0, last] with a single damage hit, so the builder has a real combat segment.</summary>
    private static Fight MakeFight(string player, double last)
    {
      var hit = new DamageRecord { Attacker = player, Defender = "TargetNpc", Total = 1000, Type = "damage" };
      var group = new ActionGroup { BeginTime = 1 };
      group.Actions.Add(hit);

      return new Fight
      {
        Id = 1,
        Name = "Gek",
        BeginDamageTime = 0,
        LastDamageTime = last,
        DamageBlocks = { group }
      };
    }

    private static PlayerStats BuildRaidStats(string player)
    {
      var fight = MakeFight(player, 60);
      var options = new GenerateStatsOptions { AllRanges = new TimeRange() };
      options.AllRanges.TimeSegments.Add(new TimeSegment(0, 60));
      options.Npcs.Add(fight);

      DamageStatsBuilder.Instance.BuildTotalStats(options);
      return DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats
        ?? throw new InvalidOperationException("damage stats did not build");
    }

    private static string UnknownSpellName()
    {
      var name = $"Legends Test Spell {Guid.NewGuid():N}";
      // the whole point: this is not in the bundled spell data
      Assert.IsNull(EQDataStore.Instance.GetSpellByName(name));
      return name;
    }

    [TestMethod]
    public void GetSpellCounts_UnknownCastDuringFight_IsCountedLikeAnyOtherSpell()
    {
      var player = "LegendsCaster";
      var spellName = UnknownSpellName();
      var unknown = EQDataStore.Instance.AddUnknownSpell(spellName);
      Assert.IsTrue(unknown.IsUnknown);

      RecordsStore.Instance.Add(new SpellCast { Caster = player, Spell = spellName, SpellData = unknown }, 5);

      var counts = SpellCountBuilder.GetSpellCounts([player], BuildRaidStats(player));

      Assert.IsTrue(counts.PlayerCastCounts.TryGetValue(player, out var byPlayer),
        "expected cast counts for the player");
      Assert.AreEqual(1u, byPlayer[spellName]);
      Assert.IsTrue(counts.UniqueSpells.ContainsKey(spellName));
      Assert.AreEqual(1u, counts.MaxCastCounts[spellName]);
    }

    /* The count table only looks inside combat segments (plus BuffOffset/HalfOffset). A cast that never lands in one
     * is invisible to the table no matter how many times it happens - the first place an unknown spell goes missing. */
    [TestMethod]
    public void GetSpellCounts_UnknownCastFarOutsideEveryFight_IsNotCounted()
    {
      var player = "LegendsCaster";
      var spellName = UnknownSpellName();
      var unknown = EQDataStore.Instance.AddUnknownSpell(spellName);

      RecordsStore.Instance.Add(new SpellCast { Caster = player, Spell = spellName, SpellData = unknown }, 500);

      var counts = SpellCountBuilder.GetSpellCounts([player], BuildRaidStats(player));

      Assert.IsFalse(counts.PlayerCastCounts.TryGetValue(player, out var byPlayer) && byPlayer.ContainsKey(spellName),
        "a cast five minutes outside every fight must not reach the count table");
    }

    /* Interrupted casts add zero to the per-player count, and the table's default 'Any Frequency' filter is
     * maxCounts > 0 - so a spell whose casts were all interrupted never shows, which is easy to mistake for 'unknown
     * spells don't appear'. Pinned here because it applies to unknown spells exactly like known ones. */
    [TestMethod]
    public void GetSpellCounts_UnknownCastOnlyInterrupted_HasZeroMaxCount()
    {
      var player = "LegendsCaster";
      var spellName = UnknownSpellName();
      var unknown = EQDataStore.Instance.AddUnknownSpell(spellName);

      var cast = new SpellCast { Caster = player, Spell = spellName, SpellData = unknown, Interrupted = true };
      RecordsStore.Instance.Add(cast, 5);

      var counts = SpellCountBuilder.GetSpellCounts([player], BuildRaidStats(player));

      Assert.IsTrue(counts.MaxCastCounts.TryGetValue(spellName, out var max), "expected the spell in the count maps");
      Assert.AreEqual(0u, max, "interrupted casts must not count, which keeps them under the Any Frequency floor");
    }
  }
}

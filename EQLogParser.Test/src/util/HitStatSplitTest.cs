using EQLogParser;

namespace EQLogParserTest
{
  /*
   * Damage and healing used to be counted by one routine taking their common base, which is why that base carried
   * fields only one of them could write. It is two routines now — StatsUtil.UpdateDamageStats and
   * UpdateHealStats — so the rules the shared switch used to enforce in one place are pinned here instead,
   * including two guards: an Absorb takes no damaging-hit modifier tallies, and a miss carrying a mask
   * scores nothing either. That second one read the record's Type inside LineModifiersParser before; it belongs
   * to whoever counts the event, because a riposte line arrives as a miss and its attacker did not do those
   * things. Numbers and reasoning: docs/DesignNotes.md → The eight bytes that were in every damage record for
   * nothing.
   */
  [TestClass]
  public sealed class HitStatSplitTest
  {
    private static short Mask(string words) => LineModifiersParser.BuildVector(words);

    private static DamageRecord Damage(string type, string subType, uint total, short mask = LineModifiersParser.None) =>
      new() { Attacker = "Zomk", Defender = "Fen Claw", Type = type, SubType = subType, Total = total, ModifiersMask = mask };

    private static HealRecord Heal(uint total, uint overTotal, short mask = LineModifiersParser.None) =>
      new() { Healer = "Cureqa", Healed = "Zomk", Type = Labels.Heal, SubType = "Complete Heal", Total = total, OverTotal = overTotal, ModifiersMask = mask };

    [TestMethod]
    public void Damage_CountsItsOwnKindAndItsPotentialIsWhatLanded()
    {
      var stats = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Dd, "Starfire S", 1234));

      Assert.AreEqual(1u, stats.SpellHits);
      Assert.AreEqual(1u, stats.Hits);
      Assert.AreEqual(1234L, stats.Total);
      Assert.AreEqual(1234u, stats.Max);
      Assert.AreEqual(1234u, stats.Min);
      Assert.AreEqual(1234u, stats.MaxPotentialHit);

      // a damage record holds no second amount (over-heal is HealRecord's), so nothing is ever owed on top of it
      Assert.AreEqual(0L, stats.Extra);
    }

    [TestMethod]
    public void Damage_CritMaskScoresTheCritTallies()
    {
      var stats = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Dd, "Starfire S", 1234, Mask("Critical")));

      Assert.AreEqual(1u, stats.CritHits);
      Assert.AreEqual(1234L, stats.TotalCrit);
      Assert.AreEqual(1u, stats.NonTwincastCritHits);
      Assert.AreEqual(1234L, stats.TotalNonTwincastCrit);
    }

    // An absorb must not invent a damaging or critical hit.
    [TestMethod]
    public void Damage_AbsorbCountsAsAnAttemptWithoutCriticalModifiers()
    {
      var stats = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Absorb, "Hits", 0, Mask("Critical")));

      Assert.AreEqual(1u, stats.Absorbs);
      Assert.AreEqual(1u, stats.MeleeAttempts);
      Assert.AreEqual(0u, stats.CritHits);
      Assert.AreEqual(0L, stats.TotalCrit);
    }

    [TestMethod]
    public void Damage_AbsorbsConnectWithoutChangingDamageStatisticsOrAccuracyFormula()
    {
      var stats = new PlayerStats();
      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Melee, "Hits", 100, Mask("Critical")));
      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Absorb, "Hits", 0, Mask("Riposte Strikethrough Critical")));
      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Miss, "Hits", 0));
      StatsUtil.UpdateDamageStats(stats, Damage(Labels.DamageAbsorb, Labels.Ds, 0, Mask("Critical")));
      StatsUtil.CalculateRates(stats, new PlayerStats(), null);

      Assert.AreEqual(3u, stats.MeleeAttempts);
      Assert.AreEqual(1u, stats.MeleeHits);
      Assert.AreEqual(1u, stats.Hits);
      Assert.AreEqual(1u, stats.Absorbs);
      Assert.AreEqual(1u, stats.DamageAbsorbs);
      Assert.AreEqual(1u, stats.StrikethroughHits);
      Assert.AreEqual(0u, stats.RiposteHits);
      Assert.AreEqual(1u, stats.CritHits);
      Assert.AreEqual(100L, stats.Total);
      Assert.AreEqual(100L, stats.Min);
      Assert.AreEqual(100L, stats.Max);
      Assert.AreEqual(100L, stats.Avg);
      Assert.AreEqual(100f, stats.CritRate);
      Assert.AreEqual(66.67f, stats.MeleeHitRate);
      Assert.AreEqual(50f, stats.MeleeAccRate, "the existing accuracy formula still excludes absorbs");
      Assert.AreEqual(0, stats.RawHitTotals, "accounting never inserts histogram samples");
    }

    [TestMethod]
    public void Damage_OnlyAbsorbsCanYieldAConnectionRateWithoutADamagingHit()
    {
      var stats = new PlayerStats();
      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Absorb, "Hits", 0));
      StatsUtil.CalculateRates(stats, new PlayerStats(), null);
      Assert.AreEqual(100f, stats.MeleeHitRate);
      Assert.AreEqual(0u, stats.Hits);
      Assert.AreEqual(0f, stats.MeleeAccRate);
    }

    [TestMethod]
    public void Damage_NotificationCountsMergeAndResetWithoutOpeningSwings()
    {
      var source = new PlayerStats();
      StatsUtil.UpdateDamageStats(source, Damage(Labels.DamageAbsorb, Labels.DamageAbsorb, 0));
      Assert.IsFalse(StatsUtil.IsMelee(Damage(Labels.DamageAbsorb, Labels.DamageAbsorb, 0)));
      Assert.IsFalse(StatsUtil.IsHitType(Labels.DamageAbsorb));
      Assert.IsFalse(StatsUtil.IsDefensiveOutcome(Labels.DamageAbsorb));
      var merged = new PlayerStats();
      StatsUtil.MergeStats(merged, source);
      StatsUtil.MergeStats(merged, source);
      StatsUtil.CalculateRates(merged, new PlayerStats(), null);
      Assert.AreEqual(2u, merged.DamageAbsorbs);
      Assert.AreEqual(0u, merged.MeleeAttempts);
      Assert.AreEqual(0u, merged.Hits);
      Assert.AreEqual(0f, merged.MeleeHitRate);
      StatsUtil.ResetPlayerStats(merged);
      Assert.AreEqual(0u, merged.DamageAbsorbs);
    }

    [TestMethod]
    public void Damage_AccuracyExclusionOverlapsSurviveMergeAndResetWithoutClampingRates()
    {
      var source = new PlayerStats();
      var swings = new DamageSwingAccounting();
      DamageRecord[] records =
      [
        Damage(Labels.Parry, "Slashes", 0, Mask("Strikethrough")),
        Damage(Labels.Absorb, "Slashes", 0, Mask("Strikethrough")),
        Damage(Labels.Melee, "Slashes", 100),
        Damage(Labels.Melee, "Slashes", 100),
        Damage(Labels.Miss, "Slashes", 0),
        Damage(Labels.Dd, "Starfire S", 100)
      ];
      foreach (var record in records)
        StatsUtil.UpdateDamageStats(source, record, swing: swings.Observe(record, 100));
      StatsUtil.CalculateRates(source, new PlayerStats(), null);
      Assert.AreEqual(1u, source.AbsorbExclusionOverlaps);
      Assert.AreEqual(4u, source.MeleeAttempts);
      Assert.AreEqual(2u, source.MeleeHits);
      Assert.AreEqual(3u, source.Hits, "spell hits are not accuracy's numerator");
      Assert.AreEqual(66.67f, source.MeleeAccRate, "two melee hits / three non-excluded attempts");
      Assert.AreEqual(75f, source.MeleeHitRate);

      var merged = new PlayerStats();
      StatsUtil.MergeStats(merged, source);
      StatsUtil.MergeStats(merged, source);
      StatsUtil.CalculateRates(merged, new PlayerStats(), null);
      Assert.AreEqual(2u, merged.AbsorbExclusionOverlaps);
      Assert.AreEqual(66.67f, merged.MeleeAccRate);
      Assert.AreEqual(75f, merged.MeleeHitRate);
      StatsUtil.ResetPlayerStats(merged);
      Assert.AreEqual(0u, merged.AbsorbExclusionOverlaps);
      StatsUtil.UpdateDamageStats(merged, Damage(Labels.Melee, "Slashes", 100));
      StatsUtil.CalculateRates(merged, new PlayerStats(), null);
      Assert.AreEqual(100f, merged.MeleeAccRate);
    }

    /*
     * A miss can arrive with a mask riding on it, and whoever counts it must not let that mask spend the
     * attacker's tallies.
     */
    [TestMethod]
    public void Damage_MissWithAMaskScoresNothing()
    {
      var stats = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Miss, "Hits", 0, Mask("Critical")));

      Assert.AreEqual(1u, stats.Misses);
      Assert.AreEqual(1u, stats.MeleeAttempts);
      Assert.AreEqual(0u, stats.CritHits);
      Assert.AreEqual(0L, stats.TotalCrit);
    }

    // A board-walk credit removes the repeated attempt and tag, not the landed damage or its own modifiers.
    [TestMethod]
    public void Damage_StruckSwingsNumberLineScoresTheBlowNotASecondAttempt()
    {
      var stats = new PlayerSubStats { Name = "Ryft" };

      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Riposte, "Ripostes", 0, Mask("Strikethrough")));

      var landed = Damage(Labels.Melee, "Gores", 132465, Mask("Riposte Strikethrough Rampage"));
      StatsUtil.UpdateDamageStats(stats, landed,
        swing: new DamageSwingCredit(true, LineModifiersParser.Strikethrough));

      Assert.AreEqual(1u, stats.MeleeAttempts, "the sentence opened the attempt; the number line is the same swing");
      Assert.AreEqual(0u, stats.RiposteHits, "a struck-through defense did not stop the swing");
      Assert.AreEqual(1u, stats.StrikethroughHits, "the tag is on both lines and belongs to the swing once");
      Assert.AreEqual(1u, stats.MeleeHits);
      Assert.AreEqual(132465L, stats.Total);
      Assert.AreEqual(1u, stats.RampageHits, "a bit the sentence never carried still scores");
      Assert.AreEqual(0L, stats.TotalRiposte,
        "the fold takes the Riposte bit WITH the Strikethrough bit: a bare Riposte bit would read as IsRiposte() and put the mob's damage in the riposte total");
    }

    [TestMethod]
    public void Heal_CountsASpellHitAndChargesTheAskToExtra()
    {
      var stats = new PlayerSubStats { Name = "Cureqa" };

      // EQ reads "for 9409 (11000)": Total is what landed, OverTotal what the line asked for
      StatsUtil.UpdateHealStats(stats, Heal(9409, 11000));

      Assert.AreEqual(1u, stats.SpellHits);
      Assert.AreEqual(1u, stats.Hits);
      Assert.AreEqual(9409L, stats.Total);
      Assert.AreEqual(1591L, stats.Extra);

      // MaxPotentialHit has always been landed + asked-for for a heal (20409 here, which double counts the part
      // that landed). Nothing prints it — the tables show Potential = Total + Extra — so it is pinned as it
      // behaves rather than as it reads; changing it is a display decision, not part of this one.
      Assert.AreEqual(20409u, stats.MaxPotentialHit);

      // the shared routine kept a running best-second total even though no heal caller asks for the flush;
      // splitting the two routines must not silently stop feeding it
      Assert.AreEqual(9409L, stats.BestSecTemp);
    }

    /* A twincast crit is still a crit, but it is not the sample the "non-twincast" averages are drawn from. */
    [TestMethod]
    public void Heal_TwincastCritScoresTheCritWithoutTheNonTwincastTally()
    {
      var stats = new PlayerSubStats { Name = "Cureqa" };

      StatsUtil.UpdateHealStats(stats, Heal(9409, 0, Mask("Twincast Critical")));

      Assert.AreEqual(1u, stats.TwincastHits);
      Assert.AreEqual(1u, stats.CritHits);
      Assert.AreEqual(9409L, stats.TotalCrit);
      Assert.AreEqual(0u, stats.NonTwincastCritHits);

      // none of a heal's counting walks the melee bookkeeping, which is what lets it skip the label switch
      Assert.AreEqual(0u, stats.MeleeAttempts);
      Assert.AreEqual(0u, stats.MeleeHits);
    }
  }
}

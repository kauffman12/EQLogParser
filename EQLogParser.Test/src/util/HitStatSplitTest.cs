using EQLogParser;

namespace EQLogParserTest
{
  /*
   * Damage and healing used to be counted by one routine taking their common base, which is why that base carried
   * fields only one of them could write. It is two routines now — StatsUtil.UpdateDamageStats and
   * UpdateHealStats — so the rules the shared switch used to enforce in one place are pinned here instead,
   * including the two guards that moved house: an Absorb takes no modifier tallies, and a miss carrying a mask
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

    /* The absorb branch is the one case in the damage switch that deliberately does not parse modifiers. */
    [TestMethod]
    public void Damage_AbsorbCountsAsAnAttemptAndTakesNoModifierTallies()
    {
      var stats = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(stats, Damage(Labels.Absorb, "Hits", 0, Mask("Critical")));

      Assert.AreEqual(1u, stats.Absorbs);
      Assert.AreEqual(1u, stats.MeleeAttempts);
      Assert.AreEqual(0u, stats.CritHits);
      Assert.AreEqual(0L, stats.TotalCrit);
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

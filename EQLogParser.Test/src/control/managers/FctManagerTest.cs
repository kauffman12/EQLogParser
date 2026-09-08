namespace EQLogParser
{
  /*
   * FCT feed tests: replay/live gating, lane mapping, evade labels and the queue's overload behavior.
   * Records are injected straight into the manager's handlers, so no log lines are parsed. The overlay
   * drains once per frame, so the tests drain the same way.
   */
  [DoNotParallelize]
  [TestClass]
  public sealed class FctManagerTest
  {
    private readonly List<FctHitCommand> _drained = [];

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "TestPlayer";
      PlayerRegistry.Instance.Clear();

      // a manager of our own, exactly as FctOverlayWindow takes one on open
      FctManager.Create();
      FctManager.Instance.Enabled = true;
    }

    [TestCleanup]
    public void Teardown() => FctManager.Instance.Dispose();

    [TestMethod]
    public void DropsFeedWhenNothingIsListening()
    {
      FctManager.Instance.Enabled = false;
      FireDamage("TestPlayer", 500);
      FireHeal("TestPlayer", 500);

      Assert.AreEqual(0, Drain());
      Assert.AreEqual(0, FctManager.Instance.DroppedCount); // gated before the queue: not lost, never taken
    }

    [TestMethod]
    public void DropsReplayRecords()
    {
      FireDamage("TestPlayer", 100, isMonitor: false);
      FireHeal("TestPlayer", 100, isMonitor: false);

      Assert.AreEqual(0, Drain());
    }

    [TestMethod]
    public void EmitsLiveDamageWithTheRightLanes()
    {
      FireDamage("TestPlayer", 500);
      FireDamage("TestPlayer", 600, crit: true);
      FireDamage("OtherGuy", 700, defender: "TestPlayer"); // they hit me -> incoming lane

      Assert.AreEqual(3, Drain());
      Assert.AreEqual(FctLane.DamageDealt, _drained[0].Lane);
      Assert.IsFalse(_drained[0].Crit);
      // a crit keeps its source lane; the renderer is what pools it into the crit lane on that side's region
      Assert.AreEqual(FctLane.DamageDealt, _drained[1].Lane);
      Assert.IsTrue(_drained[1].Crit);
      Assert.AreEqual(FctLane.DamageTaken, _drained[2].Lane);
    }

    [TestMethod]
    public void IgnoresLinesThatDoNotInvolveMe()
    {
      FireDamage("OtherGuy", 100); // someone else's fight entirely

      Assert.AreEqual(0, Drain());
    }

    [TestMethod]
    public void EvadesEmitWordsOnTheRightSide()
    {
      FireDamage("OtherGuy", 100, defender: "TestPlayer", total: 0, type: Labels.Miss); // they whiff on me -> Defensive
      FireDamage("TestPlayer", 200, total: 0, type: Labels.Dodge);                     // my swing gets dodged -> Missed

      Assert.AreEqual(2, Drain());
      Assert.AreEqual(FctLane.Defensive, _drained[0].Lane);
      Assert.AreEqual(Labels.Miss, _drained[0].ValueText);
      Assert.IsFalse(_drained[0].Crit);
      Assert.AreEqual(FctLane.Missed, _drained[1].Lane);
      Assert.AreEqual(Labels.Dodge, _drained[1].ValueText);
    }

    [TestMethod]
    public void ZeroDamageWithoutALabelIsDropped()
    {
      FireDamage("OtherGuy", 100, defender: "TestPlayer", total: 0, type: Labels.Dd);

      Assert.AreEqual(0, Drain());
    }

    [TestMethod]
    public void HealsMapByDirectionAndOtherHealsAreDropped()
    {
      FireHeal("TestPlayer", 500);                     // I heal someone -> HealingDealt
      FireHeal("OtherGuy", 600, healed: "TestPlayer"); // someone heals me -> HealingReceived
      FireHeal("OtherGuy", 700, healed: "SomeOther");  // neither side is me -> dropped

      Assert.AreEqual(2, Drain());
      Assert.AreEqual(FctLane.HealingDealt, _drained[0].Lane);
      Assert.AreEqual(FctLane.HealingReceived, _drained[1].Lane);
      Assert.AreEqual(50.0, _drained[0].Value);
      Assert.AreEqual(50.0, _drained[1].Value);
    }

    [TestMethod]
    public void SelfHealEmitsOnlyAsReceived()
    {
      FireHeal("TestPlayer", 800, healed: "TestPlayer");

      Assert.AreEqual(1, Drain());
      Assert.AreEqual(FctLane.HealingReceived, _drained[0].Lane);
    }

    [TestMethod]
    public void OverhealShowsEffectiveAmountAndDropsZeroEffective()
    {
      // "for 9409 (11000)": Total is effective, OverTotal the gross — no double count
      FireHeal("OtherGuy", 500, healed: "TestPlayer", total: 94, overTotal: 1100);
      FireHeal("OtherGuy", 600, healed: "TestPlayer", total: 0, overTotal: 500); // fully overhealed tick

      Assert.AreEqual(1, Drain());
      Assert.AreEqual(94.0, _drained[0].Value);
    }

    [TestMethod]
    public void PeriodicTicksAreFlaggedForShrinkAndGrouping()
    {
      FireDamage("TestPlayer", 100, type: Labels.Dot);
      FireHeal("TestPlayer", 200, type: Labels.Hot);

      Assert.AreEqual(2, Drain());
      Assert.IsTrue(_drained[0].Periodic);
      Assert.IsTrue(_drained[1].Periodic);
    }

    /*
     * A proc is its own record type — DamageLineParser decides it by looking the spell up in procs.txt, not from how a
     * line reads — and the overlay uses that flag to draw it slightly smaller and clear it sooner. It has to travel on
     * the command because by the time a canvas sees a number, that is all it is: nothing downstream can tell a proc from
     * the cast that provoked it.
     */
    [TestMethod]
    public void ProcsAreFlaggedSoTheOverlayCanKeepThemQuiet()
    {
      FireDamage("TestPlayer", 100, type: Labels.Proc, subType: "Soul Strike");
      FireDamage("OtherGuy", 200, defender: "TestPlayer", type: Labels.Proc);
      FireDamage("TestPlayer", 300, type: Labels.Dd);

      Assert.AreEqual(3, Drain());
      Assert.IsTrue(_drained[0].Proc, "my proc");
      Assert.IsTrue(_drained[1].Proc, "getting procced is subordinate too");
      Assert.IsFalse(_drained[2].Proc, "an ordinary spell or melee hit is not a proc");
    }

    [TestMethod]
    public void SourceIsABareNameForTheRendererToWrap()
    {
      // the canvases add the parentheses; wrapping here too is what produced "(Fireball)" inside "()"
      FireHeal("TestPlayer", 300, healed: "SomeNpc");

      Assert.AreEqual(1, Drain());
      Assert.AreEqual("Blessing", _drained[0].Source);
      Assert.IsFalse(_drained[0].Source.Contains('(', StringComparison.Ordinal));
    }

    /*
     * An evade label's source is the same attack verb a damage number shows: DamageLineParser fills SubType from
     * "X tries to crush Y, but Y dodges!" as well, even though Type carries the label there. Miss that and the same
     * swing reads "Crush" under a number and "Crushes" beside DODGE — the kind of wobble nobody notices in a log and
     * everybody notices in peripheral vision. Spell names stay untouched however they end: they are proper nouns.
     */
    [TestMethod]
    public void EvadeLabelsSingulariseTheirVerbLikeDamageDoes()
    {
      FireDamage("TestPlayer", 100, total: 0, type: Labels.Dodge, subType: "Crushes");
      FireDamage("OtherGuy", 200, defender: "TestPlayer", total: 0, type: Labels.Miss, subType: "Bites");
      FireDamage("OtherGuy", 300, defender: "TestPlayer", subType: "Crown of Stars");

      Assert.AreEqual(3, Drain());
      Assert.AreEqual("Crush", _drained[0].Source);
      Assert.AreEqual("Bite", _drained[1].Source);
      Assert.AreEqual("Crown of Stars", _drained[2].Source);
    }

    [TestMethod]
    public void StaleCommandsAreDroppedRatherThanReplayed()
    {
      // the seam keeps this deterministic: -1 marks everything queued as too old to still mean anything
      FctManager.Instance.MaxQueueAgeMs = -1;
      FireDamage("TestPlayer", 500);

      Assert.AreEqual(0, Drain());
      Assert.AreEqual(1, FctManager.Instance.DroppedCount);
    }

    [TestMethod]
    public void QueueIsBoundedAndCountsWhatItDiscards()
    {
      FctManager.Instance.MaxQueueAgeMs = 60_000; // keep the age rule out of the way; this is about the cap

      for (var i = 0; i < 700; i++)
      {
        FireDamage("TestPlayer", i);
      }

      Assert.AreEqual(FctManager.MaxPending, Drain());
      Assert.AreEqual(700 - FctManager.MaxPending, FctManager.Instance.DroppedCount);
    }

    [TestMethod]
    public void DisposeDisablesAndClearsTheFeed()
    {
      FireDamage("TestPlayer", 500);
      FctManager.Instance.Dispose();

      Assert.IsFalse(FctManager.Instance.Enabled);
      Assert.AreEqual(0, Drain());
    }

    private static void FireDamage(string attacker, double beginTime, bool crit = false, bool isMonitor = true,
      string defender = "SomeNpc", uint total = 100, string type = Labels.Dd, string subType = "melee") =>
      FctManager.Instance.HandleDamage(new DamageProcessedEvent
      {
        Record = new DamageRecord
        {
          Attacker = attacker,
          Defender = defender,
          Total = total,
          Type = type,
          SubType = subType,
          ModifiersMask = crit ? LineModifiersParser.Crit : LineModifiersParser.None,
        },
        BeginTime = beginTime,
        IsMonitor = isMonitor,
      });

    private static void FireHeal(string healer, double beginTime, string healed = "SomeNpc", bool isMonitor = true, uint total = 50, uint overTotal = 0,
      string type = Labels.Heal) =>
      FctManager.Instance.HandleHeal(new HealProcessedEvent
      {
        Record = new HealRecord
        {
          Healer = healer,
          Healed = healed,
          Total = total,
          OverTotal = overTotal,
          Type = type,
          SubType = "Blessing",
        },
        BeginTime = beginTime,
        IsMonitor = isMonitor,
      });

    private int Drain()
    {
      _drained.Clear();
      return FctManager.Instance.DrainTo(_drained);
    }
  }
}
namespace EQLogParser
{
  /* Linux-runnable tests for the chat/damage-sibling line parsers (healing, casts, misc).
   * Each parser is a static Process(LineData) that emits records into the RecordsStore;
   * assertions read the records back out of the store. Line fixtures are timestamp-stripped
   * action text, same convention as DamageLineParserTest. */
  /* Sets the process-wide CWD for data-file access, so it must not run concurrently with
   * other test classes. */
  [DoNotParallelize]
  [TestClass]
  public sealed class LineParsersTest
  {
    private EQDataStore? _dataStore;

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "TestPlayer";
      AdpsTracker.Instance.Clear();

      // data files are linked into the test output directory
      Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

      _dataStore = new EQDataStore();
      EQDataStore.Instance = _dataStore;

      RecordsStore.Instance.Clear();
      PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
      AdpsTracker.Instance.Clear();
      RecordsStore.Instance.Clear();
      PlayerRegistry.Instance.Clear();
      _dataStore = null;
    }

    private static LineData Line(string action, double time, long lineNumber = 0) =>
      new() { Action = action, BeginTime = time, LineNumber = lineNumber, Split = action.Split(' ') };

    #region HealingLineParser

    [TestMethod]
    public void Process_Healed_ParsesHealerSpellAndTotal()
    {
      var ok = HealingLineParser.Process(Line("Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.", 5));
      Assert.IsTrue(ok);

      var heal = RecordsStore.Instance.GetAllHeals().Single().Item2;
      Assert.AreEqual("Fllint", heal.Healer);
      Assert.AreEqual("Foob", heal.Healed);
      Assert.AreEqual(11820UL, heal.Total);
      Assert.AreEqual(Labels.Heal, heal.Type);
      Assert.AreEqual("Blessing of the Ancients III", heal.SubType);
    }

    [TestMethod]
    public void Process_OverTimeHeal_MarkedAsHot()
    {
      var ok = HealingLineParser.Process(Line("Snowzz healed Malkatar over time for 8211 hit points by Roar of the Lion 6.", 5));
      Assert.IsTrue(ok);

      var heal = RecordsStore.Instance.GetAllHeals().Single().Item2;
      Assert.AreEqual(Labels.Hot, heal.Type);
      Assert.AreEqual("Malkatar", heal.Healed);
      Assert.AreEqual(8211UL, heal.Total);
      Assert.AreEqual("Roar of the Lion 6", heal.SubType);
    }

    [TestMethod]
    public void Process_OverHeal_CapturesBothTotals()
    {
      var ok = HealingLineParser.Process(Line("Findawenye healed Piemastaj`s pet for 2823 (78079) hit points by Mending Splash Rk. III.", 5));
      Assert.IsTrue(ok);

      var heal = RecordsStore.Instance.GetAllHeals().Single().Item2;
      Assert.AreEqual("Findawenye", heal.Healer);
      Assert.AreEqual(2823UL, heal.Total);
      Assert.AreEqual(78079UL, heal.OverTotal);
    }

    [TestMethod]
    public void Process_SelfHealWithoutSpell_UsesSelfHealSubType()
    {
      var ok = HealingLineParser.Process(Line("Tolzol healed itself for 548 hit points.", 5));
      Assert.IsTrue(ok);

      var heal = RecordsStore.Instance.GetAllHeals().Single().Item2;
      // third-person pronouns normalize back to the actor, so a self-heal lands on the healer
      Assert.AreEqual("Tolzol", heal.Healer);
      Assert.AreEqual("Tolzol", heal.Healed);
      Assert.AreEqual(Labels.SelfHeal, heal.SubType);
    }

    [TestMethod]
    public void Process_NonHealLine_ReturnsFalseAndAddsNothing()
    {
      var ok = HealingLineParser.Process(Line("Kizant crushes Sontalak for 126225 points of damage.", 5));
      Assert.IsFalse(ok);
      Assert.AreEqual(0, RecordsStore.Instance.GetAllHeals().Count());
    }

    #endregion

    #region CastLineParser

    [TestMethod]
    public void Process_YouBeginCasting_RrecordsCastForCurrentPlayer()
    {
      var ok = CastLineParser.Process(Line("You begin casting Shield of Destiny Rk. II.", 10));
      Assert.IsTrue(ok);

      var cast = RecordsStore.Instance.GetSpellsDuring(0, 100).Select(r => r.Item2)
        .OfType<SpellCast>().Single();
      Assert.AreEqual(ConfigUtil.PlayerName, cast.Caster);
      Assert.AreEqual("Shield of Destiny Rk. II", cast.Spell);
      Assert.IsFalse(cast.Interrupted);
    }

    [TestMethod]
    public void Process_OtherActivates_RrecordsCastForThatPlayer()
    {
      var ok = CastLineParser.Process(Line("Stabborz activates Conditioned Reflexes Rk. II.", 10));
      Assert.IsTrue(ok);

      var cast = RecordsStore.Instance.GetSpellsDuring(0, 100).Select(r => r.Item2)
        .OfType<SpellCast>().Single();
      Assert.AreEqual("Stabborz", cast.Caster);
      Assert.AreEqual("Conditioned Reflexes Rk. II", cast.Spell);
    }

    [TestMethod]
    public void Process_OldStyleCast_ParsesAngledSpellName()
    {
      var ok = CastLineParser.Process(Line("Sylfvia begins to cast a spell. <Syllable of Mending Rk. II>", 10));
      Assert.IsTrue(ok);

      var cast = RecordsStore.Instance.GetSpellsDuring(0, 100).Select(r => r.Item2)
        .OfType<SpellCast>().Single();
      Assert.AreEqual("Sylfvia", cast.Caster);
      Assert.AreEqual("Syllable of Mending Rk. II", cast.Spell);
    }

    [TestMethod]
    public void Process_Interrupted_MarksMostRecentMatchingCast()
    {
      CastLineParser.Process(Line("You begin casting Stormjolt Vortex Rk. III.", 100));
      var ok = CastLineParser.Process(Line("Your Stormjolt Vortex Rk. III spell is interrupted.", 103));
      Assert.IsTrue(ok);

      var cast = RecordsStore.Instance.GetSpellsDuring(0, 200).Select(r => r.Item2)
        .OfType<SpellCast>().Single();
      Assert.IsTrue(cast.Interrupted);
    }

    [TestMethod]
    public void Process_ZoneEnter_RecordsZoneAndNothingElse()
    {
      // lands-on/zone lines are consumed before the main cast pass — Process reports false but the record is kept
      var ok = CastLineParser.Process(Line("You have entered The Eastern Wastes.", 10));
      Assert.IsFalse(ok);

      var zone = RecordsStore.Instance.GetAllZoning().Single().Item2;
      Assert.AreEqual("The Eastern Wastes", zone.Zone);
      Assert.AreEqual(0, RecordsStore.Instance.GetSpellsDuring(0, 200).Count());
    }

    #endregion

    #region MiscLineParser

    [TestMethod]
    public void Process_CorpseLoot_RrecordsItemAndNpc()
    {
      var ok = MiscLineParser.Process(Line("--You have looted a Cold-Forged Cudgel from Queen Dracnia's corpse.--", 5));
      Assert.IsTrue(ok);

      var loot = RecordsStore.Instance.GetAllLoot().Single().Item2;
      Assert.AreEqual(ConfigUtil.PlayerName, loot.Player);
      Assert.AreEqual("Cold-Forged Cudgel", loot.Item);
      Assert.AreEqual("Queen Dracnia", loot.Npc);
      Assert.AreEqual(1UL, loot.Quantity);
      Assert.IsFalse(loot.IsCurrency);
    }

    [TestMethod]
    public void Process_MasterLooterCurrency_ConvertsToCopper()
    {
      var ok = MiscLineParser.Process(Line("The master looter, Qulas, looted 32426 platinum from the corpse.", 5));
      Assert.IsTrue(ok);

      var loot = RecordsStore.Instance.GetAllLoot().Single().Item2;
      Assert.AreEqual("Qulas", loot.Player);
      Assert.IsTrue(loot.IsCurrency);
      Assert.AreEqual(1000, (int)loot.Quantity / 32426); // platinum -> copper rate
      Assert.AreEqual("32426 Platinum", loot.Item);
    }

    [TestMethod]
    public void Process_MultiCurrencySplit_SumsToCopper()
    {
      var ok = MiscLineParser.Process(Line("You receive 28 platinum, 7 gold, 2 silver and 5 copper as your split.", 5));
      Assert.IsTrue(ok);

      var loot = RecordsStore.Instance.GetAllLoot().Single().Item2;
      Assert.AreEqual(ConfigUtil.PlayerName, loot.Player);
      Assert.IsTrue(loot.IsCurrency);
      // 28p + 7g + 2s + 5c in copper
      Assert.AreEqual(28 * 1000 + 7 * 100 + 2 * 10 + 5, (int)loot.Quantity);
      Assert.AreEqual("28 Platinum, 7 Gold, 2 Silver, 5 Copper", loot.Item);
    }

    [TestMethod]
    public void Process_Resist_RrecordsAttackerDefenderAndSpell()
    {
      var ok = MiscLineParser.Process(Line("Test Ten resisted Xartik's Arcane Harmony Strike II!", 5));
      Assert.IsTrue(ok);

      var resist = RecordsStore.Instance.GetAllResists().Single().Item2;
      Assert.AreEqual("Xartik", resist.Attacker);
      Assert.AreEqual("Test Ten", resist.Defender);
      Assert.AreEqual("Arcane Harmony Strike II", resist.Spell);
    }

    [TestMethod]
    public void Process_DieRoll_RrecordsRolledRange()
    {
      var ok = MiscLineParser.Process(Line("**A Magic Die is rolled by Kizant. It could have been any number from 1 to 1000, but this time it turned up a 11.", 5));
      Assert.IsTrue(ok);

      var random = RecordsStore.Instance.GetAllRandoms().Single().Item2;
      Assert.AreEqual("Kizant", random.Player);
      Assert.AreEqual(1, random.From);
      Assert.AreEqual(1000, random.To);
      Assert.AreEqual(11, random.Rolled);
    }

    [TestMethod]
    public void Process_MezBreak_RrecordsAwakenedAndBreaker()
    {
      var ok = MiscLineParser.Process(Line("A shaded torch has been awakened by Drogbaa.", 5));
      Assert.IsTrue(ok);

      var mez = RecordsStore.Instance.GetAllMezBreaks().Single().Item2;
      Assert.AreEqual("Drogbaa", mez.Breaker);
      Assert.AreEqual("A shaded torch", mez.Awakened);
    }

    [TestMethod]
    public void Process_NeedRollWin_RrecordsLooterAndItem()
    {
      var ok = MiscLineParser.Process(Line("Hacket won the need roll on 1 item(s): Restless Velium Tainted Pelt with a roll of 996.", 5));
      Assert.IsTrue(ok);

      var loot = RecordsStore.Instance.GetAllLoot().Single().Item2;
      Assert.AreEqual("Hacket", loot.Player);
      Assert.AreEqual("Restless Velium Tainted Pelt", loot.Item);
      Assert.AreEqual("Won Roll (Not Looted)", loot.Npc);
    }

    [TestMethod]
    public void Process_LeftOnChest_RrecordsLooterAndChest()
    {
      var ok = MiscLineParser.Process(Line("--Aldryn left an Energized Minor Engram on a weathered chest .--", 5));
      Assert.IsTrue(ok);

      var loot = RecordsStore.Instance.GetAllLoot().Single().Item2;
      Assert.AreEqual("Aldryn", loot.Player);
      Assert.AreEqual("Energized Minor Engram", loot.Item);
      Assert.AreEqual("A weathered chest (Left on Chest)", loot.Npc);
    }

    #endregion

    #region PreLineParser

    [TestMethod]
    public void NeedProcessing_TargetedPlayer_VerifiesAndSkipsPipeline()
    {
      List<string> verified = [];
      var needProcessing = PreLineParser.NeedProcessing(
        Line("Targeted (Player)  Bob", 5), // production lines carry two spaces after the prefix
        (name, _) => verified.Add(name),
        (_, _) => true,
        _ => { });

      Assert.IsFalse(needProcessing);
      Assert.Contains("Bob", verified);
    }

    [TestMethod]
    public void NeedProcessing_JoinedRaid_VerifiesPlayer()
    {
      List<string> verified = [];
      var needProcessing = PreLineParser.NeedProcessing(
        Line("Cinda joined the raid.", 5),
        (name, _) => verified.Add(name),
        (_, _) => true,
        _ => { });

      Assert.IsFalse(needProcessing);
      Assert.Contains("Cinda", verified);
    }

    [TestMethod]
    public void NeedProcessing_JoinedGroup_RoutesByNameCheck()
    {
      List<string> verified = [];
      List<string> mercs = [];
      var needProcessing = PreLineParser.NeedProcessing(
        Line("Drek has joined the group.", 5),
        (name, _) => verified.Add(name),
        (_, _) => true,
        name => mercs.Add(name));

      Assert.IsFalse(needProcessing);
      Assert.Contains("Drek", verified);
      Assert.AreEqual(0, mercs.Count);

      verified.Clear();
      needProcessing = PreLineParser.NeedProcessing(
        Line("Grub has joined the group.", 5),
        (name, _) => verified.Add(name),
        (_, _) => false,
        name => mercs.Add(name));
      Assert.IsFalse(needProcessing);
      Assert.AreEqual(0, verified.Count);
      Assert.Contains("Grub", mercs);
    }

    [TestMethod]
    public void NeedProcessing_GlugLine_ExtractsDrinker()
    {
      List<string> verified = [];
      var needProcessing = PreLineParser.NeedProcessing(
        Line("Glug, glug, glug...  Fizz takes a drink from a flask.", 5),
        (name, _) => verified.Add(name),
        (_, _) => true,
        _ => { });

      Assert.IsFalse(needProcessing);
      Assert.Contains("Fizz", verified);
    }

    [TestMethod]
    public void NeedProcessing_OrdinaryLine_ReturnsTrue()
    {
      var needProcessing = PreLineParser.NeedProcessing(
        Line("Kizant crushes Sontalak for 126 points of damage.", 5),
        (_, _) => { }, (_, _) => true, _ => { });

      Assert.IsTrue(needProcessing);
    }

    [TestMethod]
    public void FindPossiblePlayerName_StopsAtSpaceAndFlagsCrossServer()
    {
      // plain name up to the stop char
      var end = PreLineParser.FindPossiblePlayerName("Bob takes a drink", out var cross, 0, -1, ' ');
      Assert.AreEqual(3, end);
      Assert.IsFalse(cross);

      // cross-server dot (realm.name) is allowed, double dots are not
      end = PreLineParser.FindPossiblePlayerName("Foo.Bar takes a drink", out cross, 0, -1, ' ');
      Assert.AreEqual(7, end);
      Assert.IsTrue(cross);

      end = PreLineParser.FindPossiblePlayerName("Foo..Bar takes", out cross, 0, -1, ' ');
      Assert.AreEqual(-1, end);
    }

    #endregion
  }
}

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

      // The suite's one shared store (loading the data files per test was most of this class's wall time).
      _dataStore = PipelineHarness.SharedDataStore();

      RecordsStore.Instance.Clear();
      HealTap.Clear();
      PlayerRegistry.Instance.Clear();

    }

    [TestCleanup]
    public void Cleanup()
    {
      AdpsTracker.Instance.Clear();
      RecordsStore.Instance.Clear();
      HealTap.Clear();
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

      var heal = HealTap.All().Single().Item2;
      Assert.AreEqual("Fllint", heal.Healer);
      Assert.AreEqual("Foob", heal.Healed);
      Assert.AreEqual(11820UL, heal.Total);
      Assert.AreEqual(Labels.Heal, heal.Type);
      Assert.AreEqual("Blessing of the Ancients III", heal.SubType);
    }

    /*
     * `Bastion of Divinity Rk. II healed Xxuro …` puts a SPELL in the healer slot and names no person. The period of
     * "Rk." is not a sentence end, so the parser must not read "the word after the last period" as the actor: it used to,
     * and the identity list filled with rows called "II" and "III" (measured on eqlog_Kizant_xegony-2.txt). The line is
     * refused whole, like every other caster-less heal subject; what must never happen again is a rank fragment walking
     * out of this parser as an entity name.
     */
    [TestMethod]
    public void Process_SpellRankSubject_MintsNoRankFragmentName()
    {
      var ok = HealingLineParser.Process(Line(
        "Bastion of Divinity Rk. II healed Xxuro over time for 6670 hit points by Bastion of Divinity Effect II.", 5));

      Assert.IsFalse(ok);
      Assert.AreEqual(0, HealTap.All().Count());
    }

    // The three glued-sentence shapes the punctuation branch exists for still find their healer.
    [TestMethod]
    public void Process_HealerAfterGluedSentence_StillParsesTheActor()
    {
      Assert.IsTrue(HealingLineParser.Process(Line(
        "Your ward heals you as it breaks! You healed Niktaza for 8970 (86306) hit points by Healing Ward. (Critical)", 5)));
      Assert.IsTrue(HealingLineParser.Process(Line(
        "Rowanoak is soothed by Brell's Soothing Wave. Farzi healed Rowanoak for 524 hit points by Brell's Sacred Soothing Wave.", 6)));
      Assert.IsTrue(HealingLineParser.Process(Line(
        "Foob's promised interposition is fulfilled Foob healed himself for 44238 hit points by Promised Interposition Heal V. (Lucky Critical)", 7)));

      var heals = HealTap.All().Select(h => h.Item2).ToList();
      Assert.AreEqual(3, heals.Count);
      Assert.AreEqual("TestPlayer", heals[0].Healer, "the bang shape still hands the heal to the local player");
      Assert.AreEqual("Niktaza", heals[0].Healed);
      Assert.AreEqual("Farzi", heals[1].Healer, "a period that really does end a sentence still ends it");
      Assert.AreEqual("Foob", heals[2].Healer);
    }

    // A rank formulation on the SPELL slot (not the subject) was never affected and must stay parseable.
    [TestMethod]
    public void Process_RankFormulationOnSpellSlot_KeepsHealerAndSpell()
    {
      Assert.IsTrue(HealingLineParser.Process(Line(
        "Findawenye healed Piemastaj`s pet for 2823 (78079) hit points by Mending Splash Rk. III. (Critical)", 5)));

      var heal = HealTap.All().Single().Item2;
      Assert.AreEqual("Findawenye", heal.Healer);
      Assert.AreEqual("Mending Splash Rk. III", heal.SubType);
      Assert.AreEqual(2823UL, heal.Total);
      Assert.AreEqual(78079UL, heal.OverTotal);
    }

    [TestMethod]
    public void Process_RepeatedHeal_KeepsEveryLineAsItsOwnValueEqualRecord()
    {
      // The repeat cache used to collapse repeated heals onto one instance; it is gone because nothing
      // retained those instances any more (fact rows and FCT commands copy the values), and the store it
      // filled was pure process-lifetime retention. What stays pinned here is the half that mattered:
      // value equality, so a repeat of a heal reads back as the same heal on every field.
      const string Text = "Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.";
      HealingLineParser.Process(Line(Text, 5));
      HealingLineParser.Process(Line(Text, 6));

      var heals = HealTap.All().ToList();
      Assert.AreEqual(2, heals.Count);
      Assert.AreEqual(heals[0].Item2, heals[1].Item2, "a restated heal is value-equal on every stored field");
      Assert.AreNotSame(heals[0].Item2, heals[1].Item2, "each line keeps its own record - no sharing store any more");
    }

    [TestMethod]
    public void Process_HealDifferingInAnyField_KeepsSeparateRecords()
    {
      HealingLineParser.Process(Line("Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.", 5));
      HealingLineParser.Process(Line("Fllint healed Foob for 11821 hit points by Blessing of the Ancients III.", 6));
      HealingLineParser.Process(Line("Fllint healed Foob for 11820 hit points by Cure of the Dead III.", 7));

      var heals = HealTap.All().Select(h => h.Item2).ToList();
      Assert.AreEqual(3, heals.Count);
      Assert.AreEqual(3, heals.Distinct().Count());
    }

    [TestMethod]
    public void Process_HealSpellName_RoundTripsThroughTheIdStore()
    {
      // a record keeps an id per name (HitRecord), so what comes back has to be the exact text the parser
      // settled on — here StringCache.GetOrAdd's title casing, unchanged from before records held ids
      HealingLineParser.Process(Line("Zezil healed Zezil for 100 hit points by healing MEND.", 5));
      Assert.AreEqual("Healing MEND", HealTap.All().Single().Item2.SubType);
    }

    [TestMethod]
    public void Process_OverTimeHeal_MarkedAsHot()
    {
      var ok = HealingLineParser.Process(Line("Snowzz healed Malkatar over time for 8211 hit points by Roar of the Lion 6.", 5));
      Assert.IsTrue(ok);

      var heal = HealTap.All().Single().Item2;
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

      var heal = HealTap.All().Single().Item2;
      Assert.AreEqual("Findawenye", heal.Healer);
      Assert.AreEqual(2823UL, heal.Total);
      Assert.AreEqual(78079UL, heal.OverTotal);
    }

    [TestMethod]
    public void Process_SelfHealWithoutSpell_UsesSelfHealSubType()
    {
      var ok = HealingLineParser.Process(Line("Tolzol healed itself for 548 hit points.", 5));
      Assert.IsTrue(ok);

      var heal = HealTap.All().Single().Item2;
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
      Assert.AreEqual(0, HealTap.All().Count());
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

    /* What a spin-off client like EverQuest Legends produces: the cast line parses fine, but the name is not in the
     * bundled spell data. The record keeps an IsUnknown stub instead of nothing, and it stays out of the real DB -
     * later casts reuse the same stub. */
    [TestMethod]
    public void Process_UnknownSpellName_RecordsCastWithAnUnknownStub()
    {
      var spell = $"Totally New Spell {Guid.NewGuid():N}";
      Assert.IsNotNull(_dataStore);
      Assert.IsNull(_dataStore.GetSpellByName(spell));

      var ok = CastLineParser.Process(Line($"You begin casting {spell}.", 10));
      Assert.IsTrue(ok);

      var cast = RecordsStore.Instance.GetSpellsDuring(0, 100).Select(r => r.Item2)
        .OfType<SpellCast>().Single();
      Assert.AreEqual(spell, cast.Spell);
      Assert.IsNotNull(cast.SpellData);
      Assert.IsTrue(cast.SpellData.IsUnknown);
      Assert.AreSame(cast.SpellData, EQDataStore.Instance.AddUnknownSpell(spell));
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
      Assert.AreEqual((28 * 1000) + (7 * 100) + (2 * 10) + 5, (int)loot.Quantity);
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
    public void Process_Resist_YourSpellNamesTheLocalPlayer()
    {
      Assert.IsTrue(MiscLineParser.Process(Line("A corrupted egg resisted your Force of Flame XXI!", 5)));

      var resist = RecordsStore.Instance.GetAllResists().Single().Item2;
      Assert.AreEqual("TestPlayer", resist.Attacker);
      Assert.AreEqual("A corrupted egg", resist.Defender);
      Assert.AreEqual("Force of Flame XXI", resist.Spell);
    }

    [TestMethod]
    public void Process_Resist_YourSpellWithNoPlayerNameStillNamesAnActor()
    {
      /*
       * ConfigUtil.PlayerName is empty before MainWindow picks a file, and in any headless run. The `your` branch of
       * the resist case used to hand that null straight to ResistRecord.Attacker, which StatsUtil.PopulateSpecials then
       * tried to use as a ConcurrentDictionary key: one mob resisting your spell threw out of EVERY damage and healing
       * board build for the rest of the session, so the panes quietly kept showing whatever they last painted.
       */
      ConfigUtil.PlayerName = "";
      try
      {
        Assert.IsTrue(MiscLineParser.Process(Line("A corrupted egg resisted your Force of Flame XXI!", 5)));

        var resist = RecordsStore.Instance.GetAllResists().Single().Item2;
        Assert.IsNotNull(resist.Attacker, "an unnamed local player is an UNKNOWN actor, never a hole in a record");
        Assert.AreEqual(Labels.Unk, resist.Attacker);
        Assert.IsTrue(ParserUtil.IsUnattributedName(resist.Attacker),
            "the placeholder has to be the word the rest of the parser already refuses to read as an entity, not a new one");
        Assert.AreEqual("A corrupted egg", resist.Defender, "the resisting mob is still named, and its resist stats still count");
        Assert.AreEqual("Force of Flame XXI", resist.Spell);
      }
      finally
      {
        ConfigUtil.PlayerName = "TestPlayer";
      }
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

    /*
     * Consume lines (R17's input). Three things are asserted by the shape of the data, not by taste: both sounds,
     * ANY vessel (the item name is whatever is being consumed) and case-insensitive matching, because EQ writes
     * the sound effect from client emote text. The actor field is the whole point — only a player character eats
     * or drinks.
     */
    [TestMethod]
    public void NeedProcessing_ConsumeLines_VerifyTheActor()
    {
      foreach (var line in new[]
      {
        "Glug, glug, glug...  Bithika takes a drink from their Water Flask.",
        "Chomp, chomp, chomp...  Bithika takes a bite from their Fresh Fish.",
        "glug, glug, glug...  Bithika takes a DRINK from an Ironbone Mead.",
      })
      {
        List<string> verified = [];
        Assert.IsFalse(PreLineParser.NeedProcessing(Line(line, 5), (name, _) => verified.Add(name), (_, _) => true, _ => { }),
          $"consume line reached the rest of the pipeline: {line}");
        CollectionAssert.Contains(verified, "Bithika", line);
      }
    }

    [TestMethod]
    public void NeedProcessing_ConsumeLine_RejectsQualifiedNamesAndOtherShapes()
    {
      foreach (var line in new[]
      {
        // Server-qualified names are never a key this pipeline uses (same rule the drink branch always applied).
        "Glug, glug, glug...  Bithika.Karana takes a drink from their Water Flask.",
        // Same lead-in, different verb: not evidence of anything.
        "Glug, glug, glug...  Bithika sharpens their sword.",
        // A mob eating someone: the actor field is multi-word, so this is not a name at all.
        "Chomp, chomp, chomp...  A rabid gnawbat takes a bite of Bithika.",
      })
      {
        List<string> verified = [];
        PreLineParser.NeedProcessing(Line(line, 5), (name, _) => verified.Add(name), (_, _) => true, _ => { });
        Assert.AreEqual(0, verified.Count, $"verified a player from: {line}");
      }
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

    /*
     * The whole point of this parser: a line it recognizes is TAKEN, so the four real parsers never split it.
     * Asserted in both directions on purpose - a guard that claimed every line would satisfy "the tilde line is
     * dropped" while quietly deleting the raid, so each test below keeps an ordinary sentence as its control.
     *
     * The tilde line is the newest shape (`~ Kizant wonders.`): the client writes it for a thought of its own and
     * nothing downstream has any use for it. It claims nothing either, which the second test pins - a name inside a
     * line nobody watched ACT is not evidence.
     */
    [TestMethod]
    public void NeedProcessing_TildeLine_IsTakenAtTheDoor()
    {
      foreach (var line in new[] { "~ Kizant wonders.", "~ Ghoul thinks about lunch." })
      {
        List<string> verified = [];
        Assert.IsFalse(PreLineParser.NeedProcessing(Line(line, 5), (name, _) => verified.Add(name), (_, _) => true, _ => { }),
          $"tilde line reached the rest of the pipeline: {line}");
        Assert.AreEqual(0, verified.Count, line);
      }

      // The control: the same sentence without its tilde is an ordinary line and stays available.
      Assert.IsTrue(PreLineParser.NeedProcessing(Line("Kizant wonders.", 5), (_, _) => { }, (_, _) => true, _ => { }));
    }

    [TestMethod]
    public void ATildeLineClaimsNothingAsEvidence()
    {
      var seen = new List<(string Name, byte Kind)>();
      Action<string, double, byte> onData = (name, _, kind) => seen.Add((name, kind));
      PreLineParser.EventsEvidence += onData;
      try
      {
        PreLineParser.NeedProcessing(Line("~ Kizant wonders.", 5), (_, _) => { }, (_, _) => true, _ => { });
      }
      finally
      {
        PreLineParser.EventsEvidence -= onData;
      }

      Assert.AreEqual(0, seen.Count, string.Join(", ", seen.Select(s => $"{s.Name}/{s.Kind}")));
    }

    /*
     * `Targeted (NPC): <name>` - claimed since R1 read it, and the line was still walking four parsers to be thrown
     * away. The evidence has to survive the claiming (that is the rule's whole input), and a line with an empty tail
     * must not fire an evidence fact about "" - which is what the direct event invoke used to do.
     */
    [TestMethod]
    public void NeedProcessing_TargetedNpc_ReportsTheNameAndTakesTheLine()
    {
      var seen = new List<(string Name, byte Kind)>();
      Action<string, double, byte> onData = (name, _, kind) => seen.Add((name, kind));
      PreLineParser.EventsEvidence += onData;
      try
      {
        Assert.IsFalse(PreLineParser.NeedProcessing(Line("Targeted (NPC): an astral barnacle", 5),
                                                    (_, _) => { }, (_, _) => true, _ => { }),
                       "Targeted (NPC) reached the rest of the pipeline");
      }
      finally
      {
        PreLineParser.EventsEvidence -= onData;
      }

      Assert.AreEqual(1, seen.Count);
      Assert.AreEqual("an astral barnacle", seen[0].Name);
      Assert.AreEqual(EvidenceFact.EvTargetedNpc, seen[0].Kind);
    }

    [TestMethod]
    public void ATargetedNpcLineWithNoName_FiresNoEvidenceButIsStillTaken()
    {
      var seen = new List<(string Name, byte Kind)>();
      Action<string, double, byte> onData = (name, _, kind) => seen.Add((name, kind));
      PreLineParser.EventsEvidence += onData;
      try
      {
        Assert.IsFalse(PreLineParser.NeedProcessing(Line("Targeted (NPC): ", 5),
                                                    (_, _) => { }, (_, _) => true, _ => { }));
      }
      finally
      {
        PreLineParser.EventsEvidence -= onData;
      }

      Assert.AreEqual(0, seen.Count, string.Join("/", seen.Select(s => s.Name)));
    }

    /*
     * R22's achievement line, same law: recognized means taken. And a line that only STARTS like the shape
     * (`Your guildmate said things.`) is not recognized, so it keeps its chance at the parsers - which is why
     * `found` follows the parse rather than the prefix.
     */
    [TestMethod]
    public void NeedProcessing_GuildmateAchievement_ReportsTheNameAndTakesTheLine()
    {
      var seen = new List<(string Name, byte Kind)>();
      Action<string, double, byte> onData = (name, _, kind) => seen.Add((name, kind));
      PreLineParser.EventsEvidence += onData;
      try
      {
        Assert.IsFalse(PreLineParser.NeedProcessing(
          Line("Your guildmate Blazem has completed Partisan of Candlemaker's Workshop achievement.", 5),
          (_, _) => { }, (_, _) => true, _ => { }),
          "the achievement line reached the rest of the pipeline");

        Assert.IsTrue(PreLineParser.NeedProcessing(Line("Your guildmate said things.", 5),
                                                   (_, _) => { }, (_, _) => true, _ => { }),
                      "a line the recognizer refused should stay available to the parsers");
      }
      finally
      {
        PreLineParser.EventsEvidence -= onData;
      }

      Assert.AreEqual(1, seen.Count);
      Assert.AreEqual("Blazem", seen[0].Name);
      Assert.AreEqual(EvidenceFact.EvGuildmate, seen[0].Kind);
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

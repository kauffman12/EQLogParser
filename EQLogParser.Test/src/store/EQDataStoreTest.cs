namespace EQLogParser
{
  /* Direct tests for EQDataStore (the combat-log knowledge base) + ConfigUtil.ReadList.
   * Class-name registration comes from a host-provided label hook, so tests wire a tiny
   * deterministic mapping instead of the WPF resx. Data-file-backed tests rely on the real
   * data files linked into the test output directory (CWD = BaseDirectory). */
  /* Swaps the process-wide CWD and CombatRecordLookup hooks, so it must not run
   * concurrently with any other test in this assembly. */
  [DoNotParallelize]
  [TestClass]
  public sealed class EQDataStoreTest
  {
    private string? _originalCwd;
    private Func<string, string?>? _originalClassLabels;

    /*
     * Stores keyed by the label map they were BUILT with. Loading a store parses spells.txt and its siblings (~0.6 s), which
     * most of this class paid per test — but the labels are baked in at construction (class-safe family seeding, the class
     * maps), so a store built for one caller's label set can never answer another's. Keying on the set shares the common one
     * and keeps every custom map honest.
     */
    private static readonly Dictionary<string, EQDataStore> SharedStores = [];

    [TestInitialize]
    public void Setup()
    {
      _originalCwd = Environment.CurrentDirectory;
      _originalClassLabels = CombatRecordLookup.ClassLabelByEnumName;
      Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
    }

    [TestCleanup]
    public void Cleanup()
    {
      if (_originalCwd is not null)
      {
        Environment.CurrentDirectory = _originalCwd;
      }
      // restore rather than reset — other test classes may carry their own wiring.
      // "" (not null): the hook's consumers only test IsNullOrEmpty, and it keeps this
      // NRT-enabled test project warning-free (the hook type is nullable-oblivious in Core).
      CombatRecordLookup.ClassLabelByEnumName = _originalClassLabels ?? (_ => "");
    }

    private static EQDataStore StoreWithClasses(params (string enumName, string label)[] classes)
    {
      var map = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (enumName, label) in classes)
      {
        map[enumName] = label;
      }

      CombatRecordLookup.ClassLabelByEnumName = name => map.TryGetValue(name, out var value) ? value : null;

      var key = string.Join(",", classes.Select(c => c.enumName + "=" + c.label));
      if (SharedStores.TryGetValue(key, out var cached))
      {
        return cached;
      }

      /* The load happens with the working directory pinned: whichever test asks first must not decide whether the shared store saw the data files. */
      var cwd = Environment.CurrentDirectory;
      try
      {
        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var store = new EQDataStore();
        SharedStores[key] = store;
        return store;
      }
      finally
      {
        Environment.CurrentDirectory = cwd;
      }
    }

    #region ParseCustomSpellData

    [TestMethod]
    public void ParseCustomSpellData_FullRow_MapsEveryColumn()
    {
      // ^-separated columns exactly as data/spells.txt lays them out
      var line = string.Join("^",
        "7",                          // 0  Id
        "Test Spell Rk. II",          // 1  Name
        "150",                        // 2  Level
        "10",                         // 3  Duration (in 6-second ticks -> seconds)
        "1",                          // 4  Beneficial
        "3",                          // 5  MaxHits
        "5",                          // 6  Target
        "2",                          // 7  ClassMask
        "-1",                         // 8  Damaging (negative == healing)
        "0",                          // 9  CombatSkill (unused)
        "4",                          // 10 Resist
        "1",                          // 11 SongWindow
        "5",                          // 12 Adps
        "1",                          // 13 Mgb
        "2",                          // 14 Rank
        "1",                          // 15 Charm (SPA 22)
        "0", "0",                     // 16/17 HasAmbiguity flags
        "you are affected by Test Spell",      // 18 LandsOnYou
        "Test Spell lands on them",            // 19 LandsOnOther
        "Test Spell wears off of you");        // 20 WearOff

      var spell = new EQDataStore().ParseCustomSpellData(line);
      Assert.IsNotNull(spell);
      Assert.AreEqual("7", spell.Id);
      Assert.AreEqual("Test Spell Rk. II", spell.Name);
      Assert.AreEqual(150, spell.Level);
      Assert.AreEqual(60, spell.Duration); // 10 ticks * 6s
      Assert.IsTrue(spell.IsBeneficial);
      Assert.AreEqual(5, spell.Target);
      Assert.AreEqual(3, spell.MaxHits);
      Assert.AreEqual((ushort)2, spell.ClassMask);
      Assert.AreEqual((short)-1, spell.Damaging);
      Assert.AreEqual((SpellResist)4, spell.Resist);
      Assert.IsTrue(spell.SongWindow);
      Assert.AreEqual(5, spell.Adps);
      Assert.IsTrue(spell.Mgb);
      Assert.AreEqual(2, spell.Rank);
      Assert.IsTrue(spell.Charm);
      Assert.IsFalse(spell.HasAmbiguity);
      Assert.AreEqual("you are affected by Test Spell", spell.LandsOnYou);
      Assert.AreEqual("Test Spell lands on them", spell.LandsOnOther);
      Assert.AreEqual("Test Spell wears off of you", spell.WearOff);
    }

    [TestMethod]
    public void ParseCustomSpellData_StripsRankFromAbbreviation()
    {
      // 21 columns, with charm after rank.
      var line = string.Join("^", "8", "Focus of Arcanum VI", "100", "0", "0", "0", "5", "2", "1",
        "0", "0", "0", "0", "0", "1", "0", "0", "0", "", "", "");
      var spell = new EQDataStore().ParseCustomSpellData(line);

      Assert.IsNotNull(spell);
      // roman-numeral ranks are stripped for the abbreviation index
      StringAssert.StartsWith("Focus of Arcanum", spell.NameAbbrv);
      Assert.IsFalse(spell.NameAbbrv.Contains("VI"));
    }

    [TestMethod]
    public void ParseCustomSpellData_ShortOrEmptyLine_ReturnsNull()
    {
      var store = new EQDataStore();
      Assert.IsNull(store.ParseCustomSpellData("1^2^3^4^5^6^7^8^9^10")); // < 11 columns
      Assert.IsNull(store.ParseCustomSpellData(""));
      Assert.IsNull(store.ParseCustomSpellData(null));
    }

    #endregion

    #region Class registration (via host label hook)

    [TestMethod]
    public void Constructor_RegistersClassesFromHook()
    {
      // hook keys are the upper-cased SpellClass enum names (Clr, War, ...)
      var store = StoreWithClasses(("CLR", "Cleric"), ("WAR", "Warrior"));

      Assert.AreEqual(2, store.GetClassListCount());
      Assert.IsTrue(store.IsValidClassName("Cleric"));
      Assert.IsTrue(store.IsValidClassName("Warrior"));
      Assert.IsFalse(store.IsValidClassName("Shaman"));
      Assert.IsFalse(store.IsValidClassName(""));
      Assert.IsTrue(store.GetClassEnum("Cleric") == SpellClass.Clr);
      // unregistered classes resolve to the zero value, never to a real class
      var unknown = store.GetClassEnum("Shaman");
      Assert.IsFalse(unknown == SpellClass.Clr || unknown == SpellClass.War);
      CollectionAssert.Contains(store.GetClassList(), "Warrior");
    }

    #endregion

    #region Real data file lookups (data/ linked into test output)

    [TestMethod]
    public void GetHealingSpellByName_ReturnsNegativeDamagingVariantOnly()
    {
      var store = new EQDataStore();

      // Soothing Wave is a known beneficial spell (damaging < 0) in spells.txt
      var healing = store.GetHealingSpellByName("Soothing Wave");
      Assert.IsNotNull(healing);
      Assert.IsTrue(healing.Damaging < 0);

      // Infusion of Holy Light II exists but is not a heal by the damaging filter
      Assert.IsNull(store.GetHealingSpellByName("Infusion of Holy Light II"));
      Assert.IsNull(store.GetHealingSpellByName(null));
    }

    [TestMethod]
    public void NpcAndOldSpellLookups_UseRealData()
    {
      var store = new EQDataStore();

      // straight from data/npcs.txt / data/oldspells.txt (first entries, stable)
      Assert.IsTrue(store.IsKnownNpc("A bomb"));
      Assert.IsFalse(store.IsKnownNpc("TotallyBogusNpc-xyz"));
      Assert.IsTrue(store.IsOldSpell("A Gnome in Hiding"));
      Assert.IsFalse(store.IsOldSpell("NotAnOldSpellAtAll"));

      /*
       * data/mercs.txt, the bot table: same case-blind dictionary, and one row of each spelling the file actually uses
       * (an article written lowercase, a name typed with capitals). What R27 does with a hit is MercDatabaseTierTest's;
       * this pins only that the file got loaded and that a name absent from it answers false.
       */
      Assert.IsTrue(store.IsKnownMerc("a hired bazu"));
      Assert.IsTrue(store.IsKnownMerc("CANDLEFOLK MENDER"));
      Assert.IsFalse(store.IsKnownMerc("TotallyBogusMerc-xyz"));
    }

    [TestMethod]
    public void GetClassFromTitle_MapsClassParenthesizedTitles()
    {
      var store = new EQDataStore();

      // data/titles.txt: Cleric=Vicar,Templar,High Priest,...
      Assert.AreEqual("Cleric", store.GetClassFromTitle("Cleric"));
      Assert.AreEqual("Cleric", store.GetClassFromTitle("High Priest (Cleric)"));
      Assert.IsNull(store.GetClassFromTitle("Vicar")); // bare titles are not mapped
    }

    [TestMethod]
    public void MissingDataFiles_StoreDegradesGracefully()
    {
      var emptyDir = Directory.CreateTempSubdirectory("eqds-empty-").FullName;
      try
      {
        Environment.CurrentDirectory = emptyDir;
        CombatRecordLookup.ClassLabelByEnumName = _ => null;

        var store = new EQDataStore(); // must not throw with every data file absent
        Assert.IsNull(store.GetSpellByName("Soothing Wave"));
        Assert.IsFalse(store.IsOldSpell("A Gnome in Hiding"));
        Assert.IsFalse(store.IsKnownNpc("A bomb"));
        Assert.AreEqual(0, store.GetClassListCount());
      }
      finally
      {
        if (_originalCwd is not null) Environment.CurrentDirectory = _originalCwd;
      }
    }

    #endregion

    #region Unknown spells

    [TestMethod]
    public void AddUnknownSpell_CreatesAndDedupes()
    {
      var store = new EQDataStore();
      var name = "Completely Unlogged Spell";

      var first = store.AddUnknownSpell(name);
      Assert.IsNotNull(first);
      Assert.IsTrue(first.IsUnknown);
      Assert.AreEqual(name, first.Name);

      // second call returns the same instance, not a duplicate
      Assert.AreSame(first, store.AddUnknownSpell(name));
    }

    #endregion

    #region ConfigUtil.ReadList

    [TestMethod]
    public void ReadList_WindowsStyleBackslashPath_ResolvesOnCurrentPlatform()
    {
      // callers write @"data\titles.txt" — on non-Windows hosts that must still resolve,
      // otherwise every data load silently returns empty (regression: separator fix)
      var lines = ConfigUtil.ReadList(@"data\titles.txt");
      Assert.IsTrue(lines.Count > 0, "expected the linked titles.txt to be found via backslash path");
    }

    [TestMethod]
    public void ReadList_MissingFile_ReturnsEmptyWithoutThrowing()
    {
      var lines = ConfigUtil.ReadList(@"data\definitely-not-a-real-file-12345.txt");
      Assert.AreEqual(0, lines.Count);
    }

    #endregion
  }
}

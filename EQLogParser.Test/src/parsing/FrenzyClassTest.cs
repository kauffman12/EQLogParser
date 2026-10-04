using Moq;

namespace EQLogParser
{
  /* Whoever frenzies IS a berserker: `X frenzies on Y for N points of damage.` is the berserker
   * frenzy AA landing. Across 8 captures all 85,685 frenzy lines carry a named player and ZERO
   * article-shaped actors — and a monster's frenzy is a different line entirely ("is struck by a
   * frenzied assault"), which names no attacker to misattribute. The claim is CLASS-only: the actor
   * need not be a player (the verb says berserker, never "one of us"), and it is time-bounded —
   * class changes mid-log (Covennx has frenzy lines in the 2025-26 captures and none before), so
   * this rides PlayerRegistry's time-bounded class records rather than any permanent verdict.
   * Sets the process-wide CWD for data-file access, so it must not run concurrently with other
   * test classes. */
  [DoNotParallelize]
  [TestClass]
  public sealed class FrenzyClassTest
  {
    private Func<string, bool>? _originalIsValidClass;

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "TestPlayer";

      // The shared harness injects the host class-label lookup; without it EQDataStore's class maps
      // are empty headless. SetActivePlayerClass additionally gates on CombatRecordLookup.
      // IsValidClassName, which defaults to `_ => false` and is wired by App.xaml.cs in production -
      // unwired here, every class write (frenzy or cast) would be refused in silence.
      PipelineHarness.EnsureDataStore();
      _originalIsValidClass = CombatRecordLookup.IsValidClassName;
      CombatRecordLookup.IsValidClassName = name => EQDataStore.Instance.IsValidClassName(name);
      PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
      PlayerRegistry.Instance.Clear();
      CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);
    }

    private static DamageRecord? Process(string action, double beginTime)
    {
      DamageRecord? captured = null;
      void OnDamage(DamageProcessedEvent e) => captured = e.Record;
      DamageLineParser.EventsDamageProcessed += OnDamage;
      try
      {
        DamageLineParser.Process(new LineData { Action = action, Split = action.Split(' '), BeginTime = beginTime });
      }
      finally
      {
        DamageLineParser.EventsDamageProcessed -= OnDamage;
      }

      return captured;
    }

    [TestMethod]
    public void AFrenziedAttackerIsABerserkerFromThatMoment()
    {
      var berserker = EQDataStore.Instance.GetClassLabel(SpellClass.Ber);
      Assert.AreEqual("Berserker", berserker, "the class write and the registry must speak the host label vocabulary");

      var record = Process("Tolzol frenzies on a scalewrought skystrike for 133065 points of damage. (Strikethrough Critical)", 100);
      Assert.IsNotNull(record);
      Assert.AreEqual("Tolzol", record!.Attacker);
      Assert.AreEqual("Frenzies", record.SubType);
      Assert.AreEqual(133065u, record.Total);

      // Readable from the frenzy's own second onward (with no older record the store answers its
      // first window even for earlier times - the store's documented read-back, not a backdating).
      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 100));
      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 500));
    }

    [TestMethod]
    public void AFrenzyNeverClaimsTheActorIsAPlayer()
    {
      // The verb proves a berserker, not a raid member: identity stays wherever the other evidence
      // left it. A frenzy must never verify somebody as a player by itself.
      Process("Grundo frenzies on a cabin boy for 900 points of damage.", 100);

      Assert.AreEqual(EQDataStore.Instance.GetClassLabel(SpellClass.Ber), PlayerRegistry.Instance.GetPlayerClass("Grundo", 100));
      Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Grundo"));
    }

    [TestMethod]
    public void AClassChangeMidLogKeepsBothWindowsReadable()
    {
      // The requirement the frenzy write leans on: class changes mid-log, and the registry keeps
      // BOTH readings. Its anti-flap rule (confidence 2 = corroborating evidence): a lone odd spell
      // counts as an alternative hypothesis but does not flip anything; eight of them commit from
      // the first sighting's own second, leaving the earlier window intact. Same confidence the
      // cast line uses, so frenzy and casts are peers under one rule.
      var berserker = EQDataStore.Instance.GetClassLabel(SpellClass.Ber);
      var cleric = EQDataStore.Instance.GetClassLabel(SpellClass.Clr);
      Process("Tolzol frenzies on a scalewrought skystrike for 100 points of damage.", 100);

      PlayerRegistry.Instance.SetActivePlayerClass("Tolzol", cleric, 2, 200);
      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 250), "one contradicting cast must not flip the class");

      for (var i = 1; i < 8; i++)
      {
        PlayerRegistry.Instance.SetActivePlayerClass("Tolzol", cleric, 2, 200 + i);
      }

      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 150));
      Assert.AreEqual(cleric, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 250));
    }

    [TestMethod]
    public void ASecondPersonFrenzyStillParsesToDamage()
    {
      // The class write rides the existing two-word-verb machinery (the "on" skip); that machinery
      // predates it and the record must come out untouched.
      var record = Process("You frenzy on a scalewrought skystrike for 42 points of damage.", 100);

      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record!.Attacker);
      Assert.AreEqual(42u, record.Total);
      Assert.AreEqual(EQDataStore.Instance.GetClassLabel(SpellClass.Ber), PlayerRegistry.Instance.GetPlayerClass(ConfigUtil.PlayerName, 100));
    }
  }
}

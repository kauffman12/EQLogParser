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

    public TestContext TestContext { get; set; } = null!;

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

    [TestMethod]
    public void AReclassIsReadAtItsBoundaryWithoutAnArrayPerRead()
    {
      /*
       * The read a board row pays for. Every stats builder fills a Class column by asking this once per player row per
       * rebuild — a couple of hundred times a second during a live raid, thousands at once on a select-all — so both halves
       * belong here: WHICH side of a boundary answers, and what the answer costs.
       */
      var berserker = EQDataStore.Instance.GetClassLabel(SpellClass.Ber);
      var cleric = EQDataStore.Instance.GetClassLabel(SpellClass.Clr);

      PlayerRegistry.Instance.SetActivePlayerClass("Tolzol", berserker, 1, 100);
      PlayerRegistry.Instance.SetActivePlayerClass("Tolzol", cleric, 1, 400);

      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 50), "before the first boundary answers the first one — a window does not backdate");
      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 100));
      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 399));
      Assert.AreEqual(cleric, PlayerRegistry.Instance.GetPlayerClass("Tolzol", 400), "a reclass takes effect at its own second");
      Assert.AreEqual(cleric, PlayerRegistry.Instance.GetPlayerClass("Tolzol", double.PositiveInfinity));

      /*
       * And the answer is read in place. It used to copy the boundary list into a new array and search that outside the lock:
       * one allocation per row, to avoid holding a lock for the few comparisons it makes over a list that is one entry long
       * for almost every name. The probe below is what distinguishes the two shapes — the old code cost ~32-56 bytes a read.
       */
      const int reads = 20_000;

      for (var i = 0; i < 500; i++)
      {
        PlayerRegistry.Instance.GetPlayerClass("Tolzol", 399);
      }

      var start = GC.GetAllocatedBytesForCurrentThread();
      for (var i = 0; i < reads; i++)
      {
        PlayerRegistry.Instance.GetPlayerClass("Tolzol", 399);
      }
      var perRead = (GC.GetAllocatedBytesForCurrentThread() - start) / (double)reads;

      /*
       * Sensitivity control: an array the size of the list this read used to copy really does trip the threshold below, so
       * that assert is not vacuous. Two guards make the allocation real, and both are load-bearing because the first version
       * of this loop — `var probe = new string[2]; kept += probe.Length;` — reported 0.0 bytes on one machine and 20 on
       * another while measuring nothing: the length of a freshly allocated array is a constant, so folding it makes the
       * object unobserved and the allocation elidable. Hence (a) the element count comes from an opaque read rather than a
       * literal, and (b) every array is PUBLISHED into a sink array that outlives the loop, which is how the FCT probe does
       * it. A control the optimizer can delete reads as "the probe is broken" instead of naming what it guards.
       */
      var boundaryCount = 2;
      var sinks = new string[64][];
      var controlStart = GC.GetAllocatedBytesForCurrentThread();
      for (var i = 0; i < reads; i++)
      {
        sinks[i & 63] = new string[Volatile.Read(ref boundaryCount)];
      }
      var perArray = (GC.GetAllocatedBytesForCurrentThread() - controlStart) / (double)reads;
      Assert.AreEqual(2, sinks[0].Length);
      GC.KeepAlive(sinks);
      TestContext.WriteLine($"[class read] {perRead:F1} B per GetPlayerClass; control array {perArray:F1} B");

      Assert.IsTrue(perArray > 8, $"the probe cannot see a per-call allocation ({perArray:F1} bytes for an array it is meant to catch)");
      Assert.IsTrue(perRead < 8,
        $"a class read allocates {perRead:F1} bytes per call: the boundary list is being copied out of the lock again "
        + "(one allocation per board row, per rebuild)");
    }

    [TestMethod]
    public void ANameNobodyClassifiedAnswersItsDefault()
    {
      // The branch beside the one above: no time-windowed record at all (a roster class, never a watched cast), and then the
      // moment a sighting commits, the window answers from its own second onward — including earlier than that, which is the
      // documented "a load is not a sighting / no backdating" shape rather than an accident.
      var cleric = EQDataStore.Instance.GetClassLabel(SpellClass.Clr);
      var berserker = EQDataStore.Instance.GetClassLabel(SpellClass.Ber);

      PlayerRegistry.Instance.SetDefaultPlayerClass("Vexil", cleric);
      Assert.AreEqual(cleric, PlayerRegistry.Instance.GetPlayerClass("Vexil", 10));

      PlayerRegistry.Instance.SetActivePlayerClass("Vexil", berserker, 2, 500);
      Assert.AreEqual(berserker, PlayerRegistry.Instance.GetPlayerClass("Vexil", 600));
    }
  }
}

namespace EQLogParser
{
  /*
   * FightManager taunt bookkeeping. The one behavior these pin is the legacy loss path: a taunt that arrives while its
   * name has no ACTIVE row is appended to a Fight that HandleNewTaunt allocates with ?? Create but never registers
   * (only the damage path calls UpdateIfNewFightMap), so it is unreachable by every board, list and census that reads
   * registered fights. The derived side cannot have this loss — CombatMirror stores every taunt in the capture at ingest
   * and routes it at materialization — which is why the census (docs/DesignNotes.md, "Spells and taunts") measures the
   * old board undercounting. This test exists so that loss is a known, named mechanism rather than a rumor: if the
   * orphan path ever gets registered, ATauntWithNoActiveRowIsLostFromEveryBoard must fail and the numbers re-measured.
   */
  [DoNotParallelize]
  [TestClass]
  public sealed class FightManagerTest
  {
    private FightManager _fm;
    private List<Fight> _registered;

    [TestInitialize]
    public void Setup()
    {
      PlayerRegistry.Instance.Clear();
      // Our own instance rather than the shared singleton: its constructor wires HandleNewTaunt onto the static
      // parser events, and registering test rows on the singleton would leak into later test classes. There is no
      // unsubscribe API (LifecycleManager only holds a reference), so the handler outlives this class exactly as it
      // does for PipelineHarness's instances — the instance clears its own state in Teardown, and any event it sees
      // afterwards lands in dictionaries nobody reads.
      _fm = new FightManager();
      _registered = [];
      _fm.EventsNewFight += f => _registered.Add(f);
    }

    [TestCleanup]
    public void Teardown()
    {
      _fm.Clear();
    }

    private static int VisibleTaunts(IEnumerable<Fight> fights)
      => fights.Sum(f => f.TauntBlocks.Sum(g => g.Actions.Count));

    private Fight RegisterRow(string name, double lastTime, bool withDamage)
    {
      // Exactly the shape Create produces for a damage-created row; one DamageBlocks group satisfies CheckExpireFights'
      // "has damage" condition so the 30 s gap is what closes it.
      var fight = new Fight
      {
        Name = name,
        BeginTimeString = StringCache.GetOrAdd("row begin"),
        LastTime = lastTime,
        Id = 1,
        CorrectMapKey = name
      };
      if (withDamage)
      {
        fight.DamageBlocks.Add(new ActionGroup { BeginTime = lastTime });
      }
      _fm.UpdateIfNewFightMap(name, fight, false);
      return fight;
    }

    private void ParseTaunt(double time)
    {
      // Process reads the reader's pre-split words, not the raw action.
      const string action = "Kilsa has captured An echo's attention!";
      DamageLineParser.Process(new LineData { Action = action, Split = action.Split(' '), BeginTime = time });
    }

    /*
     * The mechanism, end to end through the real parser: while the row is active the taunt lands on it; once the 30 s
     * gap has expired the row, the identical line appends its taunt to an unregistered orphan and nothing that reads
     * registered fights ever sees it. Both parses succeed (Process returns the same either way) — the loss is silent by
     * construction, which is how it stayed in the old board unnoticed.
     */
    [TestMethod]
    public void ATauntWithNoActiveRowIsLostFromEveryBoard()
    {
      var row = RegisterRow("An echo", 0, withDamage: true);

      ParseTaunt(10);
      Assert.AreEqual(1, VisibleTaunts(_registered), "an active row keeps its taunt");
      Assert.AreSame(row, _fm.GetFight("An echo"));

      // 30 s of quiet: the expiry sweep closes the row (diff 40 > FightTimeout and it has damage).
      _fm.CheckExpireFights(40);
      Assert.IsNull(_fm.GetFight("An echo"));

      ParseTaunt(40);
      Assert.AreEqual(1, VisibleTaunts(_registered), "the expired row's successor taunt went to an orphan, not a board");
      Assert.AreEqual(1, _registered.Count, "no new fight was registered for the orphaned taunt");
      Assert.IsNull(_fm.GetFight("An echo"), "the orphan holds no row the next lookup could find either");
    }

    /*
     * The other direction of the same rule: a row with NO damage only expires at MaxTimeout (60 s), so its taunts are
     * kept longer than a damage row's — one more reason the old board's per-name totals depended on which lines happened
     * to arrive in which seconds rather than on what was actually taunted.
     */
    [TestMethod]
    public void ATauntOnADamagelessRowSurvivesPastTheThirtySecondGap()
    {
      var row = RegisterRow("An echo", 0, withDamage: false); // no damage: only MaxTimeout may close it

      ParseTaunt(35);
      _fm.CheckExpireFights(40); // diff 40 > FightTimeout, but the row has no damage: only MaxTimeout closes it
      Assert.AreSame(row, _fm.GetFight("An echo"));

      ParseTaunt(59);
      Assert.AreEqual(2, VisibleTaunts(_registered), "both taunts sat on a still-active row");

      _fm.CheckExpireFights(61); // diff 61 > MaxTimeout: closed at last
      ParseTaunt(70);
      Assert.AreEqual(2, VisibleTaunts(_registered), "past MaxTimeout the same orphan path takes the taunt");
    }
  }
}

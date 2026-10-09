using EQLogParser;

namespace EQLogParser;

/*
 * The census behind the Names window: one row per name a capture mentions, with the classifier's verdict, the rule
 * that decided it, what the operator said, and what the name did. Three properties these tests hold, because each is
 * a place a "convenience" version would have quietly produced a wrong list:
 *
 *   - THE ROW SET IS THE NAME POOL, not "names that did something". Interning happens when a name appears on any line,
 *     so a boss that only ever got hit and a pet that only ever got healed are in it; filtering to attackers would
 *     hide exactly the rows an auditor goes looking for.
 *   - ONE ROW PER NAME, case folded. The pool holds one id per entity however the log spelled it, while roster and
 *     override rows are their own strings; a case-sensitive merge gives an audited name two rows with half each.
 *   - TOTALS COME FROM THE FACTS, not from a summary board. The census answers "how busy was this name" for the whole
 *     capture regardless of fight selection, so it must not inherit a selected-fights filter.
 *
 * ConfigUtil.ConfigDir/ServerName/PlayerName and both stores are process state, so they are parked in a temp folder
 * per test and emptied on the way out (assembly does not parallelize; see PlayerRegistryPersistenceTest for the same
 * pattern and why).
 */
[TestClass]
public class ClassificationReportTest
{
  private static PipelineHarness.DeriveRunResult? _capture;

  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = "Census Test";
    // A real session gets this from FileUtil.ParseFileName when a log is picked (fallback "You"); set it so the
    // registry sees the same shape of state it sees in the app.
    ConfigUtil.PlayerName = "Censustester";

    // Cold identity on both sides: the capture itself is parsed once and reused (it is slow), so each test starts by
    // emptying what that parse taught the process-wide registry, then loads an override file of its own.
    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init("Census Test");
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    // Both stores are process singletons: load an empty override file so a verdict written here cannot answer for
    // another test class that runs afterwards.
    IdentityOverrideStore.Instance.Init("census-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityPriorStore.Instance.Init("census-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
    try { Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  // The reference capture other derive tests use: several raiders, mobs on both sides and one heal line, which is
  // enough to exercise every column without a second parse per test.
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "mini-fight.txt");

  private static PipelineHarness.DeriveRunResult Capture()
  {
    Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");
    return _capture ??= PipelineHarness.RunFileDerived(FixturePath);
  }

  /*
   * The census as the app would build it after a derive: run the rule pass over the captured facts, replay the
   * operator's file into the result (DeriveEngine.RunDeriveAsync does the same two steps), then take the census.
   * run.Timeline alone is only SEEDED — the harness hands back what RegistrySeed left, so asking it directly reads
   * mostly Unknown and would let these tests pass on an unclassified board.
   */
  // Name with the most damage dealt. Spans are not LINQ-able, so this walks them by hand and returns the top key.
  private static string BusiestAttacker()
  {
    var facts = Capture().Facts;
    var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in facts.Facts)
    {
      var name = facts.InternedNames[f.AtkIdx];
      totals.TryGetValue(name, out var run);
      totals[name] = run + f.Total;
    }
    return totals.OrderByDescending(kvp => kvp.Value).First().Key;
  }

  /*
   * The same three steps, in the same order, that DeriveEngine.RunDeriveAsync runs on every derive: seed from what
   * the registry already knows, replay the rules over the facts from scratch, then put the operator's file on top.
   * Order matters twice over - the seed is what turns a saved players.txt name into a Player before any rule has to
   * guess, and R10 has to be last because this timeline was built from nothing and the verdict must survive it.
   */
  private static ClassificationReport Census(IdentityPriorStore? priors = null)
  {
    var capture = Capture();
    var facts = capture.Facts;
    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, facts, LogStartS(), LogEndS());
    ClassificationRules.Apply(facts, timeline, capture.HealFacts);
    IdentityOverrideStore.Instance.Apply(timeline);
    return ClassificationReport.Build(timeline, facts, capture.HealFacts,
                                      IdentityOverrideStore.Instance, PlayerRegistry.Instance, priors);
  }

  // Facts are appended in arrival order, so the ends of the table are the ends of the capture (the engine keeps the
  // same pair; a test has no capture object to ask).
  private static double LogStartS() => Capture().Facts.FactCount > 0 ? Capture().Facts.Facts[0].TimeS : double.NaN;
  private static double LogEndS() => Capture().Facts.FactCount > 0 ? Capture().Facts.Facts[Capture().Facts.FactCount - 1].TimeS : double.NaN;

  [TestMethod]
  public void EveryInternedNameGetsARow()
  {
    var report = Census();

    Assert.IsTrue(report.Rows.Count > 0, "the census came back empty - wrong fixture or no facts captured");
    foreach (var name in Capture().Facts.InternedNames)
    {
      Assert.IsNotNull(report.Find(name), $"{name} is in the name pool but not on the census");
    }

    // Case folded: the pool already holds one id per entity, so the census must not double up on any of them.
    var distinct = new HashSet<string>(report.Rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
    Assert.AreEqual(report.Rows.Count, distinct.Count, "one name produced two rows");
  }

  [TestMethod]
  public void CountsAddUpToTheRows()
  {
    var report = Census();

    Assert.AreEqual(report.Rows.Count,
                    report.Players + report.Pets + report.Mercs + report.Npcs + report.Unknown);

    /*
     * These tests are COLD by construction (the registry is emptied in Setup, and nothing here feeds it a roster or
     * npcs/players seed), which is the project's convention for identity work - AGENTS: "Tests are cold by
     * construction". What survives a cold run on this fixture is the two kinds that grammar and the shipped database
     * can decide from the lines alone: X`s pet (R5) and a name npcs.txt knows (R6). Raid members read Unknown here,
     * because placing them needs target evidence, a roster line or this log's registry, none of which a cold run has.
     */
    Assert.IsTrue(report.Pets > 0, "no X`s pet row reached Pet identity - R5 stopped working");
    Assert.IsTrue(report.Npcs > 0, "nothing npcs.txt knows reached NPC identity - R6 stopped working");
  }

  [TestMethod]
  public void DamageTotalsComeFromTheFactsAndNotFromABoard()
  {
    var facts = Capture().Facts;
    var expected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in facts.Facts)
    {
      var name = facts.InternedNames[f.AtkIdx];
      expected.TryGetValue(name, out var run);
      expected[name] = run + f.Total;
    }

    var busiest = expected.OrderByDescending(kvp => kvp.Value).First();
    var row = Census().Find(busiest.Key);

    Assert.IsNotNull(row);
    Assert.AreEqual(busiest.Value, row!.Damage, 0.5, $"{busiest.Key}'s census damage is not the sum of its facts");
    Assert.IsTrue(row.Events > 0, "a name with damage carried no events");
  }

  [TestMethod]
  public void HealingRidesInTheSameRowAsDamage()
  {
    var heals = Capture().HealFacts;
    if (heals.HealCount == 0) Assert.Inconclusive("fixture carries no heal facts");

    var facts = Capture().Facts;
    var expected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var h in heals.Heals)
    {
      var name = facts.InternedNames[h.HealerIdx];
      expected.TryGetValue(name, out var run);
      expected[name] = run + h.Total;
    }

    var busiest = expected.OrderByDescending(kvp => kvp.Value).First();
    var healer = Census().Find(busiest.Key);
    Assert.IsNotNull(healer);
    Assert.AreEqual(busiest.Value, healer!.Healing, 0.5, "the healing column is not the sum of heal facts");
  }

  [TestMethod]
  public void AnOverrideReadsBackAsTheOperatorsOwnVerdict()
  {
    // A pet whose owner the roster knows: verified-but-not-a-player is exactly the shape players.txt holds (pet
    // owners), so it starts out NOT a disagreement and only becomes one when somebody puts an NPC stamp on it.
    var name = Census().Rows.First(r => r.Kind == IdentityKind.Pet).Name;
    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(name, (long)DateUtil.ToDotNetSeconds(DateTime.Now));
    Assert.IsFalse(Census().Find(name)!.IsDisagreement, "a verified pet read as a disagreement before any override");

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc);
    var overridden = Census().Find(name)!;
    Assert.IsTrue(overridden.IsOperatorVerdict, "the census did not say the verdict was the operator's");
    Assert.AreEqual(IdentityKind.Npc, overridden.Kind, "an override that outranks nothing is not an override");

    /*
     * NOT a disagreement any more — and that is the new law, not a lost assertion (2026-10-09: a manual decision removes
     * previous knowledge). The roster's contradicting answer was evicted with the click, so there is nothing left for the row
     * to disagree WITH; painting a contradiction the operator just resolved would make the column mean "something happened"
     * instead of "somebody should look". What the flag is for survives in the companion test below: RULES that demote a name
     * the roster still claims.
     */
    Assert.IsFalse(overridden.IsDisagreement,
        "the operator's word took the roster's answer with it, so this row now reads NPC · Override and nothing contradicts it");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(name), "…because the roster entry is gone rather than overruled");

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Unknown);
    var reverted = Census().Find(name)!;
    Assert.IsFalse(reverted.IsOperatorVerdict, "reverting left the operator's mark on the row");
    Assert.AreEqual(IdentityKind.Pet, reverted.Kind, "the rules' own answer did not come back after a revert");
  }

  /*
   * The disagreement column's real job, kept on purpose: the RULES demoting a name the roster still claims is the audit signal
   * an operator acts on (a raider pushed to the enemy column loses her damage on the flip). An operator verdict no longer feeds
   * it — see AnOverrideReadsBackAsTheOperatorsOwnVerdict — so this shape needs its own fixture rather than inheriting coverage.
   */
  [TestMethod]
  public void ARuleThatDemotesANameTheRosterClaimsStillFlags()
  {
    // The registry takes any name (its IsPossiblePlayerName gate runs at SAVE, on the way to players.txt), and this test never
    // writes a file — it asks what the census reports while the roster holds the contradicting answer.
    var name = Census().Rows.First(r => r.Kind == IdentityKind.Npc).Name;

    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(name, (long)DateUtil.ToDotNetSeconds(DateTime.Now));

    var row = Census().Find(name)!;
    Assert.IsFalse(row.IsOperatorVerdict, "fixture: nothing was clicked here — the classifier is the one disagreeing");
    Assert.IsTrue(row.IsDisagreement, "players.txt says one of ours, the rules say NPC: that is the row this column exists for");
  }

  [TestMethod]
  public void AVerdictSurvivesTheFileItWasWrittenTo()
  {
    var name = Census().Rows.First(r => r.Kind == IdentityKind.Npc).Name;
    ClassificationCommands.ApplyVerdict(name, IdentityKind.Merc);

    // Re-read from disk the way a fresh window on a reopened log would.
    IdentityOverrideStore.Instance.Init("Census Test");
    Assert.IsTrue(IdentityOverrideStore.Instance.TryGet(name, out var kind), "the verdict never reached the file");
    Assert.AreEqual(IdentityKind.Merc, kind);
    Assert.AreEqual(1, Census().Rows.Count(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
  }

  /*
   * Cold is the TEST condition, not the app's: opening a file makes MainWindow read the server and character out of
   * the name, set ConfigUtil.ServerName/PlayerName, then PlayerRegistry.Init() + IdentityOverrideStore.Init(server)
   * (MainWindow.xaml.cs:1335-1345) - so every real session is warmed from that server's players.txt and pet pairs
   * before the first derive. This test walks that same path on purpose: save a roster entry, load it back from the
   * file, and require the census to show the name as a Player seeded by the registry rather than guessed.
   */
  [TestMethod]
  public void WhatWasSavedToPlayersTxtStillNamesAPlayer()
  {
    var name = BusiestAttacker();
    Assert.AreEqual(IdentityKind.Unknown, Census().Find(name)!.Kind,
                    "control: this fixture is no longer cold, so the warm half proves nothing");

    ConfigUtil.ServerName = "Warm Test";

    /*
     * Two things moved under this test on 2026-10-09 and it still asks the same question. What this app SAVES is no longer
     * players.txt but identity-priors.txt's roster lane (so the ledger has to be loaded onto the server being warmed, exactly as
     * MainWindow does at log open), and a saved name now reaches the census as a PLAYER by testimony — R25-roster — rather than
     * sitting Unknown behind a strength-8 registry seed. The reload path is unchanged: write, save, re-Init, read the census.
     */
    IdentityPriorStore.Instance.Init("Warm Test");
    PlayerRegistry.Instance.Init();
    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(name, DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();
    PlayerRegistry.Instance.Init();

    var warmed = Census().Find(name)!;
    Assert.AreEqual(IdentityKind.Player, warmed.Kind, "a name this app saved did not come back as a player");
    /*
     * The SEED is what this fixture shows rather than R25's testimony: the roster speaks only for a capture whose server its folder
     * answers for (ClassificationRules.ApplySavedRoster), and the census here runs under the fixture capture's own name. R25's word is
     * pinned in RosterMemoryClaimTest; what this test owns is that a name THIS APP SAVED comes back as a Player at all — now by way of
     * identity-priors.txt instead of the frozen players.txt.
     */
    Assert.AreEqual("RegistrySeed", warmed.Reason, "it reached Player by some route other than the saved roster");
    Assert.IsTrue(warmed.LegacySaysPlayer);
    Assert.IsFalse(warmed.TypedEntry, "an entry with an evidence timestamp loaded as hand-typed");
    Assert.IsFalse(warmed.IsDisagreement);

    // The ledger is process state and this test invented a server: leave no rows behind for another fixture to find.
    IdentityPriorStore.Instance.Init("warm-test-cleanup-" + Guid.NewGuid().ToString("N"));
  }

  /*
   * The class column used to show only the roster block and ability words: the census asked
   * GetDefaultPlayerClass, so every class LEARNED from casting - CastLineParser's confidence records, which is
   * what legacy's Verified Players grid displayed - was invisible in the window built to replace it. Both
   * getters must answer here: learned first, and the default still for names no cast taught.
   */
  [TestMethod]
  public void AClassLearnedFromCastingShowsInTheCensus()
  {
    // Class writes gate on the host validator (CombatRecordLookup.IsValidClassName, wired by App.xaml.cs,
    // "nothing is a class" headless) - same two-word stub PlayerRegistryPersistenceTest uses, restored in a
    // finally because the hook is process state.
    var savedValidator = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => name is "Cleric" or "Warrior";
    try
    {
      var raider = BusiestAttacker();
      PlayerRegistry.Instance.SetActivePlayerClass(raider, "Cleric", 2, LogStartS());

      // A default written after the learned record must not outrank it - the record says what this capture's
      // casts showed, the default is a saved guess.
      PlayerRegistry.Instance.SetDefaultPlayerClass(raider, "Warrior");

      var learnedRow = Census().Rows.Single(r => r.Name.Equals(raider, StringComparison.OrdinalIgnoreCase));
      Assert.AreEqual("Cleric", learnedRow.Class, "the spell-learned class must be the one displayed");

      // And the roster default still answers for a name that never cast a class-bearing spell.
      string? secondName = null;
      foreach (var f in Capture().Facts.Facts)
      {
        var name = Capture().Facts.InternedNames[f.AtkIdx];
        if (!name.Equals(raider, StringComparison.OrdinalIgnoreCase)) { secondName = name; break; }
      }
      Assert.IsNotNull(secondName, "the capture needs an attacker besides the raider for the default-class case");
      PlayerRegistry.Instance.SetDefaultPlayerClass(secondName!, "Warrior");

      var defaultRow = Census().Rows.Single(r => r.Name.Equals(secondName, StringComparison.OrdinalIgnoreCase));
      Assert.AreEqual("Warrior", defaultRow.Class);
    }
    finally
    {
      CombatRecordLookup.IsValidClassName = savedValidator;
    }
  }

  [TestMethod]
  public void AbsenceIsNotAContradiction()
  {
    // The busiest attacker in the fixture is a raid member this cold run cannot place (no target line, no roster).
    var busiest = BusiestAttacker();

    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(busiest, (long)DateUtil.ToDotNetSeconds(DateTime.Now));
    var report = Census();

    /*
     * Stated as a law over the whole census rather than over one row, so it survives the rules getting BETTER at
     * placing names (which would break a hard-coded "this attacker is Unknown" assertion for the wrong reason):
     * on this window a contradiction is verified-player-stamped-NPC, and nothing else. Every other combination -
     * including the very common "the roster knows the name, this capture does not" - stays off the list.
     */
    foreach (var row in report.Rows.Where(r => r.LegacySaysPlayer))
    {
      Assert.AreEqual(row.Kind == IdentityKind.Npc, row.IsDisagreement,
                      $"{row.Name}: verdict {row.Kind}, flagged {row.IsDisagreement}");
    }

    Assert.IsTrue(report.Find(busiest)!.LegacySaysPlayer, "the roster never took the name it was verified with");
  }

  /* Three counts that are easy to conflate, and expensive to get wrong: every name listed, the names THIS capture's
   * lines could not place, and the names held up by an earlier log. An operator deciding where to spend an override
   * reads them as "how much of this file am I going to have to correct?" - so a count that includes waited-on alts or
   * prior-backed rows tells them the classifier is failing when it is merely reading a sparse log. */
  [TestMethod]
  public void WhatAHandfulOfUnknownsLooksLike()
  {
    var census = Census();
    Assert.AreEqual(census.Rows.Count, census.TotalNames);
    Assert.AreEqual(census.Rows.Count(static r => r.HasFacts && r.IsUnresolved), census.UnresolvedInCapture);
    Assert.IsTrue(census.UnresolvedInCapture > 0,
                  "the fixture now places every name cold - this test measures nothing, update it");

    var row = census.Rows.First(static r => r.HasFacts && r.IsUnresolved);
    Assert.AreEqual(IdentityKind.Unknown, row.Kind);
    Assert.IsNull(row.Class);
    Assert.IsFalse(row.IsDisagreement, "absence is not a contradiction - see Row.IsDisagreement");
  }

  [TestMethod]
  public void AnAltWhoSatTheFightOutIsNotAnUnresolvedName()
  {
    var before = Census();

    // Roster membership without a single fact: Unknown in kind, but the roster HAS a claim.
    PlayerRegistry.Instance.AddVerifiedPlayer("Absenty", LogEndS());
    var after = Census();

    var row = after.Find("Absenty");
    Assert.IsNotNull(row);
    Assert.IsTrue(after.TotalNames == before.TotalNames + 1, "the roster-only row never reached the census list at all");
    Assert.IsFalse(row!.HasFacts, "a name with no facts in this capture counted as part of it");

    // Roster membership is itself a claim (the row reads Player), so IsUnresolved excludes it by construction - and
    // UnresolvedInCapture excludes it twice over, because it also demands the name appear in a fact stream.
    Assert.AreEqual(IdentityKind.Player, row.Kind);
    Assert.IsFalse(row.IsUnresolved);
    Assert.AreEqual(before.UnresolvedInCapture, after.UnresolvedInCapture,
                    "a name nobody is confused about raised the classifier's failure count");
  }

  [TestMethod]
  public void ARowHeldUpByAPriorIsNoLongerUnresolved()
  {
    var silent = BusiestAttacker();
    var before = Census();
    Assert.IsTrue(before.Find(silent)!.IsUnresolved);

    var ledger = IdentityPriorStore.Instance;
    var remembered = new EntityTimeline();
    remembered.SetIdentity(silent, IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    ledger.Record(remembered, [silent], 1_700_000_000);

    var after = Census(ledger);
    Assert.IsTrue(after.Find(silent)!.IsPrior);
    Assert.IsFalse(after.Find(silent)!.IsUnresolved, "a row with an answer on screen still counted as unanswered");
    Assert.AreEqual(before.UnresolvedInCapture - 1, after.UnresolvedInCapture);
  }

  /*
   * Cross-log memory, and the two ways it must NOT be allowed to speak. A prior fills a name this capture's rules could
   * not place; it never overrides a verdict this log reached from lines; and an operator verdict outranks it, since both
   * are claims and only one came from a human. What it DOES survive is a roster edit: taking a name out of players.txt
   * is an eviction, not a veto (see PlayerRegistryPersistenceTest), and the last pinned case below holds that line.
   */
  /*
   * A name the ledger knows and this capture never mentioned still has to be ON the list. Without it the ledger is
   * invisible for exactly the case it exists for: someone looking up a mob they remember from an earlier night, whose
   * species this log says nothing about — an empty search box would read as "no record", not as "record, from before".
   */
  [TestMethod]
  public void ANameKnownOnlyFromOlderLogsIsStillOnTheList()
  {
    var ledger = IdentityPriorStore.Instance;
    var remembered = new EntityTimeline();
    remembered.SetIdentity("Rememb", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    ledger.Record(remembered, ["Rememb"], 1_700_000_000);
    ledger.Record(remembered, ["Rememb"], 1_800_000_000);

    var row = Census(ledger).Find("Rememb");

    Assert.IsNotNull(row, "a name held only by the ledger must still reach the list");
    Assert.AreEqual(IdentityKind.Npc, row.Kind);
    StringAssert.StartsWith(row.Reason, "Prior:", "the reason says which capture's rules answered, not this one's");
    Assert.IsTrue(row.IsPrior);
    Assert.AreEqual(2, row.PriorSightings, "and how many times it was seen, since that is what makes it worth showing");
    Assert.IsFalse(row.HasFacts, "nothing in this log backs it, and the row has to say so");
  }

  [TestMethod]
  public void ALedgerFillsSilenceAndNeverContradictsEvidence()
  {
    var silent = BusiestAttacker();                              // this capture cannot place it cold
    var placed = Census().Rows.First(r => r.Kind == IdentityKind.Pet).Name;   // ...and this one from a line

    var ledger = IdentityPriorStore.Instance;
    var remembered = new EntityTimeline();
    remembered.SetIdentity(silent, IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    remembered.SetIdentity(placed, IdentityKind.Player, RuleStrength.Certain, "R1-target");
    ledger.Record(remembered, [silent, placed], 1_700_000_000);

    var borrowed = Census(ledger).Find(silent)!;
    Assert.AreEqual(IdentityKind.Npc, borrowed.Kind, "the ledger did not fill the gap");
    Assert.IsTrue(borrowed.IsPrior, "a remembered verdict was shown as this capture's own");
    Assert.AreEqual("Prior:R7-graph", borrowed.Reason);
    Assert.AreEqual(1, borrowed.PriorSightings);

    var itsOwn = Census(ledger).Find(placed)!;
    Assert.AreEqual(IdentityKind.Pet, itsOwn.Kind, "yesterday's answer outvoted a line read today");
    Assert.IsFalse(itsOwn.IsPrior);

    /*
     * And the line on the other side: taking the roster row away is an eviction, not a veto, so it does NOT silence the
     * ledger. A `!Name` tombstone used to block memory here (and every learning path); nothing in any shipped build
     * could write one, and the behaviour this asserts is the honest remainder — an old verdict stays on the list until
     * somebody overrules it or the ledger is cleared.
     */
    PlayerRegistry.Instance.RemoveVerifiedPlayer(silent);
    var evicted = Census(ledger).Find(silent)!;
    Assert.AreEqual(IdentityKind.Npc, evicted.Kind, "a roster removal revived a veto over the ledger");
    Assert.IsTrue(evicted.IsPrior, "and the row lost the one thing that made it worth listing");
  }

  [TestMethod]
  public void AnOperatorVerdictOutranksTheLedger()
  {
    var ledger = IdentityPriorStore.Instance;
    var remembered = new EntityTimeline();
    remembered.SetIdentity("Zzquietname", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    ledger.Record(remembered, ["Zzquietname"], 1_700_000_000);

    ClassificationCommands.ApplyVerdict("Zzquietname", IdentityKind.Merc);

    var row = Census(ledger).Find("Zzquietname")!;
    Assert.AreEqual(IdentityKind.Merc, row.Kind);
    Assert.IsTrue(row.IsOperatorVerdict);
    Assert.IsFalse(row.IsPrior, "a row the operator owns was attributed to last season");
  }

  /* A verdict is a claim about KIND and nothing else. players.txt is not part of it: "that is the enemy" belongs in
   * identity-overrides.txt (R10), while the roster stays a record of who is ours. (This fixture used to arrive through a
   * `!Name` rejection whose lifting the test asserted; nothing writes one any more — see
   * PlayerRegistryPersistenceTest — and the census pool has no reason to hold a name that neither the log nor the
   * roster mentions, so the surviving half is pinned on a name the capture really spoke about.) */
  [TestMethod]
  public void AVerdictClaimsAnKindWithoutTouchingTheRoster()
  {
    var name = BusiestAttacker();
    var rosterBefore = PlayerRegistry.Instance.GetVerifiedPlayers();

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc);

    CollectionAssert.AreEquivalent(rosterBefore, PlayerRegistry.Instance.GetVerifiedPlayers(),
      "\"this is an NPC\" moved the roster as well as the verdict");

    var row = Census().Find(name)!;
    Assert.AreEqual(IdentityKind.Npc, row.Kind);
    Assert.IsTrue(row.IsOperatorVerdict);
    Assert.AreEqual("R10-manual", row.Reason);
  }
}

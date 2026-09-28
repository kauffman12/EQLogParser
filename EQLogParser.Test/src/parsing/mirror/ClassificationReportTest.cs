using EQLogParser.Mirror;

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
  private static PipelineHarness.MirrorRunResult? _capture;

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
    MirrorOverrideStore.Instance.Init("Census Test");
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    // Both stores are process singletons: load an empty override file so a verdict written here cannot answer for
    // another test class that runs afterwards.
    MirrorOverrideStore.Instance.Init("census-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
    try { Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  // The reference capture other mirror tests use: several raiders, mobs on both sides and one heal line, which is
  // enough to exercise every column without a second parse per test.
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "mini-fight.txt");

  private static PipelineHarness.MirrorRunResult Capture()
  {
    Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");
    return _capture ??= PipelineHarness.RunFileWithMirror(FixturePath);
  }

  /*
   * The census as the app would build it after a derive: run the rule pass over the captured facts, replay the
   * operator's file into the result (MirrorSession.RunDeriveAsync does the same two steps), then take the census.
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
   * The same three steps, in the same order, that MirrorSession.RunDeriveAsync runs on every derive: seed from what
   * the registry already knows, replay the rules over the facts from scratch, then put the operator's file on top.
   * Order matters twice over - the seed is what turns a saved players.txt name into a Player before any rule has to
   * guess, and R10 has to be last because this timeline was built from nothing and the verdict must survive it.
   */
  private static ClassificationReport Census()
  {
    var capture = Capture();
    var facts = capture.Facts;
    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, facts, LogStartS(), LogEndS());
    ClassificationRules.Apply(facts, timeline, capture.HealFacts);
    MirrorOverrideStore.Instance.Apply(timeline);
    return ClassificationReport.Build(timeline, facts, capture.HealFacts,
                                      MirrorOverrideStore.Instance, PlayerRegistry.Instance);
  }

  // Facts are appended in arrival order, so the ends of the table are the ends of the capture (the mirror keeps the
  // same pair; a test has no mirror object to ask).
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
    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(name, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    Assert.IsFalse(Census().Find(name)!.IsDisagreement, "a verified pet read as a disagreement before any override");

    ClassificationCommands.SetVerdict(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name, IdentityKind.Npc);
    var overridden = Census().Find(name)!;
    Assert.IsTrue(overridden.IsOperatorVerdict, "the census did not say the verdict was the operator's");
    Assert.AreEqual(IdentityKind.Npc, overridden.Kind, "an override that outranks nothing is not an override");
    Assert.IsTrue(overridden.IsDisagreement, "a demoted name on the roster is exactly what this column is for");

    ClassificationCommands.ClearVerdict(MirrorOverrideStore.Instance, name);
    var reverted = Census().Find(name)!;
    Assert.IsFalse(reverted.IsOperatorVerdict, "reverting left the operator's mark on the row");
    Assert.AreEqual(IdentityKind.Pet, reverted.Kind, "the rules' own answer did not come back after a revert");
  }

  [TestMethod]
  public void AVerdictSurvivesTheFileItWasWrittenTo()
  {
    var name = Census().Rows.First(r => r.Kind == IdentityKind.Npc).Name;
    ClassificationCommands.SetVerdict(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name, IdentityKind.Merc);

    // Re-read from disk the way a fresh window on a reopened log would.
    MirrorOverrideStore.Instance.Init("Census Test");
    Assert.IsTrue(MirrorOverrideStore.Instance.TryGet(name, out var kind), "the verdict never reached the file");
    Assert.AreEqual(IdentityKind.Merc, kind);
    Assert.AreEqual(1, Census().Rows.Count(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
  }

  /*
   * Cold is the TEST condition, not the app's: opening a file makes MainWindow read the server and character out of
   * the name, set ConfigUtil.ServerName/PlayerName, then PlayerRegistry.Init() + MirrorOverrideStore.Init(server)
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
    PlayerRegistry.Instance.Init();
    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(name, DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();
    PlayerRegistry.Instance.Init();

    var row = Census().Find(name)!;
    Assert.AreEqual(IdentityKind.Player, row.Kind, "a name serialized to players.txt did not come back as a player");
    Assert.AreEqual("RegistrySeed", row.Reason, "it reached Player by some route other than the saved roster");
    Assert.IsTrue(row.LegacySaysPlayer);
    Assert.IsFalse(row.TypedEntry, "an entry with an evidence timestamp loaded as hand-typed");
    Assert.IsFalse(row.IsDisagreement);
  }

  [TestMethod]
  public void AbsenceIsNotAContradiction()
  {
    // The busiest attacker in the fixture is a raid member this cold run cannot place (no target line, no roster).
    var busiest = BusiestAttacker();

    PlayerRegistry.Instance.AddVerifiedPlayerByOperator(busiest, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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

  [TestMethod]
  public void ARejectedNameStaysOnTheListEvenWithNoEvidence()
  {
    const string name = "Zznotaname";
    ClassificationCommands.Reject(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name);

    var row = Census().Find(name);
    Assert.IsNotNull(row, "a rejection with no facts vanished from the audit list");
    Assert.IsTrue(row!.IsRejected);
    Assert.IsFalse(row.HasFacts);
    Assert.AreEqual(IdentityKind.Unknown, row.Kind, "a rejection is supposed to make no claim");
  }

  [TestMethod]
  public void SettingAVerdictLiftsARejectionWithoutClaimingARaider()
  {
    const string name = "Zznotaname";
    ClassificationCommands.Reject(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name);
    Assert.IsTrue(PlayerRegistry.Instance.IsRejectedPlayer(name));

    ClassificationCommands.SetVerdict(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name, IdentityKind.Npc);

    Assert.IsFalse(PlayerRegistry.Instance.IsRejectedPlayer(name), "an explicit verdict did not supersede the shadow");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(name), "\"this is an NPC\" must not add a roster player");

    var row = Census().Find(name)!;
    Assert.IsFalse(row.IsRejected);
    Assert.AreEqual(IdentityKind.Npc, row.Kind);
  }
}

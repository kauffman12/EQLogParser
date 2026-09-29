using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * identity-priors.txt: what this server's EARLIER logs concluded about a name, kept so a capture that says nothing
 * about a name does not have to forget it. The four properties these tests hold are the ones that stop the file from
 * becoming another players.txt - a pile of old conclusions nobody can trace or trust:
 *
 *   - Only what a rule READ off an EVENT is recorded, with its rule code - and only what a LATER LOG might not answer
 *     again. npcs.txt (R6), a name's own spelling (R14/R16), the word "pet" inside a summon's name (R5-owner), this
 *     session's character (R0-local), Manual verdicts (mirror-overrides.txt), roster names (players.txt) and this
 *     file's own contents are inputs rather than evidence: they answer the same way in every capture, so writing them
 *     down is noise that cannot be corrected from, and would turn an assertion into statistics about itself.
 *   - Agreement is idempotent per capture. The mirror re-derives whenever a filter or override changes; a counter
 *     bumped per pass would report "41 captures agreed" for one evening replayed 41 times.
 *   - A changed verdict restarts the count, so nothing advertises forty confirmations of a belief held for one.
 *   - It expires, measured against the newest entry rather than the wall clock, and a rejected name is never
 *     remembered at all - "no claim" is the operator speaking about exactly this case.
 *
 * Both globals (ConfigUtil.* and the process singleton) are parked in a temp folder per test, as elsewhere.
 */
[TestClass]
public class IdentityPriorStoreTest
{
  private static PipelineHarness.MirrorRunResult? _capture;

  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";
  private string _tempDir = "";

  private const string Server = "Ledger Test";
  private const long DayS = 24 * 60 * 60;

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "priors-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = Server;
    ConfigUtil.PlayerName = "Ledgertestee";

    IdentityPriorStore.Instance.Init(Server);
  }

  [TestCleanup]
  public void Cleanup()
  {
    // Load a nonexistent server so nothing this class remembered answers for the next one.
    IdentityPriorStore.Instance.Init("ledger-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  private static PipelineHarness.MirrorRunResult Capture()
  {
    var log = Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "mini-fight.txt");
    Assert.IsTrue(File.Exists(log), $"missing fixture: {log}");
    return _capture ??= PipelineHarness.RunFileWithMirror(log);
  }

  // A rules pass over the fixture, which is what Record() is handed in the app after a derive.
  private static EntityTimeline Classified(out DamageFactTable facts)
  {
    var capture = Capture();
    facts = capture.Facts;
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline, capture.HealFacts);
    return timeline;
  }

  private static long CaptureEndS(DamageFactTable facts)
    => facts.FactCount > 0 ? facts.Facts[facts.FactCount - 1].TimeS : 1_700_000_000;

  private string LedgerPath => Path.Combine(_tempDir, Server, "identity-priors.txt");

  /*
   * What this fixture holds: every name it places is placed by npcs.txt or by the word "pet" in the name, so a COLD
   * pass over mini-fight.txt writes nothing to the ledger - pinned below, on the real capture rather than asserted in a
   * comment. The mechanics (round trip, idempotence, expiry, forgetting) are about the file and the counters, so they
   * take names from the real capture and stamp a code the gate accepts; that keeps one fixture's rule mix from emptying
   * five tests at once while leaving what they test unchanged.
   */
  private static EntityTimeline Witnessed(DamageFactTable facts, out string[] names)
  {
    var timeline = new EntityTimeline();
    names = facts.InternedNames.Take(4).ToArray();
    Assert.IsTrue(names.Length > 1, "the fixture lost its names");
    foreach (var name in names) timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    return timeline;
  }

  [TestMethod]
  public void OnlyWhatARuleReadOffALineIsRemembered()
  {
    var timeline = Classified(out var facts);
    var store = IdentityPriorStore.Instance;
    store.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, CaptureEndS(facts));

    // A real capture, end to end: everything mini-fight.txt places rests on the shipped database or on a name's own
    // spelling, and neither is something next season's log would be unable to answer. So the ledger stays empty.
    Assert.AreEqual(0, store.Count,
                    "the cold fixture wrote " + string.Join(", ", store.All().Select(e => $"{e.Key}={e.Value.Reason}")) +
                    " - a restated database or name shape reached the file");

    // A capture that witnessed an event is remembered, WITH its rule code: a verdict nobody can trace back to a line is
    // the one thing this file must never become.
    var witnessed = Witnessed(facts, out var names);
    store.Record(witnessed, names, PlayerRegistry.Instance, CaptureEndS(facts));

    Assert.IsTrue(store.TryGet(names[0], out var remembered));
    Assert.AreEqual("R7-graph", remembered.Reason, "the rule code was not stored with the verdict");
    Assert.AreEqual(1, remembered.Sightings);

    // A name this capture never mentioned cannot be in it either.
    Assert.IsFalse(store.TryGet("Zznotaname", out _));
  }

  [TestMethod]
  public void AnOperatorVerdictIsNotRecordedAsAStatisticalSighting()
  {
    var timeline = Classified(out var facts);

    // The override store writes Manual into the SAME timeline the ledger reads; a prior must not be able to launder it.
    var target = facts.InternedNames[0];
    ClassificationRules.ApplyManualOverride(timeline, target, IdentityKind.Merc);
    IdentityPriorStore.Instance.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, CaptureEndS(facts));

    if (IdentityPriorStore.Instance.TryGet(target, out var remembered))
    {
      Assert.AreNotEqual("Manual", remembered.Reason, "an override was recorded as if a line had said it");
    }
  }

  [TestMethod]
  public void ReDerivingTheSameCaptureDoesNotInflateAgreement()
  {
    var timeline = Classified(out var facts);
    var endS = CaptureEndS(facts);
    var store = IdentityPriorStore.Instance;

    var witnessed = Witnessed(facts, out var names);
    var pet = names[0];
    for (var pass = 0; pass < 5; pass++)
      store.Record(witnessed, names, PlayerRegistry.Instance, endS);

    Assert.IsTrue(store.TryGet(pet, out var same));
    Assert.AreEqual(1, same.Sightings, "five derives of ONE log reported five captures");

    // A genuinely later capture is the only thing that counts as another sighting.
    store.Record(witnessed, names, PlayerRegistry.Instance, endS + DayS);
    Assert.IsTrue(store.TryGet(pet, out var newer));
    Assert.AreEqual(2, newer.Sightings);
    Assert.AreEqual(endS + DayS, newer.SeenAtS);
  }

  [TestMethod]
  public void AChangedVerdictStartsTheCountOver()
  {
    var store = IdentityPriorStore.Instance;
    var names = new[] { "Something" };

    // Built with SetIdentity directly: what Record() looks at is the timeline's answer plus its reason code, and this
    // lets one name carry two different conclusions without needing a fixture per verdict.
    var firstPass = new EntityTimeline();
    firstPass.SetIdentity("Something", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    store.Record(firstPass, names, PlayerRegistry.Instance, 1_700_000_000);

    // A genuinely different conclusion from another line: the graph thought NPC, a /who roster says Player.
    var secondPass = new EntityTimeline();
    secondPass.SetIdentity("Something", IdentityKind.Player, RuleStrength.Certain, "R2-who");
    store.Record(secondPass, names, PlayerRegistry.Instance, 1_700_000_000 + DayS);

    Assert.IsTrue(store.TryGet("Something", out var current));
    Assert.AreEqual(IdentityKind.Player, current.Kind, "the ledger kept the older conclusion");
    Assert.AreEqual("R2-who", current.Reason, "the count restarted without the reason that restarted it");
    Assert.AreEqual(1, current.Sightings,
                    "the count kept advertising forty confirmations of a belief held for one");
  }

  [TestMethod]
  public void ARejectedNameIsNeverRemembered()
  {
    // Spelled with a timeline that WOULD be remembered (R7-graph is on the gate). Left to the fixture's own pet name
    // this would pass for the wrong reason, since grammar-shaped verdicts are refused regardless of rejection.
    var store = IdentityPriorStore.Instance;
    var witnessed = new EntityTimeline();
    witnessed.SetIdentity("Takenback", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");

    PlayerRegistry.Instance.RemoveVerifiedPlayer("Takenback");
    store.Record(witnessed, ["Takenback"], PlayerRegistry.Instance, 1_700_000_000);

    Assert.IsFalse(store.TryGet("Takenback", out _),
                   "a name the operator took back came back wearing last season's memory");
  }

  [TestMethod]
  public void TheLedgerRoundTripsThroughItsFile()
  {
    Classified(out var facts);
    var witnessed = Witnessed(facts, out var names);
    IdentityPriorStore.Instance.Record(witnessed, names, PlayerRegistry.Instance, CaptureEndS(facts));

    Assert.IsTrue(File.Exists(LedgerPath), $"the ledger was not written per server: {LedgerPath}");
    var before = IdentityPriorStore.Instance.All();
    Assert.IsTrue(before.Count > 0);

    IdentityPriorStore.Instance.Init(Server);
    var after = IdentityPriorStore.Instance.All();
    // All() is sorted by name, so the two snapshots line up position for position.
    CollectionAssert.AreEqual(before.Select(e => e.Key).ToList(), after.Select(e => e.Key).ToList());
    foreach (var entry in before)
    {
      Assert.IsTrue(IdentityPriorStore.Instance.TryGet(entry.Key, out var reloaded));
      Assert.AreEqual(entry.Value.Kind, reloaded.Kind);
      Assert.AreEqual(entry.Value.Reason, reloaded.Reason);
      Assert.AreEqual(entry.Value.Sightings, reloaded.Sightings);
      Assert.AreEqual(entry.Value.SeenAtS, reloaded.SeenAtS);
    }
  }

  [TestMethod]
  public void MalformedLinesAreDroppedNotRepaired()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
    File.WriteAllLines(LedgerPath,
    [
      "Good=Npc|R7-graph|1700000000|4",
      "Nonsense=Npc|R7-graph|notanumber|2",
      "Short=Npc|R7-graph",
      "Mystery=NotAKind|R7-graph|1700000000|1",
      "UnknownKind=Unknown|R7-graph|1700000000|1",
    ]);

    IdentityPriorStore.Instance.Init(Server);

    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Good", out var good));
    Assert.AreEqual(4, good.Sightings);
    foreach (var junk in new[] { "Nonsense", "Short", "Mystery", "UnknownKind" })
      Assert.IsFalse(IdentityPriorStore.Instance.TryGet(junk, out _), $"{junk} was read out of a malformed line");
  }

  [TestMethod]
  public void StaleVerdictsExpireAgainstTheNewestSighting()
  {
    var store = IdentityPriorStore.Instance;
    var old = new EntityTimeline();
    var names = new[] { "Oldname", "Newname" };

    // Built through the real recording path (a timeline plus Record), so expiry is judged on what actually gets saved.
    old.SetIdentity("Oldname", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    old.SetIdentity("Newname", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    store.Record(old, names, PlayerRegistry.Instance, 1_700_000_000);

    Assert.IsTrue(store.TryGet("Oldname", out _));
    store.Record(old, names, PlayerRegistry.Instance, 1_700_000_000 + 200 * DayS);

    // Both names were re-seen at the new time... so expiry has to be judged per name: neither is stale.
    Assert.IsTrue(store.TryGet("Oldname", out var refreshed), "a name seen again was dropped for another's age");

    // Now leave one behind: only the second name keeps being sighted.
    store.Record(old, ["Newname"], PlayerRegistry.Instance, 1_700_000_000 + 400 * DayS);
    Assert.IsFalse(store.TryGet("Oldname", out _), "an entry 400 days behind the newest one outlived its grace");
    Assert.IsTrue(store.TryGet("Newname", out _));
  }

  /*
   * The gate, and the reason the file stays small. A verdict whose input the program owns forever - npcs.txt, whether
   * the name takes an article or carries a title, the word "pet" inside a summon's name, this session's own character -
   * is re-derivable in any capture that mentions the name, so writing it down buys a row and no knowledge while costing
   * the ability to ever correct the file underneath. Only EVENTS are experience.
   */
  [TestMethod]
  public void ARestatedDatabaseOrNameShapeIsNotRemembered()
  {
    var store = IdentityPriorStore.Instance;
    var restated = new EntityTimeline();
    var names = new[] { "Dbmob", "A shape walker", "Kratakel, Lord Misery", "Sancus`s pet", "Me", "Charmmob", "Whoed" };

    restated.SetIdentity("Dbmob", IdentityKind.Npc, RuleStrength.Medium, "R6-npcdb");
    restated.SetIdentity("A shape walker", IdentityKind.Npc, RuleStrength.Medium, "R14-shape");
    restated.SetIdentity("Kratakel, Lord Misery", IdentityKind.Npc, RuleStrength.Medium, "R16-comma");
    restated.SetIdentity("Sancus`s pet", IdentityKind.Pet, RuleStrength.Certain, "R5-owner:Sancus");
    restated.SetIdentity("Me", IdentityKind.Player, RuleStrength.Certain, "R0-local");
    restated.SetIdentity("Charmmob", IdentityKind.Npc, RuleStrength.Strong, "R9-charm");
    restated.SetIdentity("Whoed", IdentityKind.Player, RuleStrength.Certain, "R2-who");

    store.Record(restated, names, PlayerRegistry.Instance, 1_700_000_000);

    foreach (var noise in new[] { "Dbmob", "A shape walker", "Kratakel, Lord Misery", "Sancus`s pet", "Me" })
      Assert.IsFalse(store.TryGet(noise, out _),
                     $"{noise} rests on a file or a spelling the app always has: remembering it is noise, and noise that " +
                     "outlives a corrected npcs.txt");

    // The two whose input was an EVENT are kept - including a charm, which a later log may well not repeat.
    Assert.IsTrue(store.TryGet("Charmmob", out _), "a verdict only a line could produce was refused");
    Assert.IsTrue(store.TryGet("Whoed", out _));
  }

  /*
   * A closed vocabulary, asserted at its size: a new rule has to ASK to be remembered. Leaving an event-reading rule
   * off costs memory for those names until someone adds it (small, self-healing, and today's census still shows the
   * live verdict); letting a permanent-input rule in files every log's built-in answers as "experience" forever,
   * which is the failure nobody can see from the UI.
   */
  [TestMethod]
  public void TheRememberedVocabularyIsExactlyWhatItIs()
  {
    foreach (var remembered in new[]
             {
               "R1-target", "R1-conflict", "R2-who", "R3-chat", "R3-merc", "R3-presence", "R4-spell",
               "R5-called", "R7-graph", "R7-side", "R9-charm", "R13-merc", "R15-healed", "R17-selffeed", "R18-healedpet",
             })
      Assert.IsTrue(IdentityPriorStore.WorthRemembering(remembered), $"{remembered} reads lines and was refused");

    foreach (var noise in new[]
             {
               "R6-npcdb", "R14-shape", "R16-comma", "R0-local", "R5-owner:Sancus", "R10-manual", "Manual",
               "RegistrySeed", "You", "Prior:R7-graph", "", null,
             })
      Assert.IsFalse(IdentityPriorStore.WorthRemembering(noise), $"{noise} was remembered as if a line had said it");

    // Reasons carry what they learned after a colon ("R5-owner:Sancus", "R7-graph:8"), so matching is by prefix - and
    // the trailing separator on every entry is what stops "R1-" swallowing the operator's word two digits along.
    Assert.IsTrue(IdentityPriorStore.WorthRemembering("R7-graph:8"));
    Assert.IsFalse(IdentityPriorStore.WorthRemembering("R10-manual"));
  }

  [TestMethod]
  public void ARestatementDoesNotDowngradeWhatALineWitnessed()
  {
    var store = IdentityPriorStore.Instance;

    var witnessed = new EntityTimeline();
    witnessed.SetIdentity("Same", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    store.Record(witnessed, ["Same"], PlayerRegistry.Instance, 1_700_000_000);

    // A later capture that can only say "it is in npcs.txt": weaker, and no news. The remembered reason stays the
    // strongest thing any log witnessed, with no sighting spent on restating a shipped file.
    var restated = new EntityTimeline();
    restated.SetIdentity("Same", IdentityKind.Npc, RuleStrength.Medium, "R6-npcdb");
    store.Record(restated, ["Same"], PlayerRegistry.Instance, 1_700_000_000 + DayS);

    Assert.IsTrue(store.TryGet("Same", out var kept));
    Assert.AreEqual("R7-graph", kept.Reason, "a database restatement overwrote what a line had read");
    Assert.AreEqual(1, kept.Sightings, "saying nothing new was counted as another capture agreeing");
  }

  [TestMethod]
  public void OldRowsThatRestOnTheDatabaseAreNotLoadedBack()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
    File.WriteAllLines(LedgerPath,
    [
      "Witnessed=Npc|R7-graph|1700000000|3",
      "Dbmob=Npc|R6-npcdb|1700000000|9",
      "Shapely=Npc|R14-shape|1700000000|2",
    ]);

    IdentityPriorStore.Instance.Init(Server);

    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Witnessed", out _));
    Assert.IsFalse(IdentityPriorStore.Instance.TryGet("Dbmob", out _));
    Assert.IsFalse(IdentityPriorStore.Instance.TryGet("Shapely", out _));

    // And the FILE gets cleaned, not merely the memory: rows that are invisible on every start would leave whoever
    // opens the file arguing with entries the code refuses to read.
    Assert.IsFalse(File.ReadAllText(LedgerPath).Contains("R6-npcdb", StringComparison.Ordinal),
                   "the load dropped the row but left it in the file to be re-dropped forever");
    StringAssert.Contains(File.ReadAllText(LedgerPath), "Witnessed", "cleaning the file took the good row with it");
  }

  [TestMethod]
  public void ForgettingOneNameLeavesEveryOtherFileAlone()
  {
    var timeline = Classified(out var facts);
    var store = IdentityPriorStore.Instance;
    var witnessed = Witnessed(facts, out var names);
    store.Record(witnessed, names, PlayerRegistry.Instance, CaptureEndS(facts));

    // "Forget what older logs decided" is the smallest possible action. Read this capture's own verdicts first, so the
    // comparison below is about the clear rather than about what the rules happen to conclude.
    var ownVerdicts = names.ToDictionary(n => n, n => timeline.IdentityWithSource(n, out _));

    ClassificationCommands.ClearPrior(store, names[0]);

    Assert.IsFalse(store.TryGet(names[0], out _));
    Assert.IsTrue(store.All().Count > 1, "clearing one name emptied the ledger");
    foreach (var name in names)
      Assert.AreEqual(ownVerdicts[name], timeline.IdentityWithSource(name, out _),
                      "clearing a prior reached into this capture's own classification");
    foreach (var other in new[] { "mirror-overrides.txt", "players.txt", "petmapping.txt" })
      Assert.IsFalse(File.Exists(Path.Combine(_tempDir, Server, other)),
                     $"clearing a prior wrote {other}; one menu item touches one file");
  }
}

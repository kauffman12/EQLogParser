using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * identity-priors.txt: what this server's EARLIER logs concluded about a name, kept so a capture that says nothing
 * about a name does not have to forget it. The four properties these tests hold are the ones that stop the file from
 * becoming another players.txt - a pile of old conclusions nobody can trace or trust:
 *
 *   - Only what a rule READ off a line is recorded, with its rule code. Manual verdicts (mirror-overrides.txt),
 *     roster names (players.txt) and this file's own contents are inputs, not evidence, and recording them would turn
 *     an assertion into statistics about itself.
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

  [TestMethod]
  public void OnlyWhatARuleReadOffALineIsRemembered()
  {
    var timeline = Classified(out var facts);
    IdentityPriorStore.Instance.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, CaptureEndS(facts));

    // R5 and R6 both fire on this fixture cold (pet grammar, npcs.txt), so the ledger has to hold them with the code.
    var pet = timeline.IdentityWithSource("Sancus`s pet", out var petReason);
    Assert.AreEqual(IdentityKind.Pet, pet, "fixture no longer yields an R5 pet - rethink what this test pins");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Sancus`s pet", out var remembered));
    Assert.AreEqual(petReason, remembered.Reason, "the rule code was not stored with the verdict");
    Assert.AreEqual(1, remembered.Sightings);

    // A name this capture never mentioned cannot be in it either.
    Assert.IsFalse(IdentityPriorStore.Instance.TryGet("Zznotaname", out _));
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

    var pet = facts.InternedNames.First(n => n.EndsWith("`s pet", StringComparison.OrdinalIgnoreCase));
    for (var pass = 0; pass < 5; pass++)
      store.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, endS);

    Assert.IsTrue(store.TryGet(pet, out var same));
    Assert.AreEqual(1, same.Sightings, "five derives of ONE log reported five captures");

    // A genuinely later capture is the only thing that counts as another sighting.
    store.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, endS + DayS);
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

    var secondPass = new EntityTimeline();
    secondPass.SetIdentity("Something", IdentityKind.Pet, RuleStrength.Medium, "R5-owner:Sancus");
    store.Record(secondPass, names, PlayerRegistry.Instance, 1_700_000_000 + DayS);

    Assert.IsTrue(store.TryGet("Something", out var current));
    Assert.AreEqual(IdentityKind.Pet, current.Kind);
    Assert.AreEqual(1, current.Sightings,
                    "the count kept advertising forty confirmations of a belief held for one");
  }

  [TestMethod]
  public void ARejectedNameIsNeverRemembered()
  {
    var timeline = Classified(out var facts);
    var pet = facts.InternedNames.First(n => n.EndsWith("`s pet", StringComparison.OrdinalIgnoreCase));

    PlayerRegistry.Instance.RemoveVerifiedPlayer(pet);
    IdentityPriorStore.Instance.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, CaptureEndS(facts));

    Assert.IsFalse(IdentityPriorStore.Instance.TryGet(pet, out _),
                   "a name the operator took back came back wearing last season's memory");
  }

  [TestMethod]
  public void TheLedgerRoundTripsThroughItsFile()
  {
    var timeline = Classified(out var facts);
    IdentityPriorStore.Instance.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, CaptureEndS(facts));

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
      "Good=Npc|R6-npcdb|1700000000|4",
      "Nonsense=Npc|R6-npcdb|notanumber|2",
      "Short=Npc|R6-npcdb",
      "Mystery=NotAKind|R6-npcdb|1700000000|1",
      "UnknownKind=Unknown|R6-npcdb|1700000000|1",
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
    old.SetIdentity("Oldname", IdentityKind.Npc, RuleStrength.Medium, "R6-npcdb");
    old.SetIdentity("Newname", IdentityKind.Npc, RuleStrength.Medium, "R6-npcdb");
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

  [TestMethod]
  public void ForgettingOneNameLeavesEveryOtherFileAlone()
  {
    var timeline = Classified(out var facts);
    var store = IdentityPriorStore.Instance;
    store.Record(timeline, facts.InternedNames, PlayerRegistry.Instance, CaptureEndS(facts));

    var pet = facts.InternedNames.First(n => n.EndsWith("`s pet", StringComparison.OrdinalIgnoreCase));
    ClassificationCommands.ClearPrior(store, pet);

    Assert.IsFalse(store.TryGet(pet, out _));
    Assert.IsTrue(timeline.IdentityWithSource(pet, out _) == IdentityKind.Pet,
                  "clearing a prior reached into this capture's own classification");
    Assert.IsTrue(store.All().Count > 0, "clearing one name emptied the ledger");
  }
}

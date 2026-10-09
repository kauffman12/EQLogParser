using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * R26-savedpet: the ledger's OWNERSHIP lane testifying in the rules pass, which is where petmapping.txt's weight went when that file
 * was frozen on 2026-10-09 (docs/DesignNotes.md → "petmapping.txt is a feed now").
 *
 * The operator asked for the old mappings to count "as strong as the ones we decide by spell cast", and that is exactly what landed:
 * Strong, in the behaviour tier, running last so it cannot feed the inference stages. What keeps that safe is the refusal set these
 * tests pin — a name this capture placed keeps its own answer, a name no combat line used never enters the pool, the "nobody was ever
 * named" placeholders are data for a meter rather than a word about identity, and another server's folder does not speak.
 *
 * The lane is written here with persist:false so no file is touched, and the singleton is handed to a throwaway name in Cleanup:
 * otherwise the next test's RegistrySeed would find pets this fixture invented (AGENTS — process state is set and restored).
 */
[TestClass]
[DoNotParallelize]
public sealed class SavedPetOwnerClaimTest
{
  private const string Server = "Eqgate";

  private string _savedServerName = "";

  [TestInitialize]
  public void Setup()
  {
    _savedServerName = ConfigUtil.ServerName;
    ConfigUtil.ServerName = Server;
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityPriorStore.Instance.Init("saved-pet-claim-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ServerName = _savedServerName;
    PlayerRegistry.Instance.Clear();
  }

  /*
   * The gap the rule exists to fill. A summon this capture only ever watched being healed and hit — no possessive line, no pet-slot
   * spell, nothing the rule book can read off behaviour — was Unknown, and an old mapping could do nothing about it: ownership is not
   * identity, which is why the registry's own seed of that pair sits at strength 8 and never changed an answer. Note this fixture
   * writes ONLY the ledger lane, never PlayerRegistry: the claim arrives through the rule, so memory reaches the board even when the
   * file that seeded the live map is gone.
   */
  [TestMethod]
  public void ASavedPairNothingElsePlacedFinallyReadsPet()
  {
    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] a fang spider hits Silentra for 40 points of damage.",
        "[Mon May 04 19:00:06 2026] a fang spider hits Silentra for 41 points of damage.");

    Assert.AreEqual(IdentityKind.Unknown, Cold(run).Identity("Silentra"),
        "control: this shape must stay unplaced on its own, or the test proves nothing (pick another name if it stopped being so)");

    var timeline = WithMappedPair(run, IdentityKind.Unknown, ("Silentra", "Beorun"));

    AssertIdentity(timeline, "Silentra", IdentityKind.Pet, "R26-savedpet");

    timeline.IdentityAt("Silentra", double.PositiveInfinity, out var strength, out _);
    Assert.AreEqual(RuleStrength.Strong, strength,
        "an old mapping is meant to sit in the behaviour tier (a class-spell cast, a drink, an eye strike), not below it");

    // Cell word and hover both speak this application's words — never a filename, never the code.
    var why = IdentityVocabulary.WhyWord("R26-savedpet");
    Assert.AreEqual("Saved Pet", why);
    Assert.IsFalse(why.Contains(".txt", StringComparison.Ordinal));
    Assert.IsFalse(IdentityVocabulary.ProofText("R26-savedpet", IdentityKind.Pet).Contains("petmapping", StringComparison.OrdinalIgnoreCase));

    /*
     * It sorts with the memory lanes, not with the behaviour rules its strength matches — "we wrote this pair down years ago" is the
     * weakest thing this application can say about a name even when it is the only thing it can say. The two axes are deliberately not
     * the same number (IdentityVocabulary.ClaimRanks carries the argument).
     */
    Assert.AreEqual(20, IdentityVocabulary.ClaimRank("R26-savedpet"));
  }

  /*
   * The refusal that makes Strong safe: a name this capture PLACED keeps that answer. A mapping cannot strand a name — the failure an
   * operator named when they chose a frozen feed over a live input, and the reason the rule asks the ANSWER rather than the claim list.
   */
  [TestMethod]
  public void ANameThisCapturePlacedKeepsItsOwnVerdict()
  {
    PipelineHarness.EnsureDataStore();
    Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("Alleza"), "npcs.txt no longer knows Alleza - pick another name for this test");

    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] Alleza hits Betebeatz for 300 points of damage.",
        "[Mon May 04 19:00:06 2026] Alleza hits Betebeatz for 300 points of damage.");

    Assert.AreEqual(IdentityKind.Npc, Cold(run).Identity("Alleza"), "control: Alleza must be placed by this capture on its own");

    var timeline = WithMappedPair(run, IdentityKind.Npc, ("Alleza", "Beorun"));

    AssertAreNot(IdentityKind.Pet, timeline.Identity("Alleza"),
        "a remembered mapping outvoted this capture's own evidence about a name it had positive proof for");
  }

  /*
   * A pet map runs to hundreds of summons and a night fights some of them. Claiming the ones this capture never mentions would add rows
   * to a window about fighters and move StateStamp() over verdicts no board reads — the same pool gate R25 and R21's cast feed obey.
   */
  [TestMethod]
  public void AMappedNameThisCaptureNeverMentionsGetsNoRowAtAll()
  {
    var run = RunDerive("[Mon May 04 19:00:05 2026] a fang spider hits Betebeatz for 40 points of damage.");

    var timeline = new EntityTimeline();
    LoadLedger(run);
    IdentityPriorStore.Instance.RememberPet("Quietpaw", "Beorun", 12345, persist: false);
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    Assert.AreEqual(0, timeline.ClaimsOf("Quietpaw").Count,
        "a mapped name no combat line used still got a timeline entry (the pool gate is the rule)");
  }

  /*
   * The lane also holds rows where nobody was ever named: `Labels.Unassigned` ("Unknown Pet Owner") is what the app writes for a summon
   * it saw without a possessive line, and `Labels.Unk` is what an old hand-edited row carries. Both are real data for the meter and for
   * the Owner column — and neither is anybody's word about identity. Claiming Pet from a placeholder would let a hole in the map decide
   * a Type cell, and the placeholder is a person-word the rest of the pipeline spends care refusing (IdentityLookup.IsOneOfUs).
   */
  [TestMethod]
  public void AnUnassignedMappingClaimsNothing()
  {
    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] a fang spider hits Squirticus for 40 points of damage.",
        "[Mon May 04 19:00:06 2026] a fang spider hits Bub for 41 points of damage.");

    var cold = Cold(run);
    Assert.AreEqual(IdentityKind.Unknown, cold.Identity("Squirticus"), "control: Squirticus must start unplaced");
    Assert.AreEqual(IdentityKind.Unknown, cold.Identity("Bub"), "control: Bub must start unplaced");

    LoadLedger(run);
    IdentityPriorStore.Instance.RememberPet("Squirticus", Labels.Unassigned, 12345, persist: false);
    IdentityPriorStore.Instance.RememberPet("Bub", Labels.Unk, 12345, persist: false);

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    AssertAreNot(IdentityKind.Pet, timeline.Identity("Squirticus"),
        "the unassigned placeholder was read as an operator's word about a name");
    AssertAreNot(IdentityKind.Pet, timeline.Identity("Bub"),
        "the unknown marker was read as an operator's word about a name");
  }

  /*
   * An old server's pairs do not testify about this one. The guard is written as "both sides carry a name and they match" because
   * ConfigUtil.ServerName is NULL until a log opens while the store keeps an empty string: those print identically and are not Equal, so
   * a bare inequality refuses in one direction and, inverted, grants testimony to a folder the capture does not belong to.
   */
  [TestMethod]
  public void AForeignLedgersPetMapDoesNotSpeak()
  {
    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] a fang spider hits Silentra for 40 points of damage.",
        "[Mon May 04 19:00:06 2026] a fang spider hits Silentra for 41 points of damage.");

    IdentityPriorStore.Instance.Init("Someotherfolder");
    ConfigUtil.ServerName = Server;   // the capture is ours; the ledger loaded below is somebody else's
    Assert.IsFalse(string.Equals(IdentityPriorStore.Instance.ServerName, Server, StringComparison.OrdinalIgnoreCase),
        "fixture: this ledger has to be a foreign folder for the guard to be exercised");

    IdentityPriorStore.Instance.RememberPet("Silentra", "Beorun", 12345, persist: false);

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    AssertAreNot(IdentityKind.Pet, timeline.Identity("Silentra"),
        "another server's pet map spoke about this capture");
  }

  /*
   * Restating an import is not something "a later log might not answer again", and recording it would let file data come back next week
   * wearing the costume of a sighting. Same reasoning, same omission as R25-roster.
   */
  [TestMethod]
  public void ASavedPetClaimIsNotRememberedAsAWitnessedVerdict()
  {
    Assert.IsFalse(IdentityPriorStore.WorthRemembering("R26-savedpet"),
        "the saved-pet claim is on the remembered-rules list; an import would re-enter as observed evidence");
    Assert.IsTrue(IdentityPriorStore.WorthRemembering("R24-petslot"),
        "control: a claim only the capture could supply must still be remembered, or the list is simply broken");
  }

  // ---- helpers -----------------------------------------------------------------------------------------------

  /*
   * Applies the rules twice: once cold (the fixture's guard — each name has to read `Still` BEFORE the lane speaks, or the test proves
   * nothing), then with the pairs on the ledger's ownership lane and a fresh timeline. The registry is deliberately left empty: this is
   * the ledger speaking, not the live map.
   */
  private static EntityTimeline WithMappedPair(PipelineHarness.DeriveRunResult run, IdentityKind still, params (string Pet, string Owner)[] pairs)
  {
    var probe = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, probe, run.HealFacts);

    foreach (var (pet, _) in pairs)
      Assert.AreEqual(still, probe.Identity(pet), $"fixture: {pet} reads {probe.Identity(pet)} before the map speaks, not {still}");

    LoadLedger(run);
    foreach (var (pet, owner) in pairs) IdentityPriorStore.Instance.RememberPet(pet, owner, 12345, persist: false);


    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    return timeline;
  }

  private static void LoadLedger(PipelineHarness.DeriveRunResult run) => IdentityPriorStore.Instance.Init(Server);

  private static EntityTimeline Cold(PipelineHarness.DeriveRunResult run)
  {
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    return timeline;
  }

  private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
  {
    var kind = timeline.IdentityWithSource(name, out var source);
    Assert.AreEqual(expected, kind, $"{name}: expected {expected}, got {kind} ({source ?? "no assignment"})");
    Assert.AreEqual(expectedSource, source, $"{name}: wrong rule got there first");
  }

  private static void AssertAreNot(IdentityKind forbidden, IdentityKind actual, string message)
    => Assert.AreNotEqual(forbidden, actual, message);

  private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-savedpet-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Probeone_Eqgate.txt");   // the file name is what seeds ConfigUtil.ServerName
    try
    {
      File.WriteAllLines(log, lines);
      return PipelineHarness.RunFileDerived(log);
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }
}

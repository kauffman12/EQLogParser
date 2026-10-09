using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * R25-roster: this application's own saved roster (identity-priors.txt's roster lane, which is where players.txt's value went when
 * the file was frozen 2026-10-09) testifying in the rules pass.
 *
 * The operator's instruction was to weigh it "as strongly as things like the class spell cast", and three refusals keep a decade of
 * curated names from becoming a permanent wrong answer — see ClassificationRules.ApplySavedRoster for the reasoning. What these
 * tests pin is the shape of that compromise: a silent regular who is on the list FINALLY reads Player, while nothing this capture
 * watched happening is overwritten, no name from another server's ledger speaks, and the roster never feeds the inference stages
 * (it runs after them — which still outweighs them, because Strong beats Medium).
 *
 * IdentityPriorStore is a process singleton holding whichever folder was opened last, so every test here loads it onto its own
 * temp-free server name, writes rows with persist:false (no file), and hands the singleton to a throwaway name on the way out —
 * otherwise RegistrySeed in a later test would find raiders this fixture invented (AGENTS: process state is set and restored).
 */
[TestClass]
[DoNotParallelize]
public sealed class RosterMemoryClaimTest
{
  /*
   * The rule testifies only for a capture whose server the ledger's folder answers for, and in this harness no log really opens that
   * far — so the fixture states the server on both sides exactly as MainWindow does at log open (ConfigUtil.ServerName is a plain
   * static field, null until something assigns it). Restored in Cleanup because it is process state.
   */
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
    IdentityPriorStore.Instance.Init("roster-claim-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ServerName = _savedServerName;
    PlayerRegistry.Instance.Clear();
  }

  /*
   * The whole point of the rule. A name this capture only ever receives hits from — no cast, no chat line, no /who, nothing the
   * rule book can read off behaviour — was Unknown, which is why it disappeared from the identity list while everybody fought it.
   * Being on this application's roster is now enough to put it on our side, wearing its own word.
   */
  [TestMethod]
  public void ASavedNameNothingElsePlacedFinallyReadsPlayer()
  {
    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] a fang spider hits Silentra for 40 points of damage.",
        "[Mon May 04 19:00:06 2026] a fang spider hits Silentra for 41 points of damage.");

    Assert.AreEqual(IdentityKind.Unknown, Cold(run).Identity("Silentra"),
        "control: this shape must stay unplaced on its own, or the test proves nothing");

    var timeline = WithRoster(run, ("Silentra", IdentityKind.Unknown));

    AssertIdentity(timeline, "Silentra", IdentityKind.Player, "R25-roster");

    timeline.IdentityAt("Silentra", double.PositiveInfinity, out var strength, out _);
    Assert.AreEqual(RuleStrength.Strong, strength,
        "the roster is meant to sit in the behaviour tier (joining a raid, guild speech, a class-spell cast), not below it");

    // The cell and the hover both speak this application's words — never a filename, never the code.
    var why = IdentityVocabulary.WhyWord("R25-roster");
    Assert.AreEqual("Roster Member", why);
    Assert.IsFalse(why.Contains(".txt", StringComparison.Ordinal));
    Assert.IsFalse(IdentityVocabulary.ProofText("R25-roster", IdentityKind.Player).Contains("players", StringComparison.OrdinalIgnoreCase));
  }

  /*
   * The refusal that makes Strong safe: a name this capture PLACED keeps that answer. The roster fills shadows; it does not
   * outvote a sighting, so nothing the curated list got wrong can strand a name — the failure the operator named when they chose a
   * frozen feed over a live input ("if there's a problem with what it's doing players would be stuck with it").
   */
  [TestMethod]
  public void ANameThisCapturePlacedKeepsItsOwnVerdict()
  {
    PipelineHarness.EnsureDataStore();

    // A name the NPC database knows, sitting on the roster as a mistake would.
    Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("Alleza"), "npcs.txt no longer knows Alleza - pick another name for this test");

    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] Alleza hits Betebeatz for 300 points of damage.",
        "[Mon May 04 19:00:06 2026] Alleza hits Betebeatz for 300 points of damage.");

    /*
     * "Still" is what the name reads BEFORE the roster speaks — here, Npc, which is the point: the helper's own guard refuses to run
     * this test at all if the capture ever stops placing Alleza on its own.
     */
    var timeline = WithRoster(run, ("Alleza", IdentityKind.Npc));

    AssertAreNot(IdentityKind.Player, timeline.Identity("Alleza"),
        "the roster outvoted this capture's own evidence about a name it had positive proof for");
  }

  /*
   * A curated list runs to hundreds of names and a night fights some of them. Claiming the ones this capture never mentions would
   * add rows to a window about fighters and move StateStamp() over verdicts no board reads — the pool gate R21's cast feed learned
   * the same law on.
   */
  [TestMethod]
  public void ASavedNameThisCaptureNeverMentionsGetsNoRowAtAll()
  {
    var run = RunDerive("[Mon May 04 19:00:05 2026] a fang spider hits Betebeatz for 40 points of damage.");

    var timeline = new EntityTimeline();
    LoadLedger(run);
    IdentityPriorStore.Instance.RememberRoster("Quietone", 12345, "Cleric", persist: false);
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    Assert.AreEqual(0, timeline.ClaimsOf("Quietone").Count,
        "a roster name no combat line used still got a timeline entry (the pool gate is the rule)");
    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Quietone"));
  }

  /*
   * THE ORDERING LAW: memory must not feed inference. R7 builds sides out of what a defender IS, so if the roster spoke first, an
   * attacker that hit a saved name would be concluded hostile out of this application's own list rather than out of the capture —
   * "a guess that survives only because it was written down". R25 runs after the graph, so Grishnak stays Unknown here even though
   * the name it beat on reads Player by the end of the pass.
   */
  [TestMethod]
  public void TheRosterNeverFeedsTheGraph()
  {
    var run = RunDerive(
        "[Mon May 04 19:00:05 2026] Grishnak hits Silentra for 40 points of damage.",
        "[Mon May 04 19:00:06 2026] Grishnak hits Silentra for 41 points of damage.");

    var timeline = WithRoster(run, ("Silentra", IdentityKind.Unknown));

    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Silentra"), "control: the roster did not testify");
    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Grishnak"),
        "an attacker was concluded hostile from what this app remembered about its victim, not from the capture");
  }

  /*
   * A restatement of membership is not something "a later log might not answer again", so R25 stays out of the ledger's remember
   * allowlist. Recording it would bring tomorrow's pass wearing the roster's own word as if a rule had observed an event — the
   * laundering this store's header is written to prevent (docs/DesignNotes.md → "What this application remembers").
   */
  [TestMethod]
  public void ARosterClaimIsNeverWrittenBackAsMemory()
    => Assert.IsFalse(IdentityPriorStore.WorthRemembering("R25-roster"),
        "the roster's word was recorded as if a rule had read it off an event in a log");

  /*
   * One ledger object for the whole process, and during a log switch it can still answer for the folder opened before this one.
   * Testimony from that list would claim another server's raid as tonight's, which is the leak every writer here refuses.
   */
  [TestMethod]
  public void AForeignLedgerDoesNotTestify()
  {
    var run = RunDerive("[Mon May 04 19:00:05 2026] a fang spider hits Silentra for 40 points of damage.");

    var timeline = new EntityTimeline();
    IdentityPriorStore.Instance.Init("Someotherserver");   // a folder this capture does not belong to
    IdentityPriorStore.Instance.RememberRoster("Silentra", 12345, null, persist: false);
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Silentra"),
        "a roster loaded for another server claimed names from this capture");
  }

  // ---- helpers ----

  /*
   * Load the ledger for whatever server the capture belongs to (the harness derives it from the file name), put the given names on
   * the roster, then run the rules pass. `kindAfterRules` is a guard rail for fixture authors: each listed name must still be that
   * kind BEFORE the roster speaks, or the test below measures nothing.
   */
  private static EntityTimeline WithRoster(PipelineHarness.DeriveRunResult run, params (string Name, IdentityKind Still)[] roster)
  {
    var probe = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, probe, run.HealFacts);

    foreach (var (name, still) in roster)
      Assert.AreEqual(still, probe.Identity(name), $"fixture: {name} is already {probe.Identity(name)} before the roster speaks");

    LoadLedger(run);
    foreach (var (name, _) in roster) IdentityPriorStore.Instance.RememberRoster(name, 12345, null, persist: false);

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
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-roster-" + Guid.NewGuid().ToString("N")));
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

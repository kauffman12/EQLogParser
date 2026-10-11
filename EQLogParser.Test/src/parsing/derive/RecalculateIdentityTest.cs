using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * the take-back icon in the Name column: forget everything this application has ever WRITTEN DOWN about a name — manual override,
 * legacy/roster claims, older-log verdict, saved summon owner, and (the half "Reset" in the dropdown never had) the stored fallback
 * class — then let only the open capture answer (2026-11, docs/DesignNotes.md → "The take-back icon takes everything back").
 *
 * The memory-vs-evidence asymmetry is the law, in TWO lanes: kind is a static verdict, so a name whose only claim was memory
 * leaves and a name this capture's own lines place comes straight back; class is dynamic per-second evidence with the default as
 * fallback-only, so the fallback (memory) goes while this capture's cast-learned WINDOWS stay — "purely going to show what the current
 * log thinks" includes the class this log learned. What this file pins is
 * the forget side (every lane, in one verb) and the two things it must NOT do: leave a stored answer underneath, and write
 * anything negative at all (a "suppression" would be a sixth identity word nobody priced — there is no ban to write, which is
 * exactly why an unplaced name stays eligible for every rule on every pass).
 *
 * State hygiene copied from OperatorVerdictForgetsTest: a temp config dir plus a server name this class owns, and Clear() on
 * the registry because it is the roster lane's in-memory half. The class-write validator is swapped like FrenzyClassTest does,
 * so `SetDefaultPlayerClass`/`SetActivePlayerClass` accept test words without a data store.
 */
[TestClass]
public sealed class RecalculateIdentityTest
{
  private const string Server = "Recalc Test";

  private string _root = string.Empty;
  private string _savedConfigDir = string.Empty;
  private string _savedServerName = string.Empty;
  private Func<string, bool>? _originalIsValidClass;

  [TestInitialize]
  public void Setup()
  {
    TakenBackNames.Clear(); // process-wide display state: another test's click may not put a row in this one's census
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    _root = Path.Combine(Path.GetTempPath(), "recalc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
    ConfigUtil.ConfigDir = _root;
    ConfigUtil.ServerName = Server;

    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init(Server);
    IdentityPriorStore.Instance.Init(Server);
    IdentityLookup.LiveVerdict = null;
    IdentityLookup.LiveOwner = null;

    _originalIsValidClass = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => name is "Bard" or "Cleric";
  }

  [TestCleanup]
  public void Cleanup()
  {
    CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);

    IdentityLookup.LiveVerdict = null;
    IdentityLookup.LiveOwner = null;
    PlayerRegistry.Instance.Clear();
    IdentityPriorStore.Instance.Init("recalc-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityOverrideStore.Instance.Init("recalc-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
  }

  private static long SeenAgo(int days) => (long)DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-days));

  /*
   * Every lane, seeded the way its real writer would have written it: a verdict typed in this app, a roster row with its class
   * (the players.txt import's shape), a verified pet, a saved summon-owner pair from either feed, a default class an operator
   * chose, and a cast-learned class window. After the click, NONE of it remains — and nothing else in the registry moves.
   */
  [TestMethod]
  public void RecalculateForgetsEveryLaneTheAppHasStoredAboutAName()
  {
    const string name = "Probera";

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Player);            // the manual override
    IdentityPriorStore.Instance.RememberRoster(name, SeenAgo(3), "Bard");       // the legacy/roster lane, with its class
    PlayerRegistry.Instance.AddVerifiedPet(name);                               // parser-verified summon
    PlayerRegistry.Instance.AddPetToPlayer(name, "Probestorm");                 // the saved owner pair
    PlayerRegistry.Instance.SetDefaultPlayerClass(name, "Bard");                // an operator's chosen class
    PlayerRegistry.Instance.SetActivePlayerClass(name, "Cleric", 2, 10.0);      // tonight's cast-learned class

    Assert.IsTrue(IdentityOverrideStore.Instance.TryGet(name, out _), "fixture: the claim was written");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster(name), "fixture: the ledger carries the roster bit");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPet(name), "fixture: the pet claim stands");
    Assert.AreEqual("Probestorm", PlayerRegistry.Instance.GetPlayerFromPet(name), "fixture: the pair is stored");
    Assert.AreEqual("Cleric", PlayerRegistry.Instance.GetPlayerClass(name, 20.0), "fixture: the learned class reads back");

    ClassificationCommands.Recalculate(name);

    Assert.IsFalse(IdentityOverrideStore.Instance.TryGet(name, out _), "the override row goes…");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster(name), "…and with it the ledger's roster bit and the older-log verdict…");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPet(name), "…and the verified-pet claim…");
    Assert.IsNull(PlayerRegistry.Instance.GetPlayerFromPet(name), "…and the saved owner pair — BOTH lanes, live map and ledger column");
    Assert.AreEqual(string.Empty, PlayerRegistry.Instance.GetDefaultPlayerClass(name),
                    "the stored FALLBACK class goes: the roster row and a typed default are memory, and memory is what this click forgets");
    Assert.AreEqual("Cleric", PlayerRegistry.Instance.GetPlayerClass(name, 20.0),
                    "this capture's own cast-learned window SURVIVES the take-back — it is what the current log thinks, read against its own clock");
    Assert.IsFalse(IdentityLookup.IsOneOfUs(name), "nothing left in memory calls it one of ours");

    // The click is about one name. A neighbour's beliefs are not collateral damage.
    PlayerRegistry.Instance.AddVerifiedPet("Probestorm");
    ClassificationCommands.Recalculate(name);   // a second click on the same row must be as clean as the first
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPet("Probestorm"), "recalculating X never touches what is stored about Y");
  }

  /*
   * The other half of the verb: nothing NEGATIVE is written. There is no override saying Unknown (that would ride at Manual and
   * outrank this capture's own evidence — the exact inversion of what the click promises), no ledger row, and no ban of any kind
   * (there is no such word in the identity vocabulary). So a re-claim after the click lands exactly as it would for a name never
   * seen: an operator word outranks again, and the class lanes accept new sightings.
   */
  [TestMethod]
  public void RecalculateWritesNoSuppressionANameTheCaptureNamesIsClaimableAgain()
  {
    const string name = "Probereborn";

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Player);
    PlayerRegistry.Instance.SetDefaultPlayerClass(name, "Bard");

    ClassificationCommands.Recalculate(name);

    Assert.IsFalse(IdentityOverrideStore.Instance.TryGet(name, out _),
                   "an asserted Unknown would sit at Manual (1000) above the rules this click is asking to speak — there must be no entry at all, of any kind");

    Assert.IsTrue(ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc),
                  "the name is as claimable as a name never seen — the door that was just used does not latch shut behind itself");
    Assert.AreEqual(IdentityKind.Npc, IdentityLookup.KindAt(name));

    PlayerRegistry.Instance.SetActivePlayerClass(name, "Cleric", 2, 1.0);
    Assert.AreEqual("Cleric", PlayerRegistry.Instance.GetPlayerClass(name, 5.0),
                    "the class lanes are cleared, not locked: this capture's cast lines re-teach on the next pass like any other evidence");
  }

  /*
   * The kind/class split: kind is a STATIC decision — a Set-as overrides every identity belief and never changes inside the log —
   * while class is DYNAMIC evidence (per-second windows) with the default as fallback-only. So a kind verdict touches NEITHER class
   * lane, (and the take-back icon) — alone — takes back the fallback; the capture's own windows stay, because they are this log's opinion,
   * and they retire with the capture on their own.
   */
  [TestMethod]
  public void AKindVerdictTouchesNoClassLaneAndOnlyTheCalculatorTakesTheFallback()
  {
    const string victim = "Probemage";
    const string bystander = "Probecast";

    PlayerRegistry.Instance.SetDefaultPlayerClass(victim, "Bard");          // the fallback lane (typed default)
    PlayerRegistry.Instance.SetActivePlayerClass(victim, "Cleric", 2, 10.0); // this capture's own window
    PlayerRegistry.Instance.SetDefaultPlayerClass(bystander, "Bard");

    ClassificationCommands.ApplyVerdict(victim, IdentityKind.Npc);
    Assert.AreEqual("Bard", PlayerRegistry.Instance.GetDefaultPlayerClass(victim),
                    "a Set-as click overrides every IDENTITY belief; the class is a different question on a different clock");
    Assert.AreEqual("Cleric", PlayerRegistry.Instance.GetPlayerClass(victim, 20.0),
                    "…and the live windows keep answering their per-second reads through it");

    ClassificationCommands.Recalculate(victim);
    Assert.AreEqual(string.Empty, PlayerRegistry.Instance.GetDefaultPlayerClass(victim), "the fallback lane goes, in one spelling — the map is case-insensitive");
    Assert.AreEqual("Cleric", PlayerRegistry.Instance.GetPlayerClass(victim, 20.0),
                    "but the window read still lands: what THIS log learned about the class survives, and is read at its own second");
    Assert.AreEqual("Bard", PlayerRegistry.Instance.GetDefaultPlayerClass(bystander), "no other name in the lane moves");
  }

  [TestMethod]
  public void ABlankNameRecalculatesNobody()
  {
    ClassificationCommands.Recalculate(null);
    ClassificationCommands.Recalculate("   ");

    PlayerRegistry.Instance.SetDefaultPlayerClass("Probekeeper", "Bard");
    ClassificationCommands.Recalculate(string.Empty);
    Assert.AreEqual("Bard", PlayerRegistry.Instance.GetPlayerClass("Probekeeper", 0.0), "an empty name forgets nobody");
  }

  /*
   * THE ROW STAYS (2026-11). Taking a claim back removes MEMORY, and for a name whose every claim came from memory that used to leave
   * nothing to list: the subject of the click vanished out of the window the click happened in, which reads as a delete and left the pane's
   * only button with no visible answer. So the take-back notes the name (TakenBackNames) and the census keeps it — Unknown in Type, the
   * provenance word "TakenBack" carrying the cell ("Taken Back") and the hover ("Claim Taken Back").
   *
   * It is DISPLAY state: no override row, no ledger row, no evidence claim, nothing negative (see the test above). So a name this capture
   * places still arrives through its own source with its own rule word, and a plain `ApplyVerdict(name, Unknown)` — which writes an answer
   * rather than taking one back — is NOT the same gesture and stamps nothing. A capture nobody clicked in lists no taken-back names, so the
   * empty list stays honest (docs/DesignNotes.md → "A take-back keeps its row, reading what it did").
   */
  [TestMethod]
  public void ATakenBackNameStaysOnTheListReadingUnknownRatherThanVanishing()
  {
    const string name = "Probememonly";

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Player);
    PlayerRegistry.Instance.AddVerifiedPlayer(name, DateUtil.ToDotNetSeconds(DateTime.Now));
    Assert.AreEqual(IdentityKind.Player, IdentityLookup.KindAt(name), "control: memory alone claims it before the click");

    ClassificationCommands.Recalculate(name);

    var row = CensusWithoutACapture().Find(name);
    Assert.IsNotNull(row, "a verb whose result is that its subject disappears off the surface it was clicked on reads as a delete");
    Assert.AreEqual(IdentityKind.Unknown, row!.Kind, "memory is gone and nothing else is asserted in its place");
    Assert.IsFalse(row.IsOperatorVerdict, "the click wrote no answer — Type reads Unknown, which is what 'let the log speak' looks like");
    Assert.AreEqual(TakenBackNames.Word, row.Reason, "and the row says what made it exist rather than sitting there unexplained");

    // The words ride the same vocabulary as every rule's, so no identifier reaches the screen.
    Assert.AreEqual("Taken Back", IdentityVocabulary.WhyWord(row.Reason),
                    "not 'Not Placed' — that is the word for a name nobody ever asked about");
    Assert.AreEqual("Claim Taken Back", IdentityVocabulary.ProofText(row.Reason, row.Kind),
                    "the hover names what the operator did rather than claiming the rules missed something");

    // A neighbour nobody clicked cannot start saying an operator acted on it: the marker is per name, and a name with no click
    // and no source is not listed at all — which is exactly the state BEFORE a click, and why the verb has to note its own name
    // rather than hope some later source lists it.
    Assert.IsNull(CensusWithoutACapture().Find("Problemute"), "no click, no source, no row");
  }

  /*
   * Successive take-backs ACCUMULATE: the second click must not retire the first name's row. A single-slot marker (the shape RecalcOverlay
   * uses for the label, because a label belongs to one row) would silently delete the earlier answer, and it would delete it invisibly —
   * the pane just gets shorter.
   */
  [TestMethod]
  public void TwoTakeBacksBothKeepTheirRows()
  {
    ClassificationCommands.Recalculate("Probeanne");
    ClassificationCommands.Recalculate("Probetom");

    var census = CensusWithoutACapture();
    Assert.AreEqual(TakenBackNames.Word, census.Find("Probeanne")?.Reason);
    Assert.AreEqual(TakenBackNames.Word, census.Find("Probetom")?.Reason);
  }

  /*
   * The verb is Recalculate, not "anything that writes Unknown": an explicit Unknown verdict asserts an answer at Manual strength and must
   * not leave a taken-back marker behind (nothing was taken back — an answer was given). It is also forgotten, so nothing lists the name.
   */
  [TestMethod]
  public void AnExplicitUnknownVerdictIsNotATakeBackAndStampsNothing()
  {
    const string name = "Probeexplicit";

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Pet);
    ClassificationCommands.ApplyVerdict(name, IdentityKind.Unknown);

    Assert.AreNotEqual(TakenBackNames.Word, CensusWithoutACapture().Find(name)?.Reason,
                       "an asserted Unknown is an answer, not a withdrawal; conflating them would make every 'clear claim' pin a row forever");
  }

  /*
   * The census with NO capture: every source except the facts and the timeline. That is the case the take-back row exists for — a name this
   * log never mentioned — and it is also what makes these tests cheap (no parsing). Mirrors what NamesTable builds when no session answers,
   * minus the UI: overrides + registry + ledger, nothing else to list a name from.
   */
  private static ClassificationReport CensusWithoutACapture() =>
      ClassificationReport.Build(null, null, null, IdentityOverrideStore.Instance, PlayerRegistry.Instance, IdentityPriorStore.Instance);

}

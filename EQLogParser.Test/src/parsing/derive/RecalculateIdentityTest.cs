using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The Name column's calculator: forget everything this application has ever WRITTEN DOWN about a name — manual override,
 * legacy/roster claims, older-log verdict, saved summon owner, and (the half "Reset" in the dropdown never had) the stored fallback
 * class — then let only the open capture answer (2026-11, docs/DesignNotes.md → "The calculator takes everything back").
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
   * lane, and the calculator — alone — takes back the fallback; the capture's own windows stay, because they are this log's opinion,
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
}

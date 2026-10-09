using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * What a manual decision means here (2026-10-09, the operator's words): "if im making a manual decision id like to remove all
 * previous knowledge and accept what im saying. then of course new knowledge could be built later … my rule would override."
 *
 * That is two halves, and the app only did the second one. A verdict used to ADD an entry to identity-overrides.txt and drop the
 * ledger row, while PlayerRegistry kept answering from players.txt and petmapping.txt — several seams never ask the override at
 * all — so a name kept behaving like what it used to be. The concrete failure was `Assign X as Pet of Y`: AddPetToPlayerNoLock
 * refuses to reassign a VERIFIED PLAYER, and a summon the parser verified as a player is exactly what an operator assigns, so the
 * click wrote nothing anywhere (measured on a live capture: an assigned name's damage sat in its own row, unchanged, before and
 * after). ClassificationCommands.ApplyVerdict is now the one door for all of it — forget, then assert.
 *
 * State hygiene copied from IdentityLookupTest: a temp config dir plus a server name this class owns, so neither store can write
 * to (or answer from) the machine's real EQLogParser folder; and Clear() on the registry because it is the roster lane's in-memory half.
 */
[TestClass]
public sealed class OperatorVerdictForgetsTest
{
  private const string Server = "Forget Test";

  private string _root = string.Empty;
  private string _savedConfigDir = string.Empty;
  private string _savedServerName = string.Empty;

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    _root = Path.Combine(Path.GetTempPath(), "forget-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
    ConfigUtil.ConfigDir = _root;
    ConfigUtil.ServerName = Server;

    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init(Server);
    IdentityPriorStore.Instance.Init(Server);
    IdentityLookup.LiveVerdict = null;
    IdentityLookup.LiveOwner = null;
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityLookup.LiveVerdict = null;
    IdentityLookup.LiveOwner = null;
    PlayerRegistry.Instance.Clear();
    IdentityPriorStore.Instance.Init("forget-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityOverrideStore.Instance.Init("forget-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
  }

  private static double SeenAgo(int days) => DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-days));

  [TestMethod]
  public void AssigningANameTheAppThoughtWasAPlayerToAnOwnerLands()
  {
    const string name = "Probewarden";
    const string owner = "Probemaster";

    // The state the field report describes: something summon-shaped that the parser verified as a player (it cast, it healed).
    PlayerRegistry.Instance.AddVerifiedPlayer(name, SeenAgo(1));
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer(name), "fixture: the app currently believes this is a player");

    Assert.IsTrue(PetAssignment.Assign(name, owner));

    Assert.AreEqual(owner, PlayerRegistry.Instance.GetPlayerFromPet(name),
        "the pair is the whole point of the click; it used to be refused in silence because the name read as a verified player");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPet(name));
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(name),
        "the old answer is GONE, not sitting underneath: registry seams do not ask the override");
    Assert.AreEqual(IdentityKind.Pet, IdentityLookup.KindAt(name), "and the kind verdict came with it, so the row stops being a player row");
  }

  [TestMethod]
  public void AManualVerdictEvictsTheRosterEntryAndTheLedgerRow()
  {
    const string name = "Probepal";

    PlayerRegistry.Instance.AddVerifiedPlayer(name, SeenAgo(2));
    IdentityPriorStore.Instance.RememberRoster(name, 12345, "Bard");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster(name), "fixture: the ledger carries this name as one of ours");

    Assert.IsTrue(ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc));

    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(name), "players.txt loses the row at the next save");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster(name), "the ledger's roster lane goes too — that bit is knowledge as well");
    Assert.IsFalse(IdentityLookup.IsOneOfUs(name), "the roster may not keep calling a name the operator called NPC one of ours");
    Assert.AreEqual(IdentityKind.Npc, IdentityLookup.KindAt(name));
  }

  [TestMethod]
  public void TheManualWordOutranksKnowledgeLearnedAfterIt()
  {
    const string name = "Probelater";

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc);

    // Then the app learns things through the back doors: a chat line, a /who reply, a loot line.
    PlayerRegistry.Instance.AddVerifiedPlayer(name, SeenAgo(0));
    PlayerRegistry.Instance.AddMerc(name);

    Assert.AreEqual(IdentityKind.Npc, IdentityLookup.KindAt(name),
        "the operator's word is not a tie-breaker that new evidence outvotes — 'my rule would override'");
    Assert.IsFalse(IdentityLookup.IsOneOfUs(name), "and the same question asked the other way answers the same");
  }

  [TestMethod]
  public void AVerdictLeavesPairsWhereTheNameIsTheOwner()
  {
    const string owner = "Probemaster";
    const string pet = "Probefluffy";

    PlayerRegistry.Instance.AddPetToPlayer(pet, owner);

    ClassificationCommands.ApplyVerdict(owner, IdentityKind.Npc);

    Assert.AreEqual(owner, PlayerRegistry.Instance.GetPlayerFromPet(pet),
        "'Fluffy belongs to X' is a claim about Fluffy, not about what X is — the old cascade threw these away and made operators retype them");
  }

  [TestMethod]
  public void SayingTheSameThingAgainWritesNothingAndEvictsNothing()
  {
    const string name = "Probedoubler";

    Assert.IsTrue(ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc));

    // Knowledge arriving after the verdict must survive a second click on the same word: a no-op is a click on nothing.
    PlayerRegistry.Instance.AddPetToPlayer("Probepetsurvivor", name);

    Assert.IsFalse(ClassificationCommands.ApplyVerdict(name, IdentityKind.Npc), "no file write for re-saying the answer");
    Assert.AreEqual(name, PlayerRegistry.Instance.GetPlayerFromPet("Probepetsurvivor"),
        "and no eviction either — forgetting to re-assert the same word would cost real memory for free");
  }

  [TestMethod]
  public void TakingAClaimBackLeavesNothingStandingBehindIt()
  {
    const string name = "Probeunset";

    ClassificationCommands.ApplyVerdict(name, IdentityKind.Player);
    PlayerRegistry.Instance.AddVerifiedPet(name);

    Assert.IsTrue(ClassificationCommands.ApplyVerdict(name, IdentityKind.Unknown));

    Assert.IsFalse(IdentityOverrideStore.Instance.TryGet(name, out _), "the claim is gone…");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(name), "…and nothing the app believed is left to answer in its place");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPet(name));
  }

  [TestMethod]
  public void ABlankNameIsRefusedRatherThanForgotten()
  {
    Assert.IsFalse(ClassificationCommands.ApplyVerdict(null, IdentityKind.Npc));
    Assert.IsFalse(ClassificationCommands.ApplyVerdict("   ", IdentityKind.Player));

    PlayerRegistry.Instance.AddVerifiedPlayer("Probekeeper", SeenAgo(1));
    ClassificationCommands.Forget(string.Empty);
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Probekeeper"), "an empty name forgets nobody");
  }
}

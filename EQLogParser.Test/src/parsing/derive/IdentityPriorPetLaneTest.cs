using EQLogParser;

namespace EQLogParser;

/*
 * The PET lane of identity-priors.txt: "whose pet is this" — the statement petmapping.txt carried (`Fluffy=Ziggy|ticks`),
 * now kept beside the roster bit so both files can stop being written.
 *
 * The lane is membership-shaped, and every property below follows from that rather than from anything about pets:
 *
 *   - it is NOT a verdict (Kind stays Unknown; an old mapping must not outvote tonight's rules about what a name IS);
 *   - its stamp moves FORWARD only, so importing an old list twice does nothing and replaying a backup cannot age an
 *     active pet out of memory;
 *   - 0 means "a statement, never retires" — the hand-typed half of an old mapping file carried no time at all;
 *   - clearing it leaves a witnessed verdict underneath alone, and removes the row only when nothing else was earned.
 *
 * Temp config dir per test, exactly as IdentityPriorStoreTest does: these write real files.
 */
[TestClass]
public class IdentityPriorPetLaneTest
{
  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _tempDir = "";

  private const string Server = "Pet Lane Test";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "petlane-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = Server;

    IdentityPriorStore.Instance.Init(Server);
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityPriorStore.Instance.Init("petlane-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  private static void Restart() => IdentityPriorStore.Instance.Init(Server);

  /*
   * Real dotnet-epoch seconds, not round numbers out of a novel. The lanes age against the WALL CLOCK (StaleDays), so a
   * stamp like 4_000_000_000 reads as "unseen since the second century" and PruneRosterLocked removes the row at the very
   * next load — which makes a persistence test fail for a reason that has nothing to do with what it is pinning.
   */
  private static long NowS() => (long)DateUtil.ToDotNetSeconds(DateTime.Now);

  private const long DayS = 24 * 60 * 60;

  private string FileContents() =>
    string.Join("\n", ConfigUtil.ReadIdentityPriors(Server).Select(kv => $"{kv.Key}={kv.Value}"));

  [TestMethod]
  public void APetMappingSurvivesARestartWithItsOwnerAndItsClock()
  {
    IdentityPriorStore.Instance.RememberPet("Fluffy", "Ziggy", NowS());

    Restart();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("fluffy", out var owner), "the mapping did not come back");
    Assert.AreEqual("Ziggy", owner);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Fluffy", out var prior));
    Assert.AreEqual(NowS(), prior.SeenAtS, "the clock moved on the way through the file");
    Assert.IsTrue(IdentityPriorStore.Instance.HasOwnerRows);
  }

  [TestMethod]
  public void AnOwnerRowIsReadEvenThoughNoRuleWroteIt()
  {
    /*
     * The verdict allowlist refuses a row whose reason no capture earned, because that is this file restating an answer it
     * always has (npcs.txt). Ownership is different in kind — no rule "earns" it, the operator's map IS the source — so a
     * row like this must load. Written by hand here because nothing in a cold fixture produces one.
     */
    Directory.CreateDirectory(Path.Combine(_tempDir, Server));
    File.WriteAllLines(Path.Combine(_tempDir, Server, "identity-priors.txt"),
                      ["Fluffy=Unknown|PetMap|0|0|False||Ziggy"]);

    Restart();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var owner));
    Assert.AreEqual("Ziggy", owner);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Fluffy", out var prior));
    Assert.IsFalse(prior.Ours, "a mapping is not membership on the raid roster");
    Assert.AreEqual(IdentityKind.Unknown, prior.Kind, "a mapping is not a verdict either");
  }

  [TestMethod]
  public void AnOlderMappingCannotRewindTheClock()
  {
    var store = IdentityPriorStore.Instance;
    var today = NowS();
    store.RememberPet("Fluffy", "Ziggy", today);

    // Replaying an old players/petmapping backup, or importing a list copied off another machine.
    store.RememberPet("Fluffy", "Ziggy", today - 30 * DayS);

    Assert.IsTrue(store.TryGet("Fluffy", out var prior));
    Assert.AreEqual(today, prior.SeenAtS, "an old list aged a live mapping");
  }

  [TestMethod]
  public void ImportingTheSameMapTwiceChangesNothing()
  {
    var store = IdentityPriorStore.Instance;
    store.RememberPet("Fluffy", "Ziggy", NowS());
    var first = FileContents();

    store.RememberPet("Fluffy", "Ziggy", NowS());

    Assert.AreEqual(first, FileContents(), "a re-import rewrote the file it had just written");
  }

  [TestMethod]
  public void AnOwnerWriteNeverInventsAVerdictOrAMembership()
  {
    IdentityPriorStore.Instance.RememberPet("Sancus`s pet", "Sancus", NowS());

    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Sancus`s pet", out var prior));
    Assert.AreEqual(IdentityKind.Unknown, prior.Kind);
    Assert.IsFalse(prior.Ours);
    Assert.AreEqual(0, prior.Sightings, "ownership was not witnessed by a rule pass");

    // And the roster question — which is what a meter and a menu ask — still answers no for a pet.
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Sancus`s pet"));
  }

  [TestMethod]
  public void AWitnessedVerdictKeepsItsOwnReasonWhenItAlsoHasAnOwner()
  {
    /*
     * A name the graph decided is NPC and somebody's map calls a pet: two statements, both kept, and the rule's word stays
     * on the row because that is what the Names window must show as the proof. The owner is data beside it.
     */
    var store = IdentityPriorStore.Instance;
    var today = NowS();
    store.RememberPet("Goruuk", "Ziggy", today);
    Assert.IsTrue(store.TryGet("Goruuk", out var seeded));
    Assert.AreEqual(IdentityPriorStore.OwnerReason, seeded.Reason);

    // A capture's conclusion lands on the same row (this is what Record does after a derive).
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Goruuk", IdentityKind.Npc, 9, "R7-graph");
    store.Record(timeline, ["Goruuk"], today + DayS);

    Assert.IsTrue(store.TryGet("Goruuk", out var prior));
    Assert.AreEqual("R7-graph", prior.Reason, "the import's provenance word overwrote what a capture witnessed");
    Assert.AreEqual(IdentityKind.Npc, prior.Kind);
    Assert.AreEqual("Ziggy", prior.Owner, "recording a verdict lost the mapping that rode beside it");

    Restart();
    Assert.IsTrue(store.TryGet("Goruuk", out prior));
    Assert.AreEqual("R7-graph", prior.Reason);
    Assert.AreEqual("Ziggy", prior.Owner);
  }

  [TestMethod]
  public void ClearingAMappingTakesTheRowWithNothingElse()
  {
    var store = IdentityPriorStore.Instance;
    var today = NowS();
    store.RememberPet("Fluffy", "Ziggy", today);
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Goruuk", IdentityKind.Npc, 9, "R7-graph");
    store.Record(timeline, ["Goruuk"], today);
    store.RememberPet("Goruuk", "Ziggy", today);

    store.ForgetPet("Fluffy");
    Assert.IsFalse(store.TryGet("Fluffy", out _), "an owner-only row survived the removal of its only content");

    store.ForgetPet("Goruuk");
    Assert.IsTrue(store.TryGet("Goruuk", out var kept), "the mapping was also the verdict's home");
    Assert.AreEqual(IdentityKind.Npc, kept.Kind);
    Assert.AreEqual("R7-graph", kept.Reason);
    Assert.IsNull(kept.Owner);
  }

  [TestMethod]
  public void TheTailFieldsLoadInOrderFromAnOlderFile()
  {
    /*
       * Two older shapes have to keep parsing, or a user's memory disappears on upgrade: the four-field row written before
     * any lane existed, and the six-field row that carries membership and class but no owner. Appending fields at the END
     * is what makes this true; anything else is a migration script nobody tested.
     */
    Directory.CreateDirectory(Path.Combine(_tempDir, Server));
    File.WriteAllLines(Path.Combine(_tempDir, Server, "identity-priors.txt"),
                      [
                        $"OldRow=Npc|R7-graph|{NowS() - DayS}|4",
                        $"RosterRow=Unknown|Imported|{NowS() - 2 * DayS}|0|True|Warrior",
                      ]);

    Restart();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("OldRow", out var old));
    Assert.AreEqual(4, old.Sightings);
    Assert.IsNull(old.Owner);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("RosterRow", out var roster));
    Assert.IsTrue(roster.Ours);
    Assert.AreEqual("Warrior", roster.Class);
    Assert.IsFalse(IdentityPriorStore.Instance.HasOwnerRows);
  }
}

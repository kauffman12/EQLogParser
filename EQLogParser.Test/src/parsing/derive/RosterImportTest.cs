using EQLogParser;

namespace EQLogParser;

/*
 * The one-time move of players.txt into the roster lane of identity-priors.txt (RosterImport). What these tests hold is
 * the part a reader cannot see from the code being obviously right: that a migration which runs at log open must not
 * damage either the file it reads or the memory the app is starting up with.
 *
 *   - It carries MEMBERSHIP, never a verdict: a carried row says "this app called the name one of ours" and its Kind
 *     stays Unknown, so nine hundred remembered names cannot outvote tonight's rules pass about what any of them IS.
 *   - It writes NO stamps of its own — the `=ticks` a row carries is what ages it, and an undated row stays undated. A
 *     migration that stamped today would make an operator's whole list young (and then old) on the day it was copied.
 *   - It runs ONCE per server folder, gated on the ledger already carrying roster rows; a hand-edited players.txt
 *     arriving afterwards is not re-imported, and re-running costs one File.Exists.
 *   - The wrong folder is refused: the ledger files what it is given under the name IT holds, so an import asked about
 *     server B while the ledger answers for server A would move a raid between folders.
 *   - The registry then seeds itself from that lane (a load, not a sighting), which is what lets players.txt stop being
 *     read later without the app losing its You-mapping on the way.
 *
 * ConfigUtil.* and both singletons are process state, so every test parks them in a temp folder and leaves the ledger
 * loaded on a name no folder has (the pattern of IdentityPriorStoreTest; the assembly does not parallelize).
 */
[TestClass]
public class RosterImportTest
{
  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";
  private Func<string, bool> _savedClassNameValidator = _ => false;
  private Func<string, double, IdentityKind>? _savedLiveVerdict;
  private string _tempDir = "";

  private const string Server = "Importest";

  [TestInitialize]
  public void ParkTheGlobalsInATempFolder()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "roster-import-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = Server;
    ConfigUtil.PlayerName = "Melodyn";

    // The seam's live hop is process state too: no session is open here, and a leaked delegate from another test would
    // answer "is this one of ours?" out of a timeline this folder never saw.
    _savedLiveVerdict = IdentityLookup.LiveVerdict;
    IdentityLookup.LiveVerdict = null;

    // Class words are only stored when the host recognizes them (CombatRecordLookup.IsValidClassName is injected by
    // App.xaml.cs and answers "nothing" headless). These tests care that a class SURVIVES the move, not what the spell
    // database accepts - so the hook is wired to two words and put back afterwards.
    _savedClassNameValidator = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => name is "Wizard" or "Cleric";

    IdentityPriorStore.Instance.Init(Server);
    PlayerRegistry.Instance.Init();
  }

  [TestCleanup]
  public void EmptyTheStoresAndRestore()
  {
    // Empty ServerName first: Clear(true) saves before wiping, and the parked server is someone else's config.
    ConfigUtil.ServerName = "";
    PlayerRegistry.Instance.Clear();

    IdentityPriorStore.Instance.Init("import-cleanup-" + Guid.NewGuid().ToString("N"));

    IdentityLookup.LiveVerdict = _savedLiveVerdict;
    CombatRecordLookup.IsValidClassName = _savedClassNameValidator;
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;

    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  [TestMethod]
  public void TheFirstOpenCarriesTheRosterIntoTheLedger()
  {
    var ticks = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-30)));
    WritePlayersFile([$"Kilsa={ticks},Cleric", "Betebeatz"]);

    var result = RosterImport.ImportPlayersFileOnce(Server);

    Assert.AreEqual(2, result.Applied, $"a row was not carried; file = [{string.Join(" | ", ReadPlayersFile())}]");
    Assert.AreEqual(1, result.WithClass);
    Assert.IsTrue(File.Exists(LedgerPath), "the ledger was never written - FlushChanges is what makes one batch one save");

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Kilsa", out var cls));
    Assert.AreEqual("Cleric", cls, "the class players.txt carried did not survive the move");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Kilsa", out var prior));

    // The stamp is the file's own, not today's: 30 days old stays 30 days old, or StaleDays would restart on import.
    Assert.AreEqual((long)ticks, prior.SeenAtS, "the import stamped the roster with its own date");
    Assert.IsTrue(prior.Ours);

    /*
     * And no verdict came with it. Being on the list is membership; Kind stays Unknown and Reason says where the row
     * came from, so RegistrySeed gains a name and no opinion about what it is
     * (docs/DesignNotes.md → "The roster lane: membership is not a verdict").
     */
    Assert.AreEqual(IdentityKind.Unknown, prior.Kind, "the roster arrived wearing a verdict");
    Assert.AreEqual("Imported", prior.Reason);
    Assert.AreEqual(0, prior.Sightings, "membership advertised a witnessed capture it never had");

    // Undated rows are statements: 0 stays 0, which is the value no dial ever retires.
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Betebeatz", out var untyped));
    Assert.AreEqual(0, untyped.SeenAtS, "an undated row was given a time it never had");
    Assert.IsNull(untyped.Class);

    // The point of the whole migration, from the caller's side: the seam answers for a saved name with no session open.
    Assert.IsTrue(IdentityLookup.IsOneOfUs("Kilsa"), "a carried roster name does not answer through the seam");
  }

  [TestMethod]
  public void TheImportRunsOncePerServerFolder()
  {
    WritePlayersFile(["Kilsa"]);
    Assert.AreEqual(1, RosterImport.ImportPlayersFileOnce(Server).Applied, "the first run carried nothing");

    /*
     * A name added to players.txt afterwards is NOT picked up. That is deliberate: the gate is "has this folder's roster
     * been carried?", and re-running on every open would let an old backup dropped into the folder rewrite membership
     * behind the operator's back, for no gain now that the ledger is the durable list.
     */
    WritePlayersFile(["Kilsa", "Newperson"]);
    var second = RosterImport.ImportPlayersFileOnce(Server);

    Assert.AreEqual(0, second.Applied, $"the import ran twice; result = {second}");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Newperson"), "a second import carried a new name anyway");
  }

  [TestMethod]
  public void AFolderWithNoRosterFileLeavesNothingBehind()
  {
    // The ordinary case for every server from here on: no players.txt, so nothing to carry and nothing to say.
    var result = RosterImport.ImportPlayersFileOnce(Server);

    Assert.IsFalse(result.DidWork);
    Assert.AreEqual(0, result.Refused);
    Assert.IsFalse(File.Exists(LedgerPath), "an import with no source created a ledger nobody wrote to");
  }

  [TestMethod]
  public void AJunkLineIsRefusedAndTheGoodRowsStillArrive()
  {
    /*
     * "=0" is the hand-typed shape: a statement nobody ever saw do anything, which no dial retires. A NONZERO stamp is an observation
     * and ages — PlayerRegistryPersistenceTest pins that — so the 123 this fixture used to carry (January 1970) was expired membership
     * that the next flush correctly threw away once Save() aged the roster lane itself.
     */
    WritePlayersFile(["Unknown", "!!Goruuk", string.Empty, "   ", "Goruuk", "Akini, Xanathan=0"]);

    var result = RosterImport.ImportPlayersFileOnce(Server);

    // The unknown marker is a placeholder somebody's editor wrote, '!' is a hand crossing a line out, and blank lines are
    // not refusals - they are nothing. The two usable rows still land, so one bad line cannot cost an operator their list.
    Assert.AreEqual(2, result.Applied, $"expected Goruuk and the comma name; got {result}");
    Assert.AreEqual(2, result.Refused, $"the junk count is wrong; got {result}");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Goruuk"));

    /*
     * The comma name matters: IsPossiblePlayerName wants letters only and refuses it, and a curated file legitimately
     * holds such names (a summon with two masters reads "Akini, Xanathan"). Refusing one at LOAD is how a curated list
     * disappears silently - the failure this project has already been bitten by twice.
     */
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Akini, Xanathan"), "a name the letter gate refuses was refused here too");
  }

  [TestMethod]
  public void TheLocalPlayerRowIsImportedUnderTheRealName()
  {
    // A hand-edited file may carry the literal "You"; it means whoever is playing now, same reading PlayerRegistry gives.
    WritePlayersFile(["You"]);

    Assert.AreEqual(1, RosterImport.ImportPlayersFileOnce(Server).Applied);

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Melodyn"), "\"You\" was not remapped to the local character");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("You"),
                    "the word \"You\" itself reached the ledger - a name the identity rules meet in every later capture");
  }

  [TestMethod]
  public void AYouLineWithNoCharacterOpenIsRefused()
  {
    // Headless, or before a file is picked: there is nobody for "You" to mean, so the row is refused rather than kept.
    ConfigUtil.PlayerName = "";
    WritePlayersFile(["You", "Goruuk"]);

    var result = RosterImport.ImportPlayersFileOnce(Server);

    Assert.AreEqual(1, result.Applied, $"the ordinary row did not survive the empty PlayerName; got {result}");
    Assert.AreEqual(1, result.Refused);
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("You"));
  }

  [TestMethod]
  public void AnImportAskedAboutAnotherFolderIsRefused()
  {
    /*
     * MainWindow loads the registry before the identity stores on a server switch, so this call can arrive naming a
     * folder while the ledger still answers for the one we just left - and Save() files every row under the name the
     * STORE holds. One server's raid landing in another's identity-priors.txt is silent and permanent, so the folder is
     * checked rather than trusted.
     */
    WritePlayersFile(["Goruuk"], "Otherserver");

    var result = RosterImport.ImportPlayersFileOnce("Otherserver");

    Assert.AreEqual(0, result.Applied, "an import wrote into a ledger that answers for a different server");
    Assert.IsFalse(IdentityPriorStore.Instance.HasRosterRows, "the other folder's names landed in this ledger");
    Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "Otherserver", "identity-priors.txt")),
                    "a ledger was created for a folder whose import was refused");
  }

  [TestMethod]
  public void TheRegistrySeedsItselfFromTheLedgerWithoutPlayersTxt()
  {
    /*
     * The half that makes players.txt retireable: memory comes up carrying the ledger's roster even with no file at all,
     * so You-mapping and +Pets folding do not depend on one file surviving. init:true throughout - a load is not a
     * sighting (the pet-stamp bug is the cautionary tale: stamping on load aged a whole file out in one startup).
     */
    var ticks = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-10)));
    IdentityPriorStore.Instance.RememberRoster("Quillana", (long)ticks, "Wizard");

    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Quillana"), "the ledger's roster did not reach memory");
    Assert.AreEqual("Wizard", PlayerRegistry.Instance.GetDefaultPlayerClass("Quillana"));
    Assert.IsTrue(IdentityLookup.IsOneOfUs("Quillana"));

    // And a load still does not dirty the file: nothing here was SIGHTED, so nothing is owed a timestamp.
    PlayerRegistry.Instance.Save();
    Assert.IsFalse(File.Exists(PlayersFilePath), "seeding from the ledger marked the roster as updated and wrote players.txt");
  }

  [TestMethod]
  public void ARemovedNameStaysOutOfBothStores()
  {
    /*
     * The eviction law across the two stores: taking membership off leaves no roster row in the ledger, so the registry
     * cannot re-adopt the name at the next open - while the name is still free to be LEARNED again by evidence (this is
     * not a veto; docs/DesignNotes.md → "A veto nobody could switch on"). Through this stage players.txt is still live,
     * so the row has to leave the file too - exactly what RemoveVerifiedPlayer does when an operator clicks it.
     */
    WritePlayersFile(["Goruuk"]);
    Assert.AreEqual(1, RosterImport.ImportPlayersFileOnce(Server).Applied);

    IdentityPriorStore.Instance.ForgetRoster("Goruuk");
    WritePlayersFile([]);
    PlayerRegistry.Instance.Init();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Goruuk"));
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"),
                    "a name whose membership was taken away came back through the ledger seed");
  }

  // ---- helpers -------------------------------------------------------------------------------------------------


  [TestMethod]
  public void TheFirstOpenCarriesThePetMapIntoTheLedgerAndTheRegistry()
  {
    var stamp = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-10)));
    WritePetMapFile([$"Fluffy=Ziggy|{stamp:0}", $"Bub={Labels.Unassigned}"]);

    var result = RosterImport.ImportPetMapOnce(Server);
    Assert.AreEqual(2, result.Applied, $"a pair was not carried; file = [{string.Join(" | ", ReadPetMapFile())}]");

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var owner));
    Assert.AreEqual("Ziggy", owner, "the sighting stamp leaked into the owner's name");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Fluffy", out var prior));
    Assert.AreEqual((long)stamp, prior.SeenAtS, "the file's own clock did not survive the move");

    // The registry is a mirror of the lane: this is the order MainWindow runs (import, then Init), and it is what lets
    // petmapping.txt stop being read without the +Pets folding or the Pet Owners window losing anything.
    PlayerRegistry.Instance.Init();
    Assert.AreEqual("Ziggy", PlayerRegistry.Instance.GetPlayerFromPet("Fluffy"));
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPet("Fluffy"));
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Ziggy"), "a mapped owner is not on the player list");
  }

  [TestMethod]
  public void ThePetMapIsImportedOncePerFolder()
  {
    WritePetMapFile(["Fluffy=Ziggy"]);

    Assert.IsTrue(RosterImport.ImportPetMapOnce(Server).DidWork);
    Assert.IsFalse(RosterImport.ImportPetMapOnce(Server).DidWork, "the lane's own contents are the record that this ran");

    // A hand-restored petmapping.txt afterwards is not re-imported: the ledger is the durable map from here, and a second
    // pass could only resurrect pairs an operator had since removed.
    WritePetMapFile(["Fluffy=Ziggy", "Squirrel=Ditto"]);
    Assert.IsFalse(RosterImport.ImportPetMapOnce(Server).DidWork);
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Squirrel"));
  }

  [TestMethod]
  public void APetMapForTheWrongFolderIsRefusedAndNothingIsWritten()
  {
    WritePetMapFile(["Fluffy=Ziggy"], "Other Server");

    var result = RosterImport.ImportPetMapOnce("Other Server");

    Assert.AreEqual(0, result.Applied);
    Assert.IsFalse(IdentityPriorStore.Instance.HasOwnerRows);
    Assert.IsFalse(File.Exists(LedgerPath), "a refused migration still wrote the ledger — one server's pets in another's file");
  }

  [TestMethod]
  public void AnOwnerOfYouMeansWhoeverIsPlayingNow()
  {
    WritePetMapFile(["Fluffy=you"]);

    Assert.AreEqual(1, RosterImport.ImportPetMapOnce(Server).Applied);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var owner));
    Assert.AreEqual("Melodyn", owner, "the hand-edited You row did not resolve to the local character");

    // With no character open there is nobody to mean, so the row is refused rather than mapping a pet to the word "you".
    ConfigUtil.PlayerName = "";
    WritePetMapFile(["Squirrel=You"], "Other Server");
    Assert.AreEqual(0, RosterImport.ImportPetMapOnce("Other Server").Applied);
  }

  [TestMethod]
  public void UnassignedIsCarriedBecauseTheGridDisplaysIt()
  {
    /*
     * The unassigned text (Labels.Unassigned — "Unknown Pet Owner") is a person word that IsPersonWord refuses on the
     * ROSTER side, and it must survive here: it is
     * what petmapping.txt writes for a pet this app has seen but never mapped, the Pet Owners window lists it, and
     * PlayerRegistry's own file loop keeps loading those rows. Dropping them would make the migration shrink somebody's
     * map — one row of junk costs nothing, one curated row lost is memory that disagrees with its own file.
     */
    WritePetMapFile([$"Bub={Labels.Unassigned}"]);

    Assert.AreEqual(1, RosterImport.ImportPetMapOnce(Server).Applied);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Bub", out var owner));
    Assert.AreEqual(Labels.Unassigned, owner);
  }

  [TestMethod]
  public void AnEmptyOrMissingPetMapCostsNothingAndSaysNothing()
  {
    Assert.IsFalse(RosterImport.ImportPetMapOnce(Server).DidWork, "no file, nothing to carry");
    Assert.IsFalse(IdentityPriorStore.Instance.HasOwnerRows);

    WritePetMapFile([]);
    Assert.IsFalse(RosterImport.ImportPetMapOnce(Server).DidWork);
    Assert.AreEqual(0, IdentityPriorStore.Instance.Count);
  }

  private string LedgerPath => Path.Combine(_tempDir, Server, "identity-priors.txt");

  private string PlayersFilePath => Path.Combine(_tempDir, Server, "players.txt");

  private void WritePlayersFile(string[] lines, string server = Server)
  {
    var dir = Path.Combine(_tempDir, server);
    Directory.CreateDirectory(dir);
    File.WriteAllLines(Path.Combine(dir, "players.txt"), lines);
  }

  private List<string> ReadPlayersFile() =>
    File.Exists(PlayersFilePath) ? [.. File.ReadAllLines(PlayersFilePath)] : [];
  private string PetMapPath(string server) => Path.Combine(_tempDir, server, "petmapping.txt");

  private void WritePetMapFile(string[] lines, string server = Server)
  {
    Directory.CreateDirectory(Path.Combine(_tempDir, server));
    File.WriteAllLines(PetMapPath(server), lines);
  }

  private List<string> ReadPetMapFile() =>
    File.Exists(PetMapPath(Server)) ? [.. File.ReadAllLines(PetMapPath(Server))] : [];


}

namespace EQLogParser;

/*
 * WHAT THIS CLASS PINS SINCE 2026-10-09: identity's durable memory is ONE file — identity-priors.txt — and players.txt is a
 * frozen feed into it.
 *
 * The old version of this class tested a roster file the application rewrote every 30 seconds. That writer is deleted, which is
 * the whole subject now: the curated file is read at most once per folder (by RosterImport, before the registry loads) and never
 * edited again, while membership lives in the ledger's roster lane — the same statement (name, the dotnet-epoch second this app
 * last saw it) plus the class players.txt had nowhere to put. The operator's reasoning for a freeze rather than a live input:
 * "there's no way to change it anymore, so if there's a problem with what it's doing players would be stuck with it".
 *
 * The laws, each of which used to live on the file and now lives on the ledger:
 *
 *   - A hand-typed name survives. It used not to: the old Save() kept only timestamped rows, and Init() loads plain names at
 *     time 0, so the next save rewrote the curated list without them (and deleted their petmap rows on the way past).
 *   - Nothing rewrites players.txt — asserted as bytes, in both directions: a folder that HAS the file keeps it verbatim through
 *     a whole session of learning and removing, and a folder that does NOT have it never grows one.
 *   - A removal is an eviction, not a veto. The name leaves the roster lane and stays out across a reload, and a later capture's
 *     evidence is welcome to teach it again. (The deleted `!Name` tombstone refused exactly that; no shipped build could write one.)
 *   - "This one is ours" is membership; "that one is the enemy" is the engine's manual override (R10). The roster only ever says
 *     the first thing, and IdentityPriorStore.Record can neither set nor clear the bit.
 *   - A load is not a sighting: seeding must not move a stamp forward, which is how petmapping.txt once aged 96.6 % of its rows
 *     out in a single startup.
 *   - StaleDays is ONE dial for every memory this program keeps — the roster lane ages against the wall clock exactly as the
 *     pet file always did.
 *
 * ConfigUtil.ConfigDir/ServerName/PlayerName are process globals, and both PlayerRegistry and IdentityPriorStore are
 * process-lifetime singletons, so each test parks them in its own temp folder and leaves both stores empty on the way out (the
 * assembly does not parallelize — AGENTS).
 */
[TestClass]
public class PlayerRegistryPersistenceTest
{
  private const string Server = "Perstest";

  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";
  private Func<string, bool> _savedClassNameValidator = _ => false;
  private string _tempDir = "";

  [TestInitialize]
  public void ParkTheGlobalsInATempFolder()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "players-file-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);

    // Trailing separator like production's `%AppData%\EQLogParser\config\`, so the tests exercise the same shape.
    ConfigUtil.ConfigDir = _tempDir + Path.DirectorySeparatorChar;
    ConfigUtil.ServerName = Server;
    ConfigUtil.PlayerName = "Melodyn";

    // Class names are only stored when the host validator recognizes them (CombatRecordLookup.IsValidClassName
    // is injected by App.xaml.cs and defaults to "nothing is a class" headless). What these tests care about is
    // that a class SURVIVES the store, not what the data store accepts, so the hook is wired to two words here -
    // and put back on the way out, because it is process state.
    _savedClassNameValidator = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => name is "Wizard" or "Cleric";

    // Load from the (empty) temp folder: empty ledger, empty registry, no leftovers from another test.
    IdentityPriorStore.Instance.Init(Server);
    PlayerRegistry.Instance.Init();
  }

  [TestCleanup]
  public void EmptyTheStoresAndRestore()
  {
    // Empty ServerName first: Clear(true) saves before wiping, and the parked server is someone else's config.
    ConfigUtil.ServerName = "";
    PlayerRegistry.Instance.Clear();

    IdentityPriorStore.Instance.Init("persistence-cleanup-" + Guid.NewGuid().ToString("N"));

    CombatRecordLookup.IsValidClassName = _savedClassNameValidator;
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;

    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  /*
   * The open sequence MainWindow runs, because the import's gate ("has this folder's ledger already carried roster rows") is only
   * honest when the stores are loaded in production order: overrides -> ledger -> import -> registry.
   */
  private void OpenLogFolder()
  {
    IdentityPriorStore.Instance.Init(Server);
    RosterImport.ImportPlayersFileOnce(Server);
    PlayerRegistry.Instance.Init();
  }

  [TestMethod]
  public void AHandTypedListSurvivesTheImportAndTheLedger()
  {
    var recent = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-3)));
    WritePlayersFile(["Betebeatz", $"Strangle={recent},Wizard"]);
    OpenLogFolder();

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "the hand-typed name did not load");
    Assert.AreEqual("Wizard", PlayerRegistry.Instance.GetDefaultPlayerClass("Strangle"));

    // One newly learned name is all it took to trigger the old rewrite of the curated file.
    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Betebeatz"), "the curated row did not reach the ledger");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Strangle"));
    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("Strangle", out var strangle) && strangle.Class == "Wizard",
      "the class players.txt had nowhere to put was dropped on the way into the ledger");

    // Reload from what the LEDGER holds now — the import is one-time, so this proves the round trip, not the feed.
    OpenLogFolder();
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "the curated row did not come back after a reload");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Newbie"), "a name learned this session did not survive the reload");
  }

  /*
   * The freeze, asserted as bytes rather than as a code review. Both directions matter: the file a folder HAS must not change by
   * one line through a session that learns and removes names, and a folder that never had one must not grow one (that second half
   * is the bug report this commit answers: "i deleted all the config files and somehow it created and added to players.txt").
   */
  [TestMethod]
  public void NothingThisProgramDoesRewritesPlayersTxt()
  {
    var stamped = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-2)));
    WritePlayersFile(["# hand curated, do not touch", $"Betebeatz={stamped},Wizard", "Strangle", "!Goruuk"]);
    var before = File.ReadAllLines(PlayersFilePath);

    OpenLogFolder();
    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.AddVerifiedPet("Fluffy");
    PlayerRegistry.Instance.AddPetToPlayer("Fluffy", "Newbie");
    PlayerRegistry.Instance.RemoveVerifiedPlayer("Betebeatz");
    PlayerRegistry.Instance.Save();
    OpenLogFolder();
    PlayerRegistry.Instance.Save();

    var after = File.ReadAllLines(PlayersFilePath);
    CollectionAssert.AreEqual(before, after,
      "players.txt was edited; the file is a frozen feed now — read once per folder, never written");

    // ...and the same session's LEARNING is intact, somewhere. If this fails while the assert above passes, memory went nowhere.
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Newbie"), "the freeze also froze the writing of membership");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "the removal did not take");

    var freshFolder = Path.Combine(_tempDir, "NeverHadOne");
    Directory.CreateDirectory(freshFolder);
    ConfigUtil.ServerName = "NeverHadOne";
    IdentityPriorStore.Instance.Init("NeverHadOne");
    RosterImport.ImportPlayersFileOnce("NeverHadOne");
    PlayerRegistry.Instance.Init();
    PlayerRegistry.Instance.AddVerifiedPlayer("Somename", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(File.Exists(Path.Combine(freshFolder, "players.txt")),
      "a session invented a players.txt in a folder that never had one");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Somename"),
      "and the sighting did not land in the ledger either — the roster has no store now");

    ConfigUtil.ServerName = Server;
  }

  [TestMethod]
  public void ARemovedNameLeavesTheLedgerAndCanBeLearnedAgain()
  {
    /*
     * Both halves of "a removal is an eviction": the name leaves the roster lane and stays out across a reload, and evidence
     * afterwards is welcome to put it back. The second half is what the deleted `!Name` tombstone used to refuse.
     */
    PlayerRegistry.Instance.AddVerifiedPlayer("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Goruuk"), "control: the sighting never reached the ledger");

    PlayerRegistry.Instance.RemoveVerifiedPlayer("Goruuk");
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Goruuk"), "the removal left the membership bit standing");

    OpenLogFolder();
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"), "a removed name survived the reload");

    PlayerRegistry.Instance.AddVerifiedPlayer("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"),
      "a name the operator removed could not be re-learned from evidence — an eviction became a veto");
  }

  [TestMethod]
  public void RemovingAPlayerKeepsItsPetMapping()
  {
    PlayerRegistry.Instance.AddVerifiedPlayer("Ziggy", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.AddPetToPlayer("Fluffy", "Ziggy");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Ziggy"), "control: the owner is in the player list");

    PlayerRegistry.Instance.RemoveVerifiedPlayer("Ziggy");

    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Fluffy" && m.Owner == "Ziggy"),
      "\"Ziggy is not one of ours\" threw away \"Fluffy belongs to Ziggy\" - two different statements, and the " +
      "second one is hand-edited data in petmapping.txt");

    // ...and an eviction says nothing about the PAIR in either store: ForgetRoster clears the bit and its class only.
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Ziggy"), "control: the membership bit came down with the removal");
    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Fluffy"),
      "the pair left memory too, so a later board loses an owner it still needs (petmapping.txt keeps it for the next log)");
  }

  [TestMethod]
  public void OnlyEvidenceDatedRosterRowsRetire()
  {
    var old = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-300)));
    var fresh = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-5)));
    WritePlayersFile([$"AncientOne={old}", $"Recentone={fresh}", "Curatedone"]);
    OpenLogFolder();

    // The aged row owns a pet: the old Save() reached in and deleted mapping rows like this one.
    PlayerRegistry.Instance.AddPetToPlayer("Ancientpet", "AncientOne");
    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("AncientOne"),
      "a 300-day-old roster row was expected to retire on StaleDays");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Recentone"), "a current row was retired");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetRoster("Curatedone"),
      "an undated (hand-typed) row was retired - it is a statement, not an observation, and has no age to expire");
    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ancientpet"),
      "retiring a roster row still deletes the petmapping.txt row underneath it");
  }

  [TestMethod]
  public void TheOperatorsOwnCharacterIsAlwaysAPlayer()
  {
    // Whatever else the feed says, the person playing now is a player: You-mapping across the app depends on it, and Init()
    // claims that one name without being asked (ConfigUtil.PlayerName is set in the setup above).
    WritePlayersFile(["Betebeatz"]);
    OpenLogFolder();

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Melodyn"), "the local player was not in the list");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "an ordinary row did not load");
  }

  /*
   * A load is not a sighting, and this is the failure that cost a whole file: AddPetToPlayer once ended with AddVerifiedPet, so
   * every startup re-stamped 96.6 % of petmapping.txt and nothing could ever expire. The roster lane's stamp is the same kind of
   * number on the same clock, so seeding must leave it exactly as the ledger found it — including for the operator's own
   * character, which Init claims with TODAY's time on every single open.
   */
  [TestMethod]
  public void SeedingTheRegistryNeverRestampsTheRoster()
  {
    var ancient = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-150)));
    WritePlayersFile([$"AncientOne={ancient}"]);
    OpenLogFolder();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("AncientOne", out var before), "control: the row did not import");
    Assert.AreEqual((long)ancient, before.SeenAtS, "the import stamped a curated row instead of carrying its date");

    OpenLogFolder();   // every re-open re-seeds through AddVerifiedPlayer(init: true)
    PlayerRegistry.Instance.Save();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGet("AncientOne", out var after), "control: the row vanished on reload");
    Assert.AreEqual(before.SeenAtS, after.SeenAtS,
      "a re-open aged its own roster — the stamp moved forward without anything being sighted");

    // The local player is claimed at load with TODAY's clock. That is a load too, so it stays out of the durable roster lane.
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Melodyn"), "control: the local player is not verified");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Melodyn"),
      "init:true wrote the local player into the ledger — a startup sighting that ages nothing but pollutes everything");
  }

  /*
   * One process, two servers. IdentityPriorStore is a singleton holding whichever folder was opened last, and Save() files rows
   * under the name IT holds; writing while it still answers for another server is how one folder quietly acquires another's raid.
   */
  [TestMethod]
  public void AForeignLedgerIsNotWrittenFromHere()
  {
    IdentityPriorStore.Instance.Init("Someotherserver");

    PlayerRegistry.Instance.AddVerifiedPlayer("Somename", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Somename"),
      "tonight's sighting landed in the ledger of the server opened before this one");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Somename"),
      "the guard must skip the durable write only — memory still answers \"is this one of ours?\"");
  }

  // ---- the pet side's calendar ---------------------------------------------------------------------------------
  /*
   * petmapping.txt carries an optional sighting stamp after the owner — `Fluffy=Ziggy|4021234560` — and StaleDays is the one dial
   * both memories age on (the roster lane inside the ledger, the pairs here). Three things had to be true at once: the dead weight
   * leaves (a four-year-old file of every summon in the game was the complaint), the Owner text the Pet Owners grid shows stays
   * clean, and a row that predates the stamp format is never deleted for something it cannot have.
   */

  [TestMethod]
  public void APetUnseenForStaleDaysLeavesTheFile()
  {
    WritePetMappingFile([$"Ghrahb=Ziggy|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}"]);
    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ghrahb" && m.Owner == "Ziggy"),
      "control: the row did not load (it is dropped at SAVE, not at load, like a stale roster row)");

    // A save only writes the mapping file when something changed, so change something.
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Ziggy");
    PlayerRegistry.Instance.Save();

    var saved = ReadPetMappingFile();
    Assert.IsFalse(saved.Any(l => l.Contains("Ghrahb", StringComparison.Ordinal)),
      $"a pet not sighted for {PlayerRegistry.StaleDays} days stayed in the file; file = [{string.Join(" | ", saved)}]");
    Assert.IsTrue(saved.Any(l => l.StartsWith("Bubbles=", StringComparison.Ordinal)),
      $"the save that dropped the stale row skipped the new one too; file = [{string.Join(" | ", saved)}]");
  }

  [TestMethod]
  public void APetRowCarryingNoStampIsAStatementAndStays()
  {
    /*
     * Two kinds of row have no stamp: one an operator typed into the file, and one written by a build that predates the
     * format. Neither can be judged for an age it never recorded — this is exactly the law that stopped Save() from
     * deleting curated player rows, applied to the pet file.
     */
    WritePetMappingFile(["Fluffy=Ziggy"]);
    PlayerRegistry.Instance.Init();

    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Ziggy");
    PlayerRegistry.Instance.Save();

    var saved = ReadPetMappingFile();
    Assert.IsTrue(saved.Any(l => l.StartsWith("Fluffy=Ziggy", StringComparison.Ordinal)),
      $"an un-stamped row was retired; file = [{string.Join(" | ", saved)}]");
    Assert.IsFalse(saved.Any(l => l.Contains('|') && l.StartsWith("Fluffy", StringComparison.Ordinal)),
      $"a stamp was invented for a row nobody observed; file = [{string.Join(" | ", saved)}]");
  }

  [TestMethod]
  public void ASightingRefreshesTheStampAndTheOwnerTextStaysClean()
  {
    // A row whose stored stamp is stale, sighted again today: it must survive, and reappear stamped.
    WritePetMappingFile([$"Ghrahb=Ziggy|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}"]);
    PlayerRegistry.Instance.Init();

    // What a capture does with a possessive line: the pair is already known, and the pet is sighted anyway.
    PlayerRegistry.Instance.AddVerifiedPet("Ghrahb");
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Ziggy");
    PlayerRegistry.Instance.Save();

    var saved = ReadPetMappingFile();
    var ghrahb = saved.FirstOrDefault(l => l.StartsWith("Ghrahb=", StringComparison.Ordinal));
    Assert.IsNotNull(ghrahb, $"a pet sighted today still aged out; file = [{string.Join(" | ", saved)}]");

    var owner = ghrahb[(ghrahb.IndexOf('=') + 1)..];
    Assert.IsTrue(owner.Contains('|'), "the surviving row lost its stamp, so it can never age");
    Assert.AreEqual("Ziggy", owner[..owner.IndexOf('|')], "the stamp leaked into the owner text");

    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ghrahb" && m.Owner == "Ziggy"),
      "the in-memory mapping carries a stamp the UI would print");
  }

  [TestMethod]
  public void OneDialAgesBothMemories()
  {
    /*
     * StaleDays is one number for everything this class remembers, and both halves have to answer to it in the same save: a
     * player forgotten while their pet is still remembered is not a rule anybody chose. The player half now means the LEDGER's
     * roster lane (the file it used to live in is frozen), so this test is also the proof that the freeze did not quietly drop the
     * expiry along with the writer.
     */
    var tooOld = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-(PlayerRegistry.StaleDays + 5))));
    WritePlayersFile([$"Goruuk={tooOld}"]);
    WritePetMappingFile([$"Ghrahb=Goruuk|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}"]);
    OpenLogFolder();

    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Newbie");
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Goruuk"),
      "the roster row survived the dial (the ledger is where that row lives now)");
    Assert.IsFalse(ReadPetMappingFile().Any(l => l.Contains("Ghrahb", StringComparison.Ordinal)),
      "the pet row survived the same dial");
  }

  // ---- helpers -----------------------------------------------------------------------------------------------

  private static string StampDaysAgo(int days) => Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-days))).ToString();

  private string PetMappingFilePath => Path.Combine(_tempDir, Server, "petmapping.txt");

  private void WritePetMappingFile(string[] lines)
  {
    Directory.CreateDirectory(Path.Combine(_tempDir, Server));
    File.WriteAllLines(PetMappingFilePath, lines);
  }

  private List<string> ReadPetMappingFile() => File.Exists(PetMappingFilePath) ? [.. File.ReadAllLines(PetMappingFilePath)] : [];

  private string PlayersFilePath => Path.Combine(_tempDir, Server, "players.txt");

  private void WritePlayersFile(string[] lines)
  {
    Directory.CreateDirectory(Path.Combine(_tempDir, Server));
    File.WriteAllLines(PlayersFilePath, lines);
  }
}

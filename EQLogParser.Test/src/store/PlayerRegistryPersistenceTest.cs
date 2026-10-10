namespace EQLogParser;

/*
 * WHAT THIS CLASS PINS SINCE 2026-10-09: identity's durable memory is ONE file — identity-priors.txt — and players.txt is a
 * frozen feed into it.
 *
 * The old version of this class tested a roster file the application rewrote every 30 seconds. That writer is deleted, which is
 * the whole subject now: the curated file is read at most once per folder (by LegacyPlayerImport, before the registry loads) and never
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
    LegacyPlayerImport.ImportPlayersFileOnce(Server);
    LegacyPlayerImport.ImportPetMapOnce(Server);   // MainWindow's real open order: both frozen feeds land in the ledger, then the registry seeds
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
    LegacyPlayerImport.ImportPlayersFileOnce("NeverHadOne");
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

  /*
   * Since 2026-10-09 the pet side of that freeze is the roster side: petmapping.txt is read once per folder into the ledger's
   * OWNERSHIP lane and never written again, so what used to be asserted about file lines is now asserted about lane rows. The three
   * laws survived the move intact — dead weight leaves, a row nobody observed is never judged for an age it cannot have, and a
   * sighting restarts a clock — and two new ones arrived with the freeze: the file's bytes never change, and a folder that never had
   * one does not grow one.
   */

  [TestMethod]
  public void NothingThisProgramDoesRewritesPetMappingTxt()
  {
    /*
     * The freeze as bytes, both directions — the same pair of asserts players.txt has, because the same bug shape applies: a store
     * this program rewrites is a store an operator cannot correct. Everything a session concludes goes to identity-priors.txt.
     */
    var stamped = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-2)));
    WritePetMappingFile(["# hand curated, do not touch", $"Fluffy={stamped}", "Squirticus=" + Labels.Unassigned]);
    var before = File.ReadAllLines(PetMappingFilePath);

    OpenLogFolder();
    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.AddPetToPlayer("Fluffy", "Ziggy");      // re-assigning an existing pair
    PlayerRegistry.Instance.AddPetToPlayer("Bobo", "Newbie");        // learning a new one
    PlayerRegistry.Instance.ForgetName("Squirticus");                // taking one back
    PlayerRegistry.Instance.Save();
    OpenLogFolder();
    PlayerRegistry.Instance.AddPetToPlayer("Late", "Newbie");
    PlayerRegistry.Instance.Save();

    var after = File.ReadAllLines(PetMappingFilePath);
    CollectionAssert.AreEqual(before, after,
      "petmapping.txt was edited; the file is a frozen feed now — read once per folder, never written");

    // ...and the learning landed somewhere. If this fails while the assert above passes, memory went nowhere.
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var fluffy) && fluffy == "Ziggy",
      "a re-assigned pair did not reach the ledger's ownership lane");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Late", out var late) && late == "Newbie",
      "a pair learned after the reload did not reach the ledger either");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Squirticus"),
      "the taken-back mapping came back; a removal is an eviction, not a suggestion");

    var freshFolder = Path.Combine(_tempDir, "NeverHadPets");
    Directory.CreateDirectory(freshFolder);
    ConfigUtil.ServerName = "NeverHadPets";
    IdentityPriorStore.Instance.Init("NeverHadPets");
    LegacyPlayerImport.ImportPetMapOnce("NeverHadPets");
    PlayerRegistry.Instance.Init();
    PlayerRegistry.Instance.AddPetToPlayer("Somename", "Newbie");
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(File.Exists(Path.Combine(freshFolder, "petmapping.txt")),
      "a session invented a petmapping.txt in a folder that never had one");

    ConfigUtil.ServerName = Server;
  }

  [TestMethod]
  public void APetUnseenForStaleDaysHasNoMappingWhileRecentOnesStay()
  {
    /*
     * Dead weight still leaves, and the lane is where it lives now. One difference from the frozen-file era is worth naming: the
     * ledger prunes what it flushes, so a mapping already past the dial dies with the import that carried it rather than waiting for
     * some later write — stricter by one flush, and nobody has to open a log twice to find out their map is stale.
     */
    WritePetMappingFile([$"Ghrahb=Ziggy|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}", $"Fluffy=Ziggy|{StampDaysAgo(1)}"]);
    OpenLogFolder();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Ghrahb"),
      $"a pet not sighted for {PlayerRegistry.StaleDays} days kept its mapping");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out _),
      "the mapping that aged out took a recent one with it");
    Assert.IsFalse(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ghrahb"),
      "the expired row still reached this session's live map");
    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Fluffy"),
      "the recent row did not reach this session's live map");

    // And it stays out: the import's once-per-folder gate holds while the ledger carries mappings at all, so ageing is not quietly
    // undone by the frozen file beside it. (Delete identity-priors.txt and the operator's file comes back — that is the rollback.)
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Ziggy");
    PlayerRegistry.Instance.Save();
    OpenLogFolder();

    Assert.IsFalse(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ghrahb"),
      "the aged-out mapping came back from the frozen file on the next open");
  }

  [TestMethod]
  public void ASightingRefreshesTheStampAndTheOwnerTextStaysClean()
  {
    /*
     * A mapping whose stamp is old-but-alive (StaleDays - 30: inside the dial, so it loads), sighted again today. It must survive and
     * reappear stamped, because a row that never moves its clock is a row that leaves on the next flush no matter how often the pet
     * walks into the log. A row ALREADY past the dial is a different case and belongs to the test above.
     */
    var oldStamp = StampDaysAsLong(PlayerRegistry.StaleDays - 30);
    WritePetMappingFile([$"Ghrahb=Ziggy|{oldStamp}"]);
    OpenLogFolder();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Ghrahb", out var loaded) && loaded == "Ziggy",
      "control: a mapping inside the dial did not load");

    // What a capture does with a possessive line: the pair is already known, and the pet is sighted anyway.
    PlayerRegistry.Instance.AddVerifiedPet("Ghrahb");
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Ziggy");
    PlayerRegistry.Instance.Save();

    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Ghrahb", out var sighted),
      "a pet sighted today still aged out");

    // The stamp is a column, never part of the value: an owner that printed "Ziggy|6389..." would reach +Pets folding and every grid.
    Assert.AreEqual("Ziggy", sighted, "the stamp leaked into the owner text");

    var seenAtS = IdentityPriorStore.Instance.PetEntries().Where(e => e.Key == "Ghrahb").Select(e => e.Value.SeenAtS).Single();
    Assert.IsTrue(seenAtS > oldStamp,
      "the sighting did not move the row's clock forward, so the next prune takes it anyway");
    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ghrahb" && m.Owner == "Ziggy"),
      "the in-memory mapping carries something the UI would print");
  }

  [TestMethod]
  public void OneDialAgesBothMemories()
  {
    /*
     * StaleDays is one number for every memory this application keeps, and both lanes have to answer to it in the same flush: a
     * player forgotten while their pet is still remembered is not a rule anybody chose. Both halves live in identity-priors.txt now,
     * which makes this the proof that freezing the two files did not quietly drop the expiry along with the writers.
     */
    var tooOld = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-(PlayerRegistry.StaleDays + 5))));
    WritePlayersFile([$"Goruuk={tooOld}"]);
    WritePetMappingFile([$"Ghrahb=Goruuk|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}"]);
    OpenLogFolder();

    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Newbie");
    PlayerRegistry.Instance.Save();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Goruuk"),
      "the roster row survived the dial");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Ghrahb"),
      "the pet mapping survived the same dial");

    // Neither file paid for it: the bytes are what the operator left.
    Assert.AreEqual($"Goruuk={tooOld}", File.ReadAllLines(PlayersFilePath).Single(),
      "aging a roster name edited its frozen feed");
    Assert.AreEqual($"Ghrahb=Goruuk|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}", File.ReadAllLines(PetMappingFilePath).Single(),
      "aging a pet mapping edited its frozen feed");
  }

  // ---- helpers -----------------------------------------------------------------------------------------------

  private static string StampDaysAgo(int days) => Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-days))).ToString();

  private static long StampDaysAsLong(int days) => (long)Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-days)));

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

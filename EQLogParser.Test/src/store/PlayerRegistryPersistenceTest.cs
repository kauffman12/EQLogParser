namespace EQLogParser;

/*
 * players.txt is the one part of identity that belongs to the operator, so the rules these tests pin are about
 * the file keeping what it was told:
 *
 *   - A hand-typed name survives. It used not to: Save() kept only rows carrying a timestamp, and Init() loads
 *     plain names at time 0, so the next save - which fires as soon as the parser learns any new name - rewrote
 *     the file without them. The same branch also deleted their petmapping.txt rows on the way past, and Init()
 *     seeds every mapping OWNER into the player list at time 0, so a curated list lost both its names and its
 *     ownership data as a side effect of playing.
 *   - A removal is an eviction, not a veto. `RemoveVerifiedPlayer` takes the row out of the file and says nothing
 *     else, so a later capture's evidence is free to teach that name again. This class used to pin a `!Name`
 *     tombstone that refused every learning path permanently — machinery no shipped build could ever switch on:
 *     it arrived seven hours before the only window that could write one lost its menu entry, three days after the
 *     last release (`2.4.1`). Nothing to preserve, so nothing here argues for it.
 *   - Claiming a name is an assertion and lives elsewhere. "This one is ours" is a roster row; "that one is the
 *     enemy" is the engine's manual override (R10). players.txt only ever says the first thing.
 *
 * ConfigUtil.ConfigDir/ServerName/PlayerName are process globals and PlayerRegistry is a process-lifetime
 * singleton, so each test parks them in a temp folder (same pattern as IdentityOverrideStoreTest) and leaves the
 * registry empty on the way out - the assembly does not parallelize (AGENTS).
 */
[TestClass]
public class PlayerRegistryPersistenceTest
{
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
    ConfigUtil.ServerName = "Perstest";
    ConfigUtil.PlayerName = "Melodyn";

    // Class names are only stored when the host validator recognizes them (CombatRecordLookup.IsValidClassName
    // is injected by App.xaml.cs and defaults to "nothing is a class" headless). What these tests care about is
    // that a class SURVIVES the file, not what the data store accepts, so the hook is wired to two words here -
    // and put back on the way out, because it is process state.
    _savedClassNameValidator = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => name is "Wizard" or "Cleric";

    // Load from the (empty) temp file: an empty registry, no leftovers from another test.
    PlayerRegistry.Instance.Init();
  }

  [TestCleanup]
  public void EmptyTheRegistryAndRestore()
  {
    // Empty ServerName first: Clear(true) saves before wiping, and the parked server is someone else's config.
    ConfigUtil.ServerName = "";
    PlayerRegistry.Instance.Clear();

    CombatRecordLookup.IsValidClassName = _savedClassNameValidator;
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;

    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  [TestMethod]
  public void AHandTypedListSurvivesSaving()
  {
    var recent = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-3)));
    WritePlayersFile(["Betebeatz", $"Strangle={recent},Wizard"]);
    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "the hand-typed name did not load");
    Assert.AreEqual("Wizard", PlayerRegistry.Instance.GetDefaultPlayerClass("Strangle"));

    // One newly learned name is all it took to trigger the old rewrite.
    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();

    var saved = ReadPlayersFile();
    Assert.IsTrue(saved.Contains("Betebeatz"),
      $"the curated row vanished on save; file = [{string.Join(" | ", saved)}]");
    Assert.IsTrue(saved.Any(line => line.StartsWith("Strangle=", StringComparison.Ordinal) && line.EndsWith(",Wizard", StringComparison.Ordinal)),
      $"the curated row lost its class; file = [{string.Join(" | ", saved)}]");

    PlayerRegistry.Instance.Init();
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "the curated row did not come back after a reload");
  }

  [TestMethod]
  public void ARemovedNameLeavesTheFileAndCanBeLearnedAgain()
  {
    /*
     * Both halves of "a removal is an eviction": the row goes out of players.txt and stays out across a reload, and
     * evidence afterwards is welcome to put the name back. The second half is what the deleted `!Name` tombstone used
     * to refuse — see this class's header for why that veto is not worth carrying.
     */
    PlayerRegistry.Instance.AddVerifiedPlayer("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"), "control: the name was never in the list");

    PlayerRegistry.Instance.RemoveVerifiedPlayer("Goruuk");
    PlayerRegistry.Instance.Save();

    var saved = ReadPlayersFile();
    Assert.IsFalse(saved.Any(l => l.Contains("Goruuk", StringComparison.Ordinal)),
      $"the removal left a row behind; file = [{string.Join(" | ", saved)}]");

    PlayerRegistry.Instance.Init();
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"), "a removed name survived the reload");

    PlayerRegistry.Instance.AddVerifiedPlayer("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"),
      "a name the operator removed could not be re-learned from evidence");
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
  }

  [TestMethod]
  public void OnlyEvidenceDatedRowsRetire()
  {
    var old = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-300)));
    var fresh = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-5)));
    WritePlayersFile([$"AncientOne={old}", $"Recentone={fresh}", "Curatedone"]);
    PlayerRegistry.Instance.Init();

    // The aged row owns a pet: the old Save() reached in and deleted mapping rows like this one.
    PlayerRegistry.Instance.AddPetToPlayer("Ancientpet", "AncientOne");
    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();

    var saved = ReadPlayersFile();
    Assert.IsFalse(saved.Any(l => l.StartsWith("AncientOne", StringComparison.Ordinal)),
      $"a 300-day-old evidence row was expected to retire; file = [{string.Join(" | ", saved)}]");
    Assert.IsTrue(saved.Any(l => l.StartsWith("Recentone", StringComparison.Ordinal)), "a current row was retired");
    Assert.IsTrue(saved.Contains("Curatedone"), "an undated (hand-typed) row was retired - it has no age to expire");
    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ancientpet"),
      "retiring a player row still deletes the petmapping.txt row underneath it");
  }

  [TestMethod]
  public void TheOperatorsOwnCharacterIsAlwaysAPlayer()
  {
    // Whatever else the file says, the person playing now is a player: You-mapping across the app depends on it,
    // and Init() claims that one name without being asked (ConfigUtil.PlayerName is set in the setup above).
    WritePlayersFile(["Betebeatz"]);
    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Melodyn"), "the local player was not in the list");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Betebeatz"), "an ordinary row did not load");
  }

  // ---- the pet side's calendar ---------------------------------------------------------------------------------
  /*
   * petmapping.txt carries an optional sighting stamp after the owner — `Fluffy=Ziggy|4021234560` — and StaleDays is
   * the one dial both files age on. Three things had to be true at once: the dead weight leaves (a four-year-old file
   * of every summon in the game was the complaint), the Owner text the Pet Owners grid shows stays clean, and a row
   * that predates the stamp format is never deleted for something it cannot have.
   */

  [TestMethod]
  public void APetUnseenForStaleDaysLeavesTheFile()
  {
    WritePetMappingFile([$"Ghrahb=Ziggy|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}"]);
    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Ghrahb" && m.Owner == "Ziggy"),
      "control: the row did not load (it is dropped at SAVE, not at load, like a stale player row)");

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
  public void OneDialAgesBothFiles()
  {
    /*
     * StaleDays is one number for both files this class writes, and both halves have to answer to it in the same save:
     * a player forgotten while their pet is still remembered is not a rule anybody chose.
     */
    var tooOld = Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-(PlayerRegistry.StaleDays + 5))));
    WritePlayersFile([$"Goruuk={tooOld}"]);
    WritePetMappingFile([$"Ghrahb=Goruuk|{StampDaysAgo(PlayerRegistry.StaleDays + 5)}"]);
    PlayerRegistry.Instance.Init();

    PlayerRegistry.Instance.AddVerifiedPlayer("Newbie", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.AddPetToPlayer("Bubbles", "Newbie");
    PlayerRegistry.Instance.Save();

    var players = ReadPlayersFile();
    var pets = ReadPetMappingFile();
    Assert.IsFalse(players.Any(l => l.Contains("Goruuk", StringComparison.Ordinal)),
      $"the player row survived the dial; file = [{string.Join(" | ", players)}]");
    Assert.IsFalse(pets.Any(l => l.Contains("Ghrahb", StringComparison.Ordinal)),
      $"the pet row survived the same dial; file = [{string.Join(" | ", pets)}]");
  }

  // ---- helpers -----------------------------------------------------------------------------------------------

  private static string StampDaysAgo(int days) => Math.Round(DateUtil.ToDotNetSeconds(DateTime.Now.AddDays(-days))).ToString();

  private string PetMappingFilePath => Path.Combine(_tempDir, "Perstest", "petmapping.txt");

  private void WritePetMappingFile(string[] lines)
  {
    Directory.CreateDirectory(Path.Combine(_tempDir, "Perstest"));
    File.WriteAllLines(PetMappingFilePath, lines);
  }

  private List<string> ReadPetMappingFile() => File.Exists(PetMappingFilePath) ? [.. File.ReadAllLines(PetMappingFilePath)] : [];

  private string PlayersFilePath => Path.Combine(_tempDir, "Perstest", "players.txt");

  private void WritePlayersFile(string[] lines)
  {
    Directory.CreateDirectory(Path.Combine(_tempDir, "Perstest"));
    File.WriteAllLines(PlayersFilePath, lines);
  }

  private List<string> ReadPlayersFile() => File.Exists(PlayersFilePath) ? [.. File.ReadAllLines(PlayersFilePath)] : [];
}

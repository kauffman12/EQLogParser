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

  // ---- helpers -----------------------------------------------------------------------------------------------

  private string PlayersFilePath => Path.Combine(_tempDir, "Perstest", "players.txt");

  private void WritePlayersFile(string[] lines)
  {
    Directory.CreateDirectory(Path.Combine(_tempDir, "Perstest"));
    File.WriteAllLines(PlayersFilePath, lines);
  }

  private List<string> ReadPlayersFile() => File.Exists(PlayersFilePath) ? [.. File.ReadAllLines(PlayersFilePath)] : [];
}

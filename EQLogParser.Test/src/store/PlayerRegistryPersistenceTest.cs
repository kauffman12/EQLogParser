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
 *   - A removal means something. Deleting an entry used to last exactly one log: the first loot line of the next
 *     parse handed the name straight back. A removal is now a row in the same file (`!Name`) and the learning
 *     paths are refused, while "Set as Player" (AddVerifiedPlayerByOperator) can still revive it.
 *   - Saying "not one of ours" makes no claim. It silences the guess; it does not name the enemy. That second
 *     thing is an assertion and belongs on the mirror's manual override (R10), not here.
 *
 * ConfigUtil.ConfigDir/ServerName/PlayerName are process globals and PlayerRegistry is a process-lifetime
 * singleton, so each test parks them in a temp folder (same pattern as MirrorOverrideTest) and leaves the
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
  public void ARemovedNameIsNotLearnedBack()
  {
    PlayerRegistry.Instance.AddVerifiedPlayer("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));
    PlayerRegistry.Instance.Save();
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"), "control: the name was never in the list");

    PlayerRegistry.Instance.RemoveVerifiedPlayer("Goruuk");
    PlayerRegistry.Instance.Save();

    var saved = ReadPlayersFile();
    Assert.IsTrue(saved.Contains("!Goruuk"), $"the removal was not written as a rejection; file = [{string.Join(" | ", saved)}]");
    Assert.IsFalse(saved.Contains("Goruuk"), "the name is still in the file as a claim next to its rejection");

    // The whole point: the same evidence that put it there in the first place must not re-claim it.
    PlayerRegistry.Instance.Init();
    Assert.IsTrue(PlayerRegistry.Instance.IsRejectedPlayer("Goruuk"), "the rejection did not survive a reload");
    PlayerRegistry.Instance.AddVerifiedPlayer("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"),
      "a loot-line-shaped add re-claimed a name the operator had taken back");
  }

  [TestMethod]
  public void ARejectionWinsWhateverTheLineOrder()
  {
    WritePlayersFile(["Zyphon", "!Zyphon"]);
    PlayerRegistry.Instance.Init();
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Zyphon"), "the rejection lost to a claim written before it");

    WritePlayersFile(["!Bexala", "Bexala"]);
    PlayerRegistry.Instance.Init();
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Bexala"), "the rejection lost to a claim written after it");
  }

  [TestMethod]
  public void ARejectionIsCheckedWithoutCase()
  {
    // The parser capitalizes every name it hands out (TextUtils.CapitalizeFirst) and this file is typed by hand.
    WritePlayersFile(["!a bone walker"]);
    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.IsRejectedPlayer("A bone walker"), "the capital form of a rejected name is not rejected");
    PlayerRegistry.Instance.AddVerifiedPlayer("A bone walker", DateUtil.ToDotNetSeconds(DateTime.Now));
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("A bone walker"));
  }

  [TestMethod]
  public void AnOperatorAddRevivesARejectedName()
  {
    WritePlayersFile(["!Goruuk"]);
    PlayerRegistry.Instance.Init();

    PlayerRegistry.Instance.AddVerifiedPlayerByOperator("Goruuk", DateUtil.ToDotNetSeconds(DateTime.Now));

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Goruuk"), "the operator could not put the name back");
    Assert.IsFalse(PlayerRegistry.Instance.IsRejectedPlayer("Goruuk"), "the revival left the shadow in place");

    PlayerRegistry.Instance.Save();
    var saved = ReadPlayersFile();
    Assert.IsFalse(saved.Contains("!Goruuk"), $"the rejected row outlived the operator's decision; file = [{string.Join(" | ", saved)}]");
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
  public void TheOperatorsOwnCharacterIsNeverInShadow()
  {
    // Whatever else the file says, the person playing now is a player: You-mapping across the app depends on it.
    WritePlayersFile(["!Melodyn", "Betebeatz"]);
    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Melodyn"), "the local player was rejected");
    Assert.IsFalse(PlayerRegistry.Instance.GetRejectedPlayers().Contains("Melodyn"));
    Assert.IsFalse(PlayerRegistry.Instance.IsRejectedPlayer("Betebeatz"), "control: an untouched name became rejected");
  }

  [TestMethod]
  public void ARejectedPetMappingOwnerIsNotClaimedAsARaider()
  {
    // petmapping.txt used to smuggle rejected names back in as raid members through RegistrySeed's owner claim.
    WritePlayersFile(["!Xanathan"]);
    Directory.CreateDirectory(Path.Combine(_tempDir, "Perstest"));
    File.WriteAllLines(Path.Combine(_tempDir, "Perstest", "petmapping.txt"), ["Akini=Xanathan"]);

    PlayerRegistry.Instance.Init();

    Assert.IsTrue(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Akini" && m.Owner == "Xanathan"),
      "the ownership row should still be read - it is a different statement from who the owner is");
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Xanathan"),
      "a mapping that quotes a rejected name re-claimed it as a player");

    var timeline = new Mirror.EntityTimeline();
    Mirror.RegistrySeed.Apply(timeline, new Mirror.DamageFactTable(), double.NaN, double.NaN);
    Assert.AreEqual(Mirror.IdentityKind.Unknown, timeline.Identity("Xanathan"),
      "RegistrySeed claimed a rejected name from the pet mapping");
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

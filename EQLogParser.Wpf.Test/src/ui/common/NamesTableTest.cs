using EQLogParser;
using EQLogParser.Mirror;

namespace EQLogParser.Wpf.Test;

/*
 * The Names window's one piece of logic: turning a census row into what an operator reads. Nothing here needs WPF -
 * no element is constructed, so no Sta.Run - because the interesting part is which sentences the flag column composes,
 * not how SfDataGrid paints them.
 *
 * Why this matters more than a typical formatting test: each sentence tells a person whether to ACT. A verdict resting
 * on their own click needs no correction; one resting on an older log might; "roster says player, rules say NPC" is the
 * row where somebody's meter is wrong right now. A flag dropped by accident is a row that looks fine and isn't.
 */
[TestClass]
public class NamesTableTest
{
  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "names-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = "Names Window";
    ConfigUtil.PlayerName = "Namestestee";

    PlayerRegistry.Instance.Clear();
    MirrorOverrideStore.Instance.Init("names-window-test");
    IdentityPriorStore.Instance.Init("names-window-test");
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    MirrorOverrideStore.Instance.Init("names-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityPriorStore.Instance.Init("names-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  // Nothing open: no timeline, no fact tables. The census still lists what files claim, which is the state the window
  // is in when it is opened before a log.
  private static ClassificationReport CensusWithoutACapture(IdentityPriorStore? priors = null)
    => ClassificationReport.Build(null, null, null, MirrorOverrideStore.Instance, PlayerRegistry.Instance, priors);

  [TestMethod]
  public void AVerdictTheOperatorGaveSaysSo()
  {
    ClassificationCommands.SetVerdict(MirrorOverrideStore.Instance, PlayerRegistry.Instance, "Nicky", IdentityKind.Npc);

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Nicky")!);

    Assert.AreEqual("Npc", row.Kind);
    StringAssert.Contains(row.Flags, "your verdict");
    StringAssert.Contains(row.Flags, "not in this log", "a hand-written row should say it has no facts behind it");
  }

  [TestMethod]
  public void ARejectionIsNotReportedAsAVerdict()
  {
    ClassificationCommands.Reject(MirrorOverrideStore.Instance, PlayerRegistry.Instance, "Ghosty");

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Ghosty")!);

    StringAssert.Contains(row.Flags, "no claim");
    Assert.IsFalse(row.Flags.Contains("your verdict"),
                   "a name the operator refused reads like a name they classified - the two need different actions");
  }

  [TestMethod]
  public void ARowRestingOnOlderLogsSaysHowMany()
  {
    var ledger = IdentityPriorStore.Instance;
    var remembered = new EntityTimeline();
    remembered.SetIdentity("Rememb", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    ledger.Record(remembered, ["Rememb"], PlayerRegistry.Instance, 1_700_000_000);
    ledger.Record(remembered, ["Rememb"], PlayerRegistry.Instance, 1_800_000_000);

    var row = NamesTable.RowFrom(CensusWithoutACapture(ledger).Find("Rememb")!);

    Assert.AreEqual("Npc", row.Kind);
    StringAssert.Contains(row.Why, "Prior:R7-graph", "the reason column must not claim this capture produced it");
    StringAssert.Contains(row.Flags, "x2");
  }

  [TestMethod]
  public void TheOneFlagThatMeansSomebodysMeterIsWrong()
  {
    // Verified raider, overridden to NPC: exactly the state in which a player's damage leaves the board.
    PlayerRegistry.Instance.AddVerifiedPlayer("Berta", 1_700_000_000);
    ClassificationCommands.SetVerdict(MirrorOverrideStore.Instance, PlayerRegistry.Instance, "Berta", IdentityKind.Npc);

    var census = CensusWithoutACapture();
    var row = NamesTable.RowFrom(census.Find("Berta")!);

    Assert.IsTrue(census.Disagreements >= 1);
    StringAssert.Contains(row.Flags, "roster says player, rules say NPC");
  }

  [TestMethod]
  public void AnEmptyCensusIsAnEmptyListNotACrash()
  {
    var census = CensusWithoutACapture();
    Assert.AreEqual(0, census.TotalNames);
    Assert.AreEqual(0, census.UnresolvedInCapture);

    // Every command has to survive being asked with nothing selected and nothing open.
    ClassificationCommands.Reject(MirrorOverrideStore.Instance, PlayerRegistry.Instance, "Nobody");
    ClassificationCommands.ClearPrior(IdentityPriorStore.Instance, "Nobody");
    ClassificationCommands.ClearVerdict(MirrorOverrideStore.Instance, "Nobody");
  }
}

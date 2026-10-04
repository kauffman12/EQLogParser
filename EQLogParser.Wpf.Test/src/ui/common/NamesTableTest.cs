using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The Names window's one piece of logic: turning a census row into what an operator reads. Nothing here needs WPF -
 * no element is constructed, so no Sta.Run - because the interesting part is which sentences the WHY cell's tooltip
 * composes and what word each kind prints as, not how SfDataGrid paints them.
 *
 * The grid shows four columns (Name, Type, Why, Class). Two things the old layout carried are deliberately absent and
 * so are not asserted here: Damage/Healing (an identity list should not rank names by output) and Owner (the Pet Owners
 * window lists the same PlayerRegistry pairs).
 *
 * Why this matters more than a typical formatting test: each tooltip sentence tells a person whether to ACT. A verdict
 * resting on their own click needs no correction; one resting on an older log might; "roster says player, rules say NPC"
 * is the row where somebody's meter is wrong right now; and since Notes stopped being a column, the tooltip is the ONLY
 * place "your verdict" / "no claim (you took it back)" survive — a sentence dropped by accident is a row that looks
 * fine and isn't.
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
    IdentityOverrideStore.Instance.Init("names-window-test");
    IdentityPriorStore.Instance.Init("names-window-test");
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init("names-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityPriorStore.Instance.Init("names-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  // Nothing open: no timeline, no fact tables. The census still lists what files claim, which is the state the window
  // is in when it is opened before a log.
  private static ClassificationReport CensusWithoutACapture(IdentityPriorStore? priors = null)
    => ClassificationReport.Build(null, null, null, IdentityOverrideStore.Instance, PlayerRegistry.Instance, priors);

  [TestMethod]
  public void AVerdictTheOperatorGaveSaysSo()
  {
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, PlayerRegistry.Instance, "Nicky", IdentityKind.Npc);

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Nicky")!);

    Assert.AreEqual("NPC", row.Type);
    Assert.AreEqual("Manual", row.Why, "a verdict the operator wrote says so in the Why column itself");
    StringAssert.Contains(row.Provenance, "your verdict");
    StringAssert.Contains(row.Provenance, "not in this log", "a hand-written row should say it has no facts behind it");
  }

  [TestMethod]
  public void ARejectionIsNotReportedAsAVerdict()
  {
    ClassificationCommands.Reject(IdentityOverrideStore.Instance, PlayerRegistry.Instance, "Ghosty");

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Ghosty")!);

    StringAssert.Contains(row.Provenance, "no claim");
    Assert.IsFalse(row.Provenance.Contains("your verdict"),
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

    Assert.AreEqual("NPC", row.Type);
    StringAssert.Contains(row.Why, "Prior:R7-graph", "the reason column must not claim this capture produced it");
    StringAssert.Contains(row.Provenance, "x2");
    Assert.IsFalse(row.Provenance.Contains("cast that proved it"),
                   "a borrowed verdict has no cast behind it in THIS log; naming one would credit this capture with proving it");
  }

  [TestMethod]
  public void TheOneSentenceThatMeansSomebodysMeterIsWrong()
  {
    // Verified raider, overridden to NPC: exactly the state in which a player's damage leaves the board.
    PlayerRegistry.Instance.AddVerifiedPlayer("Berta", 1_700_000_000);
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, PlayerRegistry.Instance, "Berta", IdentityKind.Npc);

    var census = CensusWithoutACapture();
    var row = NamesTable.RowFrom(census.Find("Berta")!);

    Assert.IsTrue(census.Disagreements >= 1);
    StringAssert.Contains(row.Provenance, "roster says player, rules say NPC");
  }

  /*
   * The column header says Type, so the value has to read like a type: "Npc" is the enum's identifier, not something an
   * operator wrote or should have to decode. Unknown covers "no rule placed this name" and must not print as a blank.
   */
  [TestMethod]
  public void TheTypeColumnPrintsWordsNotEnumIdentifiers()
  {
    Assert.AreEqual("Player", NamesTable.TypeWord(IdentityKind.Player));
    Assert.AreEqual("Pet", NamesTable.TypeWord(IdentityKind.Pet));
    Assert.AreEqual("Merc", NamesTable.TypeWord(IdentityKind.Merc));
    Assert.AreEqual("NPC", NamesTable.TypeWord(IdentityKind.Npc));
    Assert.AreEqual("Unknown", NamesTable.TypeWord(IdentityKind.Unknown));
  }

  /*
   * The one thing worth adding to this window: when a verdict came from a CAST, the tooltip says which one (the census
   * puts it on Row.ReasonDetail; see CensusCastProofTest for how it is chosen). A column would have been empty for most
   * rows, so it is a hover - and rows that are not spell-based get no cast sentence at all.
   */
  [TestMethod]
  public void ASpellVerdictNamesTheCastInTheTooltip()
  {
    var bard = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "Chantoya",
      Kind = IdentityKind.Player,
      Reason = "R4-spell",
      ReasonDetail = "Boastful Bellow XLVII",
      HasFacts = true,
    });

    Assert.AreEqual("Player", bard.Type);
    Assert.AreEqual("R4-spell", bard.Why);
    StringAssert.Contains(bard.Provenance, "Boastful Bellow XLVII");

    var mob = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "A bone walker",
      Kind = IdentityKind.Npc,
      Reason = "R14-article",
      HasFacts = true,
    });

    Assert.AreEqual("NPC", mob.Type);
    Assert.IsFalse(mob.Provenance.Contains("cast that proved it"),
                   "only a spell-based verdict has a cast; inventing the sentence would put a lie in the tooltip");
  }

  [TestMethod]
  public void AnEmptyCensusIsAnEmptyListNotACrash()
  {
    var census = CensusWithoutACapture();
    Assert.AreEqual(0, census.TotalNames);
    Assert.AreEqual(0, census.UnresolvedInCapture);

    // Every command has to survive being asked with nothing selected and nothing open.
    ClassificationCommands.Reject(IdentityOverrideStore.Instance, PlayerRegistry.Instance, "Nobody");
    ClassificationCommands.ClearPrior(IdentityPriorStore.Instance, "Nobody");
    ClassificationCommands.ClearVerdict(IdentityOverrideStore.Instance, "Nobody");
  }
}

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The Names window's one piece of logic: turning a census row into what an operator reads. Nothing here needs WPF - no
 * element is constructed, so no Sta.Run - because the interesting part is which short line the WHY tooltip composes and
 * which rows get an edit icon, not how SfDataGrid paints them.
 *
 * The grid shows four columns (Name, Type, Why, Class). Two things an older layout carried are deliberately absent and so
 * are not asserted here: Damage/Healing (an identity list should not rank names by output) and Owner (the Pet Owners
 * window lists the same PlayerRegistry pairs).
 *
 * The WORDS themselves - "Chosen" for R10-manual, "Healed" for R15-healed, the dropdown's five entries - are asserted in
 * IdentityVocabularyTest (EQLogParser.Test), including the corpus check that no rule code reaches the screen. What is
 * pinned here is the part only this file owns: which LINES a row's tooltip gets, since each one tells a person whether to
 * ACT. A verdict resting on their own click needs no correction; one resting on an older log might; "players.txt says
 * player" is the row where somebody's meter is wrong right now; and "Claim taken back" must not read like "You chose" -
 * the tooltip is the only place any of those four survive, so a line dropped by accident is a row that looks fine and
 * isn't.
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
    Assert.AreEqual("Chosen", row.Why, "a verdict the operator wrote says so in the Why column itself, in two words");
    StringAssert.Contains(row.Provenance, "You chose NPC");
    StringAssert.Contains(row.Provenance, "Not in this log", "a hand-written row should say it has no facts behind it");

    // The dropdown preselects from Kind and refuses to write what the row already says; without this, a click that
    // changes nothing would spend a derive pass and rewrite mirror-overrides.txt.
    Assert.AreEqual(IdentityKind.Npc, row.Kind);
  }

  [TestMethod]
  public void ARejectionIsNotReportedAsAVerdict()
  {
    ClassificationCommands.Reject(IdentityOverrideStore.Instance, PlayerRegistry.Instance, "Ghosty");

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Ghosty")!);

    StringAssert.Contains(row.Provenance, "Claim taken back");
    Assert.IsFalse(row.Provenance.Contains("You chose"),
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
    Assert.AreEqual("Our side (earlier)", row.Why,
                    "the cell must not claim this capture produced it - and must not print the rule code either");
    StringAssert.Contains(row.Provenance, "Earlier logs x2");
    Assert.IsFalse(row.Provenance.Contains("Cast:"),
                   "a borrowed verdict has no cast behind it in THIS log; naming one would credit this capture with proving it");
  }

  [TestMethod]
  public void TheOneLineThatMeansSomebodysMeterIsWrong()
  {
    // Verified raider, overridden to NPC: exactly the state in which a player's damage leaves the board.
    PlayerRegistry.Instance.AddVerifiedPlayer("Berta", 1_700_000_000);
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, PlayerRegistry.Instance, "Berta", IdentityKind.Npc);

    var census = CensusWithoutACapture();
    var row = NamesTable.RowFrom(census.Find("Berta")!);

    Assert.IsTrue(census.Disagreements >= 1);
    StringAssert.Contains(row.Provenance, "players.txt says player");
  }

  /*
   * The one thing worth adding to this window: when a verdict came from a CAST, the tooltip names it (the census puts it
   * on Row.ReasonDetail; see CensusCastProofTest for how it is chosen). A column would have been empty for most rows, so
   * it stays a hover - and a row that is not spell-based gets no cast line at all.
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
    Assert.AreEqual("Spell", bard.Why);
    StringAssert.Contains(bard.Provenance, "Cast: Boastful Bellow XLVII");

    var mob = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "A bone walker",
      Kind = IdentityKind.Npc,
      Reason = "R14-article",
      HasFacts = true,
    });

    Assert.AreEqual("NPC", mob.Type);
    Assert.IsFalse(mob.Provenance.Contains("Cast:"),
                   "only a spell-based verdict has a cast; inventing the line would put a lie in the tooltip");
  }

  /*
   * "Healed" is decided by BREADTH, so the number that justifies it is a headcount (docs/combat-mirror-design.md →
   * "Fourth audit": pets draw 19-52 distinct casters, every genuine hostile measured tops out at 10). CensusHealProofTest
   * pins that the count is the rule's own crowd; what is pinned here is that the cell says the evidence in two words and
   * the tooltip says how much - and that no other verdict borrows the line.
   */
  [TestMethod]
  public void AHealedVerdictSaysHowManyDidIt()
  {
    var healed = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "Mercless",
      Kind = IdentityKind.Player,
      Reason = "R15-healed",
      HealedByCasters = 20,
      HasFacts = true,
    });

    Assert.AreEqual("Healed", healed.Why);
    StringAssert.Contains(healed.Provenance, "Healed by 20 raiders");

    // A mob the raid keeps getting hit by AoE heals has no crowd to report - 0 means "never asked", not "nobody".
    var npc = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "A bone walker",
      Kind = IdentityKind.Npc,
      Reason = "R6-npcdb",
      HealedByCasters = 0,
      HasFacts = true,
    });

    Assert.AreEqual("NPC list", npc.Why);
    Assert.IsFalse(npc.Provenance.Contains("Healed by"),
                   "the crowd line belongs to a heal-based verdict only; printing it elsewhere rewrites this row's provenance");
  }

  /*
   * The class pencil appears on a Player row and nowhere else, because the verb behind it is not a display choice:
   * PlayerRegistry.SetDefaultPlayerClass claims the name as a verified player AND writes it into players.txt. On an NPC
   * row that is the exact pollution this window exists to catch; on a Pet or Merc row it has no meaning (mercenaries are
   * not persisted, and a pet's class belongs to nobody's roster). An unplaced name gets no icon either - declaring itself
   * through the Type dropdown first is the step that decides whether the class verb applies.
   */
  [TestMethod]
  public void OnlyAPlayerRowOffersAClass()
  {
    foreach (var kind in new[] { IdentityKind.Player })
    {
      Assert.IsTrue(NamesTable.RowFrom(new ClassificationReport.Row { Name = "Ziggy", Kind = kind }).ClassEditable,
                    $"a {kind} row is where writing players.txt applies");
    }

    foreach (var kind in new[] { IdentityKind.Npc, IdentityKind.Pet, IdentityKind.Merc, IdentityKind.Unknown })
    {
      Assert.IsFalse(NamesTable.RowFrom(new ClassificationReport.Row { Name = "Ziggy", Kind = kind }).ClassEditable,
                    $"an icon on a {kind} row would write a roster entry for a name this window has no business claiming");
    }
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

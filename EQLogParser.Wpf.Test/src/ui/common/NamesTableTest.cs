using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The Names window's one piece of logic: turning a census row into what an operator reads. Nothing here needs WPF - no
 * element is constructed, so no Sta.Run - because the interesting part is which short line the WHY tooltip composes and
 * which rows get an edit icon, not how SfDataGrid paints them.
 *
 * The grid shows four columns in one order — Name, Type, Class, Why: the two you can edit sit together after the name and
 * the read-only explanation goes last. Two things an older layout carried are deliberately absent and so are not asserted
 * here: Damage/Healing (an identity list should not rank names by output) and Owner (the Pet Owners window lists the same
 * PlayerRegistry pairs).
 *
 * The WORDS themselves - "Chosen" for R10-manual, "Healed" for R15-healed, the dropdown's five entries - are asserted in
 * IdentityVocabularyTest (EQLogParser.Test), including the corpus check that no rule code reaches the screen. What is
 * pinned here is the part only this file owns: which LINES a row's tooltip gets, since each one tells a person whether to
 * ACT. A verdict resting on their own click needs no correction; one resting on an older log might; a claim that was taken
 * back says only "Nothing identified it", because that absence is the whole of it; and NOTHING names a file any more —
 * players.txt used to ride behind the proof as a second verdict from a source the operator cannot open from here, which
 * read as a contradiction nobody had explained on rows the rules had merely classified. The pane's own shape is pinned too: the column order, and the fact that a row which offers
 * no pencil still reserves its width (PlaceholderVisibilityConverter), so the words in the column line up.
 *
 * Most of this needs no WPF — RowFrom is a plain mapping — so only the column-order test constructs the pane, through
 * Sta.Run like every other UIElement here.
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
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, "Nicky", IdentityKind.Npc);

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Nicky")!);

    Assert.AreEqual("NPC", row.Type);
    Assert.AreEqual("Chosen", row.Why, "a verdict the operator wrote says so in the Why column itself, in two words");
    StringAssert.Contains(row.Provenance, "You chose NPC");

    /*
     * One line, and nothing about the row being outside a capture. The tooltip used to add "Not in this log" here, which
     * read as an error message on a perfectly ordinary hand-written verdict — the absence is already visible in the fact
     * columns, and the request was for ONE short line that answers in a blink (docs/DesignNotes.md).
     */
    Assert.IsFalse(row.Provenance.Contains("\n"), $"a plain override should hover as one line, got: {row.Provenance}");
    Assert.IsTrue(row.Overrulable, "an operator's own claim must always be takeable back");

    // The dropdown preselects from Kind and refuses to write what the row already says; without this, a click that
    // changes nothing would spend a derive pass and rewrite identity-overrides.txt.
    Assert.AreEqual(IdentityKind.Npc, row.Kind);
  }

  /* A claim the operator took back keeps its row (the roster still lists the name) and says nothing beyond the absence.
   * This test used to pin a "Claim taken back" sentence for the players.txt `!Name` refusal; that veto was unreachable in
   * every shipped build - see PlayerRegistry.RemoveVerifiedPlayer - and an invented explanation of a state nobody can
   * reach is worse than the plain "Nothing identified it" this state already answers with. */
  [TestMethod]
  public void AClaimTakenBackSaysNothingIdentifiedIt()
  {
    PlayerRegistry.Instance.AddVerifiedPlayer("Ghosty", DateUtil.ToDotNetSeconds(DateTime.Now));
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, "Ghosty", IdentityKind.Npc);
    Assert.IsTrue(CensusWithoutACapture().Find("Ghosty")!.IsOperatorVerdict, "control: the claim was never written");

    ClassificationCommands.ClearVerdict(IdentityOverrideStore.Instance, "Ghosty");
    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Ghosty")!);

    Assert.AreEqual(IdentityKind.Unknown, row.Kind);
    StringAssert.Contains(row.Provenance, "Nothing identified it");
    Assert.IsFalse(row.Provenance.Contains("You chose"),
                   "a taken-back claim still reads like the operator's answer - the two need different actions");
    Assert.IsFalse(row.Provenance.Contains("\n"), $"an absence should hover as one line, got: {row.Provenance}");
  }

  [TestMethod]
  public void ARowRestingOnOlderLogsSaysHowMany()
  {
    var ledger = IdentityPriorStore.Instance;
    var remembered = new EntityTimeline();
    remembered.SetIdentity("Rememb", IdentityKind.Npc, RuleStrength.Medium, "R7-graph");
    ledger.Record(remembered, ["Rememb"], 1_700_000_000);
    ledger.Record(remembered, ["Rememb"], 1_800_000_000);

    var row = NamesTable.RowFrom(CensusWithoutACapture(ledger).Find("Rememb")!);

    Assert.AreEqual("NPC", row.Type);
    /*
     * The cell says the SAME word a local verdict says ("Fights NPCs") and carries no marker: paying column width for
     * "(earlier)" was asked to stop, and a cell reading "Our side (earlier)" was two mysteries stacked. Where it does say
     * it is the tooltip, in words, with the count riding on the proof clause.
     */
    Assert.AreEqual("Fights NPCs", row.Why, "the cell names the kind of proof and nothing else");
    StringAssert.Contains(row.Provenance, "in previous log x2");
    Assert.IsFalse(row.Provenance.Contains("Cast"),
                   "a borrowed verdict has no cast behind it in THIS log; naming one would credit this capture with proving it");
  }

  /*
   * A verified raider overridden to NPC is exactly the state in which a player's damage leaves the board, and the census
   * still counts it — that count is what the caption's tooltip prints ("N contradict the roster"), where it belongs: it is
   * one number about the whole capture, not a flag welded onto every affected row. On the row itself the hover now answers
   * only the question asked, which for this name is "you said so".
   */
  [TestMethod]
  public void AContradictedRosterCountsInTheCensusAndStaysOffTheRow()
  {
    PlayerRegistry.Instance.AddVerifiedPlayer("Berta", 1_700_000_000);
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, "Berta", IdentityKind.Npc);

    var census = CensusWithoutACapture();
    var row = NamesTable.RowFrom(census.Find("Berta")!);

    Assert.IsTrue(census.Disagreements >= 1, "the disagreement stopped counting in the census");
    Assert.AreEqual("You chose NPC", row.Provenance,
                    $"the hover states the operator's own claim and appends nothing: {row.Provenance}");
  }

  /*
   * No hover in this pane names a file. Four of them sit behind these rows (players.txt, npcs.txt, identity-priors.txt,
   * identity-overrides.txt), and naming one answers "which file said so" rather than the question a tooltip is for; worst of
   * all was the roster flag on a row whose Type came from a rule, where "in players.txt" looked like an unresolved
   * contradiction. The two proof clauses that used to quote filenames say what the file IS instead — "On the NPC List",
   * "The Name of a Spell".
   */
  /*
   * The Type dropdown belongs to the ROW, not to the window. One ComboBox serves every cell and its list comes from
   * IdentityVocabulary.TypeOptionsFor — the same recognizers that hide a pencil — so no raider is offered Mercenary (a
   * mercenary is what /target reported; typing it moves her damage onto a column nothing else fills) and a summon whose
   * spelling names its master has one verdict and the way back out. RowFrom carries the list, which is what the popup binds
   * when a cell opens, so this is checked without constructing a pane.
   */
  [TestMethod]
  public void ARowOffersOnlyTheKindsItsNameAllows()
  {
    static string[] Words(NamesTable.NameRow row) => [.. row.TypeChoices.Select(o => o.Word)];

    var raider = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Berta", Kind = IdentityKind.Player, Reason = "R3-joinraid" });
    CollectionAssert.Contains(Words(raider), "Player", "the popup opens on the row's own answer");
    CollectionAssert.Contains(Words(raider), "Pet", "a person may still be overruled about a summon");
    CollectionAssert.DoesNotContain(Words(raider), "Mercenary",
                                    "Mercenary is not a verdict an operator can put on a name");

    var merc = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Stormpaw", Kind = IdentityKind.Merc, Reason = "R13-merc" });
    CollectionAssert.Contains(Words(merc), "Mercenary", "what the row already is stays in its list");

    var pet = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Sancus`s pet", Kind = IdentityKind.Pet, Reason = "R5-owner:Sancus" });
    CollectionAssert.AreEquivalent(new[] { "Pet", "Clear claim" }, Words(pet));
  }

  /*
   * The pane follows the derive — a census lands every couple of seconds while a log grows — and clearing the list on each
   * one cost both the selection and the order: rows are ranked, so the line under the cursor slid twice a second and the
   * list could not be read while it was live. MergeRows replaces a row's CELLS at its own index, drops names the capture
   * lost, and appends newcomers at the bottom; the ranking returns when the tab is opened again (rebuild: true), which is
   * the moment it is worth something.
   */
  [TestMethod]
  public void ARefreshKeepsEveryRowWhereTheReaderLeftIt()
  {
    var listed = new ObservableCollection<NamesTable.NameRow>();
    NamesTable.MergeRows(listed, [CensusRow("Ann"), CensusRow("Bob"), CensusRow("Cy")], rebuild: true);

    // Cy now outranks everyone, Ann's verdict changed, and Dee has never been listed.
    NamesTable.MergeRows(listed,
      [CensusRow("Cy"), CensusRow("Ann", IdentityKind.Player, "R3-joinraid"), CensusRow("Bob"), CensusRow("Dee")], rebuild: false);

    CollectionAssert.AreEqual(new[] { "Ann", "Bob", "Cy", "Dee" }, listed.Select(r => r.Name).ToList(),
                              "the order on screen holds; only a new name arrives, and it arrives at the bottom");
    Assert.AreEqual(IdentityKind.Player, listed[0].Kind, "a row whose verdict moved says the new thing IN ITS OLD SLOT");
    Assert.AreEqual("Player", listed[0].Type);
  }

  [TestMethod]
  public void ANameTheCensusDropsLeavesTheList()
  {
    var listed = new ObservableCollection<NamesTable.NameRow>();
    NamesTable.MergeRows(listed, [CensusRow("Ann"), CensusRow("Bob")], rebuild: true);
    NamesTable.MergeRows(listed, [CensusRow("Bob")], rebuild: false);

    CollectionAssert.AreEqual(new[] { "Bob" }, listed.Select(r => r.Name).ToList(),
                              "a name the capture stopped reporting leaves; a stale verdict parked above an empty slot is what this window exists to catch");
  }

  [TestMethod]
  public void OpeningTheTabAgainReRanksTheList()
  {
    var listed = new ObservableCollection<NamesTable.NameRow>();
    NamesTable.MergeRows(listed, [CensusRow("Ann"), CensusRow("Bob")], rebuild: true);
    NamesTable.MergeRows(listed, [CensusRow("Bob"), CensusRow("Cy")], rebuild: false);
    NamesTable.MergeRows(listed, [CensusRow("Cy"), CensusRow("Ann"), CensusRow("Bob")], rebuild: true);

    CollectionAssert.AreEqual(new[] { "Cy", "Ann", "Bob" }, listed.Select(r => r.Name).ToList(),
                              "navigating to the pane is when the ranking arrives — held still while read, current when reopened");
  }

  private static ClassificationReport.Row CensusRow(string name, IdentityKind kind = IdentityKind.Unknown, string reason = "")
    => new() { Name = name, Kind = kind, Reason = reason };

  [TestMethod]
  public void NoRowTooltipNamesAFile()
  {
    foreach (var kind in new[] { IdentityKind.Unknown, IdentityKind.Player, IdentityKind.Pet, IdentityKind.Merc, IdentityKind.Npc })
      foreach (var source in new[]
               {
                 "R5-companion", "R5-owner:Beorun", "R17-selffeed", "R6-npcdb", "R14-shape", "R7-graph:9", "Prior:R6-npcdb",
                 "R21-spelleffect", "R21-spellcast", "R9-charm", "Manual",
                 // What the app remembers rather than what the capture showed. Neither may name a file, and neither may
                 // say "RegistrySeed" — the internal name of the seam is not an answer about a name.
                 "RegistrySeed", "RegistrySeed:Strangle",
               })
      {
        var provenance = NamesTable.RowFrom(new ClassificationReport.Row { Name = "Ziggy", Kind = kind, Reason = source }).Provenance;

        foreach (var file in new[] { ".txt", "players", "npcs", "priors", "overrides" })
          Assert.IsFalse(provenance.Contains(file, StringComparison.OrdinalIgnoreCase),
                         $"{kind}/{source} puts '{file}' in the hover: {provenance}");
      }
  }

  /*
   * A row that offers no pencil still reserves its width. Hidden keeps the element's box; Collapsed — what
   * BooleanToVisibilityConverter answers — hands it back, and the words in a column stop lining up: the exceptions (spell
   * effects and self-naming summons on Type, everything not yet a Player on Class) punched a ragged left edge through the
   * middle of both columns, so a difference in what a row ALLOWS was rendered as a difference in where its text begins.
   */
  [TestMethod]
  public void ARowWithoutAPencilStillPaysForOne()
  {
    var converter = new PlaceholderVisibilityConverter();
    var culture = CultureInfo.CurrentCulture;

    Assert.AreEqual(Visibility.Visible, converter.Convert(true, typeof(Visibility), null, culture));
    Assert.AreEqual(Visibility.Hidden, converter.Convert(false, typeof(Visibility), null, culture),
                    "a missing pencil collapsed and took the column's alignment with it");
  }

  /*
   * Name, Type, Class, Why. The order is the interaction: Name identifies, the next two are the cells that answer with a
   * click, and WHY is the read-only sentence that explains them — which used to sit between the two things you edit.
   * Read inside Sta.Run like every DependencyObject (the grid's Columns belong to the thread that built them).
   */
  [TestMethod]
  public void TheColumnsGoNameTypeClassWhy()
  {
    EnsurePaneResources();

    var order = new List<string>();
    Sta.Run(() =>
    {
      foreach (var column in new NamesTable().namesGrid.Columns) order.Add(column.MappingName ?? string.Empty);
    });

    CollectionAssert.AreEqual(new[] { "Name", "Type", "PlayerClass", "Why" }, order,
                              $"column order changed: {string.Join(", ", order)}");
  }

  // The two app-level StaticResource keys this pane's markup needs, stubbed for a headless test host — same shape as
  // FightTableStartupTest's. A Style whose TargetType is the element or a base of it; anything else throws on parse.
  private static void EnsurePaneResources() => Sta.Run(() =>
  {
    _ = Application.Current ?? new Application();
    var res = Application.Current!.Resources;
    res["EQIconStyle"] ??= new Style(typeof(Image));
    res["EQTitleStyle"] ??= new Style(typeof(ContentControl));
  });

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
      Reason = "R4-spell:Boastful Bellow XLVII",
      ReasonDetail = "Boastful Bellow XLVII",
      HasFacts = true,
    });

    Assert.AreEqual("Player", bard.Type);
    Assert.AreEqual("Spell", bard.Why);

    /*
     * The cast is named in the proof LINE, not as a second labelled row: "Cast Boastful Bellow XLVII". The rank matters
     * because that rank is what the rule checked, and it travels inside the source string for the same reason the ledger
     * keeps one - an older log's verdict can still say what it read.
     */
    Assert.AreEqual("Cast Boastful Bellow XLVII", bard.Provenance);

    var mob = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "A bone walker",
      Kind = IdentityKind.Npc,
      Reason = "R14-shape",
      HasFacts = true,
    });

    Assert.AreEqual("NPC", mob.Type);
    Assert.IsFalse(mob.Provenance.Contains("Cast"),
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

    Assert.AreEqual("NPC List", npc.Why);
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
    ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, "Nobody", IdentityKind.Npc);
    ClassificationCommands.ClearPrior(IdentityPriorStore.Instance, "Nobody");
    ClassificationCommands.ClearVerdict(IdentityOverrideStore.Instance, "Nobody");
  }
}

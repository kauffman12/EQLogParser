using EQLogParser;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser.Wpf.Test;

/*
 * The Names window's one piece of logic: turning a census row into what an operator reads. Nothing here needs WPF - no
 * element is constructed, so no Sta.Run - because the interesting part is which short line the WHY tooltip composes and
 * which rows get an edit icon, not how SfDataGrid paints them.
 *
 * The grid shows five columns in one order — Name, Type, Class, Owner, Why: the cells you edit sit together after the name and the
 * read-only explanation goes last. Damage/Healing stay absent on purpose (an identity list should not rank names by output). Owner came
 * BACK in 2026-10-09: it was removed because "Pet Owners lists the same pairs", and that argument died with Pet Owners' write side when
 * petmapping.txt froze — see NamesTable's column comment and docs/DesignNotes.md → "petmapping.txt is a feed now".
 *
 * The WORDS themselves - "Chosen" for R10-manual, "Healed" for R15-healed, the dropdown's five entries - are asserted in
 * IdentityVocabularyTest (EQLogParser.Test), including the corpus check that no rule code reaches the screen. What is
 * pinned here is the part only this file owns: which LINES a row's tooltip gets, since each one tells a person whether to
 * ACT. A verdict resting on their own click needs no correction; one resting on an older log might; a claim that was taken
 * back leaves the list when memory was the only claim on the name (that removal is the repair tool — see
 * "TakingAClaimBackTakesTheLegacyMemoryWithIt"); and NOTHING names a file any more —
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
    ClassificationCommands.ApplyVerdict("Nicky", IdentityKind.Npc);

    var row = NamesTable.RowFrom(CensusWithoutACapture().Find("Nicky")!);

    Assert.AreEqual("NPC", row.Type);
    Assert.AreEqual("Chosen", row.Why, "a verdict the operator wrote says so in the Why column itself, in two words");
    StringAssert.Contains(row.Provenance, "You Chose NPC");

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

  /*
   * The take-back forgets FIRST and writes nothing after — and that is the whole of its use. It sat in the Type dropdown for a while
   * ("Clear claim", renamed "Reset" on 2026-11), but a list of ANSWERS should not carry a verb whose honest outcome is often no visible
   * change: what it removes is memory, after which this capture's own lines answer again, so a name the log identifies comes straight
   * back. It is the take-back click in the Name column now (ClassificationCommands.Recalculate, which adds the class lanes); this test pins
   * the door that click goes through.
   *
   * Every verdict write starts at `ClassificationCommands.Forget`, including the Unknown one that means "take my claim
   * back": the ledger row goes (the remembered verdict, the roster's "one of ours" bit, the class that rode with it, the
   * pet-owner column) and so does every registry claim - verified player, verified pet, pet map, merc. Measured here:
   * adding a verified player then writing NPC leaves `GetVerifiedPlayers()` WITHOUT the name, and withdrawing the claim
   * afterwards leaves nothing to list at all, because there is no memory and no capture left to list it from.
   *
   * That is deliberate and it is the reason this is the one door that fixes a name legacy got wrong (2026-10-09):
   * `players.txt` has no writer, and its importer refuses to run over a folder whose ledger already carries roster rows
   * (`IdentityPriorStore.HasRosterRows`), so a bad inherited belief does not get re-imported behind the operator's back -
   * it has to be re-earned by this capture's own lines. A name that keeps facts stays on the list at Unknown; a name whose
   * only claim was memory leaves. docs/DesignNotes.md → "Clear claim forgets everything — which is what makes it the repair".
   */
  [TestMethod]
  public void TakingAClaimBackTakesTheLegacyMemoryWithIt()
  {
    PlayerRegistry.Instance.AddVerifiedPlayer("Ghosty", DateUtil.ToDotNetSeconds(DateTime.Now));
    ClassificationCommands.ApplyVerdict("Ghosty", IdentityKind.Npc);

    Assert.IsTrue(CensusWithoutACapture().Find("Ghosty")!.IsOperatorVerdict, "control: the claim was written");
    Assert.IsFalse(PlayerRegistry.Instance.GetVerifiedPlayers().Contains("Ghosty"),
                   "the verdict is what evicts the roster row - an asserted belief replaces the memory rather than stacking on it");

    ClassificationCommands.ApplyVerdict("Ghosty", IdentityKind.Unknown);

    Assert.IsNull(CensusWithoutACapture().Find("Ghosty"),
                  "claim withdrawn, memory forgotten, no capture to answer from: nothing is left to list, and that removal is the point");
  }

  /*
   * The other half of a take-back: a name this capture DOES know stays on the list, reads Unknown, and hovers as the bare
   * absence - one line, no invented explanation. (This used to be pinned through the players.txt `!Name` refusal, a veto no
   * shipped build could ever write; see `PlayerRegistry.RemoveVerifiedPlayer`. The word itself belongs to Core
   * (`IdentityVocabularyTest`); what is pinned here is that the pane appends nothing to it.)
   */
  [TestMethod]
  public void AnUnplacedNameHoversAsTheBareAbsence()
  {
    var row = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Whisper", Kind = IdentityKind.Unknown, HasFacts = true });

    Assert.AreEqual(IdentityKind.Unknown, row.Kind);
    // The Type cell says the kind and the WHY column carries the absence ("Not Placed"): the words are Core's
    // (IdentityVocabulary.TypeWord / WhyWord) and what is pinned here is that this pane passes them through with nothing added.
    Assert.AreEqual("Unknown", row.Type);
    Assert.AreEqual("Not Placed", row.Why, "the column that answers 'why' answers it with the word for nothing");
    Assert.AreEqual("Nothing Identified It", row.Provenance,
                    $"no rider of any kind behind an absence, got: {row.Provenance}");
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
    Assert.AreEqual("Attacks NPC", row.Why, "the cell names the kind of proof and nothing else");
    StringAssert.Contains(row.Provenance, "in previous log x2");
    Assert.IsFalse(row.Provenance.Contains("Cast"),
                   "a borrowed verdict has no cast behind it in THIS log; naming one would credit this capture with proving it");
  }

  /*
   * A roster raider whose name reads NPC is exactly the state in which a player's damage leaves the board. The row knows it
   * (`Row.IsDisagreement`) and says nothing about it: the header strip that used to print a whole-capture count was removed
   * on request, so the pane carries no number, and a badge on the row would paint half the list — the hover answers only the
   * question asked, which for this name is "you said so".
   *
   * THE ORDERING IS THE LAW THIS TEST HAD TO LEARN. A single click cannot produce the contradiction any more, because the
   * verdict's own `Forget` deletes the roster claim that would be contradicted (measured: verdict-then-nothing reads
   * `LegacySaysPlayer=false`). The state is reached when memory refills AFTER the verdict — tonight's capture re-verifying
   * someone the operator called NPC last week — which is the case actually worth knowing about, so that is how the row is
   * built here. Both directions are asserted: writing the roster first and the verdict second produces no disagreement at all.
   */
  [TestMethod]
  public void AContradictedRosterIsKnownOnTheRowAndSaidNowhere()
  {
    // Verdict over a name memory has not heard of yet: nothing contradicts it. Forgetting first is why.
    ClassificationCommands.ApplyVerdict("Berta", IdentityKind.Npc);
    Assert.IsFalse(CensusWithoutACapture().Find("Berta")!.IsDisagreement,
                   "the verdict took the roster claim with it, so there is nothing left to disagree with");

    // Memory comes back afterwards — a re-verified raider under a standing NPC verdict. Now the row knows.
    PlayerRegistry.Instance.AddVerifiedPlayer("Berta", 1_700_000_000);
    var census = CensusWithoutACapture();
    var row = NamesTable.RowFrom(census.Find("Berta")!);

    Assert.IsTrue(census.Find("Berta")!.IsDisagreement,
                  "the row stopped knowing that the roster and the verdict contradict each other");
    Assert.AreEqual("You Chose NPC", row.Provenance,
                    $"the hover states the operator's own claim and appends nothing: {row.Provenance}");
    Assert.IsFalse(row.Provenance.Contains("\n"), $"a disagreement is not a tooltip section, got: {row.Provenance}");
  }

  /*
   * No hover in this pane names a file. Four of them sit behind these rows (players.txt, npcs.txt, identity-priors.txt,
   * identity-overrides.txt), and naming one answers "which file said so" rather than the question a tooltip is for; worst of
   * all was the roster flag on a row whose Type came from a rule, where "in players.txt" looked like an unresolved
   * contradiction. The two proof clauses that used to quote filenames say what the file IS instead — "On the NPC List",
   * "Name of a Known Spell". The app's word for a hostile name is NPC and for a cast-side spell is
   * Player Spell — no "monster", no "mob", no "our", nowhere on this pane.
   */
  /*
   * The Type dropdown belongs to the ROW, not to the window. One ComboBox serves every cell and its list comes from
   * IdentityVocabulary.TypeOptionsFor — the same recognizer that hides a pencil — so what a row refuses is what its NAME decides,
   * never a guess about which kinds are typeable: an eye is NPC or nothing, a summon whose spelling names its master has one
   * verdict, and Unknown is in no list at all (the take-back is the take-back icon beside the name, where an operator can see it).
   * Mercenary is offered on any row that gets a pencil — one vocabulary with the "Set … as" cascade, since 2026-11.
   * RowFrom carries the list, which is what the editor binds when a cell opens, so this is checked without constructing a pane;
   * IdentityVocabularyTest holds the same law in Core, this pins that the PANE hands the row that list.
   */
  [TestMethod]
  public void ARowOffersOnlyTheKindsItsNameAllows()
  {
    static string[] Words(NamesTable.NameRow row) => [.. row.TypeChoices.Select(o => o.Word)];

    var raider = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Berta", Kind = IdentityKind.Player, Reason = "R3-joinraid" });
    CollectionAssert.Contains(Words(raider), "Player", "the popup opens on the row's own answer");
    CollectionAssert.Contains(Words(raider), "Pet", "a person may still be overruled about a summon");
    CollectionAssert.Contains(Words(raider), "Mercenary",
                              "one vocabulary, one reach: the cascade offers Mercenary in four panes, so this dropdown may not hide the word");
    Assert.IsFalse(raider.TypeChoices.Any(o => o.Kind == IdentityKind.Unknown),
                   "no entry writes Unknown — an asserted silence at Manual strength would outrank what this capture watched");

    var merc = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Stormpaw", Kind = IdentityKind.Merc, Reason = "R13-merc" });
    CollectionAssert.Contains(Words(merc), "Mercenary", "what the row already is stays in its list");
    Assert.IsFalse(merc.TypeChoices.Count(o => o.Kind == IdentityKind.Merc) > 1, "one entry per answer, never two");

    // An eye: never a person, never somebody's pet row (that would split its owner's output beside the real pets).
    var eye = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Eye of Zamul", Kind = IdentityKind.Npc, Reason = "R6-npcdb" });
    CollectionAssert.AreEquivalent(new[] { "NPC" }, Words(eye), "the eye's list is exactly its own answer");

    var pet = NamesTable.RowFrom(new ClassificationReport.Row
    { Name = "Sancus`s pet", Kind = IdentityKind.Pet, Reason = "R5-owner:Sancus" });
    // The spelling settles the answer, so the list is exactly what it already is.
    CollectionAssert.AreEquivalent(new[] { "Pet" }, Words(pet));
  }

  /*
   * The pane follows the derive — a census lands every couple of seconds while a log grows. Since 2026-12 it no longer merges
   * into the collection it showed: the grid renders a PAGE of a published list, so each pass rebuilds and swaps the whole
   * thing (docs/DesignNotes.md → "Identity Table paging"). The reader-visible laws of the former merge design are all
   * re-stated on the PUBLISHED list — a name arrives where its key says, a name the census lost leaves, a row whose word
   * changed says the new thing — because the sort breaks every tie on the name.
   */
  [TestMethod]
  public void ARefreshKeepsEveryRowWhereTheReaderLeftIt()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([CensusRow("Ann"), CensusRow("Bob"), CensusRow("Cy")]);

      // Cy now outranks everyone, Ann's verdict changed, and Dee has never been listed.
      pane.PublishCensusForTest([CensusRow("Cy"), CensusRow("Ann", IdentityKind.Player, "R3-joinraid"), CensusRow("Bob"), CensusRow("Dee")]);

      CollectionAssert.AreEqual(new[] { "Ann", "Bob", "Cy", "Dee" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "the reader's order holds (a Name sort cannot move a name, and this one is already alphabetical); Dee arrives where its name says");
      Assert.AreEqual(IdentityKind.Player, pane.RowsForTest[0].Kind, "a row whose verdict moved says the new thing IN ITS OLD SLOT");
      Assert.AreEqual("Player", pane.RowsForTest[0].Type);
    });
  }

  [TestMethod]
  public void ANameTheCensusDropsLeavesTheList()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([CensusRow("Ann"), CensusRow("Bob")]);
      pane.PublishCensusForTest([CensusRow("Bob")]);

      CollectionAssert.AreEqual(new[] { "Bob" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "a name the capture stopped reporting leaves; a stale verdict parked above an empty slot is what this window exists to catch");
    });
  }

  [TestMethod]
  public void OpeningTheTabAgainLaysTheWholeListOutInColumnOrder()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([CensusRow("Ann"), CensusRow("Bob")]);

      // Emitted in a third order, and the published list lands in the header's order regardless: every publish lays the
      // whole list out from the census (that is what paging made cheap — one swap, no merge).
      pane.PublishCensusForTest([CensusRow("Cy"), CensusRow("Ann"), CensusRow("Bob")]);

      CollectionAssert.AreEqual(new[] { "Ann", "Bob", "Cy" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "what the census emitted first is not something a reader can see, and used to be the whole list");
    });
  }

  /*
   * The reported bug, in one row: the arrow said Name while the rows underneath it were in census order. A newcomer belongs
   * between the names it sorts between — which is where the published list puts it, because the sort breaks every tie on the name.
   */
  [TestMethod]
  public void ANameArrivingJoinsWhereItsNameSays()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([CensusRow("Ann"), CensusRow("Cy")]);

      // Bob is the name this pass learned, and the census thought of it last.
      pane.PublishCensusForTest([CensusRow("Ann"), CensusRow("Cy"), CensusRow("Bob")]);

      CollectionAssert.AreEqual(new[] { "Ann", "Bob", "Cy" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "a new name lands at the index its own key says — not at the bottom, where it read as an unsorted tail");
    });
  }

  /*
   * Sorting a column whose WORD CHANGES (Type, Class, Owner, Why are all verdicts, and verdicts move while a log loads)
   * means rows move: that is what sorting it asked for. Under the default Name sort nothing moves at all — names do not change.
   *
   * The fixture names its kinds rather than leaving them Unknown on purpose: an unplaced row's Type cell prints "Unknown", which
   * sorts AFTER every kind word, so an unkinded row is not a stand-in for an NPC and the ordering below would test the alphabet
   * instead of the rule.
   */
  [TestMethod]
  public void SortingByAWordThatChangedPutsTheMovedRowBackInPlace()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      var column = IdentityRowOrder.TypeColumn;

      pane.PublishCensusForTest([CensusRow("Ann", IdentityKind.Npc, "R6-npcdb"), CensusRow("Bob", IdentityKind.Player, "R3-joinraid"), CensusRow("Cy", IdentityKind.Npc, "R14-shape")], column: column);
      CollectionAssert.AreEqual(new[] { "Ann", "Cy", "Bob" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "NPC before Player, and the two NPCs in name order — a tie on the clicked column breaks on the name so the "
                              + "list cannot be census order wearing a sort arrow through the back door");

      // Ann is a player now. Nothing was added or removed; only a sorted word moved.
      pane.PublishCensusForTest([CensusRow("Ann", IdentityKind.Player, "R3-joinraid"), CensusRow("Bob", IdentityKind.Player, "R3-joinraid"), CensusRow("Cy", IdentityKind.Npc, "R14-shape")], column: column);
      CollectionAssert.AreEqual(new[] { "Cy", "Ann", "Bob" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "the row whose word changed goes where the sort says, and the survivors keep their relative order");
    });
  }

  /*
   * The strip's two dials (2026-12): a name filter and the closed type list. Both are VISIBILITY — the census keeps knowing
   * every name, the title says how many of them a page shows, and clearing a dial gives the whole list back (the plan's words).
   * A filtered name is not a missing name: "Showing 1 of 3" is what tells the two apart on screen.
   */
  [TestMethod]
  public void TheStripDialsNarrowTheListNotTheCensus()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([CensusRow("Ann", IdentityKind.Player, "R3-joinraid"), CensusRow("Bob", IdentityKind.Npc, "R6-npcdb"), CensusRow("Cyra", IdentityKind.Pet, "R5-owner:Ann")]);
      Assert.AreEqual($"Showing 1 to {3:N0} of {3:N0} Identities", pane.TitleTextForTest,
                      "the count is the RANGE on screen plus the size of the answer, not one number for a paged list");

      // The name dial is a case-insensitive contains: one row answers it.
      pane.SetSearchTextForTest("an");
      CollectionAssert.AreEqual(new[] { "Ann" }, pane.RowsForTest.Select(r => r.Name).ToList(), "'an' finds Ann and nothing else");
      Assert.AreEqual($"Showing 1 to {1:N0} of {1:N0} Identities, {3:N0} total", pane.TitleTextForTest,
                      "narrowed reads say the match count AND the capture's own size: the row is filtered out of the PAGE, not out of the log");

      // The type dial narrows on its own.
      pane.SetSearchTextForTest("");
      Assert.AreEqual(3, pane.RowsForTest.Count, "clearing the name dial gives the list back");
      pane.SetTypeFilterForTest(IdentityKind.Npc);
      CollectionAssert.AreEqual(new[] { "Bob" }, pane.RowsForTest.Select(r => r.Name).ToList(), "the closed vocabulary is the whole of what can be shown");

      // Both dials at once: their intersection, and the title still says 3.
      pane.SetTypeFilterForTest(IdentityKind.Player, IdentityKind.Pet);
      pane.SetSearchTextForTest("a");
      CollectionAssert.AreEqual(new[] { "Ann", "Cyra" }, pane.RowsForTest.Select(r => r.Name).ToList());
      Assert.AreEqual($"Showing 1 to {2:N0} of {2:N0} Identities, {3:N0} total", pane.TitleTextForTest);

      // And a question nobody passes is an EMPTY list, not a crash and not the old rows.
      pane.SetSearchTextForTest("zzz");
      Assert.AreEqual(0, pane.RowsForTest.Count, "no answer is a valid answer");
      Assert.AreEqual($"No matching Identities, {3:N0} total", pane.TitleTextForTest,
                      "an answer of nothing still says the capture is not empty");
      pane.SetSearchTextForTest(Resource.NAME_FILTER_TEXT);
      Assert.AreEqual(2, pane.RowsForTest.Count, "the restored watermark is not a name query");
      pane.SetTypeFilterForTest(IdentityVocabulary.TypeOptions.Select(option => option.Kind).ToArray());
      Assert.AreEqual(3, pane.RowsForTest.Count, "widening both dials recovers the complete source without another census");
    });
  }

  [TestMethod]
  public void EveryPageComparatorMatchesTheGlobalIndexIncludingClass()
  {
    var comparisonsAgree = true;
    Sta.Run(() =>
    {
      var pane = CreatePane();
      NamesTable.NameRow[] rows =
      [
        NamesTable.RowFrom(new ClassificationReport.Row
          { Name = "Ann", Kind = IdentityKind.Player, Class = "Wizard", Reason = "R3-joinraid" }),
        NamesTable.RowFrom(new ClassificationReport.Row
          { Name = "Bob", Kind = IdentityKind.Player, Class = "Bard", Reason = "R4-spell:Boastful Bellow XLVII" }),
        NamesTable.RowFrom(new ClassificationReport.Row
          { Name = "Cy", Kind = IdentityKind.Pet, PetOwner = "Bob", Reason = "R5-owner:Bob" }),
      ];
      foreach (var column in pane.namesGrid.Columns)
      {
        var installed = pane.namesGrid.SortComparers.FirstOrDefault(item => item.PropertyName == column.MappingName);
        var coreColumn = column.MappingName == "PlayerClass" ? IdentityRowOrder.ClassColumn : column.MappingName;
        var expected = IdentityRowOrder.For<NamesTable.NameRow>(coreColumn);
        foreach (var x in rows)
          foreach (var y in rows)
            comparisonsAgree &= installed is not null
              && Math.Sign(installed.Comparer.Compare(x, y)) == Math.Sign(expected(x, y));
      }
    });
    Assert.IsTrue(comparisonsAgree, "the vendor's slice sort must use the full index's comparison, not a second order");
  }

  [TestMethod]
  public void AnOldViewCannotOverwriteANewerCensusOrReplaceItsHeldResult()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.AcceptCensus([CensusRow("Ann", IdentityKind.Player)]);
      var oldView = pane.BuildViewForTest();
      pane.Editors.Open();
      pane.AcceptCensus([CensusRow("Ann", IdentityKind.Npc), CensusRow("Bob")]);
      Assert.IsFalse(pane.PublishViewForTest(oldView), "an old worker cannot replace the newest held census");
      pane.Editors.Close();
      Assert.AreEqual(2, pane.RowsForTest.Count);
      Assert.AreEqual(IdentityKind.Npc, pane.RowsForTest[0].Kind);
      Assert.IsFalse(pane.PublishViewForTest(oldView), "it cannot overwrite the already-published census either");
      Assert.AreEqual(IdentityKind.Npc, pane.RowsForTest[0].Kind);
    });
  }

  [TestMethod]
  public void AHeldAnswerCannotPaintAfterTheReaderChangesTheQuestion()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.AcceptCensus([CensusRow("Ann", IdentityKind.Player), CensusRow("Bob", IdentityKind.Npc)]);
      pane.Editors.Open();
      pane.AcceptCensus([CensusRow("Ann", IdentityKind.Player), CensusRow("Bob", IdentityKind.Npc), CensusRow("Cy")]);
      // The real TextChanged handler changes the ticket immediately, before the debounced worker is asked.
      pane.nameSearchBox.Text = "Bob";
      pane.Editors.Close();
      Assert.AreEqual(2, pane.RowsForTest.Count, "the now-stale held census did not paint its third row");
      pane.SetSearchTextForTest("Bob");
      CollectionAssert.AreEqual(new[] { "Bob" }, pane.RowsForTest.Select(row => row.Name).ToArray());
    });
  }

  [TestMethod]
  public void ANewCensusWhileFilteredKeepsBothDialsAndStillRetainsTheExcludedNames()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.AcceptCensus([CensusRow("Ann", IdentityKind.Player), CensusRow("Bob", IdentityKind.Npc)]);
      pane.SetTypeFilterForTest(IdentityKind.Player);
      pane.SetSearchTextForTest("an");
      pane.AcceptCensus([CensusRow("Ann", IdentityKind.Npc), CensusRow("Annette", IdentityKind.Player),
        CensusRow("Bob", IdentityKind.Npc), CensusRow("Takenback", IdentityKind.Unknown, TakenBackNames.Word)]);
      CollectionAssert.AreEqual(new[] { "Annette" }, pane.RowsForTest.Select(row => row.Name).ToArray());
      pane.SetSearchTextForTest("");
      pane.SetTypeFilterForTest(IdentityVocabulary.TypeOptions.Select(option => option.Kind).ToArray());
      Assert.AreEqual(4, pane.RowsForTest.Count, "All Types recovers Unknown/taken-back names too");
      Assert.AreEqual(IdentityKind.Npc, pane.RowsForTest[0].Kind, "the retained source has the new verdict");
    });
  }

  /*
   * The pane follows the derive, so a census arrives every couple of seconds — and one lands IMMEDIATELY after a verdict written
   * from another pane, which is when the reader is most likely to be mid-click in this window's Type cell. Merging rows replaces the
   * cell the popup is anchored to, WPF closes the popup, its close hook clears "which row am I editing", and the click writes nothing:
   * the field report was a dropdown that stopped responding (2026-10-09). So a census waits for the gesture, and the newest one paints
   * when it ends. Constructed inside Sta.Run like every other UIElement here.
   */
  [TestMethod]
  public void ACensusWaitsForAnOpenCellEditorRatherThanYankingTheRowOutFromUnderIt()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();

      Assert.IsTrue(pane.AcceptCensus([CensusRow("Ann"), CensusRow("Bob")]), "nothing is open: the census lands as it always did");
      Assert.AreEqual(2, pane.RowsForTest.Count);

      // The Type pencil was clicked on a row…
      pane.Editors.Open();

      Assert.IsFalse(pane.AcceptCensus([CensusRow("Ann"), CensusRow("Bob"), CensusRow("Cy")]),
          "an open editor holds the repaint instead of rebuilding the cell under the popup");
      Assert.AreEqual(2, pane.RowsForTest.Count, "the list did not move while the gesture held");

      // A second pass arrives before the click finishes: the older census is stale data and is dropped, not chained.
      Assert.IsFalse(pane.AcceptCensus([CensusRow("Ann")]), "newest wins — an older census describes a capture that moved on");

      pane.Editors.Close();

      Assert.AreEqual(1, pane.RowsForTest.Count,
          "the gesture ended and the NEWEST census painted; the held-then-superseded one never ran");
      Assert.AreEqual("Ann", pane.RowsForTest[0].Name);

      // And the pane is live again: a later pass paints straight through with no editor open.
      Assert.IsTrue(pane.AcceptCensus([CensusRow("Dee"), CensusRow("Elle")]), "the hold is not latched");
      Assert.AreEqual(2, pane.RowsForTest.Count);
    });
  }

  /*
   * What only the real controls can answer: the numbers beside and above them. The arrows are Syncfusion's own and say
   * nothing about how many pages exist, so `pagerInfo` does — and it has to follow a click on the arrows, not just a build,
   * or it lies within one gesture. The title names the RANGE on screen for the same reason: it used to name the whole
   * filtered list while the reader was looking at one page of it (operator 2026-10-13). That a census must not move the
   * pager is pinned arithmetic in NamesTableViewTest; here the question is whether the pane and the vendor agree.
   */
  [TestMethod]
  public void ThePagerSaysWhichPageItIsOnAndTheTitleNamesTheRange()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      var census = Enumerable.Range(0, 2500).Select(i => CensusRow($"Name{i:D4}", IdentityKind.Player)).ToArray();
      pane.PublishCensusForTest(census);

      Assert.AreEqual(3, pane.PagerPageCountForTest, "two and a half thousand names are three pages of a thousand");
      Assert.AreEqual("Page 1 of 3", pane.PagerTextForTest, "the arrows get a page number and a total beside them");
      Assert.AreEqual($"Showing 1 to {1000:N0} of {2500:N0} Identities", pane.TitleTextForTest,
                      "page one reads as a range, not as the whole list");

      pane.PagerPageForTest = 2;
      Assert.AreEqual("Page 3 of 3", pane.PagerTextForTest, "the arrows moved the pager by themselves; the number followed");
      Assert.AreEqual($"Showing {2001:N0} to {2500:N0} of {2500:N0} Identities", pane.TitleTextForTest,
                      "the last page ends where the list ends, and the title knows it moved without waiting for a build");

      // A row on this page is selected, then an ordinary census lands: the reader stays where they are.
      pane.SelectRowForTest("Name2100");
      pane.PublishCensusForTest(census);
      Assert.AreEqual(2, pane.PagerPageForTest, "a census that asks no question moves no one");
      Assert.AreEqual($"Showing {2001:N0} to {2500:N0} of {2500:N0} Identities", pane.TitleTextForTest);

      // Narrowing is a question, and the title says both numbers: what matched, and what the capture holds.
      pane.SetSearchTextForTest("Name0");
      Assert.AreEqual($"Showing 1 to {1000:N0} of {1000:N0} Identities, {2500:N0} total", pane.TitleTextForTest,
                      "the answer AND the capture: 'my name vanished' is usually 'my filter ate it'");
      Assert.AreEqual("Page 1 of 1", pane.PagerTextForTest);
    });
  }

  /*
   * The universe, at the door where a census becomes rows (docs/DesignNotes.md → "Identity Table paging"): a summon whose own
   * spelling names its owner is not listed at all, while the OWNER and any pet that genuinely needs one are. The strip counts what
   * the table can show, so no dial and no search promises a name nobody put in the list — an operator looking for "Kizant`s pet"
   * gets nothing back and the count says why there is nothing (it never claimed to hold it).
   */
  [TestMethod]
  public void ASummonThatSpellsItsOwnerIsNotInTheTableAtAll()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([
        CensusRow("Kizant", IdentityKind.Player, "R3-joinraid"),
        CensusRow("Kizant`s pet", IdentityKind.Pet, "R5-owner:Kizant"),
        CensusRow("Beorun`s warder", IdentityKind.Pet, "R5-owner:Beorun"),
        CensusRow("Puksuu", IdentityKind.Pet, "R24-petslot")]);

      CollectionAssert.AreEqual(new[] { "Kizant", "Puksuu" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "the two closed rows never arrive; the owner and the pet that needs an owner do");
      Assert.AreEqual($"Showing 1 to {2:N0} of {2:N0} Identities", pane.TitleTextForTest,
                      "total is the table's own universe, not everything the capture remembered");

      // The take-back exception, on the same surface: a hand-written verdict keeps its row even on a name that spells otherwise.
      pane.PublishCensusForTest([
        CensusRow("Kizant", IdentityKind.Player, "R3-joinraid"),
        new ClassificationReport.Row { Name = "Kizant`s pet", Kind = IdentityKind.Npc, Reason = "Manual", IsOperatorVerdict = true }]);
      CollectionAssert.AreEqual(new[] { "Kizant", "Kizant`s pet" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "a wrong click that left no row would be a permanent click");
    });
  }

  /*
   * The conditional half of the table's universe at the real door: an eye belonging to one of ours has no row, and gets it back the
   * moment the name inside it stops being one of ours. Headless NamesTableUniverseTest pins the arithmetic; this pins that the pane
   * consults the census's own kinds, which is the part a per-row predicate cannot do alone.
   */
  [TestMethod]
  public void AnEyeOfAPlayerHasNoRowAndComesBackWhenHeIsNotOne()
  {
    Sta.Run(() =>
    {
      var pane = CreatePane();
      pane.PublishCensusForTest([CensusRow("Kizant", IdentityKind.Player, "R3-joinraid"),
                                 CensusRow("Eye of Kizant", IdentityKind.Npc, "R6-npcdb")]);
      CollectionAssert.AreEqual(new[] { "Kizant" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "the eye is pointless while its master reads Player");

      pane.PublishCensusForTest([CensusRow("Kizant", IdentityKind.Npc, "R6-npcdb"),
                                 CensusRow("Eye of Kizant", IdentityKind.Npc, "R6-npcdb")]);
      CollectionAssert.AreEqual(new[] { "Eye of Kizant", "Kizant" }, pane.RowsForTest.Select(r => r.Name).ToList(),
                                "he stopped mapping to a player, so the eye is a name worth showing again");
    });
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

        /*
         * What is forbidden is a FILE (or the seam's internal name), never a bare store stem. The first version of this sweep
         * listed "players" and "npcs" and passed only until the vocabulary grew the sentences the evidence actually says —
         * "Attacks NPCs", "Attacks Players", "Damaged Players" — which are the app's OWN nouns (the style law: Player, Pet,
         * Mercenary, NPC, Spell, and never "mob" or "monster"). A stem cannot tell a filename from a person-word, so the
         * check names files: the extension catches every one of them however it is phrased, and the stems with no ".txt"
         * (identity-priors, identity-overrides, petmapping) are listed because the law is "do not point at a store an
         * operator cannot open from this window", which a nameless reference violates just as loudly. "registryseed" is in
         * the list because the comment above this loop has always demanded it and nothing asserted it: the seam's internal
         * name is not an answer about a name — the hover says "From the Old Verified List" or "In the Pet Map as X's".
         */
        foreach (var forbidden in new[]
                 {
                   ".txt", "npcs.txt", "players.txt", "spells.txt", "identity-priors", "identity-overrides", "petmapping",
                   "registryseed",
                 })
          Assert.IsFalse(provenance.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                         $"{kind}/{source} puts '{forbidden}' in the hover: {provenance}");
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
   * Name, Type, Class, Owner, Why. The order is the interaction: Name identifies, the next cells are the ones that answer with a
   * click, and WHY is the read-only sentence that explains them — which used to sit between the two things you edit.
   * Owner joined on 2026-10-09 when petmapping.txt froze and this window became the only door left to an owner (docs/DesignNotes.md).
   * Read inside Sta.Run like every DependencyObject (the grid's Columns belong to the thread that built them).
   */
  [TestMethod]
  public void TheColumnsGoNameTypeClassOwnerWhy()
  {

    var order = new List<string>();
    Sta.Run(() =>
    {
      foreach (var column in CreatePane().namesGrid.Columns) order.Add(column.MappingName ?? string.Empty);
    });

    CollectionAssert.AreEqual(new[] { "Name", "Type", "PlayerClass", "Owner", "Why" }, order,
                              $"column order changed: {string.Join(", ", order)}");
  }

  /*
   * A Pet row with nobody on it still answers "No Owner", and only a Pet row gets the pencil (PetOwnership.CanEditOwner): typing an owner
   * onto a raider asserts two things from one cell, which is what the fight grids' "Assign … as Pet of" is for. The write path and this
   * icon read the same recognizer, so an icon cannot exist where the guard would refuse.
   */
  [TestMethod]
  public void OnlyAPetRowCarriesAnOwnerPencilAndNobodySaysNothing()
  {
    var pet = NamesTable.RowFrom(new ClassificationReport.Row { Name = "Ziggy", Kind = IdentityKind.Pet });
    var raider = NamesTable.RowFrom(new ClassificationReport.Row { Name = "Beorun", Kind = IdentityKind.Player });

    Assert.AreEqual("No Owner", pet.Owner, "a pet with no owner printed an empty cell");
    Assert.IsTrue(pet.OwnerEditable);
    Assert.IsFalse(raider.OwnerEditable, "an owner pencil appeared on a raider");

    var mapped = NamesTable.RowFrom(new ClassificationReport.Row { Name = "Ziggy", Kind = IdentityKind.Pet, PetOwner = "Beorun" });
    Assert.AreEqual("Beorun", mapped.Owner);

    // A file from before the wording changed holds the old placeholder; the cell must not print it beside the new word.
    var legacy = NamesTable.RowFrom(new ClassificationReport.Row { Name = "Ziggy", Kind = IdentityKind.Pet, PetOwner = Labels.LegacyUnassigned });
    Assert.AreEqual(Labels.Unassigned, legacy.Owner);
  }

  [TestMethod]
  public void TheBlankClassPickTakesTheSavedClassBackAndReleasesTheEditor()
  {
    var seeded = false;
    var forgotten = false;
    var editorClosed = false;
    Sta.Run(() =>
    {
      seeded = PlayerRegistry.Instance.SetDefaultPlayerClass("Ann", "Wizard");
      var pane = CreatePane();
      pane.AcceptCensus([new ClassificationReport.Row { Name = "Ann", Kind = IdentityKind.Player, Class = "Wizard" }]);
      var row = pane.RowsForTest.Single();
      var box = new ComboBox { DataContext = row };
      pane.BeginCellEdit(row, NamesTable.ColumnClass, ["", "Wizard"], 1, box);
      box.SelectedIndex = 0;
      var handler = typeof(NamesTable).GetMethod("ClassCellEditorSelectionChanged",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new MissingMethodException("ClassCellEditorSelectionChanged");
      handler.Invoke(pane, [box,
        new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
          new[] { "Wizard" }, new[] { "" })]);
      forgotten = string.IsNullOrEmpty(PlayerRegistry.Instance.GetAssignedOrLastKnownPlayerClass("Ann"));
      editorClosed = !pane.Editors.IsOpen && string.IsNullOrEmpty(row.EditingColumn);
    });
    Assert.IsTrue(seeded, "control: the saved class exists before the gesture");
    Assert.IsTrue(forgotten, "the actual handler must pass the empty word through to the forget-class door");
    Assert.IsTrue(editorClosed, "the accepted blank pick ends the edit and releases its census hold");
  }

  // The two app-level StaticResource keys this pane's markup needs, stubbed for a headless test host — same shape as
  // FightTableStartupTest's. A Style whose TargetType is the element or a base of it; anything else throws on parse.
  private static NamesTable CreatePane()
  {
    // Resources and the pane belong to the SAME STA body, including when this test runs by itself.
    var application = Application.Current ?? new Application();
    application.Resources["EQIconStyle"] ??= new Style(typeof(Image));
    application.Resources["EQTitleStyle"] ??= new Style(typeof(ContentControl));
    return new NamesTable();
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
      Reason = "R4-spell:Boastful Bellow XLVII",
      ReasonDetail = "Boastful Bellow XLVII",
      HasFacts = true,
    });

    Assert.AreEqual("Player", bard.Type);
    /*
     * "Class Spell", which is what the cell says since the words were swept: R4's verdict means "this CAST a class rank",
     * while an R21 row reading plain "Spell" means "its NAME is a spell". Both said "Spell" for opposite reasons, and two
     * rows that mean opposite things in one word is the ambiguity the split removed — the tooltip names the actual cast.
     */
    Assert.AreEqual("Class Spell", bard.Why);

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
   * "Healed" is decided by BREADTH, so the number that justifies it is a headcount (docs/DesignNotes.md → "Breadth of evidence, measured" →
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
    StringAssert.Contains(healed.Provenance, "Healed by 20 Raiders");

    // A mob the raid keeps getting hit by AoE heals has no crowd to report - 0 means "never asked", not "nobody".
    var npc = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "A bone walker",
      Kind = IdentityKind.Npc,
      Reason = "R6-npcdb",
      HealedByCasters = 0,
      HasFacts = true,
    });

    // "NPC DB" since npcs.txt was renamed on screen: an operator calls it the DB, and the hover says "In the NPC DB".
    Assert.AreEqual("NPC DB", npc.Why);
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
    ClassificationCommands.ApplyVerdict("Nobody", IdentityKind.Npc);
    ClassificationCommands.ClearPrior(IdentityPriorStore.Instance, "Nobody");
    ClassificationCommands.ApplyVerdict("Nobody", IdentityKind.Unknown);
  }

  /*
   * The extra evidence lines ("one short phrase per line for each rule that applied", and what the capture watched the name do)
   * are built in Core — `Row.OtherEvidence` — so which phrases exist, in what order, and how they are capped is asserted by
   * EvidenceLinesTest in the portable assembly. What is THIS pane's behaviour, and therefore pinned here:
   *
   *   - the row's own proof line stays FIRST: the blink-read answer is the verdict's reason, and the rest reads downward;
   *   - the shape is additive — a row with nothing extra still hovers as exactly one line, which is what every other test in this
   *     file assumes and what most rows are;
   *   - head plus tail stays inside the ten-line budget (nine extra lines: asked for directly, because four cut a
   *     busy raider's own explanation in half);
   *   - the TYPE cell carries whatever word Core chose (a Spell row's direction: "NPC Spell" / "Player Spell"), and falls back to
   *     the plain kind word when a row was assembled by hand and says nothing.
   */
  [TestMethod]
  public void ExtraEvidenceRidesBelowTheProofLineInTheHover()
  {
    var spell = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "Strangle XVII Rk. III",
      Kind = IdentityKind.Spell,
      Reason = "R21-spellshape",
      HasFacts = true,
      TypeDisplay = IdentityVocabulary.TypeWordFor(IdentityKind.Spell, 240, 0),
      OtherEvidence = IdentityVocabulary.DamagedPlayersPhrase,
    });

    Assert.AreEqual("NPC Spell", spell.Type, "the direction word Core chose reaches the cell unchanged");

    var lines = spell.Provenance.Split('\n');
    Assert.AreEqual(2, lines.Length, $"head plus one clause: {spell.Provenance}");
    Assert.AreEqual(IdentityVocabulary.ProofText("R21-spellshape", IdentityKind.Spell), lines[0],
                    "the row's own proof line stays first — the rest is supporting evidence, not a replacement");

    var plain = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "A bone walker", Kind = IdentityKind.Npc, Reason = "R14-shape", HasFacts = true,
    });
    Assert.AreEqual("NPC", plain.Type, "a row assembled without a TypeDisplay still answers with the kind word");
    Assert.IsFalse(plain.Provenance.Contains("\n"), $"one claim and no direction hovers as one line: {plain.Provenance}");

    var busy = NamesTable.RowFrom(new ClassificationReport.Row
    {
      Name = "Frost", Kind = IdentityKind.Npc, Reason = "R9-charm", HasFacts = true,
      // Nine identical clauses: dedupe happens per DISTINCT phrase upstream, so this measures only the cap.
      OtherEvidence = string.Join("\n", Enumerable.Repeat(IdentityVocabulary.DamagedNpcsPhrase, 9)),
    });
    Assert.AreEqual(10, busy.Provenance.Split('\n').Length, "the hover budget is ten lines: the proof plus nine");
  }
}

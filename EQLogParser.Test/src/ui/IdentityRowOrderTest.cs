using Microsoft.VisualStudio.TestTools.UnitTesting;

using EQLogParser;

namespace EQLogParser.Tests.Ui;

/*
 * The Identity Table's sort, tested without a grid.
 *
 * The pane cannot demonstrate this itself on the machine the suite runs on (SfDataGrid needs Windows), and the field report
 * was exactly about order: "the indicator shows up correct for the name column but it doesnt stay sorted". So the RULE —
 * which column decides, what a tie does, where a newcomer lands — lives in Core (IdentityRowOrder) and is pinned here,
 * while NamesTable only mirrors the result into the collection it merges each census into.
 *
 * The laws, and why each one is a law rather than a preference:
 *
 *   - A tie breaks on NAME. Roughly three quarters of this table's rows share one Type word ("NPC"), so without a tiebreak
 *     the list is census order again with an arrow over it — the reported bug, smuggled back in through the column that
 *     happens to be clicked.
 *   - DESCENDING reverses the tiebreak too: a header click flips the display, not one field inside it.
 *   - Names compare WITHOUT case, because every name here arrives through CapitalizeFirst on one path and raw on another
 *     (the same law EntityTimeline and NamePool follow), and an ordinal sort would separate two spellings of one entity.
 *   - A newcomer lands at the index its key says — the specific failure: appended rows piled at the bottom of a live table
 *     for the rest of the session, in whatever order the derive thought of them.
 */
[TestClass]
public class IdentityRowOrderTest
{
  private sealed record Fake(string Name, string Type = "", string? PlayerClass = null, string Owner = "", string Why = "") : IIdentityRowKeys;

  [TestMethod]
  public void TheColumnsAreTheFiveWordsTheTableShows()
  {
    CollectionAssert.AreEqual(new[] { "Name", "Type", "Class", "Owner", "Why" }, IdentityRowOrder.Columns.ToList(),
                              "the sortable vocabulary, in header order: a sixth column has to say so here before it can sort");
    // The default is the FIRST column — which is what the pane's markup declares as its initial SortColumnDescription, so
    // an operator who never clicks opens a table read by name. ("Name" is literal here on purpose: the markup says Name.)
    Assert.AreEqual("Name", IdentityRowOrder.Columns[0], "the first header is the one the table opens sorted by");
    CollectionAssert.AreEqual(IdentityRowOrder.Columns.ToList(), new[] { IdentityRowOrder.DefaultColumn, "Type", "Class", "Owner", "Why" },
                              "and the default is that first column, so markup and rule cannot disagree about what 'nobody clicked' means");
  }

  [TestMethod]
  public void NobodyHavingClickedAnythingMeansNamesAscending()
  {
    // A descriptor this pane has never seen (null, or a word from another grid) takes the default rather than guessing.
    foreach (var column in new string?[] { null, "", "Damage", "Name" })
    {
      var rows = new List<Fake> { new("Cy"), new("Ann"), new("Bob") };
      rows.Sort(IdentityRowOrder.For<Fake>(column));
      CollectionAssert.AreEqual(new[] { "Ann", "Bob", "Cy" }, rows.Select(r => r.Name).ToList(),
                                $"unknown-or-default column \"{column}\" sorts by name");
    }
  }

  [TestMethod]
  public void NamesCompareWithoutCase()
  {
    var rows = new List<Fake> { new("beta"), new("Abby"), new("gamma") };
    rows.Sort(IdentityRowOrder.For<Fake>(IdentityRowOrder.NameColumn));

    CollectionAssert.AreEqual(new[] { "Abby", "beta", "gamma" }, rows.Select(r => r.Name).ToList(),
                              "capitalisation is not a sort key: the parser capitalizes one letter and other seams keep what the log wrote");
  }

  [TestMethod]
  public void ATieOnTheClickedColumnBreaksOnTheName()
  {
    var order = IdentityRowOrder.For<Fake>(IdentityRowOrder.TypeColumn);

    // The census emitted Zeta first; two rows share one Type word, so the NAME decides and the emission order is invisible.
    var rows = new List<Fake> { new("Zeta", "NPC"), new("Ann", "NPC"), new("Moyra", "Pet") };
    rows.Sort(order);

    CollectionAssert.AreEqual(new[] { "Ann", "Zeta", "Moyra" }, rows.Select(r => r.Name).ToList(),
                              "the majority of this table shares one Type word; without a name tiebreak the arrow would sit over census order");
  }

  [TestMethod]
  public void DescendingFlipsTheTiebreakAlongWithTheColumn()
  {
    var order = IdentityRowOrder.For<Fake>(IdentityRowOrder.TypeColumn, descending: true);

    var rows = new List<Fake> { new("Ann", "NPC"), new("Zeta", "NPC"), new("Moyra", "Player") };
    rows.Sort(order);

    CollectionAssert.AreEqual(new[] { "Moyra", "Zeta", "Ann" }, rows.Select(r => r.Name).ToList(),
                              "a second click flips the whole line — the names sharing a word flip too, not only the words");
  }

  [TestMethod]
  public void AColumnWithNoWordSortsAsEmpty()
  {
    var order = IdentityRowOrder.For<Fake>(IdentityRowOrder.ClassColumn);

    // Only Class can be absent (a Pet row has none, an unplaced name has none); it sorts as "" and does not throw.
    var rows = new List<Fake> { new("Ann"), new("Bram", "Player", "Warrior"), new("Cy") };
    rows.Sort(order);

    CollectionAssert.AreEqual(new[] { "Ann", "Cy", "Bram" }, rows.Select(r => r.Name).ToList(),
                              "no class is not an error and not the name: it sorts before every real class word");
  }

  [TestMethod]
  public void OwnerAndWhySortOnTheirOwnWords()
  {
    var byOwner = new List<Fake> { new("Ann", "Pet", Owner: "Zeru"), new("Bob", "Pet", Owner: "Aela"), new("Cy", "Pet", Owner: "No Owner") };
    byOwner.Sort(IdentityRowOrder.For<Fake>(IdentityRowOrder.OwnerColumn));
    CollectionAssert.AreEqual(new[] { "Bob", "Cy", "Ann" }, byOwner.Select(r => r.Name).ToList(),
                              "a summon's row sorts under its OWNER, and \"No Owner\" is a word like any other (it is the answer, not a blank)");

    var byWhy = new List<Fake> { new("Ann", "NPC", Why: "Who"), new("Bob", "NPC", Why: "Chat"), new("Cy", "NPC", Why: "Attacks NPCs") };
    byWhy.Sort(IdentityRowOrder.For<Fake>(IdentityRowOrder.WhyColumn));
    CollectionAssert.AreEqual(new[] { "Cy", "Bob", "Ann" }, byWhy.Select(r => r.Name).ToList(),
                              "the WHY cell sorts by the proof's two words, which is how you group everything the chat claimed");
  }

  [TestMethod]
  public void ANewcomerLandsWhereItsKeySays()
  {
    var order = IdentityRowOrder.For<Fake>(IdentityRowOrder.NameColumn);
    var listed = new List<Fake> { new("Ann"), new("Cy") };

    Assert.AreEqual(0, IdentityRowOrder.InsertIndex([], new Fake("Abel"), order), "an empty list takes it at zero");
    Assert.AreEqual(0, IdentityRowOrder.InsertIndex(listed, new Fake("Abel"), order), "before everything");
    Assert.AreEqual(1, IdentityRowOrder.InsertIndex(listed, new Fake("Bob"), order), "between the two rows it sorts between");
    Assert.AreEqual(2, IdentityRowOrder.InsertIndex(listed, new Fake("Dee"), order), "past everything it has");
  }

  [TestMethod]
  public void ANewcomerJoinsAfterTheRowsItTiesWith()
  {
    var order = IdentityRowOrder.For<Fake>(IdentityRowOrder.TypeColumn);
    var listed = new List<Fake> { new("Ann", "NPC"), new("Bob", "NPC") };

    // The duplicate ties Ann/NPC exactly, and Bob/NPC sorts AFTER it — so the answer is one, i.e. the newcomer lands
    // behind its equal rather than sliding in front of it or out to the end (2 would be past Bob, which compares greater).
    Assert.AreEqual(1, IdentityRowOrder.InsertIndex(listed, new Fake("Ann", "NPC"), order),
                    "the index is past every row that does not sort after it, so an existing neighbour never slides and an equal never jumps ahead");
  }

  [TestMethod]
  public void AnOrderCheckNoticesTheOnePairThatMoved()
  {
    var order = IdentityRowOrder.For<Fake>(IdentityRowOrder.NameColumn);

    Assert.IsTrue(IdentityRowOrder.IsOrdered([], order), "nothing to be out of order");
    Assert.IsTrue(IdentityRowOrder.IsOrdered([new Fake("Ann")], order));
    Assert.IsTrue(IdentityRowOrder.IsOrdered([new Fake("Ann"), new Fake("Bob"), new Fake("Cy")], order));
    Assert.IsFalse(IdentityRowOrder.IsOrdered([new Fake("Ann"), new Fake("Cy"), new Fake("Bob")], order),
                   "one swapped pair is enough: this is what a merge asks after every pass, so a sort cannot drift silently");
  }
}

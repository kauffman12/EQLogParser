using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/*
 * Which names the Identity Table lists AT ALL — the universe, decided before any dial or search is asked.
 *
 * The row a log settles by itself has nothing left to decide: "Kizant`s pet" is Pet because its spelling carries the summon word,
 * and R5 reads its owner out of that same string (docs/DesignNotes.md → "Identity Table paging", "A name that spells its summon
 * word has no row"). Such a row already has no Type pencil; this is the other half of the same fact — it is not a row either.
 * Everything else about the name is untouched, so these tests are as much about what must NOT change as what does.
 */
[TestClass]
public class NamesTableUniverseTest
{
  private static ClassificationReport.Row Row(string name, IdentityKind kind = IdentityKind.Pet,
    string reason = "R5-owner:Kizant", bool operatorVerdict = false)
    => new() { Name = name, Kind = kind, Reason = reason, IsOperatorVerdict = operatorVerdict };

  /*
   * The five summon words, spelled the way the client writes them (an upper-case Mount is the same shape). One of these in the
   * table would repeat a name the reader already knows and offer nothing but a blank Owner cell.
   */
  [TestMethod]
  public void ANameThatSpellsItsSummonWordHasNoRow()
  {
    foreach (var name in new[] { "Kizant`s pet", "Beorun`s warder", "Tuona`s ward", "Necra`s familiar", "Rider`s Mount" })
      Assert.IsFalse(NamesTableView.IsListed(Row(name)), $"{name} is Pet because its own spelling says so");
  }

  /*
   * The closed thing is the SUMMON WORD, not the apostrophe. A possessive that ends in something else was never claimed by R5, so
   * its kind came from a rule that can be wrong — and "…`s Ward of Destruction" is not "…`s ward": the suffix has to be the whole
   * tail (ClassificationRules.OwnerInName), which is exactly why those rows keep their row too.
   */
  [TestMethod]
  public void TheClosedShapeIsTheSummonWordNotTheApostrophe()
  {
    Assert.IsTrue(NamesTableView.IsListed(Row("The Eviscerator`s corpse", IdentityKind.Npc, "R6-npcdb")),
                  "a corpse fights as its master's pet and never gets a pet label; the name stays a row like any other");
    Assert.IsTrue(NamesTableView.IsListed(Row("Kazon Warlord`s Acolyte", IdentityKind.Npc, "R6-npcdb")));
    Assert.IsTrue(NamesTableView.IsListed(Row("Bulron`s Ward of Destruction")), "the suffix must be the whole tail to count");
  }

  /*
   * The two rows a person CAN act on: the owner (whose verdict decides which side the summon scores for) and a pet named by its
   * player, whose owner is genuinely unknown — measured, 20 of a capture's 160 Pet rows came from spell data or the petslot rule
   * rather than from a name. Hiding the closed half must not take these with it.
   */
  [TestMethod]
  public void AnOwnerNameAndACustomNamedPetBothStayListed()
  {
    Assert.IsTrue(NamesTableView.IsListed(Row("Kizant", IdentityKind.Player, "R3-joinraid")),
                  "the owner named inside the summon's name is its own row, and it is the row worth correcting");
    Assert.IsTrue(NamesTableView.IsListed(Row("Puksuu", IdentityKind.Pet, "R24-petslot")),
                  "a pet with a player-chosen name has an Owner pencil for a reason");
    Assert.IsTrue(NamesTableView.IsListed(Row("Shineko", IdentityKind.Pet, "R20-petspell")));
    Assert.IsTrue(NamesTableView.IsListed(Row("Useless", IdentityKind.Unknown, "")), "an unplaced name is the table's whole point");
  }

  /*
   * The one exception, and it is the take-back law rather than a hole: an operator's own verdict keeps its row even on a name that
   * spells otherwise, because a wrong click has to stay correctable by another one. Same reason such a row keeps its pencil
   * (IdentityVocabulary.CanOverrule).
   */
  [TestMethod]
  public void AnOperatorVerdictOnASpelledNameIsNeverHidden()
  {
    Assert.IsTrue(NamesTableView.IsListed(Row("Kizant`s pet", IdentityKind.Npc, "R10-manual", operatorVerdict: true)));
    Assert.IsTrue(IdentityVocabulary.CanOverrule("Kizant`s pet", "R10-manual"), "the pencil and the row agree");
  }

  /*
   * "Akini, Xanathan`s Warder" is one summon named after two masters: R5 drops the OWNER claim (a comma is not a raid member) but
   * keeps the kind at Certain. The kind is what settles the row's absence — an empty Owner cell is not a question anyone can answer
   * either, since the name says it belongs to two people.
   */
  [TestMethod]
  public void ASummonWithAnUnusableOwnerIsStillSettled()
    => Assert.IsFalse(NamesTableView.IsListed(Row("Akini, Xanathan`s Warder", IdentityKind.Pet, "R5-owner")));

  /*
   * Having no edit and having no row are two different decisions. A spell effect also has no pencil — typing Player onto a dot would
   * put it on the roster — but the name is still worth looking up, so R21 rows stay. If that ever changes it is a product call, not a
   * side effect of this predicate.
   */
  [TestMethod]
  public void ARowWithNoPencilIsNotAutomaticallyARowWithNoPlaceInTheList()
  {
    Assert.IsFalse(IdentityVocabulary.CanOverrule("Soul Vortex", "R21-spelleffect"), "as shipped today");
    Assert.IsTrue(NamesTableView.IsListed(Row("Soul Vortex", IdentityKind.Spell, "R21-spelleffect")));
  }

  /*
   * DISPLAY ONLY, asserted on the same data: the report row exists exactly as the rules wrote it while the table declines it. No
   * verdict, board number or ledger entry reads IsListed — this is the sentence that keeps a later change from turning a UI filter
   * into a classification decision.
   */
  [TestMethod]
  public void TheRulesStillClaimTheNameEvenThoughTheTableHasNoRow()
  {
    var row = Row("Kizant`s pet");
    Assert.AreEqual(IdentityKind.Pet, row.Kind, "the census carries the verdict unchanged…");
    Assert.AreEqual("R5-owner:Kizant", row.Reason, "…with its owner-naming provenance…");
    Assert.IsFalse(row.IsDisagreement, "…and nothing about it reads as a problem");
    Assert.IsFalse(NamesTableView.IsListed(row), "…which is precisely why the table has nowhere to put it");
  }

  /*
   * The boundary itself — a real pane, a real pager and a strip that counts what it can actually show — is asserted in
   * NamesTableTest (Windows-only assembly). Kept here as a pointer because the claim "display only" is worth naming: what this
   * file pins is the predicate, and what the pane test pins is that the predicate runs once, before the question, at the door.
   */
}

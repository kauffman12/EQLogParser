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
   * The eye rule is CONDITIONAL, and the operator's own caveat is why: `Eye of <name>` names its master the way a summon word does,
   * but NPCs in this game are legitimately named like an eye, so the shape alone proves nothing. An eye row goes only while the name
   * inside it reads Player, and comes back the moment it stops reading that way. (Measured: three captures, ~1.5 GB, zero eye-shaped
   * census rows — an eye is refused as a combatant before its name is interned. This lane exists in code, not yet in data.)
   */
  [TestMethod]
  public void AnEyeWhoseMasterIsOneOfOursHasNoRow()
  {
    var census = new[] { Row("Kizant", IdentityKind.Player, "R3-joinraid"), Row("Eye of Kizant", IdentityKind.Npc, "R6-npcdb") };
    CollectionAssert.AreEqual(new[] { "Kizant" }, NamesTableView.Universe(census).ConvertAll(r => r.Name).ToArray(),
                              "reading the name already says whose eye it is, and an eye can only ever be NPC anyway");

    // The same row with the master NOT one of ours: NPC, Pet, Unknown and absent all keep it. Shape alone decides nothing.
    foreach (var kind in new[] { IdentityKind.Npc, IdentityKind.Pet, IdentityKind.Merc, IdentityKind.Unknown })
      Assert.AreEqual(2, NamesTableView.Universe(new[] { Row("Zulmozar", kind, "R6-npcdb"), EyeOf("Zulmozar") }).Count,
                      $"{kind} is not a player, so the eye is a name worth showing");

    CollectionAssert.AreEqual(new[] { "Eye of Nobody" },
                              NamesTableView.Universe(new[] { EyeOf("Nobody") }).ConvertAll(r => r.Name).ToArray(),
                              "a master nobody has placed maps to no player, so nothing may be hidden");
  }

  /*
   * The un-hide law, both directions in one run: an operator who says "Kizant is an NPC" gets the eye row back on the next census,
   * and saying Player again takes it away. Hiding that never came back would be a filter with no handle.
   */
  [TestMethod]
  public void CallingTheMasterNPCBringsTheEyeBack()
  {
    var ours = new[] { Row("Kizant", IdentityKind.Player, "R3-joinraid"), EyeOf("Kizant") };
    Assert.AreEqual(1, NamesTableView.Universe(ours).Count);

    var theirs = new[] { Row("Kizant", IdentityKind.Npc, "R6-npcdb"), EyeOf("Kizant") };
    Assert.AreEqual(2, NamesTableView.Universe(theirs).Count, "he stopped mapping to a player, so the eye is worth a row again");

    Assert.AreEqual(1, NamesTableView.Universe(ours).Count, "and it goes again when he maps to one — no sticky hiding either way");
  }

  /*
   * The three eyes that are creatures rather than somebody's summon (the closed list ClassificationRules refuses) are not owned
   * shapes at all, so a raid member named Veeshan cannot make one of them disappear.
   */
  [TestMethod]
  public void TheRefusedThreeEyesAreNotOwnedShapes()
  {
    foreach (var name in new[] { "Veeshan", "Despair", "Mother" })
      Assert.AreEqual(2, NamesTableView.Universe(new[] { Row(name, IdentityKind.Player, "R2-who"), EyeOf(name) }).Count,
                      $"Eye of {name} is a creature, not an owned summon");
  }

  [TestMethod]
  public void AnOperatorVerdictOnAnEyeKeepsItsRowAndTheMasterIsMatchedWithoutCase()
  {
    Assert.AreEqual(2, NamesTableView.Universe(new[]
      { Row("Kizant", IdentityKind.Player, "R3-joinraid"), EyeOf("Kizant", operatorVerdict: true) }).Count,
      "a hand-written claim on the eye itself is a question the row has to stay visible to answer");

    Assert.AreEqual(1, NamesTableView.Universe(new[] { Row("Kizant", IdentityKind.Player, "R3-joinraid"), EyeOf("kizant") }).Count,
                    "the master text is copied out of a sentence, so it matches a name without case");
  }

  private static ClassificationReport.Row EyeOf(string master, bool operatorVerdict = false)
    => new() { Name = $"Eye of {master}", Kind = IdentityKind.Npc, Reason = "R6-npcdb", IsOperatorVerdict = operatorVerdict };

  /*
   * The boundary itself — a real pane, a real pager and a strip that counts what it can actually show — is asserted in
   * NamesTableTest (Windows-only assembly). Kept here as a pointer because the claim "display only" is worth naming: what this
   * file pins is the predicate, and what the pane test pins is that the predicate runs once, before the question, at the door.
   */
}

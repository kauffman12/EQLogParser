using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * Who-owns-a-summon, decided in Core: which rows may be edited, what their dropdown offers, what the cell prints, and what the two
 * writes mean (docs/DesignNotes.md → "petmapping.txt is a feed now").
 *
 * The vocabulary half matters more than it looks. `Labels.Unassigned` is a SENTINEL the boards ask about before folding damage onto a
 * person, and files already on disk hold the wording it carried before 2026-10-09 ("Unknown Pet Owner"), so every question about it goes
 * through `Labels.IsUnassignedOwner`. A comparison with `==` there does not throw — it puts a placeholder back on the board as a person
 * called "Unknown Pet Owner", which is the same class of bug as a name differing from itself by punctuation or case.
 */
[TestClass]
public sealed class PetOwnershipTest
{
  [TestMethod]
  public void TheWordForNobodyIsTwoWordsAndNotAFile()
  {
    // The cell word, from DisplayOf rather than the constant read beside its own definition: an unmapped pet prints it, and nothing
    // about the stores may leak a file name into the pane that asks about them.
    var entry = PetOwnership.DisplayOf(null);
    Assert.AreEqual("No Owner", entry, "the cell word the operator asked for is two words");
    Assert.IsFalse(entry.Contains(".txt", StringComparison.Ordinal));

    // Files hold the old wording; nothing writes it. Both must answer the same question, in either direction of the ask.
    Assert.IsTrue(Labels.IsUnassignedOwner(Labels.Unassigned));
    Assert.IsTrue(Labels.IsUnassignedOwner(Labels.LegacyUnassigned));
    Assert.IsTrue(Labels.IsUnassignedOwner("unknown pet owner"), "entity text arrives capitalized from the parser; the ask is case-free");

    Assert.IsFalse(Labels.IsUnassignedOwner("Ziggy"), "a real owner may never read as a hole in the map");
    Assert.IsFalse(Labels.IsUnassignedOwner(null));
    Assert.IsFalse(Labels.IsUnassignedOwner(string.Empty));
  }

  [TestMethod]
  public void OnlyAPetRowGetsThePencil()
  {
    // The operator's rule: an owner is a claim about a summon, so the pencil appears where the row already says Pet and nowhere else.
    Assert.IsTrue(PetOwnership.CanEditOwner(IdentityKind.Pet));

    Assert.IsFalse(PetOwnership.CanEditOwner(IdentityKind.Player));
    Assert.IsFalse(PetOwnership.CanEditOwner(IdentityKind.Merc));
    Assert.IsFalse(PetOwnership.CanEditOwner(IdentityKind.Npc));
    Assert.IsFalse(PetOwnership.CanEditOwner(IdentityKind.Spell));
    Assert.IsFalse(PetOwnership.CanEditOwner(IdentityKind.Unknown));
  }

  /*
   * The list holds VALID OWNERS ONLY (2026-11, on request): the trailing "No Owner" entry is gone because it wrote a NARROW forget
   * — the pair went, the Pet verdict stayed — beside the wider take-back in another column. Two doors that both read as "reset"
   * is exactly how this pane shipped before; now resetting means the calculator click in the Name column, and the dropdown offers
   * one thing only: who the summon belongs to.
   */
  [TestMethod]
  public void NoClearEntryIsOfferedTheTakeBackIsTheCalculator()
  {
    foreach (var current in new[] { "Ziggy", Labels.Unassigned, Labels.LegacyUnassigned, null })
    {
      var choices = PetOwnership.Choices(current, ["Beorun", "Ziggy", "Romance"]);

      Assert.AreEqual(0, choices.Count(c => PetOwnership.IsClear(c)),
          $"a clear entry crept back in for a row reading '{current ?? "<nobody>"}' — the take-back is the calculator click, never an owner to pick");
    }
  }

  /*
   * A value missing from its own dropdown reads as a blank cell, and a blank cell reads as a classifier bug — so an owner who is not on
   * this session's roster (disbanded raid, a name the capture never verifies) still has to be selectable. The placeholder is the exception
   * the other way: it is the ABSENCE of an answer, and a row storing one opens its list blank — nothing in the list can match what it
   * says, so preselecting anything would be a lie.
   */
  [TestMethod]
  public void ACurrentOwnerNobodyKnowsIsStillSelectable()
  {
    var choices = PetOwnership.Choices("Longgone", ["Beorun", "Ziggy"]);

    CollectionAssert.Contains(choices, "Longgone");
    Assert.AreEqual(1, choices.Count(c => string.Equals(c, "Longgone", StringComparison.OrdinalIgnoreCase)),
                    "the current answer appears once, whether or not the roster still knows it");
  }

  [TestMethod]
  public void AStoredPlaceholderOpensTheListBlankAndLeaksNoFileWording()
  {
    foreach (var legacy in new[] { Labels.LegacyUnassigned, Labels.Unassigned })
    {
      var choices = PetOwnership.Choices(legacy, ["Beorun", "Ziggy"]);

      Assert.AreEqual(0, choices.Count(c => PetOwnership.IsClear(c)),
          $"a row storing {legacy} must not get a word that writes nothing — its answer is nobody, and the list holds only people");
      Assert.IsFalse(choices.Any(c => c == Labels.LegacyUnassigned),
          "the wording an old file holds leaked into the dropdown");
      CollectionAssert.AreEqual(new[] { "Beorun", "Ziggy" }, choices,
          $"a row whose answer is nobody opens on its VALID OWNERS only — no word for the absence of one, and {legacy} not among them");
    }
  }

  [TestMethod]
  public void PlaceholdersAndPersonWordsAreNeverCandidates()
  {
    var choices = PetOwnership.Choices(null,
        ["Beorun", Labels.Unassigned, Labels.LegacyUnassigned, "yourself", "himself", "herself", Labels.Unk, "", "beorun"]);

    CollectionAssert.Contains(choices, "Beorun");
    Assert.AreEqual(1, choices.Count(c => string.Equals(c, "Beorun", StringComparison.OrdinalIgnoreCase)),
        "the same owner appeared twice because the roster spelled it differently");

    foreach (var junk in new[] { Labels.LegacyUnassigned, "yourself", "himself", "herself", Labels.Unk })
      Assert.IsFalse(choices.Contains(junk), $"{junk} was offered as somebody who can own a pet");

    Assert.AreEqual(0, choices.Count(c => PetOwnership.IsClear(c)), "and no take-back entry among them either");
  }

  /*
   * `IsClear` survives the removal of its only UI door: it is the VOCABULARY question "does this string name nobody", and the stored
   * wordings on disk still need an answer (a restored folder can hold rows written under either wording). What changed is who may ASK
   * it with a picked value — nobody can any more, because the list holds only candidates and the take-back moved to the calculator.
   */
  [TestMethod]
  public void ChoosingNoOwnerIsTheClearAndChoosingAPersonIsNot()
  {
    Assert.IsTrue(PetOwnership.IsClear(Labels.Unassigned));
    Assert.IsTrue(PetOwnership.IsClear(Labels.LegacyUnassigned), "a click made from a stale row still means take it back");
    Assert.IsTrue(PetOwnership.IsClear(null));

    Assert.IsFalse(PetOwnership.IsClear("Ziggy"));
    Assert.IsFalse(PetOwnership.IsClear("You"), "'You' is the local player, and a real owner");
  }

  /*
   * 'You' is not a hole in the map: petmapping.txt has always been allowed to say `Fluffy=You` for whoever is playing now (the import
   * resolves it to the open character), so the dropdown must offer it — the reflexive forms ("yourself", "himself") are what name nobody,
   * and those come from lines describing an act rather than a person.
   */
  [TestMethod]
  public void TheLocalPlayerIsAnOwnerCandidate()
  {
    var choices = PetOwnership.Choices(null, ["Beorun", "You"]);

    CollectionAssert.Contains(choices, "You", "'You' was refused as an owner, so a pet cannot be mapped to the character you are playing");
  }

  [TestMethod]
  public void TheCellSaysNoOwnerForEveryShapeOfNobody()
  {
    Assert.AreEqual(Labels.Unassigned, PetOwnership.DisplayOf(null), "an unmapped pet does not print a blank cell");
    Assert.AreEqual(Labels.Unassigned, PetOwnership.DisplayOf(string.Empty));
    Assert.AreEqual(Labels.Unassigned, PetOwnership.DisplayOf(Labels.Unassigned));
    Assert.AreEqual(Labels.Unassigned, PetOwnership.DisplayOf(Labels.LegacyUnassigned),
        "the wording an old file holds must not reach the screen after the word was changed");

    Assert.AreEqual("Ziggy", PetOwnership.DisplayOf("Ziggy"));
  }

  /*
   * The write side of the take-back: forgetting WHO owns a summon leaves the name a pet for this session (that is what keeps its damage
   * off a person's row and off the enemy column) while the durable pair goes — live map and the ledger's Owner column together.
   */
  [TestMethod]
  public void ForgettingAnOwnerKeepsTheNameAPet()
  {
    PlayerRegistry.Instance.Clear();

    PlayerRegistry.Instance.AddVerifiedPet("Fluffy");
    PlayerRegistry.Instance.AddPetToPlayer("Fluffy", "Ziggy");
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var owner) && owner == "Ziggy",
        "control: the pair never reached the ledger's ownership lane");

    PlayerRegistry.Instance.ForgetPetMapping("Fluffy");

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Fluffy"), "the durable mapping survived the take-back");
    Assert.IsFalse(PlayerRegistry.Instance.GetPetMappings().Any(m => m.Pet == "Fluffy"),
        "the live map still answers this session");
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPet("Fluffy"),
        "taking an owner away also stopped the name reading like a summon - 'I do not know whose' is not 'this is not a pet'");

    PlayerRegistry.Instance.Clear();
  }
}

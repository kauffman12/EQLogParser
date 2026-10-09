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
  public void TheTakeBackSaysNoOwner()
  {
    // What the pane actually receives as its take-back entry, rather than the constant read beside its own definition.
    var entry = PetOwnership.Choices(null, ["Ziggy"])[^1];
    Assert.AreEqual("No Owner", entry, "the dropdown entry the operator asked for is two words");
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

  [TestMethod]
  public void TheTakeBackIsTheLastEntryAndAppearsOnce()
  {
    var choices = PetOwnership.Choices("Ziggy", ["Beorun", "Ziggy", "Romance"]);

    Assert.AreEqual(Labels.Unassigned, choices[^1], "'No Owner' is the last thing in the list, so it never hides a real owner");
    Assert.AreEqual(1, choices.Count(c => PetOwnership.IsClear(c)),
        "the same click appeared twice in one dropdown; the stored wording and the new one are one entry");
  }

  /*
   * A value missing from its own dropdown reads as a blank cell, and a blank cell reads as a classifier bug — so an owner who is not on
   * this session's roster (disbanded raid, a name the capture never verifies) still has to be selectable. The placeholder is the exception
   * the other way: it is the ABSENCE of an answer, and its entry is the trailing "No Owner", not a stale string from a file.
   */
  [TestMethod]
  public void ACurrentOwnerNobodyKnowsIsStillSelectable()
  {
    var choices = PetOwnership.Choices("Longgone", ["Beorun", "Ziggy"]);

    CollectionAssert.Contains(choices, "Longgone");
    Assert.IsTrue(choices.IndexOf("Longgone") < choices.Count - 1, "the real answer sits above the take-back, not after it");
  }

  [TestMethod]
  public void AStoredPlaceholderIsNeverOfferedAsItsOwnAnswer()
  {
    foreach (var legacy in new[] { Labels.LegacyUnassigned, Labels.Unassigned })
    {
      var choices = PetOwnership.Choices(legacy, ["Beorun", "Ziggy"]);

      Assert.AreEqual(1, choices.Count(c => PetOwnership.IsClear(c)),
          $"a row storing {legacy} got a second entry for the same click");
      Assert.AreEqual(Labels.Unassigned, choices[^1], "the entry reads what it now says, whatever the file held");
      Assert.IsFalse(choices.Any(c => c == Labels.LegacyUnassigned),
          "the wording an old file holds leaked into the dropdown beside the current one");
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

    Assert.AreEqual(1, choices.Count(c => PetOwnership.IsClear(c)));
  }

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

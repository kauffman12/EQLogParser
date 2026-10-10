using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * Taking a summon's owner back, all the way down (docs/DesignNotes.md → "petmapping.txt is a feed now").
 *
 * The identity pane's Owner dropdown offers "No Owner" as a take-back that KEEPS the Pet verdict, and an operator who picks it and then
 * sees the cell unchanged has one question: did the write happen? Three places answer "who owns this", and the pane reads a different one
 * for each of its questions - the live map (what the dropdown preselects), the ledger's ownership lane (what the NEXT launch reads, and
 * what the boards fold by through `IdentityLookup`). A pair that leaves one store and not the others shows up as a cell disagreeing with
 * the damage row beside it, which reads exactly like a dead button.
 *
 * The durable half is the part a session cannot see: a flush and a reload must not resurrect the pair, because "it came back tomorrow" is
 * worse than "nothing happened" - it means the operator's decision was never a decision. (Re-importing the frozen petmapping.txt is the
 * only route back, and the per-folder gate holds: while the lane answers for ANY pet in this folder, nothing re-imports.)
 */
[TestClass]
public sealed class SummonOwnerTakeBackTest
{
  /*
   * A ledger belongs to a SERVER folder, so isolation here is a name no other test uses (the pattern IdentityPriorStoreTest and
   * RosterImportTest follow) rather than a directory this fixture owns. The store keeps the last `Init` answer, so Cleanup moves it
   * somewhere empty instead of trying to blank it.
   */
  private string _server = null!;

  [TestInitialize]
  public void Setup()
  {
    PlayerRegistry.Instance.Clear();
    _server = "takeback-" + Guid.NewGuid().ToString("N");
    IdentityPriorStore.Instance.Init(_server);
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityPriorStore.Instance.Init("takeback-cleanup-" + Guid.NewGuid().ToString("N"));
    PlayerRegistry.Instance.Clear();
  }

  /// <summary>The answer the Owner cell prints: the live map, which is what the dropdown preselects from too.</summary>
  private static string CellOwner(string name) => PetOwnership.DisplayOf(PlayerRegistry.Instance.GetPlayerFromPet(name));

  [TestMethod]
  public void ATakenBackOwnerLeavesEveryPlaceTheCellReads()
  {
    // The claim, through the seam both panes use.
    Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Kizant"), "control: the assignment itself was refused");
    Assert.AreEqual("Kizant", CellOwner("Picklepaw"), "the cell does not show the new owner");
    Assert.AreEqual("Kizant", IdentityLookup.OwnerOf("Picklepaw"), "the folding still answers nobody owns it");

    // The take-back, through the call the dropdown makes for its "No Owner" entry.
    PlayerRegistry.Instance.ForgetPetMapping("Picklepaw");

    Assert.AreEqual(Labels.Unassigned, CellOwner("Picklepaw"), "the cell still prints the owner that was just taken away");
    Assert.IsTrue(string.IsNullOrEmpty(IdentityLookup.OwnerOf("Picklepaw")),
        "the folding still routes this summon under the person the operator unmapped");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Picklepaw"),
        "the ledger's ownership lane kept the pair, so it returns on the next launch");

    // Durable: flush, reload the folder, and the pair must still be gone.
    IdentityPriorStore.Instance.FlushChanges();
    IdentityPriorStore.Instance.Init(_server);
    PlayerRegistry.Instance.Init();

    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Picklepaw"), "the flushed file wrote the pair back out");
    Assert.AreEqual(Labels.Unassigned, CellOwner("Picklepaw"), "a reload re-seeded the owner the operator removed");
  }

  /*
   * The take-back is about the PAIR, so everything else about the name survives it: the Pet verdict (the row must stay off the enemy
   * column and off a person's line), the Owner pencil, and the ability to name an owner again — including the same one, because nothing
   * about the pair is in effect any more. That last part is what `PetAssignment`'s "already in effect" refusal must not mistake for a
   * request to re-do: it asks both stores, and a take-back has to have emptied both for the refusal to be honest.
   */
  [TestMethod]
  public void LosingAnOwnerLeavesAPetThatCanBeOwnedAgain()
  {
    Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Kizant"));
    PlayerRegistry.Instance.ForgetPetMapping("Picklepaw");

    Assert.IsTrue(IdentityOverrideStore.Instance.TryGet("Picklepaw", out var kind) && kind == IdentityKind.Pet,
        "taking the owner back stopped the name reading like a summon - 'I do not know whose' is not 'this is not a pet'");
    Assert.IsTrue(PetOwnership.CanEditOwner(kind), "the row lost its Owner pencil along with its owner");

    Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Kizant"),
        "the seam refused to re-own a summon whose pair was taken back");
    Assert.AreEqual("Kizant", IdentityLookup.OwnerOf("Picklepaw"));
  }

  /*
   * The row a pane hands its dropdown is a SNAPSHOT, and a census merge replaces rows rather than mutating them (NamesTable.MergeInto
   * hands the selection — and every open editor — back by NAME). A captured instance taken before a merge carries the OLD owner into the
   * one guard that compares a pick against "what the cell already says", so a genuine change is refused as a no-op and nothing is written.
   * This pins the difference the re-pointing exists to keep honest: the stale answer and the fresh one must not be the same value here, or
   * the test cannot see the bug it guards.
   */
  [TestMethod]
  public void AStaleAnswerCannotRefuseARealChangeAsNoChange()
  {
    Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Kizant"));
    var captured = CellOwner("Picklepaw");
    Assert.AreEqual("Kizant", captured, "control: the snapshot did not capture the owner");

    PlayerRegistry.Instance.ForgetPetMapping("Picklepaw");
    var fresh = CellOwner("Picklepaw");

    Assert.AreNotEqual(captured, fresh,
        "a fresh read answers what the stale one does, so nothing on a pane could notice the pair is gone");
    Assert.IsFalse(string.Equals(fresh, "Kizant", StringComparison.OrdinalIgnoreCase),
        "the pick-against-cell comparison would still see Kizant and write nothing for a pick of No Owner");
  }
}

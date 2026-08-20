namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// Tests for TriggerStateDB re-import name matching (review item B4).
  /// Name-only matches must be kind-safe so a same-named leaf/folder sibling pair under one parent
  /// cannot erase or silently drop the other:
  /// - wrapper matched an existing trigger → overwrite branch ran with TriggerData == null (erased it)
  /// - leaf matched an existing folder    → no update/merge branch applied, leaf silently dropped
  /// Kind is defined by payload presence (trigger or overlay data), not by TriggerData alone —
  /// the overlay cases below pin the predicate's full contract. The coexistence outcome (both
  /// nodes inserted as siblings) is the pre-existing new-node path in Import(); only the matching
  /// decision changed and is what these tests pin down.
  /// </summary>
  [TestClass]
  public class TriggerStateDBImportMatchTest
  {
    private static TriggerNode ExistingTrigger(string name) => new() { Name = name, TriggerData = new Trigger() };

    // A folder node carries no TriggerData and no OverlayData.
    private static TriggerNode ExistingFolder(string name) => new() { Name = name };

    private static ExportTriggerNode IncomingLeaf(string name) => new()
    {
      Name = name,
      TriggerData = new Trigger { Pattern = "pattern", TextToDisplay = "text" }
    };

    private static ExportTriggerNode IncomingFolder(string name) => new()
    {
      Name = name,
      Nodes = [ IncomingLeaf("Child") ]
    };

    [TestMethod]
    public void MatchesReimportKind_TriggerLeafAgainstExistingTrigger_True()
    {
      Assert.IsTrue(TriggerStateDB.MatchesReimportKind(ExistingTrigger("Foo"), IncomingLeaf("Foo")));
    }

    [TestMethod]
    public void MatchesReimportKind_FolderWrapperAgainstExistingFolder_True()
    {
      Assert.IsTrue(TriggerStateDB.MatchesReimportKind(ExistingFolder("Foo"), IncomingFolder("Foo")));
    }

    // B4: a folder wrapper must never match an existing trigger, or the overwrite branch erases it.
    [TestMethod]
    public void MatchesReimportKind_FolderWrapperAgainstExistingTrigger_False()
    {
      Assert.IsFalse(TriggerStateDB.MatchesReimportKind(ExistingTrigger("Foo"), IncomingFolder("Foo")));
    }

    // Mirror case: an incoming leaf must never be absorbed by a same-named folder.
    [TestMethod]
    public void MatchesReimportKind_TriggerLeafAgainstExistingFolder_False()
    {
      Assert.IsFalse(TriggerStateDB.MatchesReimportKind(ExistingFolder("Foo"), IncomingLeaf("Foo")));
    }

    // Payload-presence, not TriggerData alone: an overlay leaf is still a leaf.
    [TestMethod]
    public void MatchesReimportKind_OverlayLeafAgainstExistingOverlay_True()
    {
      Assert.IsTrue(TriggerStateDB.MatchesReimportKind(
        new TriggerNode { Name = "Foo", OverlayData = new Overlay() },
        new ExportTriggerNode { Name = "Foo", OverlayData = new Overlay() }));
    }

    [TestMethod]
    public void MatchesReimportKind_OverlayLeafAgainstExistingFolder_False()
    {
      Assert.IsFalse(TriggerStateDB.MatchesReimportKind(
        ExistingFolder("Foo"),
        new ExportTriggerNode { Name = "Foo", OverlayData = new Overlay() }));
    }
  }
}

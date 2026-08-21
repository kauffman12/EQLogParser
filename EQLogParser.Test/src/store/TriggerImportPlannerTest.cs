using EQLogParser;

namespace EQLogParserTest
{
  /// <summary>
  /// Tests for TriggerImportPlanner — the matching + branch selection that TriggerStateDB.Import
  /// applies to LiteDB. Pure logic, runnable on any platform. Pins down review item B4: name-only
  /// matches must be kind-safe so a same-named leaf/folder sibling pair cannot erase or silently
  /// drop the other (wrapper matched a trigger → overwrite branch ran with TriggerData == null;
  /// leaf matched a folder → no branch applied, leaf dropped).
  /// </summary>
  [TestClass]
  public class TriggerImportPlannerTest
  {
    private static TriggerNode ExistingTrigger(string name, string? originalId = null) => new()
    {
      Id = Guid.NewGuid().ToString(),
      Name = name,
      OriginalId = originalId,
      TriggerData = new Trigger()
    };

    // A folder node carries no TriggerData and no OverlayData.
    private static TriggerNode ExistingFolder(string name) => new()
    {
      Id = Guid.NewGuid().ToString(),
      Name = name
    };

    private static ExportTriggerNode IncomingLeaf(string name, string? originalId = null) => new()
    {
      Name = name,
      OriginalId = originalId,
      TriggerData = new Trigger { Pattern = "pattern", TextToDisplay = "text" }
    };

    private static ExportTriggerNode IncomingFolder(string name, params ExportTriggerNode[] children) => new()
    {
      Name = name,
      Nodes = [.. children]
    };

    [TestMethod]
    public void Plan_LeafAgainstSameNameLeaf_UpdatesInPlace()
    {
      var existing = ExistingTrigger("Foo");
      var decision = TriggerImportPlanner.Plan([existing], IncomingLeaf("Foo"));

      Assert.AreEqual(ImportAction.UpdateInPlace, decision.Action);
      Assert.AreSame(existing, decision.Existing);
    }

    [TestMethod]
    public void Plan_FolderWrapperAgainstSameNameFolder_MergesIntoExisting()
    {
      var existing = ExistingFolder("Foo");
      var decision = TriggerImportPlanner.Plan([existing], IncomingFolder("Foo", IncomingLeaf("Bar")));

      Assert.AreEqual(ImportAction.MergeIntoFolder, decision.Action);
      Assert.AreSame(existing, decision.Existing);
    }

    // B4: a folder wrapper must never match an existing trigger, or the overwrite branch erases it.
    [TestMethod]
    public void Plan_FolderWrapperAgainstSameNameTrigger_InsertsNewFolder()
    {
      var decision = TriggerImportPlanner.Plan([ExistingTrigger("Foo")], IncomingFolder("Foo", IncomingLeaf("Bar")));

      Assert.AreEqual(ImportAction.InsertFolder, decision.Action);
      Assert.IsNull(decision.Existing);
    }

    // B4 mirror: an incoming leaf must never be absorbed by a same-named folder.
    [TestMethod]
    public void Plan_LeafAgainstSameNameFolder_InsertsNewLeaf()
    {
      var decision = TriggerImportPlanner.Plan([ExistingFolder("Foo")], IncomingLeaf("Foo"));

      Assert.AreEqual(ImportAction.InsertLeaf, decision.Action);
      Assert.IsNull(decision.Existing);
    }

    [TestMethod]
    public void Plan_DifferentName_InsertsNewNode()
    {
      Assert.AreEqual(ImportAction.InsertLeaf,
        TriggerImportPlanner.Plan([ExistingTrigger("Foo")], IncomingLeaf("Bar")).Action);
      Assert.AreEqual(ImportAction.InsertFolder,
        TriggerImportPlanner.Plan([ExistingTrigger("Foo")], IncomingFolder("Bar", IncomingLeaf("Baz"))).Action);
    }

    // NAG allows duplicate display names for distinct triggers: matching by name alone would
    // collapse them into one node on re-import.
    [TestMethod]
    public void Plan_OriginalId_SameNameDifferentId_InsertsNewLeaf()
    {
      var decision = TriggerImportPlanner.Plan([ExistingTrigger("Foo", "id-1")], IncomingLeaf("Foo", "id-2"));

      Assert.AreEqual(ImportAction.InsertLeaf, decision.Action);
      Assert.IsNull(decision.Existing);
    }

    [TestMethod]
    public void Plan_OriginalId_SameNameSameId_UpdatesInPlace()
    {
      var existing = ExistingTrigger("Foo", "id-1");
      var decision = TriggerImportPlanner.Plan([existing], IncomingLeaf("Foo", "id-1"));

      Assert.AreEqual(ImportAction.UpdateInPlace, decision.Action);
      Assert.AreSame(existing, decision.Existing);
    }

    // The importer renames same-name collisions ("Foo" → "Foo (2)"), so on re-import the stored
    // name may differ from the source name. Matching must survive that via the OriginalId alone,
    // otherwise every re-import inserts a new duplicate sibling.
    [TestMethod]
    public void Plan_OriginalId_RenamedExistingNode_UpdatesInPlace()
    {
      var existing = ExistingTrigger("Foo (2)", "id-1");
      var decision = TriggerImportPlanner.Plan([existing], IncomingLeaf("Foo", "id-1"));

      Assert.AreEqual(ImportAction.UpdateInPlace, decision.Action);
      Assert.AreSame(existing, decision.Existing);
    }

    // One NAG trigger can produce several sibling nodes sharing ONE OriginalId (phrase + timer
    // variants, counter resets). Inside that family the name is the stable discriminator — the
    // importer's deterministic "(n)" suffixes (UniquifySiblingNames) survive re-imports. Matching
    // by id alone would let every incoming member overwrite the first sibling found.
    [TestMethod]
    public void Plan_OriginalId_SharedIdFamily_MatchesByName()
    {
      var first = ExistingTrigger("P", "id-x");
      var timer = ExistingTrigger("P (Timer 2)", "id-x");
      var decision = TriggerImportPlanner.Plan([first, timer], IncomingLeaf("P (Timer 2)", "id-x"));

      Assert.AreEqual(ImportAction.UpdateInPlace, decision.Action);
      Assert.AreSame(timer, decision.Existing);
    }

    // A family member the user renamed no longer matches by name. Inside a shared-id family we
    // must not guess — insert a visible new sibling instead of overwriting another member's data.
    [TestMethod]
    public void Plan_OriginalId_SharedIdFamily_RenamedMember_InsertsNewLeaf()
    {
      var first = ExistingTrigger("P", "id-x");
      var timer = ExistingTrigger("My Timer", "id-x");
      var decision = TriggerImportPlanner.Plan([first, timer], IncomingLeaf("P (Timer 2)", "id-x"));

      Assert.AreEqual(ImportAction.InsertLeaf, decision.Action);
      Assert.IsNull(decision.Existing);
    }

    // First import of a shared-id family: the first member is already inserted into the live
    // sibling set before the second is planned. The batch flag tells the planner the id occurs
    // more than once this run, so the name must disambiguate even with exactly one stored sibling.
    [TestMethod]
    public void Plan_OriginalId_SharedIdInBatch_SingleStoredSibling_InsertsNewLeaf()
    {
      var first = ExistingTrigger("P", "id-x");
      var decision = TriggerImportPlanner.Plan([first], IncomingLeaf("P (Timer 2)", "id-x"),
        new HashSet<string> { "id-x" });

      Assert.AreEqual(ImportAction.InsertLeaf, decision.Action);
      Assert.IsNull(decision.Existing);
    }

    // OriginalId matching is identity-based: a node without an OriginalId never matches an
    // incoming node that carries one.
    [TestMethod]
    public void Plan_OriginalId_IgnoresNodeWithoutOriginalId()
    {
      var decision = TriggerImportPlanner.Plan([ExistingTrigger("Foo")], IncomingLeaf("Foo", "id-1"));

      Assert.AreEqual(ImportAction.InsertLeaf, decision.Action);
      Assert.IsNull(decision.Existing);
    }

    [TestMethod]
    public void Plan_FolderWrapperWithoutChildren_MatchesFolder_Skips()
    {
      var decision = TriggerImportPlanner.Plan([ExistingFolder("Foo")], IncomingFolder("Foo"));

      Assert.AreEqual(ImportAction.Skip, decision.Action);
    }

    [TestMethod]
    public void Plan_MultipleSameNameSiblings_FirstMatchWins()
    {
      var first = ExistingTrigger("Foo");
      var second = ExistingTrigger("Foo");
      var decision = TriggerImportPlanner.Plan([first, second], IncomingLeaf("Foo"));

      Assert.AreEqual(ImportAction.UpdateInPlace, decision.Action);
      Assert.AreSame(first, decision.Existing);
    }

    // The kind predicate keys on payload presence (trigger or overlay data), not on
    // TriggerData alone — an overlay leaf is still a leaf.
    [TestMethod]
    public void MatchesReimportKind_TriggerLeafAgainstExistingTrigger_True()
    {
      Assert.IsTrue(TriggerImportPlanner.MatchesReimportKind(ExistingTrigger("Foo"), IncomingLeaf("Foo")));
    }

    [TestMethod]
    public void MatchesReimportKind_FolderWrapperAgainstExistingFolder_True()
    {
      Assert.IsTrue(TriggerImportPlanner.MatchesReimportKind(ExistingFolder("Foo"), IncomingFolder("Foo", IncomingLeaf("Bar"))));
    }

    [TestMethod]
    public void MatchesReimportKind_FolderWrapperAgainstExistingTrigger_False()
    {
      Assert.IsFalse(TriggerImportPlanner.MatchesReimportKind(ExistingTrigger("Foo"), IncomingFolder("Foo", IncomingLeaf("Bar"))));
    }

    [TestMethod]
    public void MatchesReimportKind_TriggerLeafAgainstExistingFolder_False()
    {
      Assert.IsFalse(TriggerImportPlanner.MatchesReimportKind(ExistingFolder("Foo"), IncomingLeaf("Foo")));
    }

    [TestMethod]
    public void MatchesReimportKind_OverlayLeafAgainstExistingOverlay_True()
    {
      var existing = new TriggerNode { Name = "Foo", OverlayData = new Overlay() };
      var incoming = new ExportTriggerNode { Name = "Foo", OverlayData = new Overlay() };

      Assert.IsTrue(TriggerImportPlanner.MatchesReimportKind(existing, incoming));
    }

    [TestMethod]
    public void MatchesReimportKind_OverlayLeafAgainstExistingFolder_False()
    {
      var incoming = new ExportTriggerNode { Name = "Foo", OverlayData = new Overlay() };

      Assert.IsFalse(TriggerImportPlanner.MatchesReimportKind(ExistingFolder("Foo"), incoming));
    }
  }
}

namespace EQLogParser
{
  /* File-driven tests against a real NAG database dump under local/nag/ (gitignored). They skip
   * cleanly when the dump is absent so CI on a fresh checkout stays green. The dump exercises the
   * full NAG import pipeline: real trigger/overlay databases plus files-database.json for audio
   * name resolution — no Windows required now that NagUtil lives in Core. */
  [TestClass]
  public sealed class NagStoreRealDataTest : TempDirFixture
  {
    /// <summary>Loads the real NAG dump if present. Returns false (test skips) when it is not.</summary>
    private static bool TryLoadNagDump(out string databaseDirectory, out string? triggerJson, out string? overlayJson)
    {
      databaseDirectory = TestTemp.RepoFile("local/nag");
      var triggers = Path.Combine(databaseDirectory, "trigger-database.json");
      var overlays = Path.Combine(databaseDirectory, "overlays-database.json");

      if (!File.Exists(triggers) || !File.Exists(overlays))
      {
        triggerJson = null;
        overlayJson = null;
        Console.WriteLine($"skipping — optional NAG dump missing: {databaseDirectory}");
        return false;
      }

      triggerJson = File.ReadAllText(triggers);
      overlayJson = File.ReadAllText(overlays);
      return true;
    }

    private TriggerStateDB NewStore()
    {
      var dir = NewTempDir();
      return new TriggerStateDB(Path.Combine(dir, "test.db"), applyLegacyUpgrades: false);
    }

    [TestMethod]
    public void RealNagDump_ConvertTriggers_ProducesRootWrappedTree()
    {
      if (!TryLoadNagDump(out var dir, out var json, out var unusedOverlays))
      {
        return;
      }

      // The databaseDirectory arg lets NagUtil resolve audio names via files-database.json.
      var (nodes, results) = NagUtil.ConvertTriggers(json, dir);

      Assert.AreEqual(1, nodes.Count, "expected the single root-wrapped export shape");
      var root = nodes[0];
      Assert.IsNull(root.TriggerData, "root wrapper must not be a trigger leaf");
      Assert.IsTrue(root.Nodes is { Count: > 0 }, "real dump converted to an empty tree");
      Assert.IsTrue(results.Count > 0, "no import results reported for a non-empty database");

      var skipped = results.Count(r => r.Status == "Skipped");
      Console.WriteLine($"NAG dump: {root.Nodes.Count} top-level nodes, {results.Count} triggers converted, {skipped} skipped");
    }

    [TestMethod]
    public async Task RealNagDump_ImportTriggers_IsIdempotent()
    {
      if (!TryLoadNagDump(out var dir, out var json, out _))
      {
        return;
      }

      var (nodes, _) = NagUtil.ConvertTriggers(json, dir);
      Assert.AreEqual(1, nodes.Count);

      var db = NewStore();
      await using var disposed1 = db;
      var (root, _, _) = await db.GetTriggerTree("P1");

      // first import materializes the full structure
      await db.ImportTriggers(root, nodes);
      var (_, allNodes, _) = await db.GetTriggerTree("P1");
      Assert.IsTrue(allNodes.Count(n => n.TriggerData != null) > 0, "real NAG data imported no trigger leaves");
      var countAfterFirst = allNodes.Count;

      // Re-importing the same database must be idempotent (no duplicates, no drops).
      // Note: ConvertTriggers uniquifies NAG's duplicate sibling names first — without that
      // normalization re-imports both grow unbounded and leave same-name sibling collisions.
      await db.ImportTriggers(root, nodes);
      var (_, allNodes2, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(countAfterFirst, allNodes2.Count);

      // B4 invariant against real data: no parent may contain two children that are both trigger
      // leaves with the same name (a cross-kind collision would produce exactly this)
      var collisions = allNodes2
        .GroupBy(n => n.Parent)
        .Where(g => g.Key is not null)
        .SelectMany(g => g
          .Where(n => n.TriggerData != null && n.OverlayData == null)
          .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
          .Where(c => c.Count() > 1))
        .ToList();
      Assert.AreEqual(0, collisions.Count, "duplicate same-name trigger leaves under one parent");
    }

    [TestMethod]
    public async Task RealNagDump_ImportOverlays_IsIdempotent()
    {
      if (!TryLoadNagDump(out var unusedDir, out var unusedTriggers, out var json))
      {
        return;
      }

      var overlays = NagUtil.ConvertOverlays(json, out var skippedFct, out var notes);
      Assert.IsTrue(overlays.Count > 0, "real overlay database converted to zero nodes");
      Console.WriteLine($"NAG dump: {overlays.Count} overlays, {skippedFct} FCT overlays skipped, {notes.Count} fidelity notes");

      var db = NewStore();
      await using var disposed2 = db;

      await db.ImportOverlays(overlays);
      var (_, allNodes, _) = await db.GetOverlayTree();
      Assert.IsTrue(allNodes.Any(n => n.OverlayData != null));
      var countAfterFirst = allNodes.Count;

      await db.ImportOverlays(overlays);
      var (_, allNodes2, _) = await db.GetOverlayTree();
      Assert.AreEqual(countAfterFirst, allNodes2.Count);
    }
  }
}

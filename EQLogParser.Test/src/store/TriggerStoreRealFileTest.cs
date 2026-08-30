using System.IO.Compression;
using System.Text.Json;

namespace EQLogParser
{
  /* File-driven round-trip tests against real export files the user keeps under local/ (gitignored).
   * They skip cleanly when the fixture is absent so CI on a fresh checkout stays green. */
  [TestClass]
  public sealed class TriggerStoreRealFileTest : TempDirFixture
  {
    private TriggerStateDB NewStore()
    {
      var dir = NewTempDir();
      return new TriggerStateDB(Path.Combine(dir, "test.db"), applyLegacyUpgrades: false);
    }

    // Mirrors TriggerUtil.ProcessImportFile's tgf/ogf handling (gzip + JSON list of export nodes)
    private static List<ExportTriggerNode> ReadExport(string path)
    {
      using var fs = File.OpenRead(path);
      using var decompressionStream = new GZipStream(fs, CompressionMode.Decompress);
      using var reader = new StreamReader(decompressionStream);
      var json = reader.ReadToEndAsync().GetAwaiter().GetResult();
      var nodes = JsonSerializer.Deserialize<List<ExportTriggerNode>>(json, new JsonSerializerOptions { IncludeFields = true });
      if (nodes is null) throw new InvalidOperationException($"fixture '{path}' did not deserialize to a node list");
      return nodes;
    }

    [TestMethod]
    public async Task Import_WizardTgfGz_RoundTrips()
    {
      var path = TestTemp.RepoFile("local/wizard.tgf.gz");
      if (!File.Exists(path))
      {
        Console.WriteLine($"skipping — optional fixture missing: {path}");
        return; // CI-safe skip
      }

      var data = ReadExport(path);
      Assert.IsTrue(data.Count > 0, "fixture deserialized to zero nodes — format mismatch?");

      var db = NewStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");

      // first import materializes the full structure
      await db.ImportTriggers(root, data);
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      var leaves = nodes.Count(n => n.TriggerData != null);
      Assert.IsTrue(leaves > 0);
      var countAfterFirst = nodes.Count;

      // re-importing the same file must be idempotent (no duplicates, no drops)
      await db.ImportTriggers(root, data);
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(countAfterFirst, nodes2.Count);

      // B4 invariant against real data: no parent may contain two children that are both trigger
      // leaves with the same name (a cross-kind collision would produce exactly this)
      var collisions = nodes2
        .GroupBy(n => n.Parent)
        .Where(g => g.Key is not null)
        .SelectMany(g => g
          .Where(n => n.TriggerData != null && n.OverlayData == null)
          .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
          .Where(c => c.Count() > 1))
        .ToList();
      Assert.AreEqual(0, collisions.Count, "duplicate same-name trigger leaves under one parent");
    }

    /// <summary>A large real export (467 triggers / 249 folders, depth-9 nesting): import twice and
    /// require full idempotency plus the B4 no-collision invariant, with a regex pattern round-trip.</summary>
    [TestMethod]
    public async Task Import_AllraidTgfGz_RoundTrips()
    {
      var path = TestTemp.RepoFile("local/allraid.tgf.gz");
      if (!File.Exists(path))
      {
        Console.WriteLine($"skipping — optional fixture missing: {path}");
        return; // CI-safe skip
      }

      var data = ReadExport(path);

      var db = NewStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");

      await db.ImportTriggers(root, data);
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      // expected total: everything under the exported top-level wrapper(s)
      int CountAll(List<ExportTriggerNode> list) => list.Sum(n => 1 + CountAll(n.Nodes ?? []));
      Assert.AreEqual(CountAll(data[0].Nodes ?? []), nodes.Count);

      // re-import must be exactly idempotent on this scale
      await db.ImportTriggers(root, data);
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(nodes.Count, nodes2.Count);

      // B4 invariant at depth: no parent may hold two same-named trigger leaves
      var collisions = nodes2
        .GroupBy(n => n.Parent)
        .Where(g => g.Key is not null)
        .SelectMany(g => g.Where(n => n.TriggerData != null && n.OverlayData == null)
          .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(c => c.Count() > 1))
        .ToList();
      Assert.AreEqual(0, collisions.Count, "duplicate same-name trigger leaves under one parent");

      // regex patterns must survive the round-trip byte-for-byte (escaped dots etc.)
      StringAssert.Contains(
        string.Join("\n", nodes2.Select(n => n.TriggerData?.Pattern).Where(p => p != null)),
        "^Dusk recovers from its confusion\\.$");
    }

    [TestMethod]
    public async Task Import_WizardOgfGz_RoundTrips()
    {
      var path = TestTemp.RepoFile("local/wizard.ogf.gz");
      if (!File.Exists(path))
      {
        Console.WriteLine($"skipping — optional fixture missing: {path}");
        return; // CI-safe skip
      }

      var data = ReadExport(path);
      Assert.IsTrue(data.Count > 0, "fixture deserialized to zero nodes — format mismatch?");

      var db = NewStore();
      await using var _ = db;

      await db.ImportOverlays(data);
      var (_, nodes, _) = await db.GetOverlayTree();
      var countAfterFirst = nodes.Count;
      Assert.IsTrue(nodes.Any(n => n.OverlayData != null));

      await db.ImportOverlays(data);
      var (_, nodes2, _) = await db.GetOverlayTree();
      Assert.AreEqual(countAfterFirst, nodes2.Count);
    }

    /// <summary>Real timer-overlay export: the stored id survives insert (re-import matching depends
    /// on it) and re-import is duplicate-free on top of the two bootstrap default overlays.</summary>
    [TestMethod]
    public async Task Import_CooldownOverlayOgfGz_RoundTrips()
    {
      var path = TestTemp.RepoFile("local/cooldownOverlay.ogf.gz");
      if (!File.Exists(path))
      {
        Console.WriteLine($"skipping — optional fixture missing: {path}");
        return; // CI-safe skip
      }

      var data = ReadExport(path);
      var overlayId = data[0].Nodes?.Single(n => n.OverlayData != null)?.Id;
      Assert.IsFalse(string.IsNullOrEmpty(overlayId), "real .ogf overlays export their stored id");

      var db = NewStore();
      await using var _ = db;

      await db.ImportOverlays(data);
      var (_, nodes, _) = await db.GetOverlayTree();
      // two bootstrap defaults + the one imported overlay
      Assert.AreEqual(3, nodes.Count);
      Assert.AreEqual("Cooldown Overlay", nodes.Single(n => n.Id == overlayId).Name);

      await db.ImportOverlays(data);
      var (_, nodes2, _) = await db.GetOverlayTree();
      Assert.AreEqual(3, nodes2.Count); // id-matched update in place — no duplicate
    }
  }
}

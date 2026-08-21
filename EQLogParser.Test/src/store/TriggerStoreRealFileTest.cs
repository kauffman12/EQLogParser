using System.IO.Compression;
using System.Text.Json;

namespace EQLogParser
{
  /* File-driven round-trip tests against real export files the user keeps under local/ (gitignored).
   * They skip cleanly when the fixture is absent so CI on a fresh checkout stays green. */
  [TestClass]
  public sealed class TriggerStoreRealFileTest
  {
    private readonly List<string> _dirs = [];

    [TestCleanup]
    public void Cleanup()
    {
      foreach (var dir in _dirs)
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch
        {
          // best effort
        }
      }
    }

    /// <summary>Finds a file under the repository root (works no matter where the test bin lives).</summary>
    private static string FindRepoFile(string relativePath)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln")))
      {
        dir = dir.Parent;
      }

      return dir is null ? Path.Combine(Directory.GetCurrentDirectory(), relativePath) : Path.Combine(dir.FullName, relativePath);
    }

    private TriggerStateDB NewStore()
    {
      var dir = Directory.CreateDirectory(Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"))).FullName;
      _dirs.Add(dir);
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
      var path = FindRepoFile("local/wizard.tgf.gz");
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

    [TestMethod]
    public async Task Import_WizardOgfGz_RoundTrips()
    {
      var path = FindRepoFile("local/wizard.ogf.gz");
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
  }
}

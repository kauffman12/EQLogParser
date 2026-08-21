using EQLogParser;
using System.IO.Compression;
using System.Text.Json;

namespace EQLogParserTest
{
  /* Deterministic import tests against the checked-in mini fixtures under data/ (copied to
   * bin as mini-data\). They are subsets of real user exports — mini.tgf.gz is the '25th
   * Anniversary' raid section from a full raid trigger file, mini.ogf.gz the real 'Cooldown
   * Overlay' plus one text overlay — so CI exercises the same field shapes (timer/end-early/
   * warning slots, regex patterns with escaped dots, overlay ids) without depending on the
   * full, gitignored files under local/. */
  [TestClass]
  public sealed class MiniFixtureImportTest
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

    private static string FixturePath(string name)
    {
      var path = Path.Combine(AppContext.BaseDirectory, "mini-data", name);
      if (!File.Exists(path)) throw new InvalidOperationException($"checked-in fixture missing: {path}");
      return path;
    }

    private static List<ExportTriggerNode> ReadFixture(string name)
    {
      using var fs = File.OpenRead(FixturePath(name));
      using var decompressionStream = new GZipStream(fs, CompressionMode.Decompress);
      using var reader = new StreamReader(decompressionStream);
      var json = reader.ReadToEndAsync().GetAwaiter().GetResult();
      var nodes = JsonSerializer.Deserialize<List<ExportTriggerNode>>(json, new JsonSerializerOptions { IncludeFields = true });
      if (nodes is null) throw new InvalidOperationException($"fixture '{name}' did not deserialize to a node list");
      return nodes;
    }

    private TriggerStateDB NewStore()
    {
      var dir = Directory.CreateDirectory(Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"))).FullName;
      _dirs.Add(dir);
      return new TriggerStateDB(Path.Combine(dir, "test.db"), applyLegacyUpgrades: false);
    }

    [TestMethod]
    public async Task MiniTgf_ImportRoundTrips_AreIdempotent()
    {
      var data = ReadFixture("mini.tgf.gz");

      var db = NewStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");

      int CountAll(List<ExportTriggerNode> list) => list.Sum(n => 1 + CountAll(n.Nodes ?? []));
      var expected = CountAll(data[0].Nodes ?? []);

      await db.ImportTriggers(root, data);
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(expected, nodes.Count);
      Assert.IsTrue(nodes.Any(n => n.TriggerData != null), "no trigger leaves imported");

      // re-importing the same file must not duplicate or drop anything
      await db.ImportTriggers(root, data);
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(expected, nodes2.Count);

      // same-name siblings under DIFFERENT parents are normal in real data and must stay apart —
      // 'Warning' exists under the Duck, Tree Hugger and Trash Cleanup event folders
      var warnings = nodes2.Where(n => n.Name == "Warning" && n.TriggerData != null).ToList();
      Assert.AreEqual(3, warnings.Count);
      var parents = warnings.Select(w => w.Parent).Distinct(StringComparer.Ordinal).ToList();
      Assert.AreEqual(3, parents.Count);

      // regex patterns with escaped dots round-trip byte-for-byte
      StringAssert.Contains(
        string.Join("\n", nodes2.Select(n => n.TriggerData?.Pattern).Where(p => p != null)),
        "^Everyone feels a compulsion to duck\\. If enough of you do not do so, you will all suffer from distress\\.$");

      // no parent may hold two same-named trigger leaves (the B4 collision invariant)
      var collisions = nodes2
        .GroupBy(n => n.Parent)
        .Where(g => g.Key is not null)
        .SelectMany(g => g.Where(n => n.TriggerData != null && n.OverlayData == null)
          .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(c => c.Count() > 1))
        .ToList();
      Assert.AreEqual(0, collisions.Count, "duplicate same-name trigger leaves under one parent");
    }

    [TestMethod]
    public async Task MiniOgf_ImportRoundTrips_AreIdempotentById()
    {
      var data = ReadFixture("mini.ogf.gz");
      var fixtureIds = (data[0].Nodes ?? [])
        .Where(n => n.OverlayData != null)
        .Select(n => n.Id)
        .Where(id => id != null)
        .ToList();
      Assert.AreEqual(2, fixtureIds.Count);

      var db = NewStore();
      await using var _ = db;

      await db.ImportOverlays(data);
      var (_, nodes, _) = await db.GetOverlayTree();
      // two bootstrap defaults + the two imported overlays
      Assert.AreEqual(4, nodes.Count);
      foreach (var id in fixtureIds)
      {
        Assert.IsTrue(nodes.Any(n => n.Id == id), $"imported overlay lost its stored id: {id}");
      }

      await db.ImportOverlays(data);
      var (_, nodes2, _) = await db.GetOverlayTree();
      Assert.AreEqual(4, nodes2.Count); // id-matched updates in place — no duplicates
      foreach (var id in fixtureIds)
      {
        Assert.AreEqual(1, nodes2.Count(n => n.Id == id), $"duplicate imported overlay: {id}");
      }
    }
  }
}

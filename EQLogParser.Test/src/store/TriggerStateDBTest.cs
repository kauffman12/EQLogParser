using LiteDB;

namespace EQLogParser
{
  /* Store-level tests that run against real LiteDB databases in temp directories. These run on
   * any OS (no WPF) now that the store lives in Core. */
  [TestClass]
  public sealed class TriggerStateDBTest
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
          // best effort — temp dirs are harmless if a file handle is still open
        }
      }
    }

    private string NewDir() => Directory.CreateDirectory(Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Store whose database lives in <paramref name="path"/>.</summary>
    private TriggerStateDB Store(string path)
    {
      var dir = Path.GetDirectoryName(path);
      if (dir is not null) _dirs.Add(dir);
      return new TriggerStateDB(path, applyLegacyUpgrades: false);
    }

    private (TriggerStateDB Db, string Path) FreshStore()
    {
      var dir = NewDir();
      var path = Path.Combine(dir, "test.db");
      return (Store(path), path);
    }

    private static ExportTriggerNode ExportLeaf(string name, string pattern, string? originalId = null, string? sound = null) => new()
    {
      Name = name,
      OriginalId = originalId,
      TriggerData = new Trigger { Pattern = pattern, SoundToPlay = sound }
    };

    /// <summary>Wraps nodes the way real .tgf exports do — the list contains the tree root as its
    /// first element and Import() unwraps exactly that one level.</summary>
    private static List<ExportTriggerNode> Wrap(params ExportTriggerNode[] children) =>
      [new ExportTriggerNode { Name = TriggerStateDB.Triggers, Nodes = [..children] }];

    [TestMethod]
    public async Task NewDb_Bootstraps_RootsAndDefaultOverlays()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var root = (await db.GetTriggerTree("P1")).Root;
      Assert.AreEqual(TriggerStateDB.Triggers, root.Name);

      var overlay = await db.GetOverlayTree();
      Assert.AreEqual(TriggerStateDB.Overlays, overlay.Root.Name);
      // version 1.0.1 bootstrap creates the two default overlays
      var defaults = overlay.Nodes.Where(n => n.OverlayData?.IsDefault == true).Select(n => n.Name).OrderBy(x => x).ToList();
      CollectionAssert.AreEqual(new[] { "Default Text Overlay", "Default Timer Overlay" }, defaults);
    }

    [TestMethod]
    public async Task LegacyExportTriggerNodeTypeMarker_StrippedSoOldDatabasesOpen()
    {
      // Databases written by pre-refactor builds carry LiteDB's polymorphic marker
      // "_type": "EQLogParser.ExportTriggerNode, EQLogParser" on imported nodes. The class moved
      // to EQLogParser.Core, so the stale marker used to make the first tree query throw
      // "not found in current domain" — with such a document present, opening the store below
      // reproduced exactly that crash (ctor FindOne on the overlays root).
      var dir = NewDir();
      _dirs.Add(dir);
      var path = Path.Combine(dir, "legacy.db");

      string nameKey;
      using (var raw = new LiteDatabase(path))
      {
        // discover the stored field name for TriggerNode.Name by round-tripping through the mapper
        raw.GetCollection<TriggerNode>("Tree").Insert(new TriggerNode { Id = "probe", Name = "probe" });
        var probeDoc = raw.GetCollection<BsonDocument>("Tree").FindById("probe");
        nameKey = probeDoc.TryGetValue("Name", out _) ? "Name" : "name";
        raw.GetCollection<BsonDocument>("Tree").Delete("probe");

        // legacy imported node: root-level (no Parent) with the stale type marker, plus a nested
        // child carrying its own marker (pre-refactor documents may nest children)
        var nested = new BsonDocument();
        nested.Add("_id", "nested-1");
        nested.Add("_type", "EQLogParser.ExportTriggerNode, EQLogParser");
        var legacyDoc = new BsonDocument();
        legacyDoc.Add("_id", "legacy-node");
        legacyDoc.Add("_type", "EQLogParser.ExportTriggerNode, EQLogParser");
        legacyDoc.Add(nameKey, TriggerStateDB.Overlays);
        legacyDoc.Add("nodes", new BsonArray { nested });
        raw.GetCollection<BsonDocument>("Tree").Insert(legacyDoc);

        // the marker could in principle sit in any collection — the cleanup must sweep them all
        var configDoc = new BsonDocument();
        configDoc.Add("_id", "legacy-config");
        configDoc.Add("_type", "EQLogParser.ExportTriggerNode, EQLogParser");
        raw.GetCollection<BsonDocument>("Config").Insert(configDoc);
      }

      var db = Store(path);
      await using (var _ = db)
      {
        // pre-migration this constructor threw LiteException on its first tree query
        var overlay = await db.GetOverlayTree();
        Assert.AreEqual(TriggerStateDB.Overlays, overlay.Root.Name, "the legacy node itself must survive intact");
      }

      using (var check = new LiteDatabase(path))
      {
        // no stale marker left anywhere — top-level or nested
        foreach (var name in check.GetCollectionNames())
        {
          foreach (var doc in check.GetCollection<BsonDocument>(name).FindAll())
          {
            var id = doc["_id"].AsString;
            Assert.IsFalse(doc.TryGetValue("_type", out _), $"marker survived in '{name}' document {id}");
            if (doc.TryGetValue("nodes", out var nodes) && nodes.Type == BsonType.Array)
            {
              foreach (var item in nodes.AsArray)
              {
                Assert.IsFalse(item.Type == BsonType.Document && item.AsDocument.TryGetValue("_type", out _),
                  $"marker survived nested in '{name}' document {id}");
              }
            }
          }
        }

        var docs = check.GetCollection<BsonDocument>("Tree").FindAll().ToList();
        Assert.IsTrue(docs.Any(d => d.TryGetValue(nameKey, out var v) && v.AsString == TriggerStateDB.Overlays),
          "the legacy node itself must survive intact");

        // the one-time stamp must now be present in the existing FixVersion collection
        var stamped = check.GetCollection<BsonDocument>("FixVersion")
          .FindAll().Any(d => d.TryGetValue("_id", out var id) && id.AsString == "legacy-export-trigger-node-marker-stripped");
        Assert.IsTrue(stamped, "expected the marker-strip stamp in FixVersion after a clean sweep");
      }
    }

    /* The sweep must actually be one-time: after the stamp exists, a fresh stale marker must be
     * left alone (the current code never writes markers again, so this is pure cost avoidance). */
    [TestMethod]
    public async Task LegacyExportTriggerNodeTypeMarker_SweepIsOneTime()
    {
      var dir = NewDir();
      _dirs.Add(dir);
      var path = Path.Combine(dir, "legacy2.db");
      using (var raw = new LiteDatabase(path))
      {
        raw.GetCollection<BsonDocument>("Tree").Insert(new BsonDocument
        {
          ["_id"] = "legacy-node",
          ["_type"] = "EQLogParser.ExportTriggerNode, EQLogParser",
          ["Name"] = TriggerStateDB.Overlays
        });
      }

      // first open runs the sweep and stamps the database
      await using (var store = Store(path)) { }

      // now seed a marker in a collection the app never touches with typed queries
      const string StaleMarker = "EQLogParser.ExportTriggerNode, EQLogParser";
      using (var raw = new LiteDatabase(path))
      {
        raw.GetCollection<BsonDocument>("OrphanLegacy").Insert(new BsonDocument
        {
          ["_id"] = "orphan-1",
          ["_type"] = StaleMarker
        });
      }

      // second open must skip the sweep entirely (stamp present)
      await using (var store2 = Store(path)) { }

      using var check = new LiteDatabase(path);
      var orphan = check.GetCollection<BsonDocument>("OrphanLegacy").FindById("orphan-1");
      Assert.IsTrue(orphan != null && orphan.TryGetValue("_type", out _),
        "the sweep must not run again once the stamp is present");
    }

    [TestMethod]
    public async Task B4_FolderImport_CollidingWithSameNameTrigger_KeepsBoth()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      // existing trigger leaf named "Boss"
      var (root, _, _) = await db.GetTriggerTree("P1");
      // existing trigger leaf named "Boss" (created through the normal import path)
      await db.ImportTriggers(root, Wrap(ExportLeaf("Boss", "boss up")));

      // import a folder wrapper with the same name (NAG exports are wrapped in the character
      // name folder — same-named trigger/folder siblings are reachable)
      var export = Wrap(new ExportTriggerNode { Name = "Boss", Nodes = [ExportLeaf("Hit", "hit")] });
      await db.ImportTriggers(root, export);

      var (root2, nodes, _) = await db.GetTriggerTree("P1");
      var bossNodes = nodes.Where(n => n.Name == "Boss" && n.Parent == root2.Id).ToList();
      // the trigger survived AND the folder was added — neither erased the other
      Assert.AreEqual(2, bossNodes.Count);
      var trigger = bossNodes.Single(n => n.TriggerData != null);
      var folder = bossNodes.Single(n => n.OverlayData == null && n.TriggerData == null);
      StringAssert.Contains(trigger.TriggerData.Pattern, "boss up");
      Assert.AreEqual(1, nodes.Count(n => n.Parent == folder.Id && n.Name == "Hit"));
    }

    [TestMethod]
    public async Task B4_TriggerImport_CollidingWithSameNameFolder_KeepsBoth()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      // existing folder "Boss" with a child
      await db.ImportTriggers(root, Wrap(new ExportTriggerNode { Name = "Boss", Nodes = [ExportLeaf("Hit", "hit")] }));

      // now import a trigger leaf with the same name
      await db.ImportTriggers(root, Wrap(ExportLeaf("Boss", "boss up")));

      var (root2, nodes, _) = await db.GetTriggerTree("P1");
      var bossNodes = nodes.Where(n => n.Name == "Boss" && n.Parent == root2.Id).ToList();
      Assert.AreEqual(2, bossNodes.Count);
      // folder kept its child — the leaf was not silently dropped into it
      var folder = bossNodes.Single(n => n.TriggerData == null);
      Assert.AreEqual(1, nodes.Count(n => n.Parent == folder.Id && n.Name == "Hit"));
      // and the new leaf exists as a sibling with its own data
      var leaf = bossNodes.Single(n => n.TriggerData != null);
      StringAssert.Contains(leaf.TriggerData.Pattern, "boss up");
    }

    [TestMethod]
    public async Task Import_ReimportSameExport_IsIdempotent()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      var export = Wrap(
        ExportLeaf("A", "a1"),
        new ExportTriggerNode { Name = "Dir", Nodes = [ExportLeaf("B", "b1")] });

      await db.ImportTriggers(root, export);
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      var countAfterFirst = nodes.Count;

      await db.ImportTriggers(root, export);
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(countAfterFirst, nodes2.Count); // no duplicates on re-import
      Assert.AreEqual(1, nodes2.Count(n => n.Name == "A"));
    }

    [TestMethod]
    public async Task Import_OriginalId_DuplicateNames_DoNotCollapse()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      // NAG allows duplicate names for distinct triggers — OriginalId keeps them apart
      var export = Wrap(
        ExportLeaf("Dup", "first", originalId: "a"),
        ExportLeaf("Dup", "second", originalId: "b"));
      await db.ImportTriggers(root, export);
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(2, nodes.Count(n => n.Name == "Dup"));

      // re-import only the first one with a changed pattern — it must update in place and not
      // touch the second node
      await db.ImportTriggers(root, Wrap(ExportLeaf("Dup", "first-v2", originalId: "a")));
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      var dups2 = nodes2.Where(n => n.Name == "Dup").ToList();
      Assert.AreEqual(2, dups2.Count);
      Assert.AreEqual(1, dups2.Count(n => n.TriggerData.Pattern == "first-v2"));
      Assert.AreEqual(1, dups2.Count(n => n.TriggerData.Pattern == "second"));
    }

    [TestMethod]
    public async Task Import_MergeIntoExistingFolder_AddsAndUpdatesChildren()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      await db.ImportTriggers(root,
        Wrap(new ExportTriggerNode { Name = "Dir", Nodes = [ExportLeaf("In", "in-1")] }));

      // re-import the same folder with an updated child and a new child
      await db.ImportTriggers(root, Wrap(new ExportTriggerNode
      {
        Name = "Dir",
        Nodes = [ExportLeaf("In", "in-2"), ExportLeaf("Out", "out-1")]
      }));

      var (root2, nodes, _) = await db.GetTriggerTree("P1");
      var dirs = nodes.Where(n => n.Name == "Dir" && n.Parent == root2.Id).ToList();
      Assert.AreEqual(1, dirs.Count); // still exactly one folder
      var dirId = dirs[0].Id;
      var children = nodes.Where(n => n.Parent == dirId).ToList();
      Assert.AreEqual(2, children.Count);
      Assert.AreEqual("in-2", children.Single(c => c.Name == "In").TriggerData.Pattern); // updated in place
      Assert.AreEqual("out-1", children.Single(c => c.Name == "Out").TriggerData.Pattern); // added
    }

    [TestMethod]
    public async Task Import_MissingMedia_FlagsSetPerNode()
    {
      // nothing exists on disk in the test context — restore the previous hook afterwards
      var previous = TriggerStorePlatform.SoundExists;
      TriggerStorePlatform.SoundExists = _ => false;
      var (db, _) = FreshStore();
      try
      {
        var (root, _, _) = await db.GetTriggerTree("P1");
        await db.ImportTriggers(root, Wrap(ExportLeaf("WithSound", "x", sound: "missing.ogg")));
        Assert.IsTrue(db.MissingMedia.Values.Any());
      }
      finally
      {
        TriggerStorePlatform.SoundExists = previous;
        await db.Dispose();
      }
    }

    [TestMethod]
    public async Task SetState_UpdatesSubtree_PersistsAcrossReopen()
    {
      var (db, path) = FreshStore();
      try
      {
        var (root, _, _) = await db.GetTriggerTree("P1");
        var folder = (await db.CreateFolder(root.Id, "Dir", "P1")).Node;
        var t1 = (await db.CreateTrigger(folder.Id, "T1", "P1")).Node;
        var t2 = (await db.CreateTrigger(folder.Id, "T2", "P1")).Node;

        await db.SetState(["P1"], folder.Id, true); // enables the whole subtree
        var (_, _, state) = await db.GetTriggerTree("P1");
        Assert.IsTrue(state.Enabled[folder.Id]);
        Assert.IsTrue(state.Enabled[t1.Id]);
        Assert.IsTrue(state.Enabled[t2.Id]);
      }
      finally
      {
        await db.Dispose();
      }

      // reopen the same file — state must have been persisted
      var db2 = Store(path);
      try
      {
        var (_, _, state2) = await db2.GetTriggerTree("P1");
        Assert.IsTrue(state2.Enabled.Count > 0);
      }
      finally
      {
        await db2.Dispose();
      }
    }

    [TestMethod]
    public async Task FixEnabledState_DerivesFolderStateFromChildren()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      var folder = (await db.CreateFolder(root.Id, "Dir", "P1")).Node;
      var t1 = (await db.CreateTrigger(folder.Id, "T1", "P1")).Node;
      var t2 = (await db.CreateTrigger(folder.Id, "T2", "P1")).Node;

      // one child enabled -> folder is indeterminate (explicit null entry — tri-state checkbox)
      await db.SetState(["P1"], t1.Id, true);
      var (_, _, state) = await db.GetTriggerTree("P1");
      Assert.IsTrue(state.Enabled.TryGetValue(folder.Id, out var mixed));
      Assert.IsNull(mixed);

      // all children enabled -> folder derives enabled
      await db.SetState(["P1"], t2.Id, true);
      var (_, _, state2) = await db.GetTriggerTree("P1");
      Assert.IsTrue(state2.Enabled[folder.Id]);
    }

    [TestMethod]
    public async Task SetExpanded_PersistsAcrossReopen()
    {
      var (db, path) = FreshStore();
      try
      {
        await db.SetAllExpanded(true);
        var (root, _, _) = await db.GetTriggerTree("P1");
        Assert.IsTrue(root.IsExpanded);
      }
      finally
      {
        await db.Dispose();
      }

      var db3 = Store(path);
      try
      {
        var (root2, _, _) = await db3.GetTriggerTree("P1");
        Assert.IsTrue(root2.IsExpanded);
      }
      finally
      {
        await db3.Dispose();
      }
    }

    [TestMethod]
    public async Task Delete_RemovesSubtreeAndCleanUpStateEntries()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      var folder = (await db.CreateFolder(root.Id, "Dir", "P1")).Node;
      var t1 = (await db.CreateTrigger(folder.Id, "T1", "P1")).Node;
      await db.SetState(["P1"], folder.Id, true);

      await db.Delete(folder.Id);

      var (_, nodes, state) = await db.GetTriggerTree("P1");
      Assert.AreEqual(0, nodes.Count(n => n.Id == folder.Id || n.Id == t1.Id));
      Assert.IsFalse(state.Enabled.ContainsKey(folder.Id));
      Assert.IsFalse(state.Enabled.ContainsKey(t1.Id));
    }

    [TestMethod]
    public async Task CopyState_CopiesNodeEnabledMapping()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      var t1 = (await db.CreateTrigger(root.Id, "T1", "P1")).Node;
      await db.SetState(["P1"], t1.Id, true);

      // both player states must exist for CopyState to work (creates the P2 default state)
      await db.GetTriggerTree("P2");
      await db.CopyState(t1.Id, "P1", "P2");
      var (_, _, state) = await db.GetTriggerTree("P2");
      Assert.IsTrue(state.Enabled[t1.Id]);
    }

    [TestMethod]
    public async Task CreateTrigger_InheritsParentEnabledState_AndRaisesEvent()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var events = new List<(string Id, bool Checked)>();
      db.NodeCheckChanged += (id, isChecked) => events.Add((id, isChecked));

      var (root, _, _) = await db.GetTriggerTree("P1");
      var folder = (await db.CreateFolder(root.Id, "Dir", "P1")).Node;
      await db.SetState(["P1"], folder.Id, true);

      var (node, isChecked) = await db.CreateTrigger(folder.Id, "New", "P1");
      Assert.AreEqual(true, isChecked); // parent enabled -> child starts enabled
      CollectionAssert.Contains(events, (node.Id, true));

      var (_, _, state) = await db.GetTriggerTree("P1");
      Assert.IsTrue(state.Enabled[node.Id]);
    }

    [TestMethod]
    public async Task SetState_OnOverlayNode_IsNoOp()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var overlayRoot = (await db.GetOverlayTree()).Root;
      var overlay = await db.CreateOverlay(overlayRoot.Id, "My Overlay", isTextOverlay: true);

      await db.SetState(["P1"], overlay.Id, true);
      var (_, _, state) = await db.GetTriggerTree("P1");
      Assert.IsFalse(state.Enabled.ContainsKey(overlay.Id));
    }

    [TestMethod]
    public void FixColor_NormalizesHexColors()
    {
      // null/empty pass through untouched; unparseable values fall back to white
      Assert.AreEqual(null, TriggerStateDB.FixColor(null));
      Assert.AreEqual(string.Empty, TriggerStateDB.FixColor(""));
      // documented delta: named colors no longer resolve (Syncfusion-free Core)
      Assert.AreEqual("#FFFFFF", TriggerStateDB.FixColor("Red"));
      Assert.AreEqual("#FFFFFF", TriggerStateDB.FixColor("not a color"));
      // #RGB / #RRGGBB get an alpha byte (#FF0 = red+green, opaque)
      Assert.AreEqual("#FFFFFF00", TriggerStateDB.FixColor("#FF0"));
      Assert.AreEqual("#FF112233", TriggerStateDB.FixColor("0xFF112233"));
      // already AARRGGBB passes through unchanged (case-normalized)
      Assert.AreEqual("#AABBCCDD", TriggerStateDB.FixColor("#aabbccdd"));
    }
  }

  internal static class TestTemp
  {
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "eqlp-core-tests");
  }
}

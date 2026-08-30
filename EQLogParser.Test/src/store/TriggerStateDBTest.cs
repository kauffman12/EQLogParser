using LiteDB;

namespace EQLogParser
{
  /* Store-level tests that run against real LiteDB databases in temp directories. These run on
   * any OS (no WPF) now that the store lives in Core. Some tests swap the process-wide
   * TriggerStorePlatform hooks, so the whole class is kept out of the parallel run. */
  [DoNotParallelize]
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

    /// <summary>Fails if <paramref name="value"> or anything nested in it is a document with a
    /// LiteDB polymorphic "_type" marker (data must not depend on assembly identity).</summary>
    private static void AssertNoTypeMarker(BsonValue value, string where)
    {
      switch (value)
      {
        case BsonDocument doc:
          Assert.IsFalse(doc.TryGetValue("_type", out _), $"polymorphic marker found in {where}");
          foreach (var pair in doc) AssertNoTypeMarker(pair.Value, $"{where}.{pair.Key}");
          break;
        case BsonArray arr:
          for (var i = 0; i < arr.Count; i++) AssertNoTypeMarker(arr[i], $"{where}[{i}]");
          break;
      }
    }

    [TestMethod]
    public async Task NewDb_Bootstraps_RootsAndDefaultOverlays()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var root = (await db.GetTriggerTree("P1")).Root;
      Assert.AreEqual(TriggerStateDB.Triggers, root.Name);

      var overlay = await db.GetOverlayTree();
      Assert.AreEqual(TriggerStateDB.Overlays, overlay.Root.Name);
      // first-run bootstrap creates the two default overlays
      var defaults = overlay.Nodes.Where(n => n.OverlayData?.IsDefault == true).Select(n => n.Name).OrderBy(x => x).ToList();
      CollectionAssert.AreEqual(new[] { "Default Text Overlay", "Default Timer Overlay" }, defaults);
    }

    /* Pre-branch, the bootstrap block keyed off "no FixVersion document" rather than "empty file": a
     * populated database that was never version-stamped still got its first-run treatment. */
    [TestMethod]
    public async Task ExistingDbWithoutVersionDoc_FirstVersionedRun_BootstrapsDefaultsAndStamps()
    {
      var dir = NewDir();
      _dirs.Add(dir);
      var path = Path.Combine(dir, "unversioned.db");

      string nameKey;
      using (var raw = new LiteDatabase(path))
      {
        // discover the stored field name for TriggerNode.Name by round-tripping through the mapper
        raw.GetCollection<TriggerNode>("Tree").Insert(new TriggerNode { Id = "probe", Name = "probe" });
        var probeDoc = raw.GetCollection<BsonDocument>("Tree").FindById("probe");
        nameKey = probeDoc.TryGetValue("Name", out _) ? "Name" : "name";
        raw.GetCollection<BsonDocument>("Tree").Delete("probe");

        // populated tree with root nodes but no version document at all
        var overlayRoot = new BsonDocument();
        overlayRoot.Add("_id", "root-overlays");
        overlayRoot.Add(nameKey, TriggerStateDB.Overlays);
        raw.GetCollection<BsonDocument>("Tree").Insert(overlayRoot);

        var triggerRoot = new BsonDocument();
        triggerRoot.Add("_id", "root-triggers");
        triggerRoot.Add(nameKey, TriggerStateDB.Triggers);
        raw.GetCollection<BsonDocument>("Tree").Insert(triggerRoot);
      }

      var db = Store(path);
      await using (var _ = db)
      {
        // no child overlays exist yet, so the first-versioned-run bootstrap adds the defaults
        var overlay = await db.GetOverlayTree();
        var defaults = overlay.Nodes.Where(n => n.OverlayData?.IsDefault == true).Select(n => n.Name).OrderBy(x => x).ToList();
        CollectionAssert.AreEqual(new[] { "Default Text Overlay", "Default Timer Overlay" }, defaults);
      }

      using (var check = new LiteDatabase(path))
      {
        // and the migration stamped the version chain at the current version
        var versionDoc = check.GetCollection<BsonDocument>("FixVersion")
          .FindAll().FirstOrDefault(d => d.TryGetValue("_id", out var id) &&
                                         id.Type == BsonType.String && id.AsString == "1");
        Assert.IsTrue(versionDoc is not null && versionDoc.TryGetValue("Version", out var v) && v.AsString == "1.0.2",
          "expected FixVersion to be stamped to 1.0.2 on first versioned run");
      }
    }

    /* The contrast: a database that was already version-stamped is an existing user's data — even with no
     * child overlays it must not be re-bootstrapped (the pre-branch count==0 gate guaranteed this). */
    [TestMethod]
    public async Task ExistingDbWithVersionDoc_MissingOverlays_NotRebootstrapped()
    {
      var dir = NewDir();
      _dirs.Add(dir);
      var path = Path.Combine(dir, "versioned.db");

      string nameKey;
      using (var raw = new LiteDatabase(path))
      {
        raw.GetCollection<TriggerNode>("Tree").Insert(new TriggerNode { Id = "probe", Name = "probe" });
        var probeDoc = raw.GetCollection<BsonDocument>("Tree").FindById("probe");
        nameKey = probeDoc.TryGetValue("Name", out _) ? "Name" : "name";
        raw.GetCollection<BsonDocument>("Tree").Delete("probe");

        var overlayRoot = new BsonDocument();
        overlayRoot.Add("_id", "root-overlays");
        overlayRoot.Add(nameKey, TriggerStateDB.Overlays);
        raw.GetCollection<BsonDocument>("Tree").Insert(overlayRoot);

        var triggerRoot = new BsonDocument();
        triggerRoot.Add("_id", "root-triggers");
        triggerRoot.Add(nameKey, TriggerStateDB.Triggers);
        raw.GetCollection<BsonDocument>("Tree").Insert(triggerRoot);

        var versionDoc = new BsonDocument();
        versionDoc.Add("_id", "1");
        versionDoc.Add("Version", "1.0.2");
        raw.GetCollection<BsonDocument>("FixVersion").Insert(versionDoc);
      }

      var db = Store(path);
      await using (var _ = db)
      {
        var overlay = await db.GetOverlayTree();
        var defaults = overlay.Nodes.Where(n => n.OverlayData?.IsDefault == true).ToList();
        Assert.AreEqual(0, defaults.Count, "an existing versioned database must not gain default overlays");
      }
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

        // and the migration must have bumped the existing FixVersion version document to CurrentDbVersion
        var versionDoc = check.GetCollection<BsonDocument>("FixVersion")
          .FindAll().FirstOrDefault(d => d.TryGetValue("_id", out var id) &&
                                         id.Type == BsonType.String && id.AsString == "1");
        Assert.IsTrue(versionDoc is not null && versionDoc.TryGetValue("Version", out var v) && v.AsString == "1.0.2",
          "expected FixVersion to be bumped to 1.0.2 after the marker sweep");
      }
    }

    /* The sweep must actually be one-time: once the database version is current, a fresh stale marker must be
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

      // second open must skip the sweep entirely (version already current)
      await using (var store2 = Store(path)) { }

      using var check = new LiteDatabase(path);
      var orphan = check.GetCollection<BsonDocument>("OrphanLegacy").FindById("orphan-1");
      Assert.IsTrue(orphan != null && orphan.TryGetValue("_type", out _),
        "the sweep must not run again once the database version is current");
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
    public async Task Import_PaddedNames_ReimportIsIdempotent()
    {
      // LiteDB trims leading/trailing whitespace from stored strings, and source data contains
      // padded names (the NAG dump has triggers like " Emollious colours..."). Without matching
      // the incoming names against that storable form, phrase variants (#1..#4) sharing one
      // OriginalId fail to match their trimmed stored twins on re-import and duplicate.
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      List<ExportTriggerNode> BuildExport() => Wrap(new ExportTriggerNode
        {
          Name = " Padded Folder ",
          Nodes = [ExportLeaf(" Boss #1 ", "regex1", "same-id"), ExportLeaf(" Boss #2 ", "regex2", "same-id")]
        });

      await db.ImportTriggers(root, BuildExport());
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      var countAfterFirst = nodes.Count;
      Assert.AreEqual(1, nodes.Count(n => n.Name == "Padded Folder"), "stored folder must be in its storable (trimmed) form");

      // a fresh parse of the same source — padded names again on arrival
      await db.ImportTriggers(root, BuildExport());
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      Assert.AreEqual(countAfterFirst, nodes2.Count, "re-import of padded names must not duplicate");
      Assert.AreEqual(2, nodes2.Count(n => n.OriginalId == "same-id"));
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

    /* One NAG trigger can produce several siblings sharing one OriginalId (phrase + timer variants).
     * Re-import must update each stored member with ITS OWN data — matching by id alone would let
     * every incoming member overwrite the first sibling found and leave the rest stale. */
    [TestMethod]
    public async Task Import_OriginalId_SharedIdFamily_ReimportUpdatesEachMember()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var (root, _, _) = await db.GetTriggerTree("P1");
      var export = Wrap(
        ExportLeaf("P", "p-1", originalId: "fam"),
        ExportLeaf("P (Timer 2)", "timer-1", originalId: "fam"));
      await db.ImportTriggers(root, export);

      // re-import with both patterns changed — each stored member must receive its own data
      await db.ImportTriggers(root, Wrap(
        ExportLeaf("P", "p-2", originalId: "fam"),
        ExportLeaf("P (Timer 2)", "timer-2", originalId: "fam")));

      var (_, nodes, _) = await db.GetTriggerTree("P1");
      var family = nodes.Where(n => n.OriginalId == "fam").ToList();
      Assert.AreEqual(2, family.Count); // no new duplicates inserted
      Assert.AreEqual("p-2", family.Single(n => n.Name == "P").TriggerData.Pattern);
      Assert.AreEqual("timer-2", family.Single(n => n.Name == "P (Timer 2)").TriggerData.Pattern);
    }

    /* SelectedOverlays are validated down to existing overlay-leaf ids on BOTH import branches —
     * the update-in-place branch used to skip validation entirely, so re-imports could revive
     * dangling overlay references. */
    [TestMethod]
    public async Task Import_SelectedOverlays_StrippedToExistingOverlays_OnInsertAndUpdate()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var root = (await db.GetTriggerTree("P1")).Root;
      var overlayRoot = (await db.GetOverlayTree()).Root;
      var overlay = await db.CreateOverlay(overlayRoot.Id, "My Overlay", isTextOverlay: true);

      List<ExportTriggerNode> BuildExport(string pattern) => Wrap(new ExportTriggerNode
      {
        Name = "T",
        TriggerData = new Trigger
        {
          Pattern = pattern,
          SelectedOverlays = [overlay.Id, "missing-overlay-id"]
        }
      });

      // insert branch
      await db.ImportTriggers(root, BuildExport("p1"));
      var (_, nodes, _) = await db.GetTriggerTree("P1");
      CollectionAssert.AreEqual(new List<string> { overlay.Id }, nodes.Single(n => n.Name == "T").TriggerData.SelectedOverlays);

      // update-in-place branch (same name, changed pattern)
      await db.ImportTriggers(root, BuildExport("p2"));
      var (_, nodes2, _) = await db.GetTriggerTree("P1");
      var t = nodes2.Single(n => n.Name == "T");
      Assert.AreEqual("p2", t.TriggerData.Pattern);
      CollectionAssert.AreEqual(new List<string> { overlay.Id }, t.TriggerData.SelectedOverlays);
    }

    /* A folder wrapper whose OriginalId collides with a stored leaf's id must fall through to
     * insert (kind-safe matching) instead of reaching the overwrite branch with
     * TriggerData == null and erasing the leaf. */
    [TestMethod]
    public async Task Import_OriginalId_FolderWrapperCollidingWithLeaf_DoesNotErase()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var root = (await db.GetTriggerTree("P1")).Root;

      await db.ImportTriggers(root, Wrap(ExportLeaf("Boss", "boss-pat", originalId: "id-1")));

      // corrupt/hand-edited export: the folder wrapper reuses the leaf's id
      await db.ImportTriggers(root, Wrap(new ExportTriggerNode
      {
        Name = "Boss Wrapper",
        OriginalId = "id-1",
        Nodes = [ExportLeaf("Inside", "inside-pat")]
      }));

      var (_, nodes, _) = await db.GetTriggerTree("P1");
      var leaf = nodes.Single(n => n.Name == "Boss");
      Assert.IsNotNull(leaf.TriggerData, "stored leaf data must survive a kind-mismatched folder import");
      Assert.AreEqual("boss-pat", leaf.TriggerData.Pattern);

      var folder = nodes.SingleOrDefault(n => n.Name == "Boss Wrapper" && n.OverlayData is null && n.TriggerData is null);
      Assert.IsNotNull(folder, "kind mismatch must fall back to inserting a new folder");
      Assert.IsTrue(nodes.Any(n => n.Parent == folder!.Id && n.Name == "Inside"));
    }

    /* Standard .ogf overlay import (file-based, no NAG/GINA involved). Exports carry the STORED
     * id for overlay leaves only — folders export with Id == null — so re-import matches leaves by
     * id and updates them in place; this pins that contract down. */
    [TestMethod]
    public async Task ImportOverlays_ReimportByStoredId_UpdatesInPlace()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      await db.GetOverlayTree();

      var overlayId = Guid.NewGuid().ToString();
      var childId = Guid.NewGuid().ToString();

      static ExportTriggerNode Ogf(string id, string name, Overlay overlay) =>
        new() { Id = id, Name = name, OverlayData = overlay };

      // mirrors the .ogf shape: root wrapper + overlay leaves (folders would export with Id null)
      var export = new List<ExportTriggerNode> { new()
      {
        Name = TriggerStateDB.Overlays,
        Nodes =
        [
          Ogf(overlayId, "Bar Timer", new Overlay { FontSize = "12pt" }),
          Ogf(childId, "Idle Box", new Overlay()),
        ]
      } };

      await db.ImportOverlays(export);
      var (root, nodes, _) = await db.GetOverlayTree();
      // the two bootstrap default overlays plus the two imported ones
      Assert.AreEqual(4, nodes.Count);
      // the stored id must survive the insert — re-import matching depends on it
      Assert.AreEqual(overlayId, nodes.Single(n => n.Name == "Bar Timer" && n.Parent == root.Id).Id);

      // re-import: same ids, changed data and a display name — id match updates the data in
      // place (name is not part of the update, same as master)
      var export2 = new List<ExportTriggerNode> { new()
      {
        Name = TriggerStateDB.Overlays,
        Nodes =
        [
          Ogf(overlayId, "Renamed Timer", new Overlay { FontSize = "16pt" }),
          Ogf(childId, "Idle Box", new Overlay()),
        ]
      } };
      await db.ImportOverlays(export2);

      var (_, nodes2, _) = await db.GetOverlayTree();
      Assert.AreEqual(4, nodes2.Count); // no duplicates for the re-imported ids
      var timer = nodes2.Single(n => n.Id == overlayId);
      Assert.AreEqual("16pt", timer.OverlayData.FontSize);
      Assert.AreEqual("Bar Timer", timer.Name);
    }

    /* NAG overlay contract: export nodes carry no id (EQLP ids are store-generated) — the source
     * identity travels in OverlayData.Source ("nag:{overlayId}"). Re-importing with the same
     * Source must update the existing node, name included, instead of adding a second copy. */
    [TestMethod]
    public async Task ImportOverlays_NagSource_UpdatesExistingOverlayInPlace()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      await db.GetOverlayTree();

      static ExportTriggerNode Nag(string name, string fontSize) =>
        new() { Name = name, OverlayData = new Overlay { Source = "nag:ov-1", FontSize = fontSize } };

      await db.ImportOverlays([new() { Name = TriggerStateDB.Overlays, Nodes = [Nag("Combat", "12pt")] }]);
      var (root, nodes, _) = await db.GetOverlayTree();
      var imported = nodes.Single(n => n.Name == "Combat");
      Assert.IsTrue(Guid.TryParse(imported.Id, out var generatedId), "node id must be store-generated");

      // re-migration of the same NAG overlay: same Source, name and payload changed
      await db.ImportOverlays([new() { Name = TriggerStateDB.Overlays, Nodes = [Nag("Combat v2", "16pt")] }]);

      var (_, nodes2, _) = await db.GetOverlayTree();
      Assert.AreEqual(1, nodes2.Count(n => n.Name is "Combat" or "Combat v2"), "re-import must not create a second overlay");
      var updated = nodes2.Single(n => n.Parent == root.Id && n.OverlayData?.Source == "nag:ov-1");
      Assert.AreEqual("Combat v2", updated.Name);
      Assert.AreEqual("16pt", updated.OverlayData.FontSize);
      Assert.AreEqual(imported.Id, updated.Id); // same node, updated in place
    }

    /* Source matching must not over-match across distinct NAG overlays: two same-named overlays
     * from two migrated databases (different Sources) are both kept. */
    [TestMethod]
    public async Task ImportOverlays_DistinctNagSources_KeptSeparate()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      await db.GetOverlayTree();

      static ExportTriggerNode Nag(string name, string source) =>
        new() { Name = name, OverlayData = new Overlay { Source = source } };

      await db.ImportOverlays([new() { Name = TriggerStateDB.Overlays, Nodes = [Nag("Combat", "nag:ov-1")] }]);
      await db.ImportOverlays([new() { Name = TriggerStateDB.Overlays, Nodes = [Nag("Combat", "nag:ov-2")] }]);

      var (_, nodes, _) = await db.GetOverlayTree();
      Assert.AreEqual(2, nodes.Count(n => n.Name == "Combat"));
    }

    /* Overlay import must persist plain TriggerNode documents. Inserting the ExportTriggerNode
     * instance directly made LiteDB stamp "_type": "EQLogParser.ExportTriggerNode, <assembly>"
     * onto each document, coupling every imported overlay to the assembly that class lives in —
     * the same coupling whose stale markers forced the 1.0.2 legacy-marker migration. */
    [TestMethod]
    public async Task ImportOverlays_StoresPlainTriggerNodes_NoPolymorphicMarkerInDb()
    {
      var (db, path) = FreshStore();
      await using (var _ = db)
      {
        await db.GetOverlayTree();
        var export = new List<ExportTriggerNode> { new()
        {
          Name = TriggerStateDB.Overlays,
          Nodes = [new ExportTriggerNode { Id = Guid.NewGuid().ToString(), Name = "Imported Bar", OverlayData = new Overlay { FontSize = "10pt" } }]
        } };
        await db.ImportOverlays(export);
      }

      using (var check = new LiteDatabase(path))
      {
        // no marker anywhere in the file — top-level or nested inside a folder's children array
        foreach (var name in check.GetCollectionNames())
        {
          foreach (var doc in check.GetCollection<BsonDocument>(name).FindAll())
          {
            AssertNoTypeMarker(doc, $"'{name}' document {doc["_id"]}");
          }
        }
      }

      // and the marker-free document round-trips: the overlay is still fully readable after a
      // full store reopen
      var reopened = Store(path);
      await using (var _ = reopened)
      {
        var (_, nodes, _) = await reopened.GetOverlayTree();
        var bar = nodes.Single(n => n.Name == "Imported Bar");
        Assert.AreEqual("10pt", bar.OverlayData?.FontSize);
      }
    }

    /* Both NAG and GINA imports land in the store through ImportTriggers() with an
     * ExportTriggerNode tree. Every branch of the import must persist plain TriggerNode
     * documents — pins that none of them serializes the export type (see the overlay twin of
     * this test for why a "_type" marker is poison). */
    [TestMethod]
    public async Task ImportTriggers_StoresPlainTriggerNodes_NoPolymorphicMarkerInDb()
    {
      var (db, path) = FreshStore();
      await using (var _ = db)
      {
        var (root, _, _) = await db.GetTriggerTree("P1");
        // NAG-shaped export: folders, leaves with OriginalIds and a same-name duplicate kept
        // apart by id
        var export = Wrap(
          ExportLeaf("Dup", "first", originalId: "a"),
          new ExportTriggerNode { Name = "Common", Nodes =
            [
              ExportLeaf("Boss #1", "regex-1", originalId: "fam"),
              ExportLeaf("Boss #2 (Timer 2)", "timer-1", originalId: "fam"),
            ] },
          ExportLeaf("Dup", "second", originalId: "b"));
        await db.ImportTriggers(root, export);

        // and the overlay half of a NAG import, so one test covers both entry points
        await db.ImportOverlays(new List<ExportTriggerNode> { new()
        {
          Name = TriggerStateDB.Overlays,
          Nodes = [new ExportTriggerNode { Id = Guid.NewGuid().ToString(), Name = "Imported Bar", OverlayData = new Overlay() }]
        } });
      }

      using var check = new LiteDatabase(path);
      foreach (var name in check.GetCollectionNames())
      {
        foreach (var doc in check.GetCollection<BsonDocument>(name).FindAll())
        {
          AssertNoTypeMarker(doc, $"'{name}' document {doc["_id"]}");
        }
      }
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

    /* What flags the CONTAINING folder is the OR of its children's results, so a clean sibling
     * imported after a broken one must not clear the flag (assigning the per-node result instead of
     * OR-ing it did exactly that, leaving the folder without its missing-media badge). */
    [TestMethod]
    public async Task Import_MissingMedia_CleanSiblingDoesNotClearFolderFlag()
    {
      var previous = TriggerStorePlatform.SoundExists;
      TriggerStorePlatform.SoundExists = path => !path.EndsWith("bad.ogg", StringComparison.Ordinal);
      var (db, _) = FreshStore();
      try
      {
        var (root, _, _) = await db.GetTriggerTree("P1");
        await db.ImportTriggers(root, Wrap(new ExportTriggerNode
        {
          Name = "Dir",
          Nodes =
          [
            ExportLeaf("Broken", "p1", sound: "bad.ogg"),
            ExportLeaf("Clean", "p2", sound: "good.ogg")
          ]
        }));

        var (_, nodes, _) = await db.GetTriggerTree("P1");
        var folder = nodes.Single(n => n.Name == "Dir");
        Assert.IsTrue(db.MissingMedia.TryGetValue(folder.Id, out var folderFlag) && folderFlag,
          "folder must stay flagged: one of its children references a sound that is not on disk");

        // the per-node flags stay per node (only offenders are recorded)
        Assert.IsTrue(db.MissingMedia[nodes.Single(n => n.Name == "Broken").Id]);
        Assert.IsFalse(db.MissingMedia.ContainsKey(nodes.Single(n => n.Name == "Clean").Id));
      }
      finally
      {
        TriggerStorePlatform.SoundExists = previous;
        await db.Dispose();
      }
    }

    /* _id is unique across the whole overlay collection, so an exported id that is already stored
     * under a DIFFERENT parent cannot be reused: re-importing the same share into a second folder
     * inserts a copy with a store-generated id. Reusing it threw on insert and rolled the entire
     * import back, which is the bug this pins. */
    [TestMethod]
    public async Task ImportOverlays_ExportedIdTakenUnderAnotherFolder_InsertsCopy()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      await db.GetOverlayTree();

      var exportedId = Guid.NewGuid().ToString();

      // one overlay inside a folder of its own, the way a share grouped into a folder arrives
      List<ExportTriggerNode> Export(string folderName) =>
      [
        new()
        {
          Name = TriggerStateDB.Overlays,
          Nodes = [new ExportTriggerNode { Name = folderName, Nodes = [new ExportTriggerNode
          {
            Id = exportedId,
            Name = "Shared",
            OverlayData = new Overlay { FontSize = "12pt" }
          }] }]
        }
      ];

      await db.ImportOverlays(Export("First"));
      await db.ImportOverlays(Export("Second"));

      var (_, nodes, _) = await db.GetOverlayTree();
      var copies = nodes.Where(n => n.Name == "Shared").ToList();
      Assert.AreEqual(2, copies.Count, "the second import must add a copy, not fail or overwrite");
      var ids = copies.Select(c => c.Id).ToList();
      CollectionAssert.Contains(ids, exportedId); // the first import keeps the id it was given
      Assert.AreEqual(2, ids.Distinct().Count(), "the copy must get a fresh id");
      Assert.AreEqual(2, copies.Select(c => c.Parent).Distinct().Count(), "each copy sits under its own folder");
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
      db.EventsNodeCheckChanged += (id, isChecked) => events.Add((id, isChecked));

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
      // opaque-white fallback: bare "#FFFFFF" would parse as transparent black under WPF's
      // default ARGB binding, so the fallback must carry an explicit alpha
      Assert.AreEqual("#FFFFFFFF", TriggerStateDB.FixColor("Red"));
      Assert.AreEqual("#FFFFFFFF", TriggerStateDB.FixColor("not a color"));
      // #RGB / #RRGGBB get an alpha byte (#FF0 = red+green, opaque)
      Assert.AreEqual("#FFFFFF00", TriggerStateDB.FixColor("#FF0"));
      Assert.AreEqual("#FF112233", TriggerStateDB.FixColor("0xFF112233"));
      // already AARRGGBB passes through unchanged (case-normalized)
      Assert.AreEqual("#AABBCCDD", TriggerStateDB.FixColor("#aabbccdd"));
    }

    /* The singleton must fail fast when the host never wired GetDbFile, rather than build a store
     * over a null path that silently persists nothing. ResolveDbFile is the extracted seam so this
     * is testable without touching the lazily created Instance. */
    [TestMethod]
    public void ResolveDbFile_UnwiredHost_FailsFast()
    {
      var previous = TriggerStorePlatform.GetDbFile;
      try
      {
        TriggerStorePlatform.GetDbFile = null;
        Assert.Throws<InvalidOperationException>(TriggerStateDB.ResolveDbFile);

        TriggerStorePlatform.GetDbFile = () => string.Empty;
        Assert.Throws<InvalidOperationException>(TriggerStateDB.ResolveDbFile);

        TriggerStorePlatform.GetDbFile = () => "/tmp/eqlp-test.db";
        Assert.AreEqual("/tmp/eqlp-test.db", TriggerStateDB.ResolveDbFile());
      }
      finally
      {
        TriggerStorePlatform.GetDbFile = previous;
      }
    }

    /* The outer overlay import hands Import() one leaf per call (the fast path matches by id and
     * seeks the next sibling index without loading the folder). Re-importing the same ids must
     * update in place: no duplicates, no colliding sibling indexes. */
    [TestMethod]
    public async Task ImportOverlays_OneLeafPerCall_ReimportUpdatesInPlace()
    {
      var (db, _) = FreshStore();
      await using var _ = db;
      var root = (await db.GetOverlayTree()).Root;
      var baseCount = (await db.GetOverlayTree()).Nodes.Count(n => n.Parent == root.Id);

      List<ExportTriggerNode> Leaves(string fontSize) =>
      [
        new ExportTriggerNode { Id = "ov-1", Name = "One", OverlayData = new Overlay { IsTextOverlay = true, FontSize = fontSize } },
        new ExportTriggerNode { Id = "ov-2", Name = "Two", OverlayData = new Overlay { IsTimerOverlay = true } }
      ];

      foreach (var leaf in Leaves("10pt"))
      {
        await db.ImportOverlays([leaf]);
      }

      var firstImport = (await db.GetOverlayTree()).Nodes.Where(n => n.Name is "One" or "Two").ToList();

      Assert.AreEqual(2, firstImport.Count);
      Assert.AreEqual(firstImport.Count, firstImport.Select(n => n.Index).Distinct().Count(), "imported siblings must not share an index");

      // second pass over the same ids (the outer walker's update path)
      foreach (var leaf in Leaves("22pt"))
      {
        await db.ImportOverlays([leaf]);
      }

      var afterSecond = (await db.GetOverlayTree()).Nodes;
      Assert.AreEqual(baseCount + 2, afterSecond.Count(n => n.Parent == root.Id), "re-import must match by id, not append");
      Assert.AreEqual("22pt", afterSecond.Single(n => n.Name == "One").OverlayData.FontSize);

      // updated in place: same nodes, same positions in the folder
      var secondPass = afterSecond.Where(n => n.Name is "One" or "Two").ToDictionary(n => n.Name, n => n.Index);
      foreach (var node in firstImport)
      {
        Assert.AreEqual(node.Index, secondPass[node.Name], $"{node.Name} must keep its sibling index across re-import");
      }
    }
  }

  internal static class TestTemp
  {
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "eqlp-core-tests");
  }
}

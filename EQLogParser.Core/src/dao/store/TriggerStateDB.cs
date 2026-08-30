using LiteDB;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class TriggerStateDB : IAsyncDisposable
  {
    // Closes the LiteDB handle — call before re-opening the same file or on app exit.
    async ValueTask IAsyncDisposable.DisposeAsync() => await Dispose();

    internal event Action<string> DeleteEvent;
    internal event Action<bool> OverlayImportEvent;
    internal event Action<TriggerNode> TriggerUpdateEvent;
    internal event Action<TriggerConfig> TriggerConfigUpdateEvent;
    internal event Action<bool> TriggerImportEvent;
    internal event Action<List<LexiconItem>> LexiconUpdateEvent;
    internal event Action<List<TrustedPlayer>> TrustedPlayersUpdateEvent;
    // Fired after SetStateFromParent resolves the parent's enabled value for the given player —
    // the WPF host applies it to the matching view node (replaces the old direct IsChecked poke).
    internal event Action<string, bool> EventsNodeCheckChanged;
    internal const string DefaultUser = "Default";
    internal const string Overlays = "Overlays";
    internal const string Triggers = "Triggers";
    internal readonly ConcurrentDictionary<string, bool> RecentlyMerged = new();
    internal readonly ConcurrentDictionary<string, bool> MissingMedia = new();

    // System.Windows.VerticalAlignment values the overlay windows bind against (kept numeric so
    // Core stays UI-free).
    private const int AlignTop = 0;
    private const int AlignBottom = 2;

    private const string LegacyOverlayFile = "triggerOverlays.json";
    private const string LegacyTriggersFile = "triggers.json";
    private const string ConfigCol = "Config";
    private const string StatesCol = "States";
    private const string TreeCol = "Tree";
    private const string LexiconCol = "Lexicon";
    private const string TrustedPlayersCol = "TrustedPlayers";
    /* Database-versioning history (read before changing anything here):
     *
     * "Version" (BadVersionCol) — the original mechanism, in use until July 2024: a single
     * System.Version(1,0,0) document whose only job was flagging "database initialized"
     * (first run created the default overlays). Reading those documents back proved
     * unreliable, so current code treats the collection as garbage: it is deleted whole at
     * startup and never deserialized. Only one value ever existed in it, so nothing needs
     * translating into the new scheme — a database without a FixVersion document simply means
     * "older than 1.0.2", and the idempotent steps in ApplyDatabaseMigrations handle that.
     *
     * "FixVersion" (VersionCol) — the current version chain: one {_id:"1",
     * Version:"<major.minor.patch>"} document per database, stored as a plain string so the
     * document stays trivially readable by any build. Older builds seeded it with "1.0.1";
     * it is now owned exclusively by ApplyDatabaseMigrations, which rewrites it to
     * CurrentDbVersion only after every migration step succeeds. All other code may read it,
     * never write or re-stamp it — one owner per document is the invariant that keeps this
     * collection from repeating what went wrong with the old one. */
    private const string BadVersionCol = "Version";
    private const string VersionCol = "FixVersion";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerStateDB> Lazy = new(() => new TriggerStateDB(ResolveDbFile()));
    private static readonly JsonSerializerOptions SerializerOptions = new() { IncludeFields = true };
    internal static TriggerStateDB Instance => Lazy.Value; // instance
    private readonly LiteDbTaskQueue _taskQueue;
    private readonly LiteDatabase _db;

    /* dbFilePath: explicit database file (null/empty = no-op instance, e.g. tests without a
     * database). applyLegacyUpgrades: run the pre-1.0 json upgrade + 1.0.1 backup (production
     * only — test databases must not read the user's legacy files or write the last-database
     * backup copy). */
    internal TriggerStateDB(string dbFilePath, bool applyLegacyUpgrades = true)
    {
      var path = dbFilePath;
      if (!string.IsNullOrEmpty(path))
      {
        var needUpgrade = !File.Exists(path);

        try
        {
          var connString = new ConnectionString
          {
            Filename = path,
            Connection = ConnectionType.Shared
          };

          _db = new LiteDatabase(connString)
          {
            CheckpointSize = 10
          };

          // Captured before ApplyDatabaseMigrations runs: the migrations also write the
          // FixVersion version document, so the absence of a pre-existing version document is
          // the reliable "first versioned run" signal left by the time the bootstrap block
          // below runs (a brand-new file is just the empty special case of that).
          // Do not move this check past ApplyDatabaseMigrations or re-derive it from the
          // collection after migration: once migrations have run, every database — fresh or
          // ancient — has a version document and the signal is gone.
          var firstVersionedRun = _db.GetCollection<BsonDocument>(VersionCol).FindById("1") is not BsonDocument;

          Log.Info($"Opening trigger database: {path}");

          // The queue must exist even if migration throws below: a throw that left it null would
          // NRE every later call on the cached singleton for the whole session.
          _taskQueue = new LiteDbTaskQueue(_db);

          // Migration is retry-safe (idempotent, versioned) — isolate its failure so the store
          // stays usable and next startup retries. Must run before any typed query — see
          // ApplyDatabaseMigrations for why.
          try
          {
            ApplyDatabaseMigrations();
          }
          catch (Exception ex)
          {
            Log.Error("Trigger database migration failed; store stays usable and migration retries on next startup.", ex);
          }

          if (needUpgrade && applyLegacyUpgrades)
          {
            // upgrade from old json trigger format
            UpgradeFromOldParser();
          }

          // upgrade config if needed
          var configs = _db.GetCollection<TriggerConfig>(ConfigCol);
          configs.EnsureIndex(x => x.Id);
          UpgradeConfig(configs);

          // create default data
          var tree = _db.GetCollection<TriggerNode>(TreeCol);

          // create overlay node if it doesn't exist
          if (tree.FindOne(n => n.Parent == null && n.Name == Overlays) == null)
          {
            tree.Insert(new TriggerNode { Name = Overlays, Id = Guid.NewGuid().ToString() });
          }

          // create trigger node if it doesn't exist
          if (tree.FindOne(n => n.Parent == null && n.Name == Triggers) == null)
          {
            tree.Insert(new TriggerNode { Name = Triggers, Id = Guid.NewGuid().ToString() });
          }

          // fix overlay data
          if (tree.Find(n => n.OverlayData != null) is { } overlays)
          {
            var updated = new List<TriggerNode>();
            foreach (var overlay in overlays)
            {
              if (overlay.OverlayData.VerticalAlignment == -1)
              {
                SetVerticalAlignment(overlay);
                updated.Add(overlay);
              }
            }

            updated.ForEach(node => tree.Update(node));
          }

          tree.EnsureIndex(x => x.Id);
          tree.EnsureIndex(x => x.Parent);
          tree.EnsureIndex(x => x.Name);

          var states = _db.GetCollection<TriggerState>(StatesCol);
          states.EnsureIndex(x => x.Id);

          // Remove the legacy "Version" collection (see BadVersionCol). It is deleted whole,
          // never read or translated: every pre-2024 database holds the same single
          // System.Version(1,0,0) flag, which carries no information the FixVersion chain does
          // not supersede. Typed as BsonDocument on purpose — legacy documents may be
          // unreadable System.Version values, and only Count/DeleteAll ever touch this
          // collection; never query it with a predicate that would force deserialization.
          var versions = _db.GetCollection<BsonDocument>(BadVersionCol);
          if (versions.Count() > 0)
          {
            versions.DeleteAll();
          }

          if (firstVersionedRun)
          {
            // the FixVersion version document is owned by ApplyDatabaseMigrations (which ran
            // earlier), so a first-versioned database is stamped at CurrentDbVersion, not 1.0.1.
            // The gate mirrors the pre-branch fixVersions.Count()==0 check on purpose: it fires
            // for brand-new databases AND for populated databases that were never version-
            // stamped (users jumping in from a pre-2024 build), but never for an already-
            // versioned user database — even one with no overlays.

            // add default overlays if none exist
            if (!tree.Find(n => n.OverlayData != null && n.Parent != null).Any())
            {
              if (tree.FindOne(n => n.Parent == null && n.Name == Overlays) is { } parentNode)
              {
                var position = TriggerStorePlatform.DefaultTextOverlayPosition();

                var textNode = new TriggerNode
                {
                  Name = "Default Text Overlay",
                  Id = Guid.NewGuid().ToString(),
                  Parent = parentNode.Id,
                  OverlayData = new Overlay
                  {
                    IsDefault = true,
                    IsTextOverlay = true,
                    Left = position.X,
                    Top = position.Y,
                    Height = 150,
                    Width = 450,
                    FontSize = "16pt",
                    FontWeight = "Normal",
                    FontColor = "#FFE9C405",
                    UseTextDropShadow = true
                  }
                };

                SetVerticalAlignment(textNode);
                tree.Insert(textNode);

                var timerNode = new TriggerNode
                {
                  Name = "Default Timer Overlay",
                  Id = Guid.NewGuid().ToString(),
                  Parent = parentNode.Id,
                  OverlayData = new Overlay
                  {
                    IsDefault = true,
                    IsTimerOverlay = true,
                    VerticalAlignment = 0
                  }
                };

                SetVerticalAlignment(timerNode);
                tree.Insert(timerNode);
              }
            }

            // save current values
            _db.Checkpoint();

            if (applyLegacyUpgrades)
            {
              var lastPath = ConfigUtil.GetTriggersLastDbFile();
              if (!string.IsNullOrEmpty(lastPath) && !File.Exists(lastPath))
              {
                try
                {
                  // create backup during for the 1.0.1 upgrade
                  File.Copy(ConfigUtil.GetTriggersDbFile(), lastPath);
                }
                catch (Exception)
                {
                  // ignore
                }
              }
            }
          }
        }
        catch (Exception ex)
        {
          Log.Error("Error opening Trigger Database.", ex);
        }
      }
    }

    /* Fail fast when the host forgot to wire the db file: a null/empty path would build a no-op
     * instance whose every later call throws. Split out of Lazy so it is unit-testable without
     * touching (and permanently caching) the singleton. */
    internal static string ResolveDbFile()
    {
      var path = TriggerStorePlatform.GetDbFile?.Invoke();
      if (string.IsNullOrEmpty(path))
        throw new InvalidOperationException(
          "TriggerStorePlatform.GetDbFile is not wired — set it before first TriggerStateDB.Instance use.");

      return path;
    }

    internal Task<TriggerNode> GetDefaultTextOverlay() => GetDefaultOverlay(true);
    internal Task<TriggerNode> GetDefaultTimerOverlay() => GetDefaultOverlay(false);
    internal Task<TreeData> GetOverlayTree() => GetTree(Overlays);
    internal Task<TreeData> GetTriggerTree(string playerId) => GetTree(Triggers, playerId);
    /* null-safe: if the constructor failed before the task queue existed, Stop() is unavailable */
    internal async Task Dispose()
    {
      if (_taskQueue is { } taskQueue)
        await taskQueue.Stop();
    }

    internal async Task AddCharacter(string name, string filePath, string voice, int voiceRate, int customVolume,
      string activeColor, string idleColor, string resetColor, string fontColor, string parentId = null)
    {
      if (await GetConfig() is { } config)
      {
        EnsureCharacterFolders(config);
        var parent = NormalizeParent(parentId);
        var newCharacter = new TriggerCharacter
        {
          Name = name,
          FilePath = filePath,
          CustomVolume = customVolume,
          Voice = voice,
          VoiceRate = voiceRate,
          ActiveColor = activeColor,
          IdleColor = idleColor,
          ResetColor = resetColor,
          FontColor = fontColor,
          Id = Guid.NewGuid().ToString(),
          Parent = parent,
          Index = GetNextCharacterTreeIndex(config, parent)
        };

        config.Characters.Add(newCharacter);
        await UpdateConfig(config);
      }
    }

    internal async Task Copy(TriggerNode src, TriggerNode dst)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (dst?.Id is { } parentId && src.Clone() is { } copied)
        {
          if (GetCol<TriggerNode>(TreeCol) is { } tree)
          {
            copied.Id = Guid.NewGuid().ToString();
            copied.Name = (tree.FindOne(n => n.Parent == parentId && n.Name == src.Name) != null) ? $"Copied {src.Name}" : src.Name;
            copied.Parent = parentId;
            copied.Index = GetNextIndex(tree, parentId);

            if (copied.TriggerData != null)
            {
              copied.TriggerData.WorstEvalTime = -1;
            }
            else if (copied.OverlayData != null)
            {
              // can only be one
              copied.OverlayData.IsDefault = false;
            }

            tree.Insert(copied);
          }
        }

        return Task.CompletedTask;
      });
    }

    internal async Task CopyState(string nodeId, string from, string to)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (nodeId is not null && GetCol<TriggerNode>(TreeCol) is { } tree &&
            tree.FindOne(n => n.Id == nodeId) is { } node && GetCol<TriggerState>(StatesCol) is { } states)
        {
          var fromState = states.FindOne(s => s.Id == from);
          var toState = states.FindOne(s => s.Id == to);
          if (fromState is not null && toState is not null)
          {
            CopyState(node, fromState, toState);
            states.Update(toState);
          }
        }

        return Task.CompletedTask;
      });
    }

    internal async Task CreateCheckpoint()
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        _db.Checkpoint();
        return Task.CompletedTask;
      });
    }

    /* Checked: the resolved IsChecked for the calling player when its state has an explicit
     * enabled entry for the parent (null = keep the default). Applied to the view node by the
     * WPF host, replacing the old store-side IsChecked poke. */
    internal async Task<(TriggerNode Node, bool? Checked)> CreateFolder(string parentId, string name, string playerId)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var node = CreateNode(parentId, name);
        var checkedFor = SetStateFromParentInternal(parentId, playerId, node?.Id);
        return Task.FromResult((node, checkedFor));
      });
    }

    internal async Task<TriggerNode> CreateOverlay(string parentId, string name, bool isTextOverlay)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var result = CreateNode(parentId, name, Overlays, isTextOverlay);
        return Task.FromResult(result);
      });
    }

    internal async Task<(TriggerNode Node, bool? Checked)> CreateTrigger(string parentId, string name, string playerId)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var node = CreateNode(parentId, name, Triggers);
        var checkedFor = SetStateFromParentInternal(parentId, playerId, node?.Id);
        return Task.FromResult((node, checkedFor));
      });
    }

    internal async Task Delete(string id)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        var removed = new HashSet<string>();
        var removedOverlays = new HashSet<string>();

        if (GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          Delete(tree, tree.FindOne(n => n.Id == id), removed, removedOverlays);

          if (GetCol<TriggerState>(StatesCol) is { } states)
          {
            foreach (var state in states.FindAll().ToArray())
            {
              var needUpdate = false;
              foreach (var removedId in removed)
              {
                if (state.Enabled.Remove(removedId))
                {
                  needUpdate = true;
                }
              }

              if (needUpdate)
              {
                states.Update(state);
              }
            }
          }

          if (removedOverlays.Count > 0)
          {
            foreach (var node in tree.Query().Where(n => n.TriggerData != null && n.TriggerData.SelectedOverlays.Count > 0).ToArray())
            {
              var needUpdate = false;
              foreach (var overlayId in removedOverlays)
              {
                if (node.TriggerData.SelectedOverlays.Remove(overlayId))
                {
                  needUpdate = true;
                }
              }

              if (needUpdate)
              {
                tree.Update(node);
              }
            }
          }
        }

        return Task.CompletedTask;
      });

      DeleteEvent?.Invoke(id);
    }

    internal async Task DeleteCharacter(string id)
    {
      if (await GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
      {
        await _taskQueue.EnqueueTransaction(() =>
        {
          config.Characters.Remove(existing);
          GetCol<TriggerConfig>(ConfigCol)?.Update(config);

          if (GetPlayerState(id) is { } state)
          {
            GetCol<TriggerState>(StatesCol)?.Delete(state.Id);
          }

          return Task.CompletedTask;
        });

        TriggerConfigUpdateEvent?.Invoke(config);
      }
    }

    internal async Task<IEnumerable<OtData>> GetAllOverlays()
    {
      return await _taskQueue.Enqueue(() =>
      {
        IEnumerable<OtData> result = null;
        if (GetCol<TriggerNode>(TreeCol)?.FindAll() is { } all)
        {
          // Materialize: callers (e.g. UnassignAll*Overlays) enumerate the result after this queue
          // callback returns, and a deferred LiteDB cursor would then be read off-queue — possibly
          // while another transaction holds the handle or after it has been disposed.
          result = all.Where(n => n.OverlayData != null)
            .Select(n => new OtData { Name = n.Name, Id = n.Id, OverlayData = n.OverlayData })
            .ToList();
        }
        return Task.FromResult(result ?? []);
      });
    }

    internal Task<TriggerConfig> GetConfig()
    {
      return _taskQueue.EnqueueTransaction(() =>
      {
        if (GetCol<TriggerConfig>(ConfigCol) is { } configs)
        {
          if (configs.Count() == 0)
          {
            configs.Insert(new TriggerConfig { Id = Guid.NewGuid().ToString() });
          }

          var config = configs.FindAll().FirstOrDefault();
          if (config != null)
          {
            EnsureCharacterFolders(config);
            if (AssignCharacterTreeIndicesIfNeeded(config))
            {
              configs.Update(config);
            }
          }

          return Task.FromResult(config);
        }

        return Task.FromResult<TriggerConfig>(null);
      });
    }

    internal async Task<TriggerNode> GetDefaultOverlay(bool isTextOverlay)
    {
      return await _taskQueue.Enqueue(() =>
      {
        TriggerNode result = null;
        if (GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          if (isTextOverlay)
          {
            result = tree.Query().Where(n => n.OverlayData != null && n.OverlayData.IsDefault
              && n.OverlayData.IsTextOverlay).FirstOrDefault();
          }
          else
          {
            result = tree.Query().Where(n => n.OverlayData != null && n.OverlayData.IsDefault
              && n.OverlayData.IsTimerOverlay).FirstOrDefault();
          }
        }

        return Task.FromResult(result);
      });
    }

    internal async Task<List<OtData>> GetEnabledTriggers(string playerId)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var result = new List<OtData>();
        if (GetPlayerState(playerId) is { } state)
        {
          var tree = _db.GetCollection<TriggerNode>(TreeCol);
          foreach (var node in tree.FindAll().Where(n => n.TriggerData != null).ToArray())
          {
            if (node.Id is { } id && state.Enabled.TryGetValue(id, out var value) && value is true)
            {
              // test - now real copies
              var trigCopy = node.TriggerData.Clone();
              var ovlCopy = node.OverlayData?.Clone();
              result.Add(new OtData { Id = node.Id, Name = node.Name, Trigger = trigCopy, OverlayData = ovlCopy });
            }
          }
        }

        return Task.FromResult(result);
      });
    }

    internal async Task<List<LexiconItem>> GetLexicon()
    {
      return await _taskQueue.Enqueue(() => Task.FromResult(GetCol<LexiconItem>(LexiconCol)?.FindAll()?.ToList() ?? []));
    }

    internal async Task<List<TrustedPlayer>> GetTrustedPlayers()
    {
      return await _taskQueue.Enqueue(() => Task.FromResult(GetCol<TrustedPlayer>(TrustedPlayersCol)?.FindAll()?.ToList() ?? []));
    }

    internal async Task<TriggerNode> GetOverlayById(string id)
    {
      return await _taskQueue.Enqueue(() => Task.FromResult(GetCol<TriggerNode>(TreeCol)?.FindOne(n => n.Id == id && n.OverlayData != null)));
    }

    // from GINA, Quick Share, or NAG import (NAG overlays carry their source identity in
    // OverlayData.Source and update the existing node on re-import — see the leaf branch in Import)
    internal async Task ImportOverlays(IEnumerable<ExportTriggerNode> imported)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          // Overlays only have the one root node
          var root = tree.FindOne(n => n.Parent == null && n.Name == Overlays);
          Import(root, imported, Overlays);
        }

        return Task.CompletedTask;
      });

      OverlayImportEvent?.Invoke(true);
    }

    internal async Task ImportTriggers(TriggerNode parent, IEnumerable<ExportTriggerNode> imported)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        Import(parent, imported, Triggers);
        return Task.CompletedTask;
      });

      TriggerImportEvent?.Invoke(true);
    }

    // from GINA or Quick Share with custom Folder name
    // Returns a mapping of OriginalId (e.g. NAG triggerId) → EQLP node Ids for import-time
    // lookups. Multi-phrase NAG triggers produce one node per phrase, so each key maps to a list.
    internal async Task ImportTriggers(string name, IEnumerable<ExportTriggerNode> imported, HashSet<string> characterIds = null)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          var root = tree.FindOne(n => n.Parent == null && n.Name == Triggers);
          // LiteDB trims stored strings, so normalize the folder name to its storable form
          var parent = string.IsNullOrEmpty(name) ? root : CreateNode(root.Id, name.Trim());
          Import(parent, imported, Triggers, characterIds);
        }

        return Task.CompletedTask;
      });

      TriggerImportEvent?.Invoke(true);
    }

    // Count nodes directly under a top-level root (e.g. Triggers) whose names start with a
    // prefix — used to warn when re-importing NAG data would add another copy of an earlier import.
    internal async Task<int> CountChildren(string topName, string namePrefix)
    {
      return await _taskQueue.Enqueue(() =>
      {
        var count = 0;
        if (GetCol<TriggerNode>(TreeCol) is { } tree &&
            tree.FindOne(n => n.Parent == null && n.Name == topName) is { } root)
        {
          count = tree.FindAll().Count(n => n.Parent == root.Id && n.Name.StartsWith(namePrefix, StringComparison.Ordinal));
        }

        return Task.FromResult(count);
      });
    }

    internal async Task<bool> IsAnyEnabled(string triggerId)
    {
      return await _taskQueue.Enqueue(() =>
      {
        if (triggerId != null && GetCol<TriggerState>(StatesCol) is { } states)
        {
          foreach (var state in states.FindAll().ToArray())
          {
            if (state.Enabled.TryGetValue(triggerId, out var enabled) && enabled is true)
            {
              return Task.FromResult(true);
            }
          }
        }
        return Task.FromResult(false);
      });
    }

    internal async Task SaveLexicon(List<LexiconItem> list)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (GetCol<LexiconItem>(LexiconCol) is { } lexicon)
        {
          lexicon.DeleteAll();
          lexicon.InsertBulk(list);
        }

        return Task.CompletedTask;
      });

      LexiconUpdateEvent?.Invoke(list);
    }

    internal async Task SaveTrustedPlayers(List<TrustedPlayer> list)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (GetCol<TrustedPlayer>(TrustedPlayersCol) is { } trustedPlayers)
        {
          trustedPlayers.DeleteAll();
          trustedPlayers.InsertBulk(list);
        }

        return Task.CompletedTask;
      });

      TrustedPlayersUpdateEvent?.Invoke(list);
    }

    internal async Task SetAllExpanded(bool expanded)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = @0", expanded);
        return Task.CompletedTask;
      });
    }

    /* Bound, never interpolated: an imported overlay id containing a quote used to throw LiteException
     * on every expand/collapse of that node. See docs/CodingStandards.md → LiteDB Commands. */
    internal async Task SetExpanded(string id, bool isExpanded)
    {
      await _taskQueue.Enqueue(() =>
      {
        if (id is not null)
        {
          _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = @0 WHERE _id = @1", isExpanded, id);
        }

        return Task.CompletedTask;
      });
    }

    internal async Task SetState(List<string> playerIds, string nodeId, bool? isChecked)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (nodeId is not null && GetCol<TriggerNode>(TreeCol) is { } tree &&
            // overlays have no state
            tree.FindOne(n => n.Id == nodeId) is { OverlayData: null } &&
            GetCol<TriggerState>(StatesCol) is { } states)
        {
          // Load the subtree once — shared by every character's state update below.
          var childrenByParent = new Dictionary<string, List<TriggerNode>>();
          LoadSubtree(tree, nodeId, childrenByParent);

          foreach (var playerId in playerIds)
          {
            if (states.FindOne(s => s.Id == playerId) is { } state)
            {
              UpdateChildState(state, nodeId, isChecked, childrenByParent);
              states.Update(state);
            }
          }
        }

        return Task.CompletedTask;
      });
    }

    internal async Task SetStateFromParent(string parentId, string playerId, string nodeId)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        SetStateFromParentInternal(parentId, playerId, nodeId);
        return Task.CompletedTask;
      });
    }

    internal async Task AssignOverlay(string id, List<TriggerNode> nodes)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        AssignOverlay(GetCol<TriggerNode>(TreeCol), id, nodes);
        return Task.CompletedTask;
      });
    }

    internal async Task AssignPriority(int pri, List<TriggerNode> nodes)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        AssignPriority(GetCol<TriggerNode>(TreeCol), pri, nodes);
        return Task.CompletedTask;
      });
    }

    internal async Task UnassignOverlay(string id, List<TriggerNode> nodes)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        UnassignOverlays(GetCol<TriggerNode>(TreeCol), [id], nodes);
        return Task.CompletedTask;
      });
    }

    internal async Task UnassignAllTextOverlays(List<TriggerNode> nodes)
    {
      var ids = (await GetAllOverlays()).Where(overlay => overlay.OverlayData.IsTextOverlay).Select(overlay => overlay.Id).ToList();
      await _taskQueue.EnqueueTransaction(() =>
      {
        UnassignOverlays(GetCol<TriggerNode>(TreeCol), ids, nodes);
        return Task.CompletedTask;
      });
    }

    internal async Task UnassignAllTimerOverlays(List<TriggerNode> nodes)
    {
      var ids = (await GetAllOverlays()).Where(overlay => overlay.OverlayData.IsTimerOverlay).Select(overlay => overlay.Id).ToList();
      // one set of updates per transaction
      await _taskQueue.EnqueueTransaction(() =>
      {
        UnassignOverlays(GetCol<TriggerNode>(TreeCol), ids, nodes);
        return Task.CompletedTask;
      });
    }

    // node already updated with new parentId that it wants
    internal async Task Update(TriggerNode node, bool updateIndex = false)
    {
      if (node?.Id is null) return;

      await _taskQueue.EnqueueTransaction(() =>
      {
        if (GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          if (updateIndex)
          {
            node.Index = GetNextIndex(tree, node.Parent);
          }

          if (node.OverlayData is { IsDefault: true })
          {
            EnsureNoOtherDefaults(tree, node.Id, node.OverlayData.IsTextOverlay);
          }

          tree.Update(node);
        }

        return Task.CompletedTask;
      });

      TriggerUpdateEvent?.Invoke(node);
    }

    internal async Task UpdateCharacter(TriggerCharacter update)
    {
      if (await GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == update.Id) is { } existing)
      {
        existing.Name = update.Name;
        existing.FilePath = update.FilePath;
        existing.IsEnabled = update.IsEnabled;
        existing.IsWaiting = update.IsWaiting;
        await UpdateConfig(config);
      }
    }

    /* Persist IsEnabled for many characters in one write (folder recursive checkbox). */
    internal async Task UpdateCharactersEnabled(IEnumerable<(string Id, bool Enabled)> updates)
    {
      if (await GetConfig() is not { } config)
      {
        return;
      }

      var byId = updates.ToDictionary(item => item.Id, item => item.Enabled);
      var changed = false;
      foreach (var character in config.Characters)
      {
        if (byId.TryGetValue(character.Id, out var enabled) && character.IsEnabled != enabled)
        {
          character.IsEnabled = enabled;
          if (enabled)
          {
            character.IsWaiting = true;
          }

          changed = true;
        }
      }

      if (changed)
      {
        await UpdateConfig(config);
      }
    }

    /* Create a character folder under parentId (empty/null = root). */
    internal async Task<TriggerCharacterFolder> CreateCharacterFolder(string parentId, string name)
    {
      if (await GetConfig() is not { } config)
      {
        return null;
      }

      EnsureCharacterFolders(config);
      var parent = NormalizeParent(parentId);
      var folder = new TriggerCharacterFolder
      {
        Id = Guid.NewGuid().ToString(),
        Name = name,
        Parent = parent,
        Index = GetNextCharacterTreeIndex(config, parent),
        IsExpanded = true
      };

      config.CharacterFolders.Add(folder);
      await UpdateConfig(config);
      return folder;
    }

    internal async Task RenameCharacterFolder(string id, string name)
    {
      if (await GetConfig() is not { } config)
      {
        return;
      }

      EnsureCharacterFolders(config);
      if (config.CharacterFolders.FirstOrDefault(folder => folder.Id == id) is { } existing)
      {
        existing.Name = name;
        await UpdateConfigSilent(config);
      }
    }

    /* Remove the folder and move its children (characters and nested folders) to the parent. */
    internal async Task DeleteCharacterFolder(string id)
    {
      if (await GetConfig() is not { } config)
      {
        return;
      }

      EnsureCharacterFolders(config);
      if (config.CharacterFolders.FirstOrDefault(folder => folder.Id == id) is not { } existing)
      {
        return;
      }

      var newParent = NormalizeParent(existing.Parent);
      var nextIndex = GetNextCharacterTreeIndex(config, newParent);
      foreach (var folder in config.CharacterFolders.Where(folder => SameParent(folder.Parent, existing.Id)).ToList())
      {
        folder.Parent = newParent;
        folder.Index = nextIndex++;
      }

      foreach (var character in config.Characters.Where(character => SameParent(character.Parent, existing.Id)).ToList())
      {
        character.Parent = newParent;
        character.Index = nextIndex++;
      }

      config.CharacterFolders.Remove(existing);
      await UpdateConfig(config);
    }

    /* Persist Parent/Index after drag-and-drop. */
    internal async Task UpdateCharacterTreePositions(IReadOnlyList<(string Id, bool IsFolder, string Parent, int Index)> positions)
    {
      if (await GetConfig() is not { } config || positions == null || positions.Count == 0)
      {
        return;
      }

      EnsureCharacterFolders(config);
      var folderById = config.CharacterFolders.ToDictionary(folder => folder.Id);
      var characterById = config.Characters.ToDictionary(character => character.Id);
      foreach (var position in positions)
      {
        var parent = NormalizeParent(position.Parent);
        if (position.IsFolder)
        {
          if (folderById.TryGetValue(position.Id, out var folder))
          {
            folder.Parent = parent;
            folder.Index = position.Index;
          }
        }
        else if (characterById.TryGetValue(position.Id, out var character))
        {
          character.Parent = parent;
          character.Index = position.Index;
        }
      }

      await UpdateConfig(config);
    }

    internal async Task SetCharacterFolderExpanded(string id, bool expanded)
    {
      if (await GetConfig() is not { } config)
      {
        return;
      }

      EnsureCharacterFolders(config);
      if (config.CharacterFolders.FirstOrDefault(folder => folder.Id == id) is { } existing)
      {
        existing.IsExpanded = expanded;
        await UpdateConfigSilent(config);
      }
    }

    internal static string NormalizeParent(string parent) => string.IsNullOrEmpty(parent) ? "" : parent;

    internal static bool SameParent(string left, string right) =>
      string.Equals(NormalizeParent(left), NormalizeParent(right), StringComparison.Ordinal);

    internal async Task UpdateCharacter(string id, string name, string filePath, string voice, int voiceRate, int customVolume, string activeColor, string idleColor, string resetColor, string fontColor)
    {
      if (await GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
      {
        existing.Name = name;
        existing.FilePath = filePath;
        existing.CustomVolume = customVolume;
        existing.Voice = voice;
        existing.VoiceRate = voiceRate;
        existing.ActiveColor = activeColor;
        existing.IdleColor = idleColor;
        existing.ResetColor = resetColor;
        existing.FontColor = fontColor;
        await UpdateConfig(config);
      }
    }

    internal async Task UpdateConfig(TriggerConfig config)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        GetCol<TriggerConfig>(ConfigCol)?.Update(config);
        return Task.CompletedTask;
      });

      TriggerConfigUpdateEvent?.Invoke(config);
    }

    internal static string FixColor(string value)
    {
      if (!string.IsNullOrEmpty(value))
      {
        // Opaque white fallback: bare "#FFFFFF" under WPF's default ARGB binding parses as
        // transparent black (invisible text).
        return NormalizeHexColor(value) ?? "#FFFFFFFF";
      }

      return value;
    }

    // Normalizes a legacy color to #AARRGGBB; null when the value is not a hex color (FixColor
    // then falls back to #FFFFFFFF, same as the old non-parseable path). Replaces the Syncfusion
    // ColorConverter — named colors in pre-1.0 data now fall back instead of being resolved.
    internal static string NormalizeHexColor(string value)
    {
      var v = value.Trim();
      if (v.StartsWith('#'))
      {
        v = v[1..];
      }
      else if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      {
        v = v[2..];
      }

      switch (v.Length)
      {
        case 3: // #RGB
          return AllHex(v) ? $"#FF{v[0]}{v[0]}{v[1]}{v[1]}{v[2]}{v[2]}".ToUpperInvariant() : null;
        case 6: // #RRGGBB
          return AllHex(v) ? $"#FF{v}".ToUpperInvariant() : null;
        case 8: // #AARRGGBB
          return AllHex(v) ? $"#{v}".ToUpperInvariant() : null;
        default:
          return null;
      }

      static bool AllHex(string s) => s.All(c =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    /* Persist config without notifying listeners (folder expand/rename). */
    private async Task UpdateConfigSilent(TriggerConfig config)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        GetCol<TriggerConfig>(ConfigCol)?.Update(config);
        return Task.CompletedTask;
      });
    }

    private static void AssignOverlay(ILiteCollection<TriggerNode> tree, string id, List<TriggerNode> nodes)
    {
      if (tree == null || nodes == null || string.IsNullOrEmpty(id)) return;

      foreach (var node in nodes)
      {
        if (node.TriggerData?.SelectedOverlays?.Contains(id) is false)
        {
          node.TriggerData.SelectedOverlays.Add(id);
          tree.Update(node);
        }
      }
    }

    private static void AssignPriority(ILiteCollection<TriggerNode> tree, int pri, List<TriggerNode> nodes)
    {
      if (tree == null || nodes == null || pri < 1 || pri > 5) return;

      foreach (var node in nodes)
      {
        if (node.TriggerData?.Priority is { } priority && priority != pri)
        {
          node.TriggerData.Priority = pri;
          tree.Update(node);
        }
      }
    }

    private static void UnassignOverlays(ILiteCollection<TriggerNode> tree, List<string> ids, List<TriggerNode> nodes)
    {
      if (tree == null || nodes == null || ids.Count == 0) return;

      foreach (var node in nodes)
      {
        if (node.TriggerData.SelectedOverlays.RemoveAll(ids.Contains) > 0)
        {
          tree.Update(node);
        }
      }
    }

    private static void EnsureNoOtherDefaults(ILiteCollection<TriggerNode> tree, string id, bool isTextOverlay)
    {
      if (tree == null) return;

      foreach (var node in tree.Query().Where(o => o.Id != id && o.OverlayData != null &&
        o.OverlayData.IsTextOverlay == isTextOverlay && o.OverlayData.IsDefault).ToArray())
      {
        node.OverlayData.IsDefault = false;
        tree.Update(node);
      }
    }

    private void CopyState(TriggerNode node, TriggerState fromState, TriggerState toState)
    {
      if (node?.Id == null) return;

      if (fromState.Enabled.TryGetValue(node.Id, out var value))
      {
        toState.Enabled[node.Id] = value;
      }
      else
      {
        toState.Enabled.Remove(node.Id);
      }

      if (GetCol<TriggerNode>(TreeCol) is { } tree)
      {
        foreach (var child in tree.Query().Where(n => n.Parent == node.Id).ToArray())
        {
          CopyState(child, fromState, toState);
        }
      }
    }

    private TriggerNode CreateNode(string parentId, string name, string type = null, bool isTextOverlay = false)
    {
      TriggerNode newNode = null;
      if (GetCol<TriggerNode>(TreeCol) is { } tree)
      {
        newNode = new TriggerNode
        {
          Name = name,
          Id = Guid.NewGuid().ToString(),
          Parent = parentId,
          Index = GetNextIndex(tree, parentId),
        };

        if (type == Triggers)
        {
          newNode.TriggerData = new Trigger();
          newNode.IsExpanded = false;
        }
        else if (type == Overlays)
        {
          newNode.OverlayData = new Overlay();
          newNode.IsExpanded = false;
          newNode.OverlayData.IsTimerOverlay = !isTextOverlay;
          newNode.OverlayData.IsTextOverlay = isTextOverlay;
          SetVerticalAlignment(newNode);

          // better default for text
          if (newNode.OverlayData.IsTextOverlay)
          {
            newNode.OverlayData.FontSize = "20pt";
          }
        }
        // folder
        else
        {
          newNode.IsExpanded = true;
        }

        tree.Insert(newNode);
      }

      return newNode;
    }

    private static void Delete(ILiteCollection<TriggerNode> tree, TriggerNode node, HashSet<string> removed, HashSet<string> removedOverlays)
    {
      if (node?.Id is not { } id) return;

      // must be a directory
      if (node.OverlayData == null && node.TriggerData == null)
      {
        foreach (var child in tree.Query().Where(n => n.Parent == id).ToArray())
        {
          Delete(tree, child, removed, removedOverlays);
        }
      }

      if (node.OverlayData != null)
      {
        removedOverlays.Add(id);
      }

      removed.Add(id);
      tree.Delete(id);
    }

    /* Per-import caches: one instance per Import call (i.e. per LiteDB transaction), shared by the
     * whole node loop and its folder recursion. The loop used to redo the same lookups for every
     * single node — a full scan of the overlay collection per trigger (ValidateOverlays) plus a
     * config read, an EQ-directory walk and a bitmap decode per trigger with an icon
     * (CheckMissingMedia). None of those results can change during an import: a trigger import
     * never inserts overlay leaves, the config document is only read here, and sprite/icon files do
     * not appear or vanish mid-import — so caching for one call is exact, not approximate. */
    private sealed class ImportCache
    {
      private readonly Dictionary<string, string> _spritePathByIcon = new(StringComparer.Ordinal);
      private readonly Dictionary<string, bool> _iconValidByPath = new(StringComparer.Ordinal);
      private HashSet<string> _overlayIds;
      private TriggerConfig _config;
      private bool _configRead;

      /* Ids of every overlay leaf: SelectedOverlays may only reference those. */
      internal HashSet<string> GetOverlayIds(ILiteCollection<TriggerNode> tree) =>
        _overlayIds ??= tree.Find(node => node.OverlayData != null).Select(node => node.Id).ToHashSet();

      /* The single TriggerConfig document, read directly because we are inside a transaction. */
      internal TriggerConfig GetConfig(ILiteCollection<TriggerConfig> configs)
      {
        if (_configRead) return _config;

        _configRead = true;
        return _config = configs?.FindAll().FirstOrDefault();
      }

      /* Host probe that scans the EQ installs for a moved sprite file; keyed by exported path. */
      internal string ValidateSpritePath(ILiteCollection<TriggerConfig> configs, string iconSource)
      {
        if (_spritePathByIcon.TryGetValue(iconSource, out var cached)) return cached;

        return _spritePathByIcon[iconSource] = TriggerStorePlatform.ValidateSpritePath(GetConfig(configs), iconSource);
      }

      /* Host probe that loads the bitmap; keyed by the resolved path. */
      internal bool IconIsValid(string iconSource)
      {
        if (_iconValidByPath.TryGetValue(iconSource, out var cached)) return cached;

        return _iconValidByPath[iconSource] = TriggerStorePlatform.IconIsValid(iconSource);
      }
    }

    private void Import(TriggerNode parent, IEnumerable<ExportTriggerNode> imported, string type, HashSet<string> characterIds = null)
    {
      if (parent?.Id is not { } parentId || imported == null || GetCol<TriggerNode>(TreeCol) is not { } tree) return;

      // get character state if needed (here so we can search once)
      List<TriggerState> characterStates = null;
      if (characterIds?.Count > 0 && GetCol<TriggerState>(StatesCol) is { } states)
      {
        characterStates = states.Query().Where(s => characterIds.Contains(s.Id)).ToList();
      }

      var triggers = type == Triggers;

      // LiteDB silently trims leading/trailing whitespace from stored strings (inner spaces are
      // kept). Source data may contain padded names — the NAG dump has triggers like
      // " Emollious colours..." — and re-imports match incoming names against stored ones, so a
      // padded name would never match its trimmed stored twin and duplicate nodes. Normalize every
      // incoming name to the form the store will actually keep before any planning happens.
      var incoming = imported.ToList();
      foreach (var node in incoming)
      {
        NormalizeName(node);
      }

      // one cache for the whole import (see ImportCache) — created here, at the single entry point
      var cache = new ImportCache();

      // Character state documents touched by any node in this import; written once at the end
      // (still inside the caller's transaction) instead of once per merged node per character.
      var dirtyStates = new HashSet<TriggerState>();

      // exports include the tree root so ignore
      foreach (var newNode in incoming)
      {
        if (newNode.Nodes?.Count > 0)
        {
          Import(tree, parentId, newNode.Nodes, type, characterStates, cache, dirtyStates);
        }
        // Overlay leaf nodes (no child Nodes) — process directly via the second overload
        else if (!triggers && newNode.OverlayData != null)
        {
          Import(tree, parentId, new[] { newNode }, type, characterStates, cache, dirtyStates);
        }
      }

      FlushStateUpdates(dirtyStates);
    }

    /* Persists every character state document touched during an import exactly once. Updating per
     * merged node — as this used to — re-serialized the whole Enabled dictionary for every node of
     * every character, which grew quadratically with import size: a 600 trigger / 8 character NAG
     * import went from ~750ms to ~40ms once the writes were batched here. */
    private void FlushStateUpdates(HashSet<TriggerState> dirtyStates)
    {
      if (dirtyStates.Count == 0 || GetCol<TriggerState>(StatesCol) is not { } states) return;

      foreach (var state in dirtyStates)
      {
        states.Update(state);
      }

      dirtyStates.Clear();
    }

    // Trims a name and recurses into folder children (see caller comment for why the trim matters).
    private static void NormalizeName(ExportTriggerNode node)
    {
      node.Name = node.Name?.Trim();
      node.Nodes?.ForEach(NormalizeName);
    }

    private bool Import(ILiteCollection<TriggerNode> tree, string parentId,
      IEnumerable<ExportTriggerNode> imported, string type, List<TriggerState> characterStates, ImportCache cache,
      HashSet<TriggerState> dirtyStates)
    {
      var hasMissingMedia = false;
      var triggers = type == Triggers;

      // One NAG trigger can export several siblings sharing a single OriginalId (phrase + timer
      // variants, counter resets). The planner needs to know which ids occur more than once in
      // THIS batch: an earlier member was already inserted into the live sibling set, so later
      // members must disambiguate by name or they would overwrite each other.
      var nodes = imported.ToList();
      var batchSharedOriginalIds = nodes.Where(n => n.OriginalId != null)
        .GroupBy(n => n.OriginalId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

      // Load the target folder's siblings ONCE instead of re-querying per node: an N-node import
      // used to materialize all S siblings N times (O(N·S) deserializations). Nodes inserted below
      // are appended so later matches see them — exactly like the old live Find calls did.
      // Exception: the outer walker hands each overlay leaf to this method in its own call, where a
      // full sibling load costs more than the indexed single-node seek it replaced. Keep that case
      // cheap (GetNextIndex stays a descending index seek and needs no full list).
      var singleOverlayLeaf = !triggers && nodes.Count == 1 && nodes[0].Id is not null;
      // LiteDB compiles the predicate to an index scan, and it cannot evaluate a captured array
      // access inside the expression tree — keep the id in a local.
      var overlayLeafId = singleOverlayLeaf ? nodes[0].Id : null;
      var siblings = singleOverlayLeaf
        ? tree.Find(n => n.Parent == parentId && n.Id == overlayLeafId).ToList()
        : tree.Find(n => n.Parent == parentId).ToList();
      var nextIndex = singleOverlayLeaf ?
        GetNextIndex(tree, parentId) :
        siblings.Count > 0 ? siblings.Max(n => n.Index) + 1 : 0;

      foreach (var newNode in nodes)
      {
        // per-node: the block at the bottom of this iteration applies it to the node handled above
        string enableId = null;

        if (triggers)
        {
          // Matching + branch selection lives in TriggerImportPlanner (pure, unit-tested on any
          // platform). A leaf updates only an existing leaf and a folder wrapper merges only into
          // an existing folder — siblings of the other kind are inserted as new nodes instead of
          // erasing or dropping the other. See the planner for the full rationale.
          var decision = TriggerImportPlanner.Plan(siblings, newNode, batchSharedOriginalIds);

          switch (decision.Action)
          {
            case ImportAction.UpdateInPlace when decision.Existing is { } foundTrigger:
              // update trigger data — only a payload-carrying incoming node may overwrite (kind
              // safety also lives in the planner; this guard is belt-and-braces)
              if (foundTrigger.TriggerData != null && newNode.TriggerData is { } newTriggerData)
              {
                newTriggerData.SelectedOverlays = ValidateOverlays(newTriggerData.SelectedOverlays, tree, cache);
                foundTrigger.TriggerData = newTriggerData;
                tree.Update(foundTrigger);
                enableId = foundTrigger.Id;
                // OR, not assign: the return value is what flags the CONTAINING folder as having
                // missing media, so a later clean sibling must not clear an earlier hit.
                hasMissingMedia |= CheckMissingMedia(tree, newNode, foundTrigger, cache);
              }

              break;

            case ImportAction.MergeIntoFolder when decision.Existing is { } folder:
              if (Import(tree, folder.Id, newNode.Nodes, type, characterStates, cache, dirtyStates))
              {
                MissingMedia[folder.Id] = true;
                hasMissingMedia = true;
              }

              enableId = folder.Id;
              break;

            case ImportAction.InsertLeaf when newNode.ToTriggerNode() is { } node:
              // new trigger and replace the exported version
              node.TriggerData.SelectedOverlays = ValidateOverlays(newNode.TriggerData.SelectedOverlays, tree, cache);
              Insert(node, nextIndex++);
              siblings.Add(node);
              enableId = node.Id;
              hasMissingMedia |= CheckMissingMedia(tree, newNode, node, cache);
              break;

            case ImportAction.InsertFolder when newNode.ToTriggerNode() is { } node2:
              // make sure it's a new directory and replace the exported version
              Insert(node2, nextIndex++);
              siblings.Add(node2);

              if (Import(tree, node2.Id, newNode.Nodes, type, characterStates, cache, dirtyStates))
              {
                MissingMedia[node2.Id] = true;
                hasMissingMedia = true;
              }

              enableId = node2.Id;
              break;
          }
        }
        else
        {
          // Exported node ids are only trusted for GINA/Quick Share re-exports (the exporter wrote
          // them). External sources like NAG never provide a node id — the store generates UUIDs
          // and the source identity travels in OverlayData.Source instead. A Source match updates
          // the existing overlay in place on re-import, so re-migrating a NAG database refreshes an
          // earlier import's overlays (name included) rather than adding a second copy.
          var matchedBySource = false;
          TriggerNode foundOverlay = siblings.FirstOrDefault(n => n.Id == newNode.Id);
          if (foundOverlay is null && newNode.OverlayData?.Source is { Length: > 0 } source)
          {
            matchedBySource = true;
            foundOverlay = siblings.FirstOrDefault(n => n.OverlayData?.Source == source);
          }

          if (foundOverlay is not null)
          {
            // update overlay data
            if (foundOverlay.OverlayData != null)
            {
              foundOverlay.OverlayData = newNode.OverlayData;
              if (matchedBySource && newNode.Name is { Length: > 0 })
              {
                // the overlay's content follows the latest migration of its NAG source
                foundOverlay.Name = newNode.Name;
              }
              // fix alignment from old imports if needed
              SetVerticalAlignment(foundOverlay);
              tree.Update(foundOverlay);
            }
            // directory but make sure it is one
            else if (foundOverlay.OverlayData == null && foundOverlay.TriggerData == null && newNode.Nodes?.Count > 0)
            {
              Import(tree, foundOverlay.Id, newNode.Nodes, type, characterStates, cache, dirtyStates);
              enableId = foundOverlay.Id;
            }
          }
          else
          {
            // new overlay
            if (newNode.OverlayData != null)
            {
              // fix alignment from old imports if needed
              // Persist a plain TriggerNode (no ExportTriggerNode _type marker) so the
              // document doesn't depend on which assembly the class lives in — the same
              // coupling that required the 1.0.2 legacy-marker migration.
              SetVerticalAlignment(newNode);
              var inserted = newNode.ToTriggerNode();
              // Keep the exported id only while it is free (_id is unique across the collection, and
              // re-importing a share is routine). See docs/DesignNotes.md → Node ids when inserting.
              var exportedId = newNode.Id is { Length: > 0 } exportId && tree.FindById(exportId) is null
                ? exportId
                : null;

              Insert(inserted, nextIndex++, exportedId);
              siblings.Add(inserted);
            }
            // make sure it's a new directory
            else if (newNode.OverlayData == null && newNode.TriggerData == null && newNode.ToTriggerNode() is { } node)
            {
              Insert(node, nextIndex++);
              siblings.Add(node);
              Import(tree, node.Id, newNode.Nodes, type, characterStates, cache, dirtyStates);
              enableId = node.Id;
            }
          }
        }

        if (enableId != null)
        {
          RecentlyMerged[enableId] = true;

          if (characterStates != null)
          {
            foreach (var state in characterStates)
            {
              state.Enabled[enableId] = true;
              dirtyStates.Add(state);
            }
          }
        }
      }

      return hasMissingMedia;

      void Insert(TriggerNode node, int index, string overrideId = null)
      {
        node.Parent = parentId;
        node.Id = overrideId ?? Guid.NewGuid().ToString();
        node.Index = index;
        node.IsExpanded = false;
        tree.Insert(node);
      }
    }

    private bool CheckMissingMedia(ILiteCollection<TriggerNode> tree, ExportTriggerNode imported, TriggerNode stored,
      ImportCache cache)
    {
      if (!string.IsNullOrEmpty(stored.Id) && Check(imported, stored))
      {
        MissingMedia[stored.Id] = true;
        return true;
      }

      bool Check(ExportTriggerNode node, TriggerNode storedNode)
      {
        // set by gina import
        if (node.HasMissingMedia) return true;
        // check icon loads
        if (!string.IsNullOrEmpty(storedNode.TriggerData.IconSource))
        {
          // config read + sprite/icon probes go through ImportCache: both host probes touch the
          // filesystem (directory walk / bitmap decode) and their answers are stable for one import
          var configs = GetCol<TriggerConfig>(ConfigCol);

          // validate path/replace value if similar sprite path found in a different EQ folder
          var updated = false;
          var updatedPath = cache.ValidateSpritePath(configs, storedNode.TriggerData.IconSource);
          if (updatedPath != null && !Equals(updatedPath, storedNode.TriggerData.IconSource))
          {
            storedNode.TriggerData.IconSource = updatedPath;
            updated = true;
          }

          // make sure it actually works
          var valid = cache.IconIsValid(storedNode.TriggerData.IconSource);
          if (valid && updated)
          {
            tree.Update(storedNode);
          }

          return !valid;
        }

        // check sound files
        if (!string.IsNullOrEmpty(storedNode.TriggerData.SoundToPlay) && !TriggerStorePlatform.SoundExists(storedNode.TriggerData.SoundToPlay)) return true;
        if (!string.IsNullOrEmpty(storedNode.TriggerData.EndSoundToPlay) && !TriggerStorePlatform.SoundExists(storedNode.TriggerData.EndSoundToPlay)) return true;
        if (!string.IsNullOrEmpty(storedNode.TriggerData.EndEarlySoundToPlay) && !TriggerStorePlatform.SoundExists(storedNode.TriggerData.EndEarlySoundToPlay)) return true;
        if (!string.IsNullOrEmpty(storedNode.TriggerData.WarningSoundToPlay) && !TriggerStorePlatform.SoundExists(storedNode.TriggerData.WarningSoundToPlay)) return true;
        return false;
      }

      return false;
    }

    /* Applies isEnabled to the node and all its descendants. The caller loads the subtree once
     * (LoadSubtree — one query per folder level) so this walk is pure in-memory; the old version
     * issued one LiteDB query per node, leaves included. */
    private static void UpdateChildState(TriggerState state, string nodeId, bool? isEnabled,
      Dictionary<string, List<TriggerNode>> childrenByParent)
    {
      if (string.IsNullOrEmpty(nodeId)) return;

      state.Enabled[nodeId] = isEnabled;
      if (!childrenByParent.TryGetValue(nodeId, out var children)) return;

      foreach (var child in children)
      {
        UpdateChildState(state, child.Id, isEnabled, childrenByParent);
      }
    }

    /* Loads the subtree under nodeId (one query per folder level) into a parent→children map for
     * the in-memory state walks. Leaf nodes have no entry. */
    private static void LoadSubtree(ILiteCollection<TriggerNode> tree, string nodeId,
      Dictionary<string, List<TriggerNode>> childrenByParent)
    {
      var children = tree.Query().Where(n => n.Parent == nodeId).OrderBy(n => n.Index).ToArray();
      childrenByParent[nodeId] = children.ToList();

      foreach (var child in children)
      {
        if (child.OverlayData == null && child.TriggerData == null)
        {
          LoadSubtree(tree, child.Id, childrenByParent);
        }
      }
    }

    // Enables/disables the node's subtree to match the parent's enabled value. Returns the
    // resolved value for the calling player (null = no explicit parent entry for that player)
    // so Create* can seed a new view node's IsChecked; also raises EventsNodeCheckChanged for an
    // already-visible node (drag-and-drop path).
    private bool? SetStateFromParentInternal(string parentId, string playerId, string nodeId)
    {
      if (GetCol<TriggerState>(StatesCol) is { } states && GetCol<TriggerNode>(TreeCol) is { } tree)
      {
        // Load the subtree once — shared by every character's state update below. Only when there is
        // a node: with no id LoadSubtree walks every root-level folder, and UpdateChildState ignores
        // an empty id anyway (see CreateFolder/CreateTrigger passing node?.Id).
        var childrenByParent = new Dictionary<string, List<TriggerNode>>();
        if (!string.IsNullOrEmpty(nodeId))
        {
          LoadSubtree(tree, nodeId, childrenByParent);
        }

        bool? checkedFor = null;
        foreach (var state in states.FindAll().ToArray())
        {
          // if parent is enabled for the player then also enable the new trigger
          if (state.Enabled.TryGetValue(parentId, out var currentState))
          {
            if (playerId == state.Id)
            {
              checkedFor = currentState is true;
              EventsNodeCheckChanged?.Invoke(nodeId, checkedFor.Value);
            }

            UpdateChildState(state, nodeId, currentState is true, childrenByParent);
            states.Update(state);
          }
        }

        return checkedFor;
      }

      return null;
    }

    // Raw data for the view-tree builders in the WPF host: the root node, every node under it
    // (pre-order, children by Index) and the player's state (null for the overlay tree).
    private Task<TreeData> GetTree(string name, string playerId = null)
    {
      return _taskQueue.EnqueueTransaction(() =>
      {
        TriggerNode root = null;
        TriggerState state = null;
        var nodes = new List<TriggerNode>();

        if (GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          if (name == Triggers)
          {
            state = GetPlayerState(playerId);
          }

          if (tree.FindOne(n => n.Parent == null && n.Name == name) is { } parent)
          {
            root = parent;

            // Load the subtree once — one query per folder level — and share the parent→children
            // map between node collection and state fix-up. The old code ran FixEnabledState and
            // Collect as two separate per-folder query walks over the same tree.
            var childrenByParent = new Dictionary<string, List<TriggerNode>>();
            Collect(tree, parent.Id, nodes, childrenByParent);

            if (name == Triggers && state != null)
            {
              var needUpdate = false;
              FixEnabledState(parent, state, childrenByParent, ref needUpdate);

              if (needUpdate)
              {
                GetCol<TriggerState>(StatesCol)?.Update(state);
              }
            }
          }
        }

        return Task.FromResult(new TreeData(root, nodes, state));
      });

      static void Collect(ILiteCollection<TriggerNode> tree, string parentId, List<TriggerNode> nodes,
        Dictionary<string, List<TriggerNode>> childrenByParent)
      {
        var children = tree.Query().Where(n => n.Parent == parentId).OrderBy(n => n.Index).ToArray();
        childrenByParent[parentId] = children.ToList();

        foreach (var child in children)
        {
          nodes.Add(child);
          if (child.OverlayData == null && child.TriggerData == null)
          {
            Collect(tree, child.Id, nodes, childrenByParent);
          }
        }
      }
    }

    private TriggerState GetPlayerState(string playerId)
    {
      TriggerState state = null;
      if (playerId != null && GetCol<TriggerState>(StatesCol) is { } states)
      {
        state = states.FindOne(s => s.Id == playerId);

        if (state == null)
        {
          state = new TriggerState { Id = playerId };
          states.Insert(state);
        }
      }
      return state;
    }

    /* Filter a trigger's selected overlays down to ids that exist as overlay leaves —
     * SelectedOverlays may only reference overlay leaves, so unknown and non-overlay ids are
     * dropped. The id set comes from ImportCache: loaded once per import instead of scanning the
     * whole overlay collection once per imported trigger. */
    private static List<string> ValidateOverlays(IEnumerable<string> existing, ILiteCollection<TriggerNode> tree,
      ImportCache cache)
    {
      if (existing == null) return [];

      // resolve the id set once instead of on every element
      var overlayIds = cache.GetOverlayIds(tree);
      return existing.Where(overlayIds.Contains).ToList();
    }

    /* Store-side port of the old view-tree walk: re-derives a folder's saved enabled flag from
     * its children (same computation; TriggerNode has no checked state of its own). A child's
     * effective check mirrors CreateViewNode: Enabled value with a false default, null for
     * overlays. Walks the already-loaded parent→children map (GetTree loads each folder level
     * once) instead of re-querying LiteDB per folder. */
    private static void FixEnabledState(TriggerNode folder, TriggerState state,
      Dictionary<string, List<TriggerNode>> childrenByParent, ref bool needUpdate)
    {
      if (folder.OverlayData != null || folder.TriggerData != null) return;

      if (!childrenByParent.TryGetValue(folder.Id, out var children) || children.Count == 0) return;

      foreach (var child in children)
      {
        FixEnabledState(child, state, childrenByParent, ref needUpdate);
      }

      var checkedCount = 0;
      var uncheckCount = 0;

      foreach (var child in children)
      {
        if (ChildChecked(child) is true) checkedCount++;
        else if (ChildChecked(child) is false) uncheckCount++;
      }
      var viewChecked = state.Enabled.GetValueOrDefault(folder.Id, false);
      var changed = false;

      if (checkedCount == children.Count)
      {
        if (viewChecked != true)
        {
          viewChecked = true;
          changed = true;
        }
      }
      else if (uncheckCount == children.Count)
      {
        if (viewChecked != false)
        {
          viewChecked = false;
          changed = true;
        }
      }
      else if (viewChecked != null)
      {
        viewChecked = null;
        changed = true;
      }

      if (changed)
      {
        if (state.Enabled.TryGetValue(folder.Id, out var value))
        {
          if (value != viewChecked)
          {
            state.Enabled[folder.Id] = viewChecked;
            needUpdate = true;
          }
        }
        else
        {
          state.Enabled[folder.Id] = viewChecked;
          needUpdate = true;
        }
      }

      // mirrors CreateViewNode: view IsChecked derives from Enabled with a false default and
      // stays null for overlays (excluded from both counts below)
      bool? ChildChecked(TriggerNode child) =>
        child.OverlayData == null ? (bool?)state.Enabled.GetValueOrDefault(child.Id, false) : null;
    }

    private static void SetVerticalAlignment(TriggerNode overlay)
    {
      if (overlay.OverlayData?.VerticalAlignment == -1)
      {
        overlay.OverlayData.VerticalAlignment = overlay.OverlayData.IsTextOverlay ? AlignBottom : AlignTop;
      }
    }

    private static int GetNextIndex(ILiteCollection<TriggerNode> tree, string parentId)
    {
      var highest = tree.Query().Where(n => n.Parent == parentId).OrderByDescending(n => n.Index).FirstOrDefault();
      return highest?.Index + 1 ?? 0;
    }

    private static void EnsureCharacterFolders(TriggerConfig config)
    {
      config.CharacterFolders ??= [];
      foreach (var character in config.Characters)
      {
        character.Parent = NormalizeParent(character.Parent);
      }

      foreach (var folder in config.CharacterFolders)
      {
        folder.Parent = NormalizeParent(folder.Parent);
      }
    }

    /* First-load: if every character still has Index 0 and there are no folders, keep the old name sort. */
    private static bool AssignCharacterTreeIndicesIfNeeded(TriggerConfig config)
    {
      if (config.CharacterFolders.Count > 0 || config.Characters.Count <= 1)
      {
        return false;
      }

      if (config.Characters.Any(character => character.Index != 0))
      {
        return false;
      }

      config.Characters.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));
      for (var i = 0; i < config.Characters.Count; i++)
      {
        config.Characters[i].Index = i;
      }

      return true;
    }

    private static int GetNextCharacterTreeIndex(TriggerConfig config, string parent)
    {
      parent = NormalizeParent(parent);
      var folderMax = config.CharacterFolders
        .Where(folder => SameParent(folder.Parent, parent))
        .Select(folder => folder.Index)
        .DefaultIfEmpty(-1)
        .Max();
      var characterMax = config.Characters
        .Where(character => SameParent(character.Parent, parent))
        .Select(character => character.Index)
        .DefaultIfEmpty(-1)
        .Max();
      return Math.Max(folderMax, characterMax) + 1;
    }

    /* One-time database migrations, gated by the version number in the existing FixVersion
     * collection (older builds seeded it as {Id:"1", Version:"1.0.1"}): a missing or older
     * stored version applies every step below it; the current version does nothing and every
     * startup pays only for a tiny read of FixVersion. When adding a migration, bump
     * CurrentDbVersion and append a new ordered step here.
     *
     * The new version is stamped only when every step reported success: an incomplete run leaves
     * the stored version alone so the next launch retries, rather than recording a half-applied
     * migration as done (which would strand the documents the failed step never reached). */
    private const string CurrentDbVersion = "1.0.2";

    private void ApplyDatabaseMigrations()
    {
      var versions = _db.GetCollection<BsonDocument>(VersionCol);
      var stored = ReadStoredDbVersion(versions) ?? (0, 0, 0);
      var completed = true;

      /* v1.0.2 — strip the stale ExportTriggerNode type marker from every collection so
       * pre-refactor databases stay readable (see StripLegacyTypeMarkers). ValueTuple has no
       * ordering operators, hence the Comparer. */
      if (Comparer<(int, int, int)>.Default.Compare(stored, (1, 0, 2)) < 0)
      {
        completed = StripLegacyTypeMarkers();
      }

      // future migrations: else if (stored < (1, 0, 3)) { ... }

      if (!completed)
      {
        Log.Error("Trigger database migration did not complete; leaving the stored version unchanged so the next startup retries.");
        return;
      }

      // written only after every step ran to completion, so an interrupted run retries on next launch
      WriteDbVersion(versions, CurrentDbVersion);
    }

    /* Major/Minor/Patch, in order; null when no (readable) version is stored. */
    private static (int, int, int)? ReadStoredDbVersion(ILiteCollection<BsonDocument> versions)
    {
      foreach (var doc in versions.FindAll().ToList())
      {
        if (doc.TryGetValue("_id", out var id) && id.Type == BsonType.String && id.AsString == "1" &&
            doc.TryGetValue("Version", out var raw) && raw.Type == BsonType.String)
        {
          var parts = raw.AsString.Split('.');
          if (parts.Length >= 3 &&
              int.TryParse(parts[0], out var major) &&
              int.TryParse(parts[1], out var minor) &&
              int.TryParse(parts[2], out var patch))
          {
            return (major, minor, patch);
          }
        }
      }

      return null;
    }

    /* Upserts the {Id:"1"} document older builds used for their version stamps. */
    private static void WriteDbVersion(ILiteCollection<BsonDocument> versions, string version)
    {
      var doc = versions.FindById("1");
      if (doc is null)
      {
        versions.Insert(new BsonDocument
        {
          ["_id"] = "1",
          ["Version"] = version
        });
      }
      else
      {
        doc["Version"] = version;
        versions.Update(doc);
      }
    }

    /* Databases written by pre-refactor builds stored imported nodes with LiteDB's polymorphic
     * type marker "_type" = "EQLogParser.ExportTriggerNode, EQLogParser". That class now lives in
     * EQLogParser.Core, so resolving the stale marker throws 'Type ... not found in current
     * domain' and every typed query touching an affected document fails — the whole database
     * becomes unreadable (ctor queries, GetTree/FixEnabledState, LoadOverlayStyles).
     *
     * The pass is raw (BsonDocument) so it cannot itself trip over the marker, and it runs in the
     * constructor before any typed query. Stripping is lossless: nodes were always stored flat via
     * Parent/Id links and the export type persisted nothing beyond TriggerNode itself. Nested child
     * sub-documents are cleaned too. No-op for every database this build writes, so it reports and
     * writes only on the first launch after an upgrade. Returns false when a collection could not
     * be cleaned — the caller then leaves the stored version alone so the next start retries. */
    private bool StripLegacyTypeMarkers()
    {
      const string StaleMarker = "EQLogParser.ExportTriggerNode, EQLogParser";
      var completed = true;

      foreach (var name in _db.GetCollectionNames())
      {
        try
        {
          var removed = 0;
          var raw = _db.GetCollection<BsonDocument>(name);
          // The read must be fully materialized before any write: on the shared connection this
          // store uses, starting a write aborts a live query cursor ("no more active transaction
          // for this cursor") and would leave the rest of the collection un-cleaned.
          foreach (var doc in raw.FindAll().ToList())
          {
            if (StripStaleMarker(doc, StaleMarker))
            {
              raw.Update(doc);
              removed++;
            }
          }

          if (removed > 0)
            Log.Info($"Removed {removed} stale ExportTriggerNode type marker(s) from the '{name}' collection.");
        }
        catch (Exception ex)
        {
          // one bad collection must not block cleanup of the others or app startup, but the
          // migration is not finished either — ApplyDatabaseMigrations must not stamp it done
          completed = false;
          Log.Error($"Failed to clean legacy type markers in the '{name}' collection.", ex);
        }
      }

      return completed;
    }

    /* Removes the stale marker from a document and any nested sub-documents; true if changed. */
    private static bool StripStaleMarker(BsonDocument doc, string staleMarker)
    {
      var changed = false;

      if (doc.TryGetValue("_type", out var marker) &&
          marker.Type == BsonType.String &&
          marker.AsString == staleMarker)
      {
        doc.Remove("_type");
        changed = true;
      }

      foreach (var (field, value) in doc)
      {
        if (value.Type is BsonType.Document && StripStaleMarker(value.AsDocument, staleMarker))
        {
          changed = true;
        }
        else if (value.Type == BsonType.Array)
        {
          foreach (var item in value.AsArray)
          {
            if (item.Type is BsonType.Document && StripStaleMarker(item.AsDocument, staleMarker))
            {
              changed = true;
            }
          }
        }
      }

      return changed;
    }

    private static void UpgradeConfig(ILiteCollection<TriggerConfig> configs)
    {
      if (configs.FindAll().FirstOrDefault() is { } config)
      {
        var needUpdate = false;
        var rate = ConfigUtil.GetSettingAsInteger("TriggersVoiceRate", 0);
        var voice = ConfigUtil.GetSetting("TriggersSelectedVoice");
        if (string.IsNullOrEmpty(config.Voice))
        {
          config.VoiceRate = rate;
          config.Voice = voice;
          needUpdate = true;
        }

        foreach (var character in config.Characters)
        {
          if (string.IsNullOrEmpty(character.Voice))
          {
            character.VoiceRate = rate;
            character.Voice = voice;
            needUpdate = true;
          }
        }

        if (needUpdate)
        {
          configs.Update(config);
        }
      }
    }

    private void UpgradeFromOldParser()
    {
      var overlayIds = new Dictionary<string, string>();
      var defaultEnabled = new Dictionary<string, bool?>();

      ReadJson(LegacyOverlayFile, Overlays);
      ReadJson(LegacyTriggersFile, Triggers);

      if (defaultEnabled.Count > 0)
      {
        var states = GetCol<TriggerState>(StatesCol);
        states?.Insert(new TriggerState { Id = DefaultUser, Enabled = defaultEnabled });
      }

      if (ConfigUtil.IfSetOrElse("TriggersEnabled"))
      {
        var config = new TriggerConfig { IsEnabled = true, Id = Guid.NewGuid().ToString() };
        GetCol<TriggerConfig>(ConfigCol)?.Insert(config);
      }

      _db?.Checkpoint();
      return;

      void ReadJson(string file, string title)
      {
        if (ConfigUtil.ReadConfigFile(file) is { } json)
        {
          try
          {
            if (System.Text.Json.JsonSerializer.Deserialize<LegacyTriggerNode>(json, SerializerOptions) is { } legacy)
            {
              legacy.Name = title;
              UpgradeTree(legacy, overlayIds, defaultEnabled);
            }
          }
          catch (Exception ex)
          {
            Log.Error($"Error Upgrading Triggers {file}", ex);
          }
        }
      }
    }

    private void UpgradeTree(LegacyTriggerNode old, IDictionary<string, string> overlayIds,
      IDictionary<string, bool?> defaultEnabled, string parent = null, int index = -1)
    {
      var newNode = new TriggerNode
      {
        Name = old.Name ?? "Name Unknown",
        IsExpanded = old.IsExpanded,
        Id = Guid.NewGuid().ToString(),
        TriggerData = old.TriggerData,
        Parent = parent,
        Index = index
      };

      // overlays don't have a state
      if (old.OverlayData == null)
      {
        defaultEnabled[newNode.Id] = old.IsEnabled;
      }
      else
      {
        newNode.OverlayData = old.OverlayData.ToOverlay();
        newNode.OverlayData.OverlayColor = FixColor(newNode.OverlayData.OverlayColor);
        newNode.OverlayData.FontColor = FixColor(newNode.OverlayData.FontColor);
        newNode.OverlayData.ActiveColor = FixColor(newNode.OverlayData.ActiveColor);
        newNode.OverlayData.BackgroundColor = FixColor(newNode.OverlayData.BackgroundColor);
        newNode.OverlayData.IdleColor = FixColor(newNode.OverlayData.IdleColor);
        newNode.OverlayData.ResetColor = FixColor(newNode.OverlayData.ResetColor);

        if (old.OverlayData.Id != null)
        {
          overlayIds[old.OverlayData.Id] = newNode.Id;
        }

        SetVerticalAlignment(newNode);
      }

      if (newNode.TriggerData != null)
      {
        newNode.TriggerData.FontColor = FixColor(newNode.TriggerData.FontColor);
        newNode.TriggerData.ActiveColor = FixColor(newNode.TriggerData.ActiveColor);
        newNode.TriggerData.IdleColor = FixColor(newNode.TriggerData.IdleColor);
        newNode.TriggerData.ResetColor = FixColor(newNode.TriggerData.ResetColor);
        if (newNode.TriggerData.SelectedOverlays is { } selected)
        {
          var remapped = selected.Where(overlayIds.ContainsKey).Select(id => overlayIds[id]).ToList();
          selected.Clear();
          selected.AddRange(remapped);
        }
      }

      GetCol<TriggerNode>(TreeCol)?.Insert(newNode);

      if (old.Nodes != null)
      {
        for (var i = 0; i < old.Nodes.Count; i++)
        {
          UpgradeTree(old.Nodes[i], overlayIds, defaultEnabled, newNode.Id, i);
        }
      }
    }

    private ILiteCollection<T> GetCol<T>(string colName) => _db?.GetCollection<T>(colName);
  }

  /* Root + subtree data for the WPF view-tree builders (see TriggerTreeViewBuilder). */
  internal readonly record struct TreeData(TriggerNode Root, List<TriggerNode> Nodes, TriggerState State);

  internal class OtData
  {
    public string Id { get; set; }
    public string Name { get; init; }
    public Trigger Trigger { get; init; }
    public Overlay OverlayData { get; init; }
  }
}

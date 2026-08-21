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
    /// <summary>Closes the LiteDB handle — call before re-opening the same file or on app exit.</summary>
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
    internal event Action<string, bool> NodeCheckChanged;
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
    private const string BadVersionCol = "Version";
    private const string VersionCol = "FixVersion";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerStateDB> Lazy = new(() => new TriggerStateDB(TriggerStorePlatform.GetDbFile?.Invoke()));
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
          // FixVersion version document, so an empty collection list is the only reliable
          // "brand-new file" signal left by the time the bootstrap block below runs.
          var isNewDb = _db.GetCollectionNames().Count() == 0;

          Log.Info($"Opening trigger database: {path}");

          // Must run before any typed query — see ApplyDatabaseMigrations for why.
          ApplyDatabaseMigrations();

          /* print all data
          Directory.CreateDirectory(@"r:\dump");
          foreach (var name in _db.GetCollectionNames())
          {
            var safeName = string.Concat(name.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            var output = Path.Combine(@"r:\dump", $"{safeName}.json");

            _db.Execute($"select $ into $file('{output.Replace("\\", "\\\\")}') from {name}");
          }
          */

          _taskQueue = new LiteDbTaskQueue(_db);

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

          /* legacy type-marker cleanup runs earlier, in StripLegacyTypeMarkers */

          /* fix broken
          var parent = tree.FindOne(n => n.Parent == null && n.Name == Triggers);
          var test = tree.FindOne(n => n.Id == n.Parent);
          if (test != null)
          {
            test.Parent = parent?.Id;
            tree.Update(test);
          }
          */

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

          // remove old bad version
          var versions = _db.GetCollection<Version>(BadVersionCol);
          if (versions.Count() > 0)
          {
            versions.DeleteAll();
          }

          if (isNewDb)
          {
            // the FixVersion version document is owned by ApplyDatabaseMigrations (which ran
            // earlier), so a fresh database is stamped at CurrentDbVersion, not 1.0.1

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
        if (nodeId != null && GetCol<TriggerNode>(TreeCol) is { } tree &&
            tree.FindOne(n => n.Id == nodeId) is { } node && GetCol<TriggerState>(StatesCol) is { } states)
        {
          var fromState = states.FindOne(s => s.Id == from);
          var toState = states.FindOne(s => s.Id == to);
          if (fromState != null && toState != null)
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
          result = all.Where(n => n.OverlayData != null).Select(n => new OtData { Name = n.Name, Id = n.Id, OverlayData = n.OverlayData });
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

    // from GINA or Quick Share
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
          var parent = string.IsNullOrEmpty(name) ? root : CreateNode(root.Id, name);
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
        _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = {expanded}");
        return Task.CompletedTask;
      });
    }

    internal async Task SetExpanded(string id, bool isExpanded)
    {
      await _taskQueue.Enqueue(() =>
      {
        if (id != null)
        {
          _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = {isExpanded} WHERE _id = '{id}'");
        }

        return Task.CompletedTask;
      });
    }

    internal async Task SetState(List<string> playerIds, string nodeId, bool? isChecked)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (nodeId != null && GetCol<TriggerNode>(TreeCol) is { } tree &&
            // overlays have no state
            tree.FindOne(n => n.Id == nodeId) is { OverlayData: null } &&
            GetCol<TriggerState>(StatesCol) is { } states)
        {
          foreach (var playerId in playerIds)
          {
            if (states.FindOne(s => s.Id == playerId) is { } state)
            {
              UpdateChildState(state, tree, nodeId, isChecked);
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

    /* Persist config without notifying listeners (folder expand/rename). */
    private async Task UpdateConfigSilent(TriggerConfig config)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        GetCol<TriggerConfig>(ConfigCol)?.Update(config);
        return Task.CompletedTask;
      });
    }

    internal async void UpdateLastTriggered(string id, double updatedTime)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (id is not null && GetCol<TriggerNode>(TreeCol) is { } tree)
        {
          if (tree.FindOne(n => n.Id == id && n.TriggerData != null) is { } found)
          {
            found.TriggerData.LastTriggered = updatedTime;
            tree.Update(found);
          }
        }

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

      // exports include the tree root so ignore
      foreach (var newNode in imported)
      {
        if (newNode.Nodes?.Count > 0)
        {
          Import(tree, parentId, newNode.Nodes, type, characterStates);
        }
        // Overlay leaf nodes (no child Nodes) — process directly via the second overload
        else if (!triggers && newNode.OverlayData != null)
        {
          Import(tree, parentId, new[] { newNode }, type, characterStates);
        }
      }
    }

    private bool Import(ILiteCollection<TriggerNode> tree, string parentId,
      IEnumerable<ExportTriggerNode> imported, string type, List<TriggerState> characterStates)
    {
      var hasMissingMedia = false;
      var triggers = type == Triggers;
      string enableId = null;

      foreach (var newNode in imported)
      {
        if (triggers)
        {
          // Matching + branch selection lives in TriggerImportPlanner (pure, unit-tested on any
          // platform). A leaf updates only an existing leaf and a folder wrapper merges only into
          // an existing folder — same-named siblings of the other kind are inserted as new nodes
          // instead of erasing or dropping the other. See the planner for the full rationale.
          var decision = TriggerImportPlanner.Plan(tree.Find(n => n.Parent == parentId), newNode);

          switch (decision.Action)
          {
            case ImportAction.UpdateInPlace when decision.Existing is { } foundTrigger:
              // update trigger data
              if (foundTrigger.TriggerData != null)
              {
                foundTrigger.TriggerData = newNode.TriggerData;
                tree.Update(foundTrigger);
                enableId = foundTrigger.Id;
                hasMissingMedia = CheckMissingMedia(tree, newNode, foundTrigger);
              }

              break;

            case ImportAction.MergeIntoFolder when decision.Existing is { } folder:
              if (Import(tree, folder.Id, newNode.Nodes, type, characterStates))
              {
                MissingMedia[folder.Id] = true;
                hasMissingMedia = true;
              }

              enableId = folder.Id;
              break;

            case ImportAction.InsertLeaf when newNode.ToTriggerNode() is { } node:
              // new trigger and replace the exported version
              node.TriggerData.SelectedOverlays = ValidateOverlays(newNode.TriggerData.SelectedOverlays);
              Insert(node, GetNextIndex(tree, parentId));
              enableId = node.Id;
              hasMissingMedia = CheckMissingMedia(tree, newNode, node);
              break;

            case ImportAction.InsertFolder when newNode.ToTriggerNode() is { } node2:
              // make sure it's a new directory and replace the exported version
              Insert(node2, GetNextIndex(tree, parentId));

              if (Import(tree, node2.Id, newNode.Nodes, type, characterStates))
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
          if (tree.FindOne(n => n.Parent == parentId && n.Id == newNode.Id) is { } foundOverlay)
          {
            // update overlay data
            if (foundOverlay.OverlayData != null)
            {
              foundOverlay.OverlayData = newNode.OverlayData;
              // fix alignment from old imports if needed
              SetVerticalAlignment(foundOverlay);
              tree.Update(foundOverlay);
            }
            // directory but make sure it is one
            else if (foundOverlay.OverlayData == null && foundOverlay.TriggerData == null && newNode.Nodes?.Count > 0)
            {
              Import(tree, foundOverlay.Id, newNode.Nodes, type, characterStates);
              enableId = foundOverlay.Id;
            }
          }
          else
          {
            var index = GetNextIndex(tree, parentId);

            // new overlay
            if (newNode.OverlayData != null)
            {
              // fix alignment from old imports if needed
              SetVerticalAlignment(newNode);
              Insert(newNode, index, newNode.Id);
            }
            // make sure it's a new directory
            else if (newNode.OverlayData == null && newNode.TriggerData == null && newNode.ToTriggerNode() is { } node)
            {
              Insert(node, index);
              Import(tree, node.Id, newNode.Nodes, type, characterStates);
              enableId = node.Id;
            }
          }
        }

        if (enableId != null)
        {
          RecentlyMerged[enableId] = true;

          if (characterStates != null && GetCol<TriggerState>(StatesCol) is { } states)
          {
            foreach (var state in characterStates)
            {
              state.Enabled[enableId] = true;
              states.Update(state);
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

    private bool CheckMissingMedia(ILiteCollection<TriggerNode> tree, ExportTriggerNode imported, TriggerNode stored)
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
          // get direct config reference as we are within a transaction
          TriggerConfig config = null;
          if (GetCol<TriggerConfig>(ConfigCol) is { } configs)
          {
            config = configs.FindAll().FirstOrDefault();
          }

          // validate path/replace value if similar sprite path found in a different EQ folder
          var updated = false;
          var updatedPath = TriggerStorePlatform.ValidateSpritePath(config, storedNode.TriggerData.IconSource);
          if (updatedPath != null && !Equals(updatedPath, storedNode.TriggerData.IconSource))
          {
            storedNode.TriggerData.IconSource = updatedPath;
            updated = true;
          }

          // make sure it actually works
          var valid = TriggerStorePlatform.IconIsValid(storedNode.TriggerData.IconSource);
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

    // Store-side port of the old view-tree walk: applies isEnabled to the node and all its
    // descendants (queried from the tree instead of walked through view ChildNodes — same set).
    private static void UpdateChildState(TriggerState state, ILiteCollection<TriggerNode> tree, string nodeId, bool? isEnabled)
    {
      if (string.IsNullOrEmpty(nodeId)) return;

      state.Enabled[nodeId] = isEnabled;
      foreach (var child in tree.Query().Where(n => n.Parent == nodeId).ToArray())
      {
        UpdateChildState(state, tree, child.Id, isEnabled);
      }
    }

    // Enables/disables the node's subtree to match the parent's enabled value. Returns the
    // resolved value for the calling player (null = no explicit parent entry for that player)
    // so Create* can seed a new view node's IsChecked; also raises NodeCheckChanged for an
    // already-visible node (drag-and-drop path).
    private bool? SetStateFromParentInternal(string parentId, string playerId, string nodeId)
    {
      if (GetCol<TriggerState>(StatesCol) is { } states && GetCol<TriggerNode>(TreeCol) is { } tree)
      {
        bool? checkedFor = null;
        foreach (var state in states.FindAll().ToArray())
        {
          // if parent is enabled for the player then also enable the new trigger
          if (state.Enabled.TryGetValue(parentId, out var currentState))
          {
            if (playerId == state.Id)
            {
              checkedFor = currentState is true;
              NodeCheckChanged?.Invoke(nodeId, checkedFor.Value);
            }

            UpdateChildState(state, tree, nodeId, currentState is true);
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

            if (name == Triggers && state != null)
            {
              var needUpdate = false;
              FixEnabledState(tree, parent, state, ref needUpdate);

              if (needUpdate)
              {
                GetCol<TriggerState>(StatesCol)?.Update(state);
              }
            }

            Collect(tree, parent.Id, nodes);
          }
        }

        return Task.FromResult(new TreeData(root, nodes, state));
      });

      static void Collect(ILiteCollection<TriggerNode> tree, string parentId, List<TriggerNode> nodes)
      {
        foreach (var child in tree.Query().Where(n => n.Parent == parentId).OrderBy(n => n.Index).ToArray())
        {
          nodes.Add(child);
          if (child.OverlayData == null && child.TriggerData == null)
          {
            Collect(tree, child.Id, nodes);
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

    private List<string> ValidateOverlays(IEnumerable<string> existing)
    {
      if (GetCol<TriggerNode>(TreeCol) is { } tree)
      {
        var allOverlays = tree.Find(node => node.OverlayData != null).ToList();
        return existing?.Where(id => tree.FindOne(node => node.Id == id) != null).ToList() ?? [];
      }

      return [];
    }

    // Store-side port of the old view-tree walk: re-derives a folder's saved enabled flag from
    // its children (same computation; TriggerNode has no checked state of its own). A child's
    // effective check mirrors CreateViewNode: Enabled value with a false default, null for overlays.
    private static void FixEnabledState(ILiteCollection<TriggerNode> tree, TriggerNode folder, TriggerState state, ref bool needUpdate)
    {
      if (folder.OverlayData != null || folder.TriggerData != null) return;

      var children = tree.Query().Where(n => n.Parent == folder.Id).OrderBy(n => n.Index).ToArray();
      if (children.Length == 0) return;

      foreach (var child in children)
      {
        FixEnabledState(tree, child, state, ref needUpdate);
      }

      var checkedCount = children.Count(child => ChildChecked(child) is true);
      var uncheckCount = children.Count(child => ChildChecked(child) is false);
      var viewChecked = state.Enabled.GetValueOrDefault(folder.Id, false);
      var changed = false;

      if (checkedCount == children.Length)
      {
        if (viewChecked != true)
        {
          viewChecked = true;
          changed = true;
        }
      }
      else if (uncheckCount == children.Length)
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

    // remove eventually
    /* Databases written by pre-refactor builds stored imported nodes with LiteDB's polymorphic
     * type marker "_type" = "EQLogParser.ExportTriggerNode, EQLogParser". That class now lives in
     * EQLogParser.Core, so resolving the stale marker throws 'Type ... not found in current
     * domain' and every typed query touching an affected document fails — the whole database
     * becomes unreadable (ctor queries, GetTree/FixEnabledState, LoadOverlayStyles).
     *
     * The pass is raw (BsonDocument) so it cannot itself trip over the marker, and it runs in
     * the constructor before any typed query, so no earlier failure can skip it. Stripping is
     * lossless: nodes were always stored flat via Parent/Id links and the export type persisted
     * nothing beyond TriggerNode itself. Nested child sub-documents are cleaned too. No-op for
     * every database the current build writes (it never emits the marker), so this reports and
     * writes only on the first launch after an upgrade. */
    /* One-time database migrations, gated by the version number in the existing FixVersion
     * collection (older builds seeded it as {Id:"1", Version:"1.0.1"}): a missing or older
     * stored version applies every step below it; the current version does nothing and every
     * startup pays only for a tiny read of FixVersion. When adding a migration, bump
     * CurrentDbVersion and append a new ordered step here. */
    private const string CurrentDbVersion = "1.0.2";

    private void ApplyDatabaseMigrations()
    {
      var versions = _db.GetCollection<BsonDocument>(VersionCol);
      var stored = ReadStoredDbVersion(versions) ?? (0, 0, 0);

      /* v1.0.2 — strip the stale ExportTriggerNode type marker from every collection so
       * pre-refactor databases stay readable (see StripLegacyTypeMarkers). ValueTuple has no
       * ordering operators, hence the Comparer. */
      if (Comparer<(int, int, int)>.Default.Compare(stored, (1, 0, 2)) < 0)
      {
        StripLegacyTypeMarkers();
      }

      // future migrations: else if (stored < (1, 0, 3)) { ... }

      // written only after every step ran, so an interrupted run retries on next launch
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

    private void StripLegacyTypeMarkers()
    {
      const string StaleMarker = "EQLogParser.ExportTriggerNode, EQLogParser";
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
          // one bad collection must not block cleanup of the others or app startup
          Log.Error($"Failed to clean legacy type markers in the '{name}' collection.", ex);
        }
      }
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
      else if (old.OverlayData != null)
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

    internal static string FixColor(string value)
    {
      if (!string.IsNullOrEmpty(value))
      {
        return NormalizeHexColor(value) ?? "#FFFFFF";
      }

      return value;
    }

    // Normalizes a legacy color to #AARRGGBB; null when the value is not a hex color (FixColor
    // then falls back to #FFFFFF, same as the old non-parseable path). Replaces the Syncfusion
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

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
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EQLogParser
{
  internal class TriggerStateManager
  {
    internal event Action<string> DeleteEvent;
    internal event Action<TriggerNode> TriggerUpdateEvent;
    internal event Action<TriggerConfig> TriggerConfigUpdateEvent;
    internal event Action<bool> TriggerImportEvent;
    internal event Action<List<LexiconItem>> LexiconUpdateEvent;
    internal const string DefaultUser = "Default";
    internal const string Overlays = "Overlays";
    internal const string Triggers = "Triggers";
    internal readonly ConcurrentDictionary<string, bool> RecentlyMerged = new();

    private const string LegacyOverlayFile = "triggerOverlays.json";
    private const string LegacyTriggersFile = "triggers.json";
    private const string ConfigCol = "Config";
    private const string StatesCol = "States";
    private const string TreeCol = "Tree";
    private const string LexiconCol = "Lexicon";
    private const string BadVersionCol = "Version";
    private const string VersionCol = "FixVersion";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerStateManager> Lazy = new(() => new TriggerStateManager());
    internal static TriggerStateManager Instance => Lazy.Value; // instance
    private readonly LiteDbTaskQueue _taskQueue;
    private readonly LiteDatabase _db;

    private TriggerStateManager()
    {
      var path = ConfigUtil.GetTriggersDbFile();
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

          _taskQueue = new LiteDbTaskQueue(_db);

          if (needUpgrade)
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

          // remove old bad version
          var versions = _db.GetCollection<Version>(BadVersionCol);
          if (versions.Count() > 0)
          {
            versions.DeleteAll();
          }

          var fixVersions = _db.GetCollection<VersionData>(VersionCol);
          if (fixVersions.Count() == 0)
          {
            fixVersions.Insert(new VersionData { Id = "1", Version = "1.0.1" });

            // add default overlays if none exist
            if (!tree.Find(n => n.OverlayData != null && n.Parent != null).Any())
            {
              if (tree.FindOne(n => n.Parent == null && n.Name == Overlays) is { } parentNode)
              {
                var position = TriggerUtil.CalculateDefaultTextOverlayPosition();

                var textNode = new TriggerNode
                {
                  Name = "Default Text Overlay",
                  Id = Guid.NewGuid().ToString(),
                  Parent = parentNode.Id,
                  OverlayData = new Overlay
                  {
                    IsDefault = true,
                    IsTextOverlay = true,
                    Left = (long)position.X,
                    Top = (long)position.Y,
                    Height = 150,
                    Width = 450,
                    FontSize = "16pt",
                    FontColor = "#FFE9C405"
                  }
                };

                tree.Insert(textNode);

                var timerNode = new TriggerNode
                {
                  Name = "Default Timer Overlay",
                  Id = Guid.NewGuid().ToString(),
                  Parent = parentNode.Id,
                  OverlayData = new Overlay
                  {
                    IsDefault = true,
                    IsTimerOverlay = true
                  }
                };

                tree.Insert(timerNode);
              }
            }

            // save current values
            _db.Checkpoint();

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
        catch (Exception ex)
        {
          Log.Warn("Error opening Trigger Database.", ex);
        }
      }
    }

    internal Task<TriggerNode> GetDefaultTextOverlay() => GetDefaultOverlay(true);
    internal Task<TriggerNode> GetDefaultTimerOverlay() => GetDefaultOverlay(false);
    internal Task<TriggerTreeViewNode> GetOverlayTreeView() => GetTreeView(Overlays);
    internal Task<TriggerTreeViewNode> GetTriggerTreeView(string playerId) => GetTreeView(Triggers, playerId);
    internal async Task Dispose() => await _taskQueue.Stop();

    internal async Task AddCharacter(string name, string filePath, string voice, int voiceRate, string activeColor, string fontColor)
    {
      if (await GetConfig() is { } config)
      {
        var newCharacter = new TriggerCharacter
        {
          Name = name,
          FilePath = filePath,
          Voice = voice,
          VoiceRate = voiceRate,
          ActiveColor = activeColor,
          FontColor = fontColor,
          Id = Guid.NewGuid().ToString()
        };

        config.Characters.Add(newCharacter);
        config.Characters.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));
        await UpdateConfig(config);
      }
    }

    internal async Task AssignOverlay(string id, IEnumerable<TriggerNode> nodes)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        AssignOverlay(_db?.GetCollection<TriggerNode>(TreeCol), id, nodes);
        return Task.CompletedTask;
      });
    }

    internal async Task AssignPriority(int pri, IEnumerable<TriggerNode> nodes)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        AssignPriority(_db?.GetCollection<TriggerNode>(TreeCol), pri, nodes);
        return Task.CompletedTask;
      });
    }

    internal async Task Copy(TriggerNode src, TriggerNode dst)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (dst?.Id is { } parentId && App.AutoMap.Map(src, new TriggerNode()) is { } copied)
        {
          if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
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

    internal async Task CopyState(TriggerTreeViewNode treeView, string from, string to)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (treeView?.SerializedData is { } node && _db?.GetCollection<TriggerState>(StatesCol) is { } states)
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

    internal async Task<TriggerTreeViewNode> CreateFolder(string parentId, string name, string playerId)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var node = CreateNode(parentId, name);
        SetStateFromParentInternal(parentId, playerId, node);
        return Task.FromResult(node);
      });
    }

    internal async Task<TriggerTreeViewNode> CreateOverlay(string parentId, string name, bool isTextOverlay)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var result = CreateNode(parentId, name, Overlays, isTextOverlay);
        return Task.FromResult(result);
      });
    }

    internal async Task<TriggerTreeViewNode> CreateTrigger(string parentId, string name, string playerId)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var node = CreateNode(parentId, name, Triggers);
        SetStateFromParentInternal(parentId, playerId, node);
        return Task.FromResult(node);
      });
    }

    internal async Task Delete(string id)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        var removed = new HashSet<string>();
        var removedOverlays = new HashSet<string>();

        if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
        {
          Delete(tree, tree.FindOne(n => n.Id == id), removed, removedOverlays);

          if (_db?.GetCollection<TriggerState>(StatesCol) is { } states)
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
          _db?.GetCollection<TriggerConfig>(ConfigCol)?.Update(config);

          if (GetPlayerState(id) is { } state)
          {
            _db?.GetCollection<TriggerState>(StatesCol)?.Delete(state.Id);
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
        if (_db?.GetCollection<TriggerNode>(TreeCol)?.FindAll() is { } all)
        {
          result = all.Where(n => n.OverlayData != null).Select(n => new OtData { Name = n.Name, Id = n.Id, OverlayData = n.OverlayData });
        }
        return Task.FromResult(result ?? Enumerable.Empty<OtData>());
      });
    }

    internal Task<TriggerConfig> GetConfig()
    {
      return _taskQueue.EnqueueTransaction(() =>
      {
        if (_db?.GetCollection<TriggerConfig>(ConfigCol) is { } configs)
        {
          if (configs.Count() == 0)
          {
            configs.Insert(new TriggerConfig { Id = Guid.NewGuid().ToString() });
          }

          return Task.FromResult(configs.FindAll().FirstOrDefault());
        }

        return Task.FromResult<TriggerConfig>(null);
      });
    }

    internal async Task<TriggerNode> GetDefaultOverlay(bool isTextOverlay)
    {
      return await _taskQueue.Enqueue(() =>
      {
        TriggerNode result = null;
        if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
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

    internal async Task<IEnumerable<OtData>> GetEnabledTriggers(string playerId)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        var result = new List<OtData>();
        if (GetPlayerState(playerId) is { } state)
        {
          var tree = _db.GetCollection<TriggerNode>(TreeCol);
          foreach (var node in tree.FindAll().Where(n => n.TriggerData != null).ToArray())
          {
            if (node.Id is { } id && state.Enabled.TryGetValue(id, out var value) && value == true)
            {
              result.Add(new OtData { Id = node.Id, Name = node.Name, Trigger = node.TriggerData, OverlayData = node.OverlayData });
            }
          }
        }

        return Task.FromResult(result);
      });
    }

    internal async Task<List<LexiconItem>> GetLexicon()
    {
      return await _taskQueue.Enqueue(() => Task.FromResult(_db?.GetCollection<LexiconItem>(LexiconCol)?.FindAll()?.ToList() ?? []));
    }

    internal async Task<TriggerNode> GetOverlayById(string id)
    {
      return await _taskQueue.Enqueue(() => Task.FromResult(_db?.GetCollection<TriggerNode>(TreeCol)?.FindOne(n => n.Id == id && n.OverlayData != null)));
    }

    internal async Task ImportOverlays(TriggerNode parent, IEnumerable<ExportTriggerNode> imported)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        Import(parent, imported, Overlays);
        return Task.CompletedTask;
      });
    }

    internal async Task ImportTriggers(TriggerNode parent, IEnumerable<ExportTriggerNode> imported)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        Import(parent, imported, Triggers);
        return Task.CompletedTask;
      });
    }

    internal async Task<bool> IsAnyEnabled(string triggerId)
    {
      return await _taskQueue.Enqueue(() =>
      {
        if (triggerId != null && _db?.GetCollection<TriggerState>(StatesCol) is { } states)
        {
          foreach (var state in states.FindAll().ToArray())
          {
            if (state.Enabled.TryGetValue(triggerId, out var enabled) && enabled == true)
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
        if (_db?.GetCollection<LexiconItem>(LexiconCol) is { } lexicon)
        {
          lexicon.DeleteAll();
          lexicon.InsertBulk(list);
        }

        return Task.CompletedTask;
      });

      LexiconUpdateEvent?.Invoke(list);
    }

    internal async Task SetAllExpanded(bool expanded)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = {expanded}");
        return Task.CompletedTask;
      });
    }

    internal async Task SetExpanded(TriggerTreeViewNode viewNode)
    {
      await _taskQueue.Enqueue(() =>
      {
        if (viewNode?.SerializedData is { Id: not null } node)
        {
          _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = {viewNode.IsExpanded} WHERE _id = '{node.Id}'");
        }

        return Task.CompletedTask;
      });
    }

    internal async Task SetState(List<string> playerIds, TriggerTreeViewNode viewNode)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (viewNode?.SerializedData is not null && !viewNode.IsOverlay() &&
            _db?.GetCollection<TriggerState>(StatesCol) is { } states)
        {
          foreach (var playerId in playerIds)
          {
            if (states.FindOne(s => s.Id == playerId) is { } state)
            {
              UpdateChildState(state, viewNode, viewNode.IsChecked);
              states.Update(state);
            }
          }
        }

        return Task.CompletedTask;
      });
    }

    internal async Task SetStateFromParent(string parentId, string playerId, TriggerTreeViewNode node)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        SetStateFromParentInternal(parentId, playerId, node);
        return Task.CompletedTask;
      });
    }

    internal async Task UnassignOverlay(string id, IEnumerable<TriggerNode> nodes)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        UnassignOverlay(_db?.GetCollection<TriggerNode>(TreeCol), id, nodes);
        return Task.CompletedTask;
      });
    }

    // node already updated with new parentId that it wants
    internal async Task Update(TriggerNode node, bool updateIndex = false)
    {
      if (node?.Id is null) return;

      await _taskQueue.EnqueueTransaction(() =>
      {
        if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
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

    internal async Task UpdateCharacter(string id, string name, string filePath, string voice, int voiceRate, string activeColor, string fontColor)
    {
      if (await GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
      {
        existing.Name = name;
        existing.FilePath = filePath;
        existing.Voice = voice;
        existing.VoiceRate = voiceRate;
        existing.ActiveColor = activeColor;
        existing.FontColor = fontColor;
        await UpdateConfig(config);
      }
    }

    internal async Task UpdateConfig(TriggerConfig config)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        _db?.GetCollection<TriggerConfig>(ConfigCol)?.Update(config);
        return Task.CompletedTask;
      });

      TriggerConfigUpdateEvent?.Invoke(config);
    }

    internal async void UpdateLastTriggered(string id, double updatedTime)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (id is not null && _db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
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

    // from GINA or Quick Share with custom Folder name
    internal async Task ImportTriggers(string name, IEnumerable<ExportTriggerNode> imported, HashSet<string> characterIds)
    {
      await _taskQueue.EnqueueTransaction(() =>
      {
        if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
        {
          var root = tree.FindOne(n => n.Parent == null && n.Name == Triggers);
          var parent = string.IsNullOrEmpty(name) ? root : CreateNode(root.Id, name).SerializedData;
          Import(parent, imported, Triggers, characterIds);
        }

        return Task.CompletedTask;
      });

      TriggerImportEvent?.Invoke(true);
    }

    private static void AssignOverlay(ILiteCollection<TriggerNode> tree, string id, IEnumerable<TriggerNode> nodes)
    {
      if (tree == null || nodes == null) return;

      foreach (var node in nodes)
      {
        if (node.TriggerData?.SelectedOverlays.Contains(id) == false)
        {
          node.TriggerData.SelectedOverlays.Add(id);
          tree.Update(node);
        }
        else if (node.TriggerData == null && node.OverlayData == null)
        {
          AssignOverlay(tree, id, tree.Find(n => n.Parent == node.Id));
        }
      }
    }

    private static void UnassignOverlay(ILiteCollection<TriggerNode> tree, string id, IEnumerable<TriggerNode> nodes)
    {
      if (tree == null || nodes == null) return;

      foreach (var node in nodes)
      {
        if (node.TriggerData?.SelectedOverlays.Contains(id) == true)
        {
          node.TriggerData.SelectedOverlays.Remove(id);
          tree.Update(node);
        }
        else if (node.TriggerData == null && node.OverlayData == null)
        {
          UnassignOverlay(tree, id, tree.Find(n => n.Parent == node.Id));
        }
      }
    }

    private static void AssignPriority(ILiteCollection<TriggerNode> tree, int pri, IEnumerable<TriggerNode> nodes)
    {
      if (tree == null || nodes == null) return;

      foreach (var node in nodes)
      {
        if (node.TriggerData?.Priority is { } priority && priority != pri)
        {
          node.TriggerData.Priority = pri;
          tree.Update(node);
        }
        else if (node.TriggerData == null && node.OverlayData == null)
        {
          AssignPriority(tree, pri, tree.Find(n => n.Parent == node.Id));
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

      toState.Enabled[node.Id] = fromState.Enabled[node.Id];
      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        foreach (var child in tree.Query().Where(n => n.Parent == node.Id).ToArray())
        {
          CopyState(child, fromState, toState);
        }
      }
    }

    private TriggerTreeViewNode CreateNode(string parentId, string name, string type = null, bool isTextOverlay = false)
    {
      TriggerTreeViewNode viewNode = null;
      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        var newNode = new TriggerNode
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
          newNode.OverlayData.VerticalAlignment = (int)(isTextOverlay ? VerticalAlignment.Bottom : VerticalAlignment.Top);

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
        viewNode = CreateViewNode(newNode);
      }

      return viewNode;
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
      if (parent?.Id is not { } parentId || imported == null || _db?.GetCollection<TriggerNode>(TreeCol) is not { } tree) return;

      // get character state if needed (here so we can search once)
      List<TriggerState> characterStates = null;
      if (characterIds?.Count > 0 && _db?.GetCollection<TriggerState>(StatesCol) is { } states)
      {
        characterStates = states.Query().Where(s => characterIds.Contains(s.Id)).ToList();
      }

      // exports include the tree root so ignore
      foreach (var newNode in imported)
      {
        if (newNode.Nodes?.Count > 0)
        {
          Import(tree, parentId, newNode.Nodes, type, characterStates);
        }
      }
    }

    private void Import(ILiteCollection<TriggerNode> tree, string parentId,
      IEnumerable<ExportTriggerNode> imported, string type, List<TriggerState> characterStates)
    {
      var triggers = type == Triggers;
      string enableId = null;

      foreach (var newNode in imported)
      {
        if (triggers)
        {
          if (tree.FindOne(n => n.Parent == parentId && n.Name == newNode.Name) is { } foundTrigger)
          {
            // update trigger data
            if (foundTrigger.TriggerData != null)
            {
              foundTrigger.TriggerData = newNode.TriggerData;
              tree.Update(foundTrigger);
              enableId = foundTrigger.Id;
            }
            // directory but make sure it is one
            else if (foundTrigger.OverlayData == null && foundTrigger.TriggerData == null && newNode.Nodes?.Count > 0)
            {
              Import(tree, foundTrigger.Id, newNode.Nodes, type, characterStates);
              enableId = foundTrigger.Id;
            }
          }
          else
          {
            var index = GetNextIndex(tree, parentId);

            // new trigger
            if (newNode.TriggerData != null)
            {
              newNode.TriggerData.SelectedOverlays = ValidateOverlays(newNode.TriggerData.SelectedOverlays);
              Insert(newNode, index);
              enableId = newNode.Id;
            }
            // make sure it's a new directory
            else if (newNode.OverlayData == null && newNode.TriggerData == null && App.AutoMap.Map(newNode, new TriggerNode()) is { } node)
            {
              Insert(node, index);
              Import(tree, node.Id, newNode.Nodes, type, characterStates);
              enableId = node.Id;
            }
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
              newNode.OverlayData = newNode.OverlayData;
              // fix alignment from old imports if needed
              SetVerticalAlignment(newNode);
              Insert(newNode, index, newNode.Id);
            }
            // make sure it's a new directory
            else if (newNode.OverlayData == null && newNode.TriggerData == null && App.AutoMap.Map(newNode, new TriggerNode()) is { } node)
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
          if (characterStates != null && _db?.GetCollection<TriggerState>(StatesCol) is { } states)
          {
            foreach (var state in characterStates)
            {
              state.Enabled[enableId] = true;
              states.Update(state);
            }
          }
        }
      }

      return;

      void Insert(TriggerNode node, int index, string overrideId = null)
      {
        node.Parent = parentId;
        node.Id = overrideId ?? Guid.NewGuid().ToString();
        node.Index = index;
        node.IsExpanded = false;
        tree.Insert(node);
      }
    }

    private static void UpdateChildState(TriggerState state, TriggerTreeViewNode node, bool? isEnabled)
    {
      if (node?.SerializedData?.Id is not { } id) return;

      state.Enabled[id] = isEnabled;
      foreach (var child in node.ChildNodes)
      {
        UpdateChildState(state, child as TriggerTreeViewNode, isEnabled);
      }
    }

    private void SetStateFromParentInternal(string parentId, string playerId, TriggerTreeViewNode node)
    {
      if (_db?.GetCollection<TriggerState>(StatesCol) is { } states)
      {
        foreach (var state in states.FindAll().ToArray())
        {
          // if parent is enabled for the player then also enable the new trigger
          if (state.Enabled.TryGetValue(parentId, out var currentState))
          {
            if (playerId == state.Id)
            {
              UiUtil.InvokeNow(() =>
              {
                node.IsChecked = currentState == true;
              }, DispatcherPriority.Render);
            }

            UpdateChildState(state, node, currentState == true);
            states.Update(state);
          }
        }
      }
    }

    private async Task<TriggerTreeViewNode> GetTreeView(string name, string playerId = null)
    {
      return await _taskQueue.EnqueueTransaction(() =>
      {
        TriggerTreeViewNode root = null;
        if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
        {
          TriggerState state = null;
          if (name == Triggers)
          {
            state = GetPlayerState(playerId);
          }

          if (tree.FindOne(n => n.Parent == null && n.Name == name) is { } parent)
          {
            root = CreateViewNode(parent, state);
            Populate(root, state, tree);
          }

          if (name == Triggers && state != null)
          {
            var needUpdate = false;
            FixEnabledState(root, state, ref needUpdate);

            if (needUpdate)
            {
              _db?.GetCollection<TriggerState>(StatesCol)?.Update(state);
            }
          }
        }

        return Task.FromResult(root);
      });
    }

    private TriggerState GetPlayerState(string playerId)
    {
      TriggerState state = null;
      if (playerId != null && _db?.GetCollection<TriggerState>(StatesCol) is { } states)
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

    private TriggerTreeViewNode CreateViewNode(TriggerNode node, TriggerState state = null)
    {
      var treeNode = new TriggerTreeViewNode
      {
        Content = node.Name,
        IsExpanded = node.IsExpanded,
        SerializedData = node,
        IsRecentlyMerged = RecentlyMerged.ContainsKey(node.Id)
      };

      if (node.OverlayData == null && state != null)
      {
        treeNode.IsChecked = state.Enabled.GetValueOrDefault(node.Id, false);
      }

      return treeNode;
    }

    private void Populate(TriggerTreeViewNode parent, TriggerState state, ILiteCollection<TriggerNode> tree)
    {
      if (parent.SerializedData.Id is { } parentId)
      {
        foreach (var node in tree.Query().Where(n => n.Parent == parentId).OrderBy(n => n.Index).ToArray())
        {
          var child = CreateViewNode(node, state);
          if (child.IsDir())
          {
            Populate(child, state, tree);
          }

          parent.ChildNodes.Add(child);
        }
      }
    }

    private List<string> ValidateOverlays(IEnumerable<string> existing)
    {
      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        var allOverlays = tree.Find(node => node.OverlayData != null).ToList();
        return existing?.Where(id => tree.FindOne(node => node.Id == id) != null).ToList() ?? [];
      }

      return [];
    }

    private static void FixEnabledState(TriggerTreeViewNode viewNode, TriggerState state, ref bool needUpdate)
    {
      if (!viewNode.IsDir()) return;

      if (viewNode.HasChildNodes)
      {
        foreach (var child in viewNode.ChildNodes)
        {
          FixEnabledState(child as TriggerTreeViewNode, state, ref needUpdate);
        }

        var checkedCount = viewNode.ChildNodes.Count(c => c.IsChecked == true);
        var uncheckCount = viewNode.ChildNodes.Count(c => c.IsChecked == false);
        var changed = false;

        if (checkedCount == viewNode.ChildNodes.Count)
        {
          if (viewNode.IsChecked != true)
          {
            viewNode.IsChecked = true;
            changed = true;
          }
        }
        else if (uncheckCount == viewNode.ChildNodes.Count)
        {
          if (viewNode.IsChecked != false)
          {
            viewNode.IsChecked = false;
            changed = true;
          }
        }
        else if (viewNode.IsChecked != null)
        {
          viewNode.IsChecked = null;
          changed = true;
        }

        if (changed)
        {
          if (state.Enabled.TryGetValue(viewNode.SerializedData.Id, out var value))
          {
            if (value != viewNode.IsChecked)
            {
              state.Enabled[viewNode.SerializedData.Id] = viewNode.IsChecked;
              needUpdate = true;
            }
          }
          else
          {
            state.Enabled[viewNode.SerializedData.Id] = viewNode.IsChecked;
            needUpdate = true;
          }
        }
      }
    }

    private static void SetVerticalAlignment(TriggerNode overlay)
    {
      if (overlay.OverlayData?.VerticalAlignment == -1)
      {
        overlay.OverlayData.VerticalAlignment = (int)(overlay.OverlayData.IsTextOverlay ? VerticalAlignment.Bottom : VerticalAlignment.Top);
      }
    }

    private static int GetNextIndex(ILiteCollection<TriggerNode> tree, string parentId)
    {
      var highest = tree.Query().Where(n => n.Parent == parentId).OrderByDescending(n => n.Index).FirstOrDefault();
      return highest?.Index + 1 ?? 0;
    }

    // remove eventually
    private static void UpgradeConfig(ILiteCollection<TriggerConfig> configs)
    {
      if (configs.FindAll().FirstOrDefault() is { } config)
      {
        var needUpdate = false;
        var rate = ConfigUtil.GetSettingAsInteger("TriggersVoiceRate");
        var voice = ConfigUtil.GetSetting("TriggersSelectedVoice");
        if (string.IsNullOrEmpty(config.Voice))
        {
          config.VoiceRate = (rate == int.MaxValue) ? 0 : rate;
          config.Voice = voice;
          needUpdate = true;
        }

        foreach (var character in config.Characters)
        {
          if (string.IsNullOrEmpty(character.Voice))
          {
            character.VoiceRate = (rate == int.MaxValue) ? 0 : rate;
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
        var states = _db?.GetCollection<TriggerState>(StatesCol);
        states?.Insert(new TriggerState { Id = DefaultUser, Enabled = defaultEnabled });
      }

      if (ConfigUtil.IfSetOrElse("TriggersEnabled"))
      {
        var config = new TriggerConfig { IsEnabled = true, Id = Guid.NewGuid().ToString() };
        _db?.GetCollection<TriggerConfig>(ConfigCol)?.Insert(config);
      }

      _db?.Checkpoint();
      return;

      void ReadJson(string file, string title)
      {
        if (ConfigUtil.ReadConfigFile(file) is { } json)
        {
          try
          {
            if (JsonSerializer.Deserialize<LegacyTriggerNode>(json, new JsonSerializerOptions { IncludeFields = true }) is { } legacy)
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
        newNode.OverlayData = App.AutoMap.Map(old.OverlayData, new Overlay());
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
      }

      if (newNode.TriggerData != null)
      {
        newNode.TriggerData.FontColor = FixColor(newNode.TriggerData.FontColor);
        if (newNode.TriggerData.SelectedOverlays is { } selected)
        {
          var remapped = selected.Where(overlayIds.ContainsKey).Select(id => overlayIds[id]).ToList();
          selected.Clear();
          selected.AddRange(remapped);
        }
      }

      _db?.GetCollection<TriggerNode>(TreeCol)?.Insert(newNode);

      if (old.Nodes != null)
      {
        for (var i = 0; i < old.Nodes.Count; i++)
        {
          UpgradeTree(old.Nodes[i], overlayIds, defaultEnabled, newNode.Id, i);
        }
      }
    }

    private static string FixColor(string value)
    {
      if (!string.IsNullOrEmpty(value))
      {
        if (ColorConverter.ConvertFromString(value) is Color color)
        {
          return color.ToHexString();
        }
        return "#FFFFFF";
      }

      return value;
    }

    private class VersionData
    {
      public string Id { get; set; }
      public string Version { get; set; }
    }
  }

  internal class OtData
  {
    public string Id { get; set; }
    public string Name { get; init; }
    public Trigger Trigger { get; init; }
    public Overlay OverlayData { get; init; }
  }
}

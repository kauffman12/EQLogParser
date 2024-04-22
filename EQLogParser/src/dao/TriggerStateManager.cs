using LiteDB;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
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
    private const string VersionCol = "Version";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerStateManager> Lazy = new(() => new TriggerStateManager());
    internal static TriggerStateManager Instance => Lazy.Value; // instance
    private readonly object _lockObject = new();
    private readonly object _configLock = new();
    private readonly LiteDatabase _db;

    private TriggerStateManager()
    {
      var path = ConfigUtil.GetTriggersDbFile();
      var needUpgrade = !File.Exists(path);

      try
      {
        _db = new LiteDatabase(path)
        {
          CheckpointSize = 10
        };

        if (needUpgrade)
        {
          // upgrade from old json trigger format
          Upgrade();
        }

        var configs = _db.GetCollection<TriggerConfig>(ConfigCol);
        configs.EnsureIndex(x => x.Id);
        UpgradeConfig(configs);

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

        tree.EnsureIndex(x => x.Id);
        tree.EnsureIndex(x => x.Parent);
        tree.EnsureIndex(x => x.Name);

        var states = _db.GetCollection<TriggerState>(StatesCol);
        states.EnsureIndex(x => x.Id);

        // add version if not defined
        var versions = _db.GetCollection<Version>(VersionCol);
        if (versions.FindAll().FirstOrDefault() == null)
        {
          versions.Insert(new Version(1, 0, 0));

          // add default overlays if none exist
          if (tree.Find(n => n.OverlayData != null && n.Parent != null).FirstOrDefault() == null)
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
        }
      }
      catch (Exception ex)
      {
        if (ex is IOException)
        {
          Log.Warn("Trigger Database already in use.");
        }
        else
        {
          Log.Error(ex);
        }
      }
    }

    internal void AssignOverlay(string id, IEnumerable<TriggerNode> nodes) => AssignOverlay(_db?.GetCollection<TriggerNode>(TreeCol), id, nodes);
    internal void AssignPriority(int pri, IEnumerable<TriggerNode> nodes) => AssignPriority(_db?.GetCollection<TriggerNode>(TreeCol), pri, nodes);
    internal void UnassignOverlay(string id, IEnumerable<TriggerNode> nodes) => UnassignOverlay(_db?.GetCollection<TriggerNode>(TreeCol), id, nodes);
    internal TriggerTreeViewNode CreateFolder(string parentId, string name) => CreateNode(parentId, name);
    internal TriggerTreeViewNode CreateTrigger(string parentId, string name) => CreateNode(parentId, name, Triggers);
    internal TriggerTreeViewNode CreateOverlay(string parentId, string name, bool isTextOverlay) => CreateNode(parentId, name, Overlays, isTextOverlay);
    internal TriggerTreeViewNode GetTriggerTreeView(string playerId) => GetTreeView(Triggers, playerId);
    internal TriggerTreeViewNode GetOverlayTreeView() => GetTreeView(Overlays);
    internal TriggerNode GetDefaultTextOverlay() => GetDefaultOverlay(true);
    internal TriggerNode GetDefaultTimerOverlay() => GetDefaultOverlay(false);
    internal TriggerNode GetOverlayById(string id) => _db?.GetCollection<TriggerNode>(TreeCol).FindOne(n => n.Id == id && n.OverlayData != null);
    internal void ImportTriggers(TriggerNode parent, IEnumerable<ExportTriggerNode> imported) => Import(parent, imported, Triggers);
    internal void ImportOverlays(TriggerNode parent, IEnumerable<ExportTriggerNode> imported) => Import(parent, imported, Overlays);
    internal bool IsActive() => _db != null;
    internal void SetAllExpanded(bool expanded) => _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = {expanded}");
    internal IEnumerable<LexiconItem> GetLexicon() => _db?.GetCollection<LexiconItem>(LexiconCol)?.FindAll();
    internal void Stop() => _db?.Dispose();

    internal void SaveLexicon(List<LexiconItem> list)
    {
      if (_db?.GetCollection<LexiconItem>(LexiconCol) is { } lexicon)
      {
        lexicon.DeleteAll();
        lexicon.InsertBulk(list);
        LexiconUpdateEvent?.Invoke(list);
      }
    }

    internal void AddCharacter(string name, string filePath, string voice, int voiceRate, string activeColor, string fontColor)
    {
      if (GetConfig() is { } config)
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
        UpdateConfig(config);
      }
    }

    internal void CopyState(TriggerTreeViewNode treeView, string from, string to)
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
    }

    internal void DeleteCharacter(string id)
    {
      if (GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
      {
        config.Characters.Remove(existing);
        UpdateConfig(config);

        if (GetPlayerState(id) is { } state)
        {
          _db?.GetCollection<TriggerState>(StatesCol)?.Delete(state.Id);
        }
      }
    }

    internal void UpdateCharacter(TriggerCharacter update)
    {
      if (GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == update.Id) is { } existing)
      {
        existing.Name = update.Name;
        existing.FilePath = update.FilePath;
        existing.IsEnabled = update.IsEnabled;
        UpdateConfig(config);
      }
    }

    internal void UpdateCharacter(string id, string name, string filePath, string voice, int voiceRate, string activeColor, string fontColor)
    {
      if (GetConfig() is { } config && config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
      {
        existing.Name = name;
        existing.FilePath = filePath;
        existing.Voice = voice;
        existing.VoiceRate = voiceRate;
        existing.ActiveColor = activeColor;
        existing.FontColor = fontColor;
        UpdateConfig(config);
      }
    }

    internal void UpdateLastTriggered(string id, double updatedTime)
    {
      if (id is not null && _db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        if (tree.FindOne(n => n.Id == id && n.TriggerData != null) is { } found)
        {
          found.TriggerData.LastTriggered = updatedTime;
          tree.Update(found);
        }
      }
    }

    internal TriggerConfig GetConfig()
    {
      if (_db?.GetCollection<TriggerConfig>(ConfigCol) is { } configs)
      {
        lock (_configLock)
        {
          if (configs.Count() == 0)
          {
            configs.Insert(new TriggerConfig { Id = Guid.NewGuid().ToString() });
          }

          return configs.FindAll().FirstOrDefault();
        }
      }

      return null;
    }

    internal void UpdateConfig(TriggerConfig config)
    {
      _db?.GetCollection<TriggerConfig>(ConfigCol).Update(config);
      TriggerConfigUpdateEvent?.Invoke(config);
    }

    internal TriggerNode GetDefaultOverlay(bool isTextOverlay)
    {
      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        if (isTextOverlay)
        {
          return tree.Query().Where(n => n.OverlayData != null && n.OverlayData.IsDefault && n.OverlayData.IsTextOverlay).FirstOrDefault();
        }

        return tree.Query().Where(n => n.OverlayData != null && n.OverlayData.IsDefault && n.OverlayData.IsTimerOverlay).FirstOrDefault();
      }
      return null;
    }

    internal void Copy(TriggerNode src, TriggerNode dst)
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
    }

    internal IEnumerable<OtData> GetEnabledTriggers(string playerId)
    {
      var active = new List<OtData>();
      if (GetPlayerState(playerId) is { } state)
      {
        var tree = _db.GetCollection<TriggerNode>(TreeCol);
        foreach (var node in tree.FindAll().Where(n => n.TriggerData != null))
        {
          if (node.Id is { } id && state.Enabled.TryGetValue(id, out var value) && value == true)
          {
            active.Add(new OtData { Id = node.Id, Name = node.Name, Trigger = node.TriggerData, OverlayData = node.OverlayData });
          }
        }
      }
      return active;
    }

    // node already updated with new parentId that it wants
    internal void Update(TriggerNode node, bool updateIndex = false)
    {
      if (node?.Id is null) return;

      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        lock (_lockObject)
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
          TriggerUpdateEvent?.Invoke(node);
        }
      }
    }

    internal IEnumerable<OtData> GetAllOverlays()
    {
      return _db?.GetCollection<TriggerNode>(TreeCol).FindAll().Where(n => n.OverlayData != null)
        .Select(n => new OtData { Name = n.Name, Id = n.Id, OverlayData = n.OverlayData }) ?? [];
    }

    internal void SetExpanded(TriggerTreeViewNode viewNode)
    {
      if (viewNode?.SerializedData is { Id: not null } node)
      {
        _db?.Execute($"UPDATE {TreeCol} SET IsExpanded = {viewNode.IsExpanded} WHERE _id = '{node.Id}'");
      }
    }

    internal void Delete(string id)
    {
      var removed = new HashSet<string>();
      var removedOverlays = new HashSet<string>();

      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        lock (_lockObject)
        {
          Delete(tree, tree.FindOne(n => n.Id == id), removed, removedOverlays);

          if (_db?.GetCollection<TriggerState>(StatesCol) is { } states)
          {
            foreach (var state in states.FindAll())
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
            foreach (var node in tree.Query().Where(n => n.TriggerData != null && n.TriggerData.SelectedOverlays.Count > 0).ToEnumerable())
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

        DeleteEvent?.Invoke(id);
      }
    }

    internal bool IsAnyEnabled(string triggerId)
    {
      if (triggerId != null && _db?.GetCollection<TriggerState>(StatesCol) is { } states)
      {
        foreach (var state in states.FindAll())
        {
          if (state.Enabled.TryGetValue(triggerId, out var enabled) && enabled == true)
          {
            return true;
          }
        }
      }
      return false;
    }

    internal void SetState(string playerId, TriggerTreeViewNode viewNode)
    {
      if (viewNode?.SerializedData is not null && !viewNode.IsOverlay() &&
        _db?.GetCollection<TriggerState>(StatesCol) is { } states)
      {
        if (states.FindOne(s => s.Id == playerId) is { } state)
        {
          UpdateChildState(state, viewNode);
          states.Update(state);
        }
      }
    }

    // from GINA or Quick Share with custom Folder name
    internal void ImportTriggers(string name, IEnumerable<ExportTriggerNode> imported, HashSet<string> characterIds)
    {
      if (_db?.GetCollection<TriggerNode>(TreeCol) is { } tree)
      {
        var root = tree.FindOne(n => n.Parent == null && n.Name == Triggers);
        var parent = string.IsNullOrEmpty(name) ? root : CreateNode(root.Id, name).SerializedData;
        Import(parent, imported, Triggers, characterIds);
        TriggerImportEvent?.Invoke(true);
      }
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
        o.OverlayData.IsTextOverlay == isTextOverlay && o.OverlayData.IsDefault).ToEnumerable())
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
        foreach (var child in tree.Query().Where(n => n.Parent == node.Id).ToEnumerable())
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
        lock (_lockObject)
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
      }

      return viewNode;
    }

    private static void Delete(ILiteCollection<TriggerNode> tree, TriggerNode node, HashSet<string> removed, HashSet<string> removedOverlays)
    {
      if (node?.Id is not { } id) return;

      // must be a directory
      if (node.OverlayData == null && node.TriggerData == null)
      {
        foreach (var child in tree.Query().Where(n => n.Parent == id).ToEnumerable())
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

      lock (_lockObject)
      {
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
    }

    private void Import(ILiteCollection<TriggerNode> tree, string parentId,
      IEnumerable<ExportTriggerNode> imported, string type, List<TriggerState> characterStates)
    {
      var triggers = type == Triggers;
      string enableId = null;

      foreach (var newNode in imported)
      {
        if (tree.FindOne(n => n.Parent == parentId && n.Name == newNode.Name) is { } found)
        {
          // trigger
          if (triggers && found.TriggerData != null)
          {
            found.TriggerData = newNode.TriggerData;
            tree.Update(found);
            enableId = found.Id;
          }
          // overlay
          else if (!triggers && found.OverlayData != null)
          {
            found.OverlayData = newNode.OverlayData;
            tree.Update(found);
          }
          // make sure it is a directory
          else if (found.OverlayData == null && found.TriggerData == null && newNode.Nodes?.Count > 0)
          {
            Import(tree, found.Id, newNode.Nodes, type, characterStates);
            enableId = found.Id;
          }
        }
        else
        {
          var index = GetNextIndex(tree, parentId);

          // new trigger
          if (triggers && newNode.TriggerData != null)
          {
            newNode.TriggerData.SelectedOverlays = ValidateOverlays(tree, newNode.TriggerData.SelectedOverlays);
            Insert(newNode, index);
            enableId = newNode.Id;
          }
          // new overlay
          if (!triggers && newNode.OverlayData != null)
          {
            newNode.OverlayData = newNode.OverlayData;
            Insert(newNode, index);
          }
          // make sure it's a new directory
          else if (newNode.OverlayData == null && newNode.TriggerData == null && App.AutoMap.Map(newNode, new TriggerNode()) is { } node)
          {
            Insert(node, index);
            Import(tree, node.Id, newNode.Nodes, type, characterStates);
            enableId = node.Id;
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

      void Insert(TriggerNode node, int index)
      {
        node.Parent = parentId;
        node.Id = Guid.NewGuid().ToString();
        node.Index = index;
        node.IsExpanded = false;
        tree.Insert(node);
      }
    }

    private static void UpdateChildState(TriggerState state, TriggerTreeViewNode node)
    {
      if (node?.SerializedData?.Id is not { } id) return;

      state.Enabled[id] = node.IsChecked;
      foreach (var child in node.ChildNodes)
      {
        UpdateChildState(state, child as TriggerTreeViewNode);
      }
    }

    private TriggerTreeViewNode GetTreeView(string name, string playerId = null)
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
            _db?.GetCollection<TriggerState>(StatesCol).Update(state);
          }
        }
      }

      return root;
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
        foreach (var node in tree.Query().Where(n => n.Parent == parentId).OrderBy(n => n.Index).ToEnumerable())
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

    private static int GetNextIndex(ILiteCollection<TriggerNode> tree, string parentId)
    {
      var highest = tree.Query().Where(n => n.Parent == parentId).OrderByDescending(n => n.Index).FirstOrDefault();
      return highest?.Index + 1 ?? 0;
    }

    private static List<string> ValidateOverlays(ILiteCollection<TriggerNode> tree, IEnumerable<string> existing)
    {
      return existing?.Where(id => tree.Exists(o => o.Id == id && o.OverlayData != null)).ToList() ?? [];
    }

    // remove eventually
    private void UpgradeConfig(ILiteCollection<TriggerConfig> configs)
    {
      lock (_configLock)
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
    }

    private void Upgrade()
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
        _db?.GetCollection<TriggerConfig>(ConfigCol).Insert(config);
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

      _db?.GetCollection<TriggerNode>(TreeCol).Insert(newNode);

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
  }

  internal class OtData
  {
    public string Id { get; set; }
    public string Name { get; init; }
    public Trigger Trigger { get; init; }
    public Overlay OverlayData { get; init; }
  }
}

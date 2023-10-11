using LiteDB;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
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
    internal const string DEFAULT_USER = "Default";
    internal const string OVERLAYS = "Overlays";
    internal const string TRIGGERS = "Triggers";
    private const string LEGACY_OVERLAY_FILE = "triggerOverlays.json";
    private const string LEGACY_TRIGGERS_FILE = "triggers.json";
    private const string CONFIG_COL = "Config";
    private const string STATES_COL = "States";
    private const string TREE_COL = "Tree";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<TriggerStateManager> _lazy = new(() => new TriggerStateManager());
    internal static TriggerStateManager Instance => _lazy.Value; // instance
    private readonly object LockObject = new();
    private readonly object ConfigLock = new();
    private LiteDatabase DB;

    private TriggerStateManager()
    {
      var path = ConfigUtil.GetTriggersDBFile();
      var needUpgrade = !File.Exists(path);

      try
      {
        DB = new LiteDatabase(path)
        {
          CheckpointSize = 10
        };

        if (needUpgrade)
        {
          Upgrade();
        }

        var config = DB.GetCollection<TriggerConfig>(CONFIG_COL);
        config.EnsureIndex(x => x.Id);

        var tree = DB.GetCollection<TriggerNode>(TREE_COL);
        // create root nodes if none exist
        if (tree.Count() == 0)
        {
          tree.Insert(new TriggerNode { Name = TRIGGERS, Id = Guid.NewGuid().ToString() });
          tree.Insert(new TriggerNode { Name = OVERLAYS, Id = Guid.NewGuid().ToString() });
        }

        tree.EnsureIndex(x => x.Id);
        tree.EnsureIndex(x => x.Parent);
        tree.EnsureIndex(x => x.Name);

        var states = DB.GetCollection<TriggerState>(STATES_COL);
        states.EnsureIndex(x => x.Id);
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

    internal void AssignOverlay(string id, IEnumerable<TriggerNode> nodes) => AssignOverlay(DB?.GetCollection<TriggerNode>(TREE_COL), id, nodes);
    internal void UnassignOverlay(string id, IEnumerable<TriggerNode> nodes) => UnassignOverlay(DB?.GetCollection<TriggerNode>(TREE_COL), id, nodes);
    internal void AssignPriority(int pri, IEnumerable<TriggerNode> nodes) => AssignPriority(DB?.GetCollection<TriggerNode>(TREE_COL), pri, nodes);
    internal TriggerTreeViewNode CreateFolder(string parentId, string name) => CreateNode(parentId, name);
    internal TriggerTreeViewNode CreateTrigger(string parentId, string name) => CreateNode(parentId, name, TRIGGERS);
    internal TriggerTreeViewNode CreateOverlay(string parentId, string name, bool isTextOverlay) => CreateNode(parentId, name, OVERLAYS, isTextOverlay);
    internal TriggerTreeViewNode GetTriggerTreeView(string playerId) => GetTreeView(TRIGGERS, playerId);
    internal TriggerTreeViewNode GetOverlayTreeView() => GetTreeView(OVERLAYS);
    internal TriggerNode GetDefaultTextOverlay() => GetDefaultOverlay(true);
    internal TriggerNode GetDefaultTimerOverlay() => GetDefaultOverlay(false);
    internal void ImportTriggers(TriggerNode parent, IEnumerable<ExportTriggerNode> imported) => Import(parent, imported, TRIGGERS);
    internal void ImportOverlays(TriggerNode parent, IEnumerable<ExportTriggerNode> imported) => Import(parent, imported, OVERLAYS);
    internal bool IsActive() => DB != null;

    internal void Stop()
    {
      lock (LockObject)
      {
        DB?.Dispose();
        DB = null;
      }
    }

    internal void AddCharacter(string name, string filePath)
    {
      lock (LockObject)
      {
        if (GetConfig() is { } config)
        {
          var newCharacter = new TriggerCharacter
          {
            Name = name,
            FilePath = filePath,
            Id = Guid.NewGuid().ToString()
          };

          config.Characters.Add(newCharacter);
          config.Characters.Sort((x, y) => x.Name.CompareTo(y.Name));
          UpdateConfig(config);
        }
      }
    }

    internal void DeleteCharacter(string id)
    {
      lock (LockObject)
      {
        if (GetConfig() is { } config)
        {
          if (config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
          {
            config.Characters.Remove(existing);
            UpdateConfig(config);

            if (GetPlayerState(id) is { } state)
            {
              DB?.GetCollection<TriggerState>(STATES_COL)?.Delete(state.Id);
            }
          }
        }
      }
    }

    internal void UpdateCharacter(TriggerCharacter update)
    {
      lock (LockObject)
      {
        if (GetConfig() is { } config)
        {
          if (config.Characters.FirstOrDefault(character => character.Id == update.Id) is { } existing)
          {
            existing.Name = update.Name;
            existing.FilePath = update.FilePath;
            existing.IsEnabled = update.IsEnabled;
            UpdateConfig(config);
          }
        }
      }
    }

    internal void UpdateCharacter(string id, string name, string filePath)
    {
      lock (LockObject)
      {
        if (GetConfig() is { } config)
        {
          if (config.Characters.FirstOrDefault(character => character.Id == id) is { } existing)
          {
            existing.Name = name;
            existing.FilePath = filePath;
            UpdateConfig(config);
          }
        }
      }
    }

    internal TriggerConfig GetConfig()
    {
      if (DB?.GetCollection<TriggerConfig>(CONFIG_COL) is { } configs)
      {
        lock (ConfigLock)
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
      lock (ConfigLock)
      {
        DB?.GetCollection<TriggerConfig>(CONFIG_COL).Update(config);
        TriggerConfigUpdateEvent?.Invoke(config);
      }
    }

    internal TriggerNode GetDefaultOverlay(bool isTextOverlay)
    {
      if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
      {
        lock (LockObject)
        {
          return tree.Query().Where(n => n.OverlayData != null && n.OverlayData.IsDefault && n.OverlayData.IsTextOverlay == isTextOverlay).FirstOrDefault();
        }
      }
      return null;
    }

    internal void Copy(TriggerNode src, TriggerNode dst)
    {
      if (dst?.Id is { } parentId && (Application.Current as App).AutoMap.Map(src, new TriggerNode()) is { } copied)
      {
        if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
        {
          lock (LockObject)
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
    }

    internal IEnumerable<OTData> GetEnabledTriggers(string playerId)
    {
      var active = new List<OTData>();
      lock (LockObject)
      {
        if (GetPlayerState(playerId) is { } state)
        {
          var tree = DB.GetCollection<TriggerNode>(TREE_COL);
          foreach (var node in tree.FindAll().Where(n => n.TriggerData != null))
          {
            if (node.Id is { } id && state.Enabled.TryGetValue(id, out var value) && value == true)
            {
              active.Add(new OTData { Id = node.Id, Name = node.Name, Trigger = node.TriggerData, OverlayData = node.OverlayData });
            }
          }
        }
      }
      return active;
    }

    // node already updated with new parentId that it wants
    internal void Update(TriggerNode node, bool updateIndex = false)
    {
      if (node?.Id is not null)
      {
        if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
        {
          lock (LockObject)
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
    }

    internal IEnumerable<OTData> GetAllOverlays()
    {
      lock (LockObject)
      {
        return DB?.GetCollection<TriggerNode>(TREE_COL).FindAll().Where(n => n.OverlayData != null)
          .Select(n => new OTData { Name = n.Name, Id = n.Id, OverlayData = n.OverlayData })
          ?? Enumerable.Empty<OTData>();
      }
    }

    internal TriggerNode GetOverlayById(string id)
    {
      lock (LockObject)
      {
        return DB?.GetCollection<TriggerNode>(TREE_COL).FindOne(n => n.Id == id && n.OverlayData != null);
      }
    }

    internal void SetAllExpanded(bool expanded)
    {
      lock (LockObject)
      {
        DB?.Execute($"UPDATE {TREE_COL} SET IsExpanded = {expanded}");
      }
    }

    internal void Delete(string id)
    {
      var removed = new HashSet<string>();
      var removedOverlays = new HashSet<string>();
      var tree = DB?.GetCollection<TriggerNode>(TREE_COL);

      lock (LockObject)
      {
        Delete(tree, tree?.FindOne(n => n.Id == id), removed, removedOverlays);

        if (DB?.GetCollection<TriggerState>(STATES_COL) is { } states)
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

    internal void SetExpanded(TriggerTreeViewNode viewNode)
    {
      if (viewNode?.SerializedData is { } node)
      {
        lock (LockObject)
        {
          // update UI model just incase
          node.IsExpanded = viewNode.IsExpanded;
          DB?.GetCollection<TriggerNode>(TREE_COL).Update(node);
        }
      }
    }

    internal bool IsAnyEnabled(string triggerId)
    {
      if (triggerId != null)
      {
        lock (LockObject)
        {
          foreach (var state in DB?.GetCollection<TriggerState>(STATES_COL).FindAll())
          {
            if (state.Enabled.TryGetValue(triggerId, out var enabled) && enabled == true)
            {
              return true;
            }
          }
        }
      }
      return false;
    }

    internal void SetState(string playerId, TriggerTreeViewNode viewNode)
    {
      if (viewNode?.SerializedData is not null && !viewNode.IsOverlay() &&
        DB?.GetCollection<TriggerState>(STATES_COL) is { } states)
      {
        lock (LockObject)
        {
          if (states.FindOne(s => s.Id == playerId) is { } state)
          {
            UpdateChildState(state, viewNode);
            states.Update(state);
          }
        }
      }
    }

    // from GINA with custom Folder name
    internal void ImportTriggers(string name, IEnumerable<ExportTriggerNode> imported)
    {
      if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
      {
        lock (LockObject)
        {
          var root = tree.FindOne(n => n.Parent == null && n.Name == TRIGGERS);
          var parent = string.IsNullOrEmpty(name) ? root : CreateNode(root.Id, name).SerializedData;
          Import(parent, imported, TRIGGERS);
          TriggerImportEvent?.Invoke(true);
        }
      }
    }

    private void AssignOverlay(ILiteCollection<TriggerNode> tree, string id, IEnumerable<TriggerNode> nodes)
    {
      if (tree != null && nodes != null)
      {
        lock (LockObject)
        {
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
      }
    }

    private void UnassignOverlay(ILiteCollection<TriggerNode> tree, string id, IEnumerable<TriggerNode> nodes)
    {
      if (tree != null && nodes != null)
      {
        lock (LockObject)
        {
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
      }
    }

    private void AssignPriority(ILiteCollection<TriggerNode> tree, int pri, IEnumerable<TriggerNode> nodes)
    {
      if (tree != null && nodes != null)
      {
        lock (LockObject)
        {
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
      }
    }

    private static void EnsureNoOtherDefaults(ILiteCollection<TriggerNode> tree, string id, bool isTextOverlay)
    {
      foreach (var node in tree?.Query().Where(o => o.Id != id && o.OverlayData != null &&
        o.OverlayData.IsTextOverlay == isTextOverlay && o.OverlayData.IsDefault).ToEnumerable())
      {
        node.OverlayData.IsDefault = false;
        tree.Update(node);
      }
    }

    private TriggerTreeViewNode CreateNode(string parentId, string name, string type = null, bool isTextOverlay = false)
    {
      TriggerTreeViewNode viewNode = null;
      if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
      {
        lock (LockObject)
        {
          var newNode = new TriggerNode
          {
            Name = name,
            Id = Guid.NewGuid().ToString(),
            Parent = parentId,
            Index = GetNextIndex(tree, parentId),
          };

          if (type == TRIGGERS)
          {
            newNode.TriggerData = new Trigger();
            newNode.IsExpanded = false;
          }
          else if (type == OVERLAYS)
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

    private void Delete(ILiteCollection<TriggerNode> tree, TriggerNode node, HashSet<string> removed, HashSet<string> removedOverlays)
    {
      if (node?.Id is { } id)
      {
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
    }

    private void Import(TriggerNode parent, IEnumerable<ExportTriggerNode> imported, string type)
    {
      if (parent?.Id is { } parentId && imported != null)
      {
        if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
        {
          lock (LockObject)
          {
            // exports include the tree root so ignore
            foreach (var newNode in imported)
            {
              if (newNode.Nodes?.Count > 0)
              {
                Import(tree, parentId, newNode.Nodes, type);
              }
            }
          }
        }
      }
    }

    private void Import(ILiteCollection<TriggerNode> tree, string parentId, IEnumerable<ExportTriggerNode> imported, string type)
    {
      var triggers = type == TRIGGERS;
      foreach (var newNode in imported)
      {
        if (tree.FindOne(n => n.Parent == parentId && n.Name == newNode.Name) is { } found)
        {
          // trigger
          if (triggers && found.TriggerData != null)
          {
            found.TriggerData = newNode.TriggerData;
            tree.Update(found);
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
            Import(tree, found.Id, newNode.Nodes, type);
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
          }
          // new overlay
          if (!triggers && newNode.OverlayData != null)
          {
            newNode.OverlayData = newNode.OverlayData;
            Insert(newNode, index);
          }
          // make sure it's a new directory
          else if (newNode.OverlayData == null && newNode.TriggerData == null &&
            (Application.Current as App).AutoMap.Map(newNode, new TriggerNode()) is { } node)
          {
            Insert(node, index);
            Import(tree, node.Id, newNode.Nodes, type);
          }
        }
      }

      void Insert(TriggerNode node, int index)
      {
        node.Parent = parentId;
        node.Id = Guid.NewGuid().ToString();
        node.Index = index;
        node.IsExpanded = false;
        tree.Insert(node);
      }
    }

    private void UpdateChildState(TriggerState state, TriggerTreeViewNode node)
    {
      if (node?.SerializedData?.Id is { } id)
      {
        state.Enabled[id] = node.IsChecked;
        foreach (var child in node.ChildNodes)
        {
          UpdateChildState(state, child as TriggerTreeViewNode);
        }
      }
    }

    private TriggerTreeViewNode GetTreeView(string name, string playerId = null)
    {
      TriggerTreeViewNode root = null;

      if (DB?.GetCollection<TriggerNode>(TREE_COL) is { } tree)
      {
        TriggerState state = null;
        if (name == TRIGGERS)
        {
          state = GetPlayerState(playerId);
        }

        if (tree.FindOne(n => n.Parent == null && n.Name == name) is { } parent)
        {
          root = CreateViewNode(parent, state);
          Populate(root, state, tree);
        }

        if (name == TRIGGERS && state != null)
        {
          var needUpdate = false;
          FixEnabledState(root, state, ref needUpdate);

          if (needUpdate)
          {
            DB?.GetCollection<TriggerState>(STATES_COL).Update(state);
          }
        }
      }

      return root;
    }

    private TriggerState GetPlayerState(string playerId)
    {
      TriggerState state = null;
      if (playerId != null && DB?.GetCollection<TriggerState>(STATES_COL) is { } states)
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

    private static TriggerTreeViewNode CreateViewNode(TriggerNode node, TriggerState state = null)
    {
      var treeNode = new TriggerTreeViewNode
      {
        Content = node.Name,
        IsExpanded = node.IsExpanded,
        SerializedData = node,
      };

      if (node.OverlayData == null && state != null)
      {
        treeNode.IsChecked = state.Enabled.TryGetValue(node.Id, out var enabled) ? enabled : false;
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
          parent.ChildNodes.Add(child);
          if (child.IsDir())
          {
            Populate(child, state, tree);
          }
        }
      }
    }

    private static void FixEnabledState(TriggerTreeViewNode viewNode, TriggerState state, ref bool needUpdate)
    {
      if (viewNode.IsDir())
      {
        if (!viewNode.HasChildNodes)
        {
          if (viewNode.IsChecked != false)
          {
            state.Enabled[viewNode.SerializedData.Id] = false;
            needUpdate = true;
          }
        }
        else
        {
          foreach (var child in viewNode.ChildNodes)
          {
            FixEnabledState(child as TriggerTreeViewNode, state, ref needUpdate);
          }

          var chkCount = viewNode.ChildNodes.Count(c => c.IsChecked == true);
          var unchkCount = viewNode.ChildNodes.Count - chkCount;
          var changed = false;

          if (chkCount == viewNode.ChildNodes.Count)
          {
            if (viewNode.IsChecked != true)
            {
              viewNode.IsChecked = true;
              changed = true;
            }
          }
          else if (unchkCount == viewNode.ChildNodes.Count)
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

          if (changed && state.Enabled.TryGetValue(viewNode.SerializedData.Id, out var value) && value != viewNode.IsChecked)
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
      return existing?.Where(id => tree.Exists(o => o.Id == id && o.OverlayData != null)).ToList() ?? new List<string>();
    }

    // remove eventually
    private void Upgrade()
    {
      var overlayIds = new Dictionary<string, string>();
      var defaultEnabled = new Dictionary<string, bool?>();

      ReadJson(LEGACY_OVERLAY_FILE, OVERLAYS);
      ReadJson(LEGACY_TRIGGERS_FILE, TRIGGERS);

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

      if (defaultEnabled.Count > 0)
      {
        var states = DB?.GetCollection<TriggerState>(STATES_COL);
        states?.Insert(new TriggerState { Id = DEFAULT_USER, Enabled = defaultEnabled });
      }

      if (ConfigUtil.IfSetOrElse("TriggersEnabled"))
      {
        var config = new TriggerConfig { IsEnabled = true, Id = Guid.NewGuid().ToString() };
        DB?.GetCollection<TriggerConfig>(CONFIG_COL).Insert(config);
      }

      DB?.Checkpoint();
    }

    private void UpgradeTree(LegacyTriggerNode old, Dictionary<string, string> overlayIds,
      Dictionary<string, bool?> defaultEnabled, string parent = null, int index = -1)
    {
      var newNode = new TriggerNode
      {
        Name = old.Name,
        IsExpanded = old.IsExpanded,
        Id = Guid.NewGuid().ToString(),
        TriggerData = old.TriggerData,
        Parent = parent,
        Index = index
      };

      newNode.Name ??= "Name Unknown";

      // overlays don't have a state
      if (old.OverlayData == null)
      {
        defaultEnabled[newNode.Id] = old.IsEnabled;
      }
      else if (old.OverlayData != null)
      {
        newNode.OverlayData = (Application.Current as App).AutoMap.Map(old.OverlayData, new Overlay());
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

      DB?.GetCollection<TriggerNode>(TREE_COL).Insert(newNode);

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

  internal class OTData
  {
    public string Id { get; set; }
    public string Name { get; init; }
    public Trigger Trigger { get; init; }
    public Overlay OverlayData { get; init; }
  }
}

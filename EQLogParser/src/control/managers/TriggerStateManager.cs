using LiteDB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EQLogParser
{
  internal class TriggerStateManager
  {
    internal event Action<TriggerNode> TriggerUpdateEvent;
    internal const string DEFAULT_USER = "Default";
    internal const string OVERLAYS = "Overlays";
    internal const string TRIGGERS = "Triggers";
    private const string LEGACY_OVERLAY_FILE = "triggerOverlays.json";
    private const string LEGACY_TRIGGERS_FILE = "triggers.json";
    private const string STATES_COL = "States";
    private const string TREE_COL = "Tree";
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<TriggerStateManager> _lazy = new Lazy<TriggerStateManager>(() => new TriggerStateManager());
    internal static TriggerStateManager Instance => _lazy.Value; // instance
    private readonly object LockObject = new object();
    private LiteDatabase DB;

    private TriggerStateManager()
    {
      var path = ConfigUtil.GetTriggersDBFile();
      var needUpgrade = !File.Exists(path);

      try
      {
        DB = new LiteDatabase(path);
        DB.CheckpointSize = 10;

        if (needUpgrade)
        {
          Upgrade();
        }

        var tree = DB.GetCollection<TriggerNode>(TREE_COL);
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
          LOG.Warn("Trigger Database already in use.");
        }
        else
        {
          LOG.Error(ex);
        }
      }
    }

    internal void AssignOverlay(string id, IEnumerable<TriggerNode> nodes) => AssignOverlay(DB?.GetCollection<TriggerNode>(TREE_COL), id, nodes);
    internal void AssignPriority(int pri, IEnumerable<TriggerNode> nodes) => AssignPriority(DB?.GetCollection<TriggerNode>(TREE_COL), pri, nodes);
    internal TriggerTreeViewNode CreateFolder(string parentId, string name) => CreateNode(parentId, name);
    internal TriggerTreeViewNode CreateTrigger(string parentId, string name) => CreateNode(parentId, name, TRIGGERS);
    internal TriggerTreeViewNode CreateOverlay(string parentId, string name, bool isTimer) => CreateNode(parentId, name, OVERLAYS, isTimer);
    internal TriggerTreeViewNode GetTriggerTreeView(string playerId) => GetTreeView(TRIGGERS, playerId);
    internal TriggerTreeViewNode GetOverlayTreeView() => GetTreeView(OVERLAYS);
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

    internal void Copy(TriggerNode src, TriggerNode dst)
    {
      if (dst?.Id is string parentId && (Application.Current as App).AutoMap.Map(src, new TriggerNode()) is TriggerNode copied)
      {
        if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
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

            tree?.Insert(copied);
          }
        }
      }
    }

    internal IEnumerable<OTData> GetEnabledTriggers(string playerId)
    {
      var active = new List<OTData>();
      lock (LockObject)
      {
        if (GetPlayerState(playerId) is TriggerState state)
        {
          var tree = DB.GetCollection<TriggerNode>(TREE_COL);
          foreach (var node in tree.FindAll().Where(n => n.TriggerData != null))
          {
            if (node?.Id is string id && state.Enabled.TryGetValue(id, out var value) && value == true)
            {
              active.Add(new OTData { Id = node.Id, Name = node.Name, Trigger = node.TriggerData, OverlayData = node.OverlayData });
            }
          }
        }
      }
      return active;
    }

    internal void UnassignOverlay(bool isTextOverlay, IEnumerable<TriggerNode> nodes)
    {
      if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
      {
        lock (LockObject)
        {
          var found = tree.Query().Where(n => n.OverlayData != null && n.OverlayData.IsTextOverlay == isTextOverlay);
          UnassignOverlay(tree, nodes, found.Select(n => n.Id).ToList());
        }
      }
    }

    // node already updated with new parentId that it wants
    internal void Update(TriggerNode node, bool updateIndex = false)
    {
      if (node?.Id is string)
      {
        if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
        {
          lock (LockObject)
          {
            if (updateIndex)
            {
              node.Index = GetNextIndex(tree, node.Parent);
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

        if (DB?.GetCollection<TriggerState>(STATES_COL) is ILiteCollection<TriggerState> states)
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
    }

    internal void SetExpanded(TriggerTreeViewNode viewNode)
    {
      if (viewNode?.SerializedData is TriggerNode node)
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
      if (viewNode?.SerializedData is TriggerNode node && !viewNode.IsOverlay() &&
        DB?.GetCollection<TriggerState>(STATES_COL) is ILiteCollection<TriggerState> states)
      {
        lock (LockObject)
        {
          if (states.FindOne(s => s.Id == playerId) is TriggerState state)
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
      TriggerNode parent = null;
      if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
      {
        lock (LockObject)
        {
          var root = tree.FindOne(n => n.Parent == null && n.Name == TRIGGERS);
          parent = string.IsNullOrEmpty(name) ? root : CreateNode(root.Id, name, null).SerializedData;
          Import(parent, imported, TRIGGERS);
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

    private void AssignPriority(ILiteCollection<TriggerNode> tree, int pri, IEnumerable<TriggerNode> nodes)
    {
      if (tree != null && nodes != null)
      {
        lock (LockObject)
        {
          foreach (var node in nodes)
          {
            if (node.TriggerData?.Priority is long priority && priority != pri)
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

    private void UnassignOverlay(ILiteCollection<TriggerNode> tree, IEnumerable<TriggerNode> nodes, List<string> toRemove)
    {
      if (tree != null && nodes != null)
      {
        foreach (var node in nodes)
        {
          if (node.TriggerData?.SelectedOverlays.RemoveAll(o => toRemove.Contains(o)) is int count && count > 0)
          {
            tree.Update(node);
          }
          else if (node.TriggerData == null && node.OverlayData == null)
          {
            UnassignOverlay(tree, tree.Find(n => n.Parent == node.Id), toRemove);
          }
        }
      }
    }

    private TriggerTreeViewNode CreateNode(string parentId, string name, string type = null, bool isTimer = false)
    {
      TriggerTreeViewNode viewNode = null;
      if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
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
            newNode.OverlayData.IsTimerOverlay = isTimer;
            newNode.OverlayData.IsTextOverlay = !isTimer;

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
      if (node?.Id is string id)
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
      if (parent?.Id is string parentId && imported != null)
      {
        if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
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
        if (tree.FindOne(n => n.Parent == parentId && n.Name == newNode.Name) is TriggerNode found)
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
            (Application.Current as App).AutoMap.Map(newNode, new TriggerNode()) is TriggerNode node)
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
      if (node?.SerializedData?.Id is string id)
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

      if (DB?.GetCollection<TriggerNode>(TREE_COL) is ILiteCollection<TriggerNode> tree)
      {
        TriggerState state = null;
        if (name == TRIGGERS)
        {
          state = GetPlayerState(playerId);
        }

        if (tree?.FindOne(n => n.Parent == null && n.Name == name) is TriggerNode parent)
        {
          root = CreateViewNode(parent, state);
          Populate(root, state, tree);
        }

        if (name == TRIGGERS)
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
      if (DB?.GetCollection<TriggerState>(STATES_COL) is ILiteCollection<TriggerState> states)
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
      };

      if (node.OverlayData == null && state != null)
      {
        treeNode.IsChecked = state.Enabled.TryGetValue(node.Id, out var enabled) ? enabled : false;
      }

      return treeNode;
    }

    private void Populate(TriggerTreeViewNode parent, TriggerState state, ILiteCollection<TriggerNode> tree)
    {
      if (parent.SerializedData.Id is string parentId)
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

    private int GetNextIndex(ILiteCollection<TriggerNode> tree, string parentId)
    {
      var highest = tree.Query().Where(n => n.Parent == parentId).OrderByDescending(n => n.Index).FirstOrDefault();
      return highest?.Index + 1 ?? 0;
    }

    private List<string> ValidateOverlays(ILiteCollection<TriggerNode> tree, IEnumerable<string> existing)
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
        if (ConfigUtil.ReadConfigFile(file) is string json)
        {
          try
          {
            if (JsonSerializer.Deserialize<LegacyTriggerNode>(json, new JsonSerializerOptions { IncludeFields = true }) is LegacyTriggerNode legacy)
            {
              legacy.Name = title;
              UpgradeTree(legacy, overlayIds, defaultEnabled);
            }
          }
          catch (Exception ex)
          {
            LOG.Error($"Error Upgrading Triggers {file}", ex);
          }
        }
      }

      if (defaultEnabled.Count > 0)
      {
        var states = DB?.GetCollection<TriggerState>(STATES_COL);
        states?.Insert(new TriggerState { Id = DEFAULT_USER, Enabled = defaultEnabled });
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
        if (newNode.TriggerData.SelectedOverlays is List<string> selected)
        {
          var remapped = selected.Where(id => overlayIds.ContainsKey(id)).Select(id => overlayIds[id]).ToList();
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

    string FixColor(string value)
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

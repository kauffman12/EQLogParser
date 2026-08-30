using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /* Assembles Syncfusion view nodes from the store data produced by TriggerStateDB (Core).
   * Kept in the WPF host because TriggerTreeViewNode derives from a Syncfusion type. */
  internal static class TriggerTreeViewBuilder
  {
    public static TriggerTreeViewNode Build(TreeData data)
    {
      if (data.Root is not { } root) return null;

      // "?? """: Dictionary rejects a null key, so one orphan document with no Parent would fail
      // the whole trigger/overlay tree load. Such nodes are never collected by TriggerStateDB
      // (it queries children by parent), but the builder must not depend on that.
      var childrenByParent = data.Nodes.GroupBy(node => node.Parent ?? "")
        .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Index).ToList());
      return CreateViewNode(root, data.State, childrenByParent);
    }

    // Single newly created node (no children yet). Checked seeds IsChecked when non-null —
    // see TriggerStateDB.CreateFolder/CreateTrigger.
    public static TriggerTreeViewNode Build(TriggerNode node, bool? isChecked = null)
    {
      var viewNode = CreateViewNode(node, null, null);
      if (isChecked is not null)
      {
        viewNode.IsChecked = isChecked;
      }

      return viewNode;
    }

    private static TriggerTreeViewNode CreateViewNode(TriggerNode node, TriggerState state,
      Dictionary<string, List<TriggerNode>> childrenByParent)
    {
      var viewNode = new TriggerTreeViewNode
      {
        Content = node.Name,
        IsExpanded = node.IsExpanded,
        SerializedData = node,
        // mirrors the store's view flags (dictionaries live on the Core instance, reachable via IVT)
        IsRecentlyMerged = TriggerStateDB.Instance.RecentlyMerged.ContainsKey(node.Id) && !TriggerStateDB.Instance.MissingMedia.ContainsKey(node.Id),
        HasMissingMedia = TriggerStateDB.Instance.MissingMedia.ContainsKey(node.Id)
      };

      if (node.OverlayData is null && state is not null)
      {
        viewNode.IsChecked = state.Enabled.GetValueOrDefault(node.Id, false);
      }

      if (node.OverlayData is null && node.TriggerData is null &&
          childrenByParent is { } && childrenByParent.TryGetValue(node.Id, out var children))
      {
        foreach (var child in children)
        {
          viewNode.ChildNodes.Add(CreateViewNode(child, state, childrenByParent));
        }
      }

      return viewNode;
    }
  }
}

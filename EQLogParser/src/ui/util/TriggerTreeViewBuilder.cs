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

      var childrenByParent = data.Nodes.GroupBy(node => node.Parent)
        .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Index).ToList());
      return CreateViewNode(root, data.State, childrenByParent);
    }

    // Single newly created node (no children yet). Checked seeds IsChecked when non-null —
    // see TriggerStateDB.CreateFolder/CreateTrigger.
    public static TriggerTreeViewNode Build(TriggerNode node, bool? isChecked = null)
    {
      var viewNode = CreateViewNode(node, null, null);
      if (isChecked != null)
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

      if (node.OverlayData == null && state != null)
      {
        viewNode.IsChecked = state.Enabled.GetValueOrDefault(node.Id, false);
      }

      if (node.OverlayData == null && node.TriggerData == null &&
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

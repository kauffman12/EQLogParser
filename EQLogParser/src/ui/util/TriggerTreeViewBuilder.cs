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
      // Databases older than the id-owning migrations can hold a node without one (UpgradeTree
      // tolerates that elsewhere), and every lookup below throws on a null key — so an id-less node
      // used to take down the whole tree build instead of just rendering without flags.
      var hasId = !string.IsNullOrEmpty(node.Id);
      // mirrors the store's view flags (dictionaries live on the Core instance, reachable via IVT)
      var recentlyMerged = TriggerStateDB.Instance.RecentlyMerged;
      var missingMedia = TriggerStateDB.Instance.MissingMedia;

      var viewNode = new TriggerTreeViewNode
      {
        Content = node.Name,
        IsExpanded = node.IsExpanded,
        SerializedData = node,
        IsRecentlyMerged = hasId && recentlyMerged.ContainsKey(node.Id) && !missingMedia.ContainsKey(node.Id),
        HasMissingMedia = hasId && missingMedia.ContainsKey(node.Id)
      };

      if (hasId && node.OverlayData is null && state is not null)
      {
        viewNode.IsChecked = state.Enabled.GetValueOrDefault(node.Id, false);
      }

      if (hasId && node.OverlayData is null && node.TriggerData is null &&
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

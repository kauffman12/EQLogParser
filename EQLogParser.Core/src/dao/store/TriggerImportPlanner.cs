using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /* What Import should do with one incoming node once its siblings are known. */
  internal enum ImportAction
  {
    /* No import branch applies to this combination — nothing happens. */
    Skip,
    /* Re-import of a leaf: overwrite the existing node's data in place. */
    UpdateInPlace,
    /* Folder wrapper matched an existing folder: recurse the incoming children into it. */
    MergeIntoFolder,
    /* No matching sibling: insert as a new sibling trigger leaf. */
    InsertLeaf,
    /* No matching sibling: insert as a new sibling folder and recurse into its children. */
    InsertFolder
  }

  internal readonly record struct ImportDecision(ImportAction Action, TriggerNode Existing);

  // Pure matching + branch selection for TriggerStateDB.Import: the store passes the existing
  // sibling nodes under the target parent and applies the returned decision to LiteDB. Keeping
  // this free of LiteDB/WPF makes the whole decision matrix unit-testable on any platform.
  internal static class TriggerImportPlanner
  {
    public static ImportDecision Plan(IEnumerable<TriggerNode> siblings, ExportTriggerNode incoming) =>
      Decide(FindExisting(siblings, incoming), incoming);

    // Match an existing node to update in place on re-import. Nodes carrying an OriginalId
    // (NAG imports) match by source id alone: NAG allows duplicate names for distinct triggers,
    // and name is not stable — the importer renames a same-name collision ("X" → "X (2)"),
    // after which a name+id match would fail and every re-import would insert yet another
    // duplicate. The OriginalId is the stable source identity and survives on the stored node.
    // Name-only matches are kind-safe (MatchesReimportKind): without it, a folder wrapper could
    // match an existing same-named trigger and reach the overwrite branch with TriggerData ==
    // null, erasing the trigger's data.
    public static TriggerNode FindExisting(IEnumerable<TriggerNode> siblings, ExportTriggerNode incoming)
    {
      if (incoming.OriginalId != null)
      {
        return siblings.FirstOrDefault(n => n.OriginalId == incoming.OriginalId);
      }

      return siblings.FirstOrDefault(n => n.Name == incoming.Name && MatchesReimportKind(n, incoming));
    }

    // Re-import name matches must be kind-safe: a payload-carrying leaf (trigger or overlay)
    // updates only an existing leaf, and a folder wrapper merges only into an existing folder.
    // TriggerNode has no explicit kind field — kind is defined by payload presence (see
    // TriggerTreeViewNode.IsDir()). Without this, a folder wrapper could match a same-named
    // trigger and reach the overwrite branch with null data (erasing it), or a leaf matching a
    // same-named folder would hit no update/merge branch and be silently dropped.
    public static bool MatchesReimportKind(TriggerNode existing, ExportTriggerNode incoming) =>
      HasDataPayload(existing) == HasDataPayload(incoming);

    private static bool HasDataPayload(TriggerNode node) =>
      node.TriggerData != null || node.OverlayData != null;

    // Branch selection for one incoming node — mirrors the update/merge/insert logic in
    // TriggerStateDB.Import exactly:
    // - existing leaf, any incoming leaf data  → overwrite in place
    // - existing folder + incoming children     → merge into the existing folder
    // - no match + incoming trigger leaf        → insert as a new sibling
    // - no match + incoming folder wrapper      → insert as a new sibling and recurse
    // - anything else                           → no-op
    public static ImportDecision Decide(TriggerNode existing, ExportTriggerNode incoming)
    {
      if (existing != null)
      {
        if (existing.TriggerData != null)
        {
          return new ImportDecision(ImportAction.UpdateInPlace, existing);
        }

        // directory but make sure it is one
        if (existing.OverlayData == null && existing.TriggerData == null && incoming.Nodes?.Count > 0)
        {
          return new ImportDecision(ImportAction.MergeIntoFolder, existing);
        }

        return new ImportDecision(ImportAction.Skip, existing);
      }

      if (incoming.TriggerData != null)
      {
        return new ImportDecision(ImportAction.InsertLeaf, existing);
      }

      // make sure it's a new directory
      if (incoming.OverlayData == null && incoming.TriggerData == null)
      {
        return new ImportDecision(ImportAction.InsertFolder, existing);
      }

      return new ImportDecision(ImportAction.Skip, existing);
    }
  }
}

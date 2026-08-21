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
  // sibling nodes under the target parent plus the OriginalIds that occur more than once in the
  // SAME incoming batch and applies the returned decision to LiteDB. Keeping this free of
  // LiteDB/WPF makes the whole decision matrix unit-testable on any platform.
  internal static class TriggerImportPlanner
  {
    public static ImportDecision Plan(IEnumerable<TriggerNode> siblings, ExportTriggerNode incoming,
      ISet<string> batchSharedOriginalIds = null) =>
      Decide(FindExisting(siblings, incoming, batchSharedOriginalIds), incoming);

    // Match an existing node to update in place on re-import. Nodes carrying an OriginalId
    // (NAG imports) match by source id: NAG allows duplicate names for distinct triggers,
    // and name is not stable — the importer renames a same-name collision ("X" → "X (2)"),
    // after which a strict name+id match would fail and every re-import would insert yet
    // another duplicate. The OriginalId is the stable source identity and survives on the
    // stored node.
    // One NAG trigger can also produce SEVERAL siblings sharing one OriginalId (phrase + timer
    // variants, counter resets). Inside such a family the name is the stable discriminator
    // (the importer's deterministic "(n)" suffixes survive re-imports), so when more than one
    // stored sibling carries the id — or the incoming batch itself carries it more than once
    // (first import: an earlier member was already inserted into the same live sibling set)
    // — the name must agree. Otherwise every incoming member would overwrite the first sibling
    // found. A renamed family member then matches nothing and inserts as a new, visible
    // sibling instead of guessing which stored node it came from.
    // Name-only matches (no OriginalId) are kind-safe (MatchesReimportKind): without it, a
    // folder wrapper could match an existing same-named trigger and reach the overwrite branch
    // with TriggerData == null, erasing the trigger's data.
    public static TriggerNode FindExisting(IEnumerable<TriggerNode> siblings, ExportTriggerNode incoming,
      ISet<string> batchSharedOriginalIds = null)
    {
      if (incoming.OriginalId != null)
      {
        var family = siblings.Where(n => n.OriginalId == incoming.OriginalId).ToList();

        var nameDisambiguates = family.Count > 1 ||
          (batchSharedOriginalIds?.Contains(incoming.OriginalId) ?? false);

        return nameDisambiguates ?
          family.FirstOrDefault(n => n.Name == incoming.Name) :
          family.FirstOrDefault();
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

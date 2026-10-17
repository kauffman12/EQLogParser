namespace EQLogParser;

/// <summary>Frozen identity and class history, queried at each exported record's log time.</summary>
internal static class CaptureExportLens
{
  internal static CaptureExport.Lens Create(EntityTimeline source, IReadOnlyList<string> names)
  {
    var timeline = source.Copy();
    var allNames = names.Concat(timeline.NamesWithIdentity()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in allNames)
    {
      var owner = PlayerRegistry.Instance.GetPlayerFromPet(name);
      if (!string.IsNullOrEmpty(owner) && !ParserUtil.IsUnattributedName(owner) && owner != Labels.Unassigned)
        owners[name] = owner;
    }

    var classes = PlayerRegistry.Instance.CaptureClassLookup(
      allNames.Concat(owners.Values).Distinct(StringComparer.OrdinalIgnoreCase));
    return new CaptureExport.Lens((name, time) =>
    {
      var kind = timeline.IdentityAt(name, time);
      var owned = timeline.IsOurPetAt(name, time) || timeline.IsCharmedAt(name, time);
      var side = owned || kind is IdentityKind.Player or IdentityKind.Pet or IdentityKind.Merc;
      var owner = timeline.OwnerOf(name, time);
      if (side && string.IsNullOrEmpty(owner) && kind is not (IdentityKind.Player or IdentityKind.Merc))
        owner = ClassificationRules.OwnerInName(name) ?? owners.GetValueOrDefault(name);
      return new CaptureExport.Identity(side, owner ?? string.Empty, classes(name, time),
        IdentityVocabulary.TypeWord(kind));
    }, spell => EQDataStore.Instance.AbbreviateSpellName(spell));
  }
}

using System.Collections.Generic;

namespace EQLogParser
{
  // Hidden custody rows belong only to the encounter the charm closed, never to an unrelated overlapping fight.
  // Ordinary pet output already lives in its victim's encounter through FightProjection.
  internal static class CharmPetRows
  {
    public static bool IsPetRow(DerivedFight fight) => fight is { RaidPet: true };

    public static List<DerivedFight> Visible(IReadOnlyList<DerivedFight> fights)
    {
      var visible = new List<DerivedFight>(fights.Count);
      foreach (var f in fights)
        if (!IsPetRow(f)) visible.Add(f);
      return visible;
    }

    // EncounterRow is explicit membership, not a clock heuristic. Unpaired custody remains in the capture.
    public static List<DerivedFight> WithHiddenPets(IReadOnlyList<DerivedFight> selected, IReadOnlyList<DerivedFight> all)
    {
      if (selected is not { Count: > 0 } || all is not { Count: > 0 })
        return selected as List<DerivedFight> ?? new List<DerivedFight>(selected ?? []);

      var selectedSet = new HashSet<DerivedFight>(selected);
      var added = new HashSet<DerivedFight>(selectedSet);
      var result = new List<DerivedFight>(selected);
      foreach (var f in all)
      {
        if (!IsPetRow(f) || f.EncounterRow is null || !selectedSet.Contains(f.EncounterRow) || !added.Add(f))
          continue;
        result.Add(f);
      }

      if (result.Count != selected.Count) result.Sort(static (a, b) => a.BeginTime.CompareTo(b.BeginTime));
      return result;
    }
  }
}

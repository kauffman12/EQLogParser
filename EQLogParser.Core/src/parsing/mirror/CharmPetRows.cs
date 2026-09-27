using System.Collections.Generic;

namespace EQLogParser.Mirror
{
  /*
   * Which derived rows belong on the fight list, and what a selection still owes the ones that are not.
   *
   * A charmed mob under raid control is OUR PET, and pets do not get fight-list rows — neither a player's
   * `Ziggy`s pet` nor a charmed `A bone walker` appears there in the legacy table; their output rides along inside
   * the encounter it happened during and reaches the damage board as `+Pets` under the person who owns them
   * (agreed 2026-10). `DerivedFight.RaidPet` marks exactly those rows. It is NOT `CharmedOwned`, which is the
   * broader "this row exists because a window had the name flipped" flag the status column reads: a charmed RAID
   * MEMBER carries that and no `RaidPet`, because she is an encounter the raid fights and must stay in the list.
   * Nor is it set on the NPC row the charm itself closed — that keeps `EndReason.Charmed` and stays listed, since
   * being taken by the raid *is* how that encounter ended.
   *
   * The second half is the part that is easy to get wrong. Hiding a row in the grid does not delete its facts, and
   * the board is built from whatever selection a click produced — so a hidden row that never comes back takes the
   * charmer's pet damage with it (and the raid members' stray swings on their own pet, which stay counted for the
   * same reason they were exempted from the friendly-fire drop). `WithHiddenPets` returns them to a stats build
   * when the hidden row's own span overlaps a fight that WAS selected: the pet did its work during an engagement
   * the user asked about, and its owner's entry is then computed the same way as any other pet's.
   */
  internal static class CharmPetRows
  {
    // True for a row that is a charmed mob's own output rather than an encounter: hidden from the list, kept
    // reachable by the stats builds it belongs to.
    public static bool IsPetRow(DerivedFight fight) => fight is { RaidPet: true };

    // The list as displayed: same order, no pet rows. Sectionizer still inserts its inactivity dividers,
    // now between the rows a user can actually see.
    public static List<DerivedFight> Visible(IReadOnlyList<DerivedFight> fights)
    {
      var visible = new List<DerivedFight>(fights.Count);
      foreach (var f in fights)
        if (!IsPetRow(f)) visible.Add(f);
      return visible;
    }

    /*
     * A selection plus the hidden pet rows that belong to it, in projection order, without duplicates. Two ways in:
     *
     *   - `EncounterRow` points at a selected row: this is the same mob's post-charm half of an encounter the user
     *     clicked. It has to be a link rather than a time test, because the pet's facts begin AFTER that
     *     encounter closed — CharmRowProjectionTest.HidingAPetRowDoesNotDeleteItsDamage is that case.
     *   - its span overlaps the selection: a pet row with no encounter of its own (the raid took a mob it never
     *     fought, then its stray swings landed on it) still counts toward whatever the user asked about.
     *
     * Name-keyed inclusion is deliberately NOT one of them: a raid charms `A bone walker` in three pulls, and a
     * selection about pull two must not inherit the pets from the other two.
     */
    public static List<DerivedFight> WithHiddenPets(IReadOnlyList<DerivedFight> selected, IReadOnlyList<DerivedFight> all)
    {
      if (selected is not { Count: > 0 } || all is not { Count: > 0 }) return selected as List<DerivedFight> ?? new List<DerivedFight>(selected ?? []);

      // Single pass to find the span the user asked about: selections are made over contiguous display rows.
      double from = double.PositiveInfinity, to = double.NegativeInfinity;
      foreach (var f in selected)
      {
        if (f.BeginTime < from) from = f.BeginTime;
        if (f.EndTime > to) to = f.EndTime;
      }

      var result = new List<DerivedFight>(selected.Count + 8);
      var added = new HashSet<DerivedFight>(selected);
      foreach (var f in all)
      {
        if (!IsPetRow(f) || added.Contains(f)) continue;
        var paired = f.EncounterRow is not null && added.Contains(f.EncounterRow);
        if (!paired && (f.BeginTime > to || f.EndTime < from)) continue;   // outside the asked-about window
        added.Add(f);
        result.Add(f);
      }

      if (result.Count == 0) return new List<DerivedFight>(selected);

      // Projection order, so the summary sees rows in the same sequence it would have without the hiding.
      var merged = new List<DerivedFight>(selected.Count + result.Count);
      merged.AddRange(selected);
      merged.AddRange(result);
      merged.Sort(static (a, b) => a.BeginTime.CompareTo(b.BeginTime));
      return merged;
    }
  }
}

using System.Collections.Frozen;

#nullable enable annotations

namespace EQLogParser;

/*
 * Editing WHO OWNS a summon, decided in Core so the rules are testable without a window and cannot drift between the panes that offer
 * it (Names and Identities today; the fight grids' "Assign <name> as Pet of ▸" item already asks the same questions in its own shape).
 *
 * Three laws, all of them answers to something the old Pet Owners window got wrong or never offered:
 *
 *   Only a row that READS Pet gets the pencil. Typing an owner onto a raider is exactly the pollution this identity window exists to
 *   catch, and Pet/Merc/Spell/NPC rows have nothing to persist here — ownership is a claim about a summon. (The operator chose this over
 *   "any row, where choosing an owner also asserts Pet".)
 *
 *   The last entry is the take-back: `Labels.Unassigned`, which prints "No Owner" (2026-10-09 — the window used to offer a choice
 *   labelled "Unknown Pet Owner", and the operator asked for two words that say what the click does). Choosing it forgets the pair and
 *   KEEPS the Pet verdict: "I do not know whose this is" is not "this is not a pet". Clearing everything a name is stays where it was,
 *   under the Type column's "Clear claim".
 *
 *   The row's current answer is always in its own list, even when it is junk. A value missing from the dropdown it opens with reads as a
 *   blank cell, and a blank cell reads as a classifier bug — so an owner who left the raid, or a placeholder stored under the old
 *   wording, stays selectable while the list of real candidates refuses them (a placeholder offered as a candidate would put "Unknown
 *   Pet Owner" back on the board as a person).
 */
internal static class PetOwnership
{
  /// <summary>Does this row's kind allow an owner to be written? Pet rows only — see the law above.</summary>
  internal static bool CanEditOwner(IdentityKind kind) => kind == IdentityKind.Pet;

  /// <summary>Is this choice "forget the mapping" rather than "this person owns it"?</summary>
  internal static bool IsClear(string? choice) => string.IsNullOrEmpty(choice) || Labels.IsUnassignedOwner(choice);

  /*
   * What the cell shows. An unmapped pet and a pet mapped to the placeholder are the same sentence to a reader — "nobody said whose" —
   * so both print the one word; the difference between them is invisible on purpose, because nothing downstream acts on it either (an
   * unassigned owner folds no damage onto anyone, which is what `Labels.IsUnassignedOwner` is asked before every fold). The legacy
   * wording of a stored row never reaches the screen either: identity-priors.txt and petmapping.txt rows already on disk hold "Unknown
   * Pet Owner", and printing it after asking for the change would make the file, not the operator, the author of what this pane says.
   */
  internal static string DisplayOf(string? owner) =>
    string.IsNullOrEmpty(owner) || Labels.IsUnassignedOwner(owner) ? Labels.Unassigned : owner;

  /*
   * Build the list a cell popup shows: real candidates (sorted, deduped case-insensitively because entity names are looked up without
   * case), then `Labels.Unassigned` last. Placeholders and the frozen person words ("you", "himself", "Unknown Pet Owner") are refused
   * as CANDIDATES — a stored owner that is a pronoun or a hole stays visible through the "current value" rule below, but it must not be
   * offered to anybody else's row.
   */
  internal static List<string> Choices(string? current, IEnumerable<string> owners)
  {
    var list = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var owner in owners)
    {
      if (!IsCandidate(owner)) continue;
      if (seen.Add(owner)) list.Add(owner);
    }

    /*
     * The row's own answer comes first, so the popup opens on what it says even when that answer is nobody the roster knows — but a
     * placeholder is not "its own answer", it is the absence of one: adding the stored wording beside the trailing entry would give the
     * same click two lines in one dropdown (and preselect the stale text instead of "No Owner").
     */
    if (IsCandidate(current) && seen.Add(current!)) list.Insert(0, current!);

    list.Sort(System.StringComparer.Ordinal);

    /*
     * The take-back goes last, whatever else the list holds — including when the row ALREADY reads unassigned: an ownerless pet must be
     * able to open its own dropdown on its own value (preselecting the entry is what lets the handler refuse a click that changes
     * nothing, rather than spending a derive pass and rewriting a store).
     */
    list.Add(Labels.Unassigned);
    return list;
  }

  /*
   * The words that name nobody as an owner. Note this is NOT `PlayerRegistry.IsPersonWord`: that question asks "is this STRING a person
   * rather than an entity?", and "you" answers yes — the local player, which is why petmapping.txt has always been allowed to say
   * `Fluffy=You` (the import resolves it to whoever is playing). What cannot own a summon is the reflexive forms: a line writes
   * "yourself" when the pet healed its own owner, and that is a sentence about an act, not a name to put in a column.
   */
  private static readonly System.Collections.Frozen.FrozenSet<string> RefusedOwners =
    new[] { "yourself", "your", "himself", "herself", "itself" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

  /*
   * A person this list may offer — for a new assignment or as the row's own current answer. The two uses are deliberately the same test:
   * what is not fit to be chosen for another row is not fit to sit selectable in this one either, and the placeholder already has its own
   * entry at the end of the list.
   */
  private static bool IsCandidate(string? owner) =>
    !string.IsNullOrEmpty(owner)
    && !Labels.IsUnassignedOwner(owner)
    && !Labels.Unk.Equals(owner, StringComparison.OrdinalIgnoreCase)
    && !RefusedOwners.Contains(owner);
}

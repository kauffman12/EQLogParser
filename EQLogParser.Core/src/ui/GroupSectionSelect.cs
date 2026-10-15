using System;
using System.Collections.Generic;

namespace EQLogParser;

/*
 * The decisions behind the fight list's "Select Group" / "Unselect Group", stated as numbers so they can be
 * answered outside a window: which row the gesture is about, what section that row sits in, and which rows the gesture
 * leaves selected.
 *
 * A GROUP is a SECTION: the run of fight rows between two inactivity dividers. That is the legacy meaning (the old
 * table stamped `Fight.GroupId` while it walked the list, and its menu took "every row with my current row's
 * GroupId"); the derived list uses the dividers in the complete display-row list, BEFORE either checkbox hides rows.
 * Hiding a divider changes visibility, never membership. Group gestures take only visible fights from that section.
 *
 * Which ROW a gesture is about is the part that broke (field report: "if i right-click on another group area and do it
 * then it only unselects the group i was previously in. its like the select cursor/item stays where it was"). The
 * deleted legacy table answered this with one line in its ContextMenuOpening — it set the grid's `CurrentItem` to the
 * row whose data was under the pointer — and every position-based item then read that current row. Without that line
 * nothing moves the cursor on a right-click (SfDataGrid selects on the left button), so the menu acted on wherever the
 * person had last LEFT-clicked. Both halves are here: which row the pointer named, and what section that row sits in.
 *
 * Inputs are one bool per displayed row plus plain indices, because a test in the cross-platform assembly can hand
 * over `false, false, true, true, false, true` and say exactly what the pane would do; `DerivedFightRow` itself only
 * exists on the WPF side. The bool means "this row names a section", i.e. it is a fight row and not a divider.
 */
internal static class GroupSectionSelect
{
  /// No row. Matches `IList.IndexOf` failure so callers can pass one another's answers straight through.
  internal const int NoRow = -1;

  /*
   * The row a group gesture is about, in priority order: the row the pointer opened the menu on, then the grid's own
   * current row, then the first selected fight row.
   *
   * A row that names no section is SKIPPED rather than inherited, and that ordering is what keeps the everyday case
   * working: right-clicking an inactivity divider (or the empty space under the last row) leaves the pointer without a
   * section, so the gesture falls back to the cursor and then to the selection — which is why "unselect group" from a
   * divider still removes the group you had selected instead of doing nothing.
   */
  internal static int TargetFor(int clickedIndex, bool clickedNamesSection, int currentIndex, bool currentNamesSection, int firstSelectedIndex)
  {
    if (clickedNamesSection && clickedIndex >= 0) return clickedIndex;
    if (currentNamesSection && currentIndex >= 0) return currentIndex;
    return firstSelectedIndex;
  }

  /*
   * The bounds of the section around `target`, inclusive: walk out to the nearest rows that do NOT name a section.
   * (-1, -1) when there is no section to take — target is out of range, or it names none (a divider carries no section
   * of its own: it is the gap between two, and it belongs to neither).
   */
  internal static (int From, int To) BoundsOf(IReadOnlyList<bool> namesSection, int target)
  {
    if (namesSection is null || target < 0 || target >= namesSection.Count || !namesSection[target])
      return (NoRow, NoRow);

    var from = target;
    while (from > 0 && namesSection[from - 1]) from--;

    var to = target;
    while (to + 1 < namesSection.Count && namesSection[to + 1]) to++;

    return (from, to);
  }

  /// <summary>Find the section before filtering, then take only its visible fight rows.</summary>
  internal static List<T> VisibleMembers<T>(IReadOnlyList<T> rows, int target, Predicate<T> namesSection,
    Predicate<T> isShown) where T : class
  {
    if (target < 0 || target >= rows.Count || !isShown(rows[target])) return [];

    var sections = new bool[rows.Count];
    for (var i = 0; i < rows.Count; i++) sections[i] = namesSection(rows[i]);
    var (from, to) = BoundsOf(sections, target);
    if (from == NoRow) return [];

    var members = new List<T>();
    for (var i = from; i <= to; i++)
    {
      if (isShown(rows[i])) members.Add(rows[i]);
    }

    return members;
  }

  /*
   * Which rows a group gesture LEAVES selected (docs/DesignNotes.md → "Select Group stands alone").
   *
   * Without a modifier the section stands ALONE (2026-10-14). "Select my group" asks for one encounter, and taking it on top
   * of whatever working selection was standing meant the item could not answer the question it is asked with a full list on
   * screen — Select All then Select Group left the whole night selected. The legacy menu added (`if (!SelectedItems.Contains(fight))
   * Add`) because it had no way to tell a first pick from an addition; this one can, from the modifiers riding the press that
   * opened the menu, so plain means "this group and nothing else".
   *
   * Ctrl or Shift on that press keeps the ADDITIVE answer — that is how somebody builds a selection out of several groups on
   * purpose, and it is also why the pane remembers the modifier rather than reading it at the click: by the time a menu item is
   * clicked the button has long been released, and a menu the keyboard opened carries Shift in its own keystroke (Shift+F10),
   * which says nothing about rows.
   *
   * Order is not part of the answer — the pane selects in list order either way. Rows are compared by identity, because these
   * are row objects a derive pass replaces wholesale.
   */
  internal static List<T> SelectedAfterGesture<T>(List<T> section, IEnumerable<T> selected, bool additive) where T : class
  {
    if (!additive || selected is null) return section;

    var joined = new List<T>(section);
    var seen = new HashSet<T>(joined, ReferenceEqualityComparer.Instance);
    foreach (var row in selected)
      if (row is not null && seen.Add(row)) joined.Add(row);

    return joined;
  }

  /*
   * Whether "Select Group" may be offered at all for this target. Kept separate from the unselect rule because the two
   * ask different questions: selecting needs a section to add, while un-selecting also needs something selected to take
   * away. Consequence, deliberate: with an empty selection a right-click ON a fight row still offers Select Group ("give
   * me this whole pull"), which is what the legacy menu did too — it enabled these items whenever the grid had rows.
   */
  internal static bool CanSelect(bool hasTargetSection) => hasTargetSection;

  /// Whether "Unselect Group" may be offered: a section to remove AND a selection to remove anything from.
  internal static bool CanUnselect(bool hasTargetSection, int selectedCount) => hasTargetSection && selectedCount > 0;
}

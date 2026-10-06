namespace EQLogParser;

/*
 * The damage meter's display step, restored over the derived numbers: what the bars show is not "the top N of this board"
 * but the legacy answer a player had trained to — the class filter (All Classes = every name) and the local player's slot.
 *
 * Both behaviors were lost in the port because the legacy overlay applied them inside its own tallying loop, and the derived
 * meter reads a prebuilt CombinedStats instead. This is that step as a pure function over what the builder already produced:
 *
 *   - the input is the builder's StatsList — sorted by Total descending, ranks 1..N dense, ClassName filled from the
 *     registry (null when it never saw the name cast) — so "rank" means position in the FULL board;
 *   - under a class filter the local player is still shown: once the walk reaches them, `myIndex == list.Count` is the
 *     legacy's own expression for "this row is the one the filter cannot drop";
 *   - a local player ranked below the last bar keeps the LAST bar rather than disappearing: that was the felt difference of
 *     the old meter ("your number is always on it"), and it costs at most one slot of somebody else's;
 *   - ranks are NOT renumbered after the filter. The legacy assigned `rank++` while walking the full list, before testing
 *     the class — so a filtered board shows gaps (1, 2, 5), which is what a player comparing against the summary reads as
 *     "these are my real places on the raid".
 *
 * It runs over both panels (damage and tanking used the same maxRows and selectedClass in the legacy build).
 */
internal static class MeterDisplaySelection
{
  /*
   * Whether this stat row is the operator's own character. The meter highlights that bar with a different brush, and the
   * highlight and the slot must never disagree about WHO it is — which is why this one predicate serves both, instead of the
   * StartsWith-only test each used to carry: "Runes" is not the player "Rune", so the boundary check stays, and the comparison
   * is case-insensitive because the name comes back capitalized while the setting does not have to be.
   */
  internal static bool IsPlayerName(string name, string playerName)
    => !string.IsNullOrEmpty(playerName)
       && !string.IsNullOrEmpty(name)
       && name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase)
       && (playerName.Length >= name.Length || name[playerName.Length] == ' ');

  /*
   * Which rows of a board's StatsList the meter's bars hold. `selectedClass` is the combo's word; null/empty or the resource's
   * All-Classes word means no filter. `maxRows` is how many player bars exist (the title bar is not one). Returns a NEW list —
   * the caller replaces what it renders with this, and the board itself keeps holding its full ranking.
   */
  internal static List<PlayerStats> SelectRows(IReadOnlyList<PlayerStats> stats, string selectedClass, int maxRows, string playerName)
  {
    var anyClass = string.IsNullOrEmpty(selectedClass) || CombatRecordLookup.AnyClass is { Length: > 0 } any &&
      string.Equals(selectedClass, any, StringComparison.OrdinalIgnoreCase);

    var list = new List<PlayerStats>(stats.Count);
    var myIndex = -1;

    foreach (var stat in stats)   // already Total-descending with its full-board rank
    {
      if (myIndex < 0 && IsPlayerName(stat.Name, playerName))
      {
        myIndex = list.Count;
      }

      if (myIndex == list.Count || anyClass || string.Equals(selectedClass, stat.ClassName, StringComparison.OrdinalIgnoreCase))
      {
        list.Add(stat);
      }
    }

    if (maxRows < 1) return [];

    // myIndex is an index into the FILTERED list, and the local player was added to it unconditionally, so it is always in range.
    if (myIndex > maxRows - 1)
    {
      // The local player's bar is the last one; everyone above it yields exactly their slot. Ranks keep their full-board places.
      var me = list[myIndex];
      var top = new List<PlayerStats>(list.Take(maxRows - 1));
      top.Add(me);
      return top;
    }

    return list.Take(maxRows).ToList();
  }
}

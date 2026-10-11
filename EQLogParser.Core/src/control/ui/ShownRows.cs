using System;
using System.Collections.Generic;

namespace EQLogParser;

// Visibility moves references, never copies rows. Grid range indices match this list only when unsorted.
// See docs/DesignNotes.md → "The fight list dials filter our rows, not the grid".
internal static class ShownRows
{
  /*
   * The visible subsequence of `rows`, in the same order and by the same references — no row is copied, and a row that stays visible keeps
   * its identity, which is what makes it legal to restore a selection by instance across a rebuild.
   */
  internal static List<T> Project<T>(IReadOnlyList<T> rows, Func<T, bool> shown) where T : class
  {
    var visible = new List<T>(rows.Count);
    for (var i = 0; i < rows.Count; i++)
    {
      if (shown(rows[i])) visible.Add(rows[i]);
    }

    return visible;
  }

  /*
   * Reconcile unique row references whose survivors retain relative order (guaranteed by RowPatch.Build at the caller).
   * This is an insert/remove merge, not a general reorder algorithm. Report how many edits were spent.
   *
   * Both lists hold shared references, so membership is a reference test and the diff needs no key: drop what left, then place what arrived
   * at its own index. Removals run descending (an index cannot shift under a row still to be visited) and insertions ascending (each insert
   * puts a row exactly where `wanted` says, and everything after it has not been looked at yet).
   *
   * This is the live path, so what it buys is what a caller must not lose: an ObservableCollection edited by these two loops notifies per
   * edit rather than wholesale, which is why a row the reader has selected survives a pass in which some OTHER fight crossed a dial's line.
   * A toggle that moves most of the list at once is worth a wholesale swap instead, and the pane does that itself.
   */
  internal static (int Removals, int Insertions) Reconcile<T>(IList<T> current, IReadOnlyList<T> wanted) where T : class
  {
    var stays = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);

    var removals = 0;
    for (var i = current.Count - 1; i >= 0; i--)
    {
      if (stays.Contains(current[i])) continue;
      current.RemoveAt(i);
      removals++;
    }

    var insertions = 0;
    for (var i = 0; i < wanted.Count; i++)
    {
      if (i < current.Count && ReferenceEquals(current[i], wanted[i])) continue;
      current.Insert(i, wanted[i]);
      insertions++;
    }

    return (removals, insertions);
  }

  /*
   * The closed runs of matching rows as [from, to] index pairs — grid selects want contiguous ranges, so "these seven rows and that one"
   * is two calls rather than eight. Unlike the walk it replaces, this one counts positions in the list it is handed, because the list it is
   * handed is already only the visible rows; there is no hidden-row bookkeeping left to do here.
   */
  internal static List<(int From, int To)> Runs<T>(IReadOnlyList<T> rows, Predicate<T> matches) where T : class
  {
    var runs = new List<(int From, int To)>();
    var runStart = -1;
    for (var i = 0; i <= rows.Count; i++)
    {
      var hit = i < rows.Count && matches(rows[i]);
      if (hit)
      {
        if (runStart < 0) runStart = i;
      }
      else if (runStart >= 0)
      {
        runs.Add((runStart, i - 1));
        runStart = -1;
      }
    }

    return runs;
  }
}

/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): the query is a word the reader
 * typed and genuinely may be empty, and stating that must not turn on a warning wave in code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * The Identity Table's view decisions, split out of NamesTable because they are the part a Windows grid cannot demonstrate:
 * WHICH names a census answers (filter + order) and WHERE the reader's pager sits once the answer has a size.
 *
 * Why these live here at all: since 2026-12 the pane no longer hands the grid its whole list. It publishes ONE ordered,
 * filtered list (f(census, question)) and the grid shows it a page at a time — so a name can now be missing from the grid
 * for reasons that have nothing to do with the census (the reader's own filter), and a page can be owed a jump when the
 * selection survives a rebuild. Both of those are arithmetic, and arithmetic that is only exercised on a live 25,000-name
 * capture during somebody's raid is arithmetic nobody tests. See docs/names-table-paging-plan.md → "The publish door".
 */
internal static class NamesTableView
{
  /// <summary>What one page of the Identity Table holds (docs/names-table-paging-plan.md → "What we picked"): 25,000 names
  /// in twenty-five pages, and 1,000 is exactly what this app's other big grids already render without complaint — the
  /// vendor's own sample runs 576k rows at this page size. A constant by design: a dial nobody asked for.</summary>
  internal const int DefaultPageSize = 1000;

  /*
   * The reader's own question, in the identity form a build travels with. Two fields of it are VALUES (the word typed into
   * the filter box) and two are STATE (which types the strip's multi-select shows, which column the header sorts). A build
   * captures one at start and is dropped at publish if the strip no longer asks it — that compare is what this record IS.
   */
  internal sealed record Question(string Query, string TypesKey, string Column, bool Descending);

  /// <summary>The question as the pane's own fields read right now.</summary>
  internal static Question For(string query, IReadOnlyCollection<IdentityKind> kinds, string column, bool descending)
    => new(query.Length == 0 ? "" : query, TypesKey(kinds), column, descending);

  /*
   * The multi-select's identity: the chosen kinds as one word. Sorted and joined, so {Player, Pet} and {Pet, Player} are
   * the same question — a build that answers "both" must not be dropped because the reader clicked them in a different order.
   */
  internal static string TypesKey(IReadOnlyCollection<IdentityKind> kinds)
  {
    var sorted = kinds.ToArray();
    Array.Sort(sorted);
    return string.Join("|", sorted);
  }

  /*
   * The whole index build in one call: keep the names that pass, order them by the header's column. This is the method the
   * off-thread lane runs, and it is deliberately the only place both halves meet — a filtered list that is not ordered is a
   * list whose header lies, and an ordered list that is not filtered is the old 25k-row grid with a costume on.
   */
  internal static List<T> FilterOrdered<T>(IReadOnlyList<T> rows, Func<T, bool> keep, Comparison<T> order)
  {
    var result = new List<T>(rows.Count);
    foreach (var row in rows)
      if (keep(row)) result.Add(row);
    result.Sort(order);
    return result;
  }

  /*
   * The pager's answer for one published list: how many pages, which page to be on, and whether that is a JUMP the reader
   * should feel. Two rules, both from the plan's publish door:
   *
   *   - **The page clamps.** A filter can shrink ten pages to two; a pager still standing on page seven would show an empty
   *     grid with a strip saying "8 of 10" — the list must land on the last page that exists, and a list with no rows has
   *     no pages at all (0, not 1: an empty page reads as a loading failure).
   *   - **The selection follows.** If the name the reader selected still answers the question, the pager moves to the page
   *     it lives on BEFORE the selection is handed back — handing the grid an object its current page does not show is how
   *     "the selection vanished" would happen. No selection, no jump: a rebuild never moves a viewport nobody is tracking.
   */
  internal sealed record PageDecision(int PageCount, int PageIndex, bool JumpToSelection, int SliceStart, int SliceCount);

  /// <summary>`selectedIndex` is the name's position in the ordered list, -1 when nothing is selected or it no longer passes.</summary>
  internal static PageDecision DecidePage(int totalCount, int pageSize, int currentPageIndex, int selectedIndex)
  {
    if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "a page must hold rows");

    var pageCount = (totalCount + pageSize - 1) / pageSize;   // 0 rows -> 0 pages
    var page = Math.Clamp(currentPageIndex, 0, Math.Max(0, pageCount - 1));

    var jump = false;
    if (pageCount > 0 && selectedIndex >= 0 && selectedIndex < totalCount)
    {
      var selectedPage = selectedIndex / pageSize;
      if (selectedPage != page)
      {
        page = selectedPage;
        jump = true;
      }
    }

    var start = page * pageSize;
    var count = Math.Max(0, Math.Min(pageSize, totalCount - start));
    return new PageDecision(pageCount, page, jump, start, count);
  }
}

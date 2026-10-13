namespace EQLogParser;

internal interface IIdentityCensusRow : IIdentityRowKeys
{
  IdentityKind Kind { get; }
}

// Whole-census questions and publish tickets. See docs/DesignNotes.md → "Identity Table paging".
internal static class NamesTableView
{
  internal const int DefaultPageSize = 1000;

  internal sealed record Question(string Query, uint Kinds, string Column, bool Descending);

  internal sealed record BuildStamp(long Epoch, int SessionId, long CensusRevision, long WriteGeneration, Question Question,
    int DeriveRequestGeneration = 0);

  internal sealed record PageDecision(int PageCount, int PageIndex, bool JumpToSelection, int SliceStart, int SliceCount);

  private static readonly uint AllKinds = KindMask(IdentityVocabulary.TypeOptions.Select(o => o.Kind));

  internal static Question For(string query, IReadOnlyCollection<IdentityKind> kinds, string column, bool descending)
    => new(query, KindMask(kinds), column, descending);

  internal static string QueryText(string text, string cue) => text == cue ? string.Empty : text;

  internal static bool IsCurrent(BuildStamp answer, BuildStamp current) => answer == current;

  internal static bool Matches(string name, IdentityKind kind, Question question)
  {
    // All Types is unfiltered, including unplaced and taken-back rows. A narrowed set includes only its chosen kinds.
    var kindMatches = question.Kinds == AllKinds || (question.Kinds & (1u << (int)kind)) != 0;
    return kindMatches && (question.Query.Length == 0 || name.Contains(question.Query, StringComparison.OrdinalIgnoreCase));
  }

  internal static List<T> Build<T>(IReadOnlyList<T> completeCensus, Question question) where T : IIdentityCensusRow
    => FilterOrdered(completeCensus, row => Matches(row.Name, row.Kind, question),
      IdentityRowOrder.For<T>(question.Column, question.Descending));

  internal static List<T> FilterOrdered<T>(IReadOnlyList<T> rows, Func<T, bool> keep, Comparison<T> order)
  {
    var result = new List<T>(rows.Count);
    foreach (var row in rows)
      if (keep(row)) result.Add(row);
    result.Sort(order);
    return result;
  }

  /*
   * Where the pager stands after a rebuild: always clamped to the pages that still exist, and — ONLY when the caller says
   * the QUESTION changed (a search, a type dial, a sort header) — moved to the page the selection lives on.
   *
   * `followSelection` is an argument rather than a rule inside this arithmetic because the two cases are different gestures
   * made by different people: a reader who just narrowed the list asked "where is that name", while a reader who paged away
   * from their selection six pages ago asked nothing, and the census landing two seconds later has no business carrying them
   * back. Following unconditionally was exactly that bug (2026-10-13): an ordinary census yanked the pager to the selected
   * row's page, which reads as the table moving out from under the operator.
   */
  internal static PageDecision DecidePage(int totalCount, int pageSize, int currentPageIndex, int selectedIndex,
    bool followSelection)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

    var pageCount = totalCount == 0 ? 0 : 1 + ((totalCount - 1) / pageSize);
    var page = Math.Clamp(currentPageIndex, 0, Math.Max(0, pageCount - 1));
    var jump = false;
    if (followSelection && selectedIndex >= 0 && selectedIndex < totalCount)
    {
      var selectedPage = selectedIndex / pageSize;
      jump = selectedPage != page;
      page = selectedPage;
    }

    var start = page * pageSize;
    return new PageDecision(pageCount, page, jump, start, Math.Min(pageSize, totalCount - start));
  }

  /*
   * The strip's count, in the shape an operator reads it: what THIS page shows, how many rows the current question has,
   * and — when a dial narrowed it — how big the capture still is. The last one is the whole reason the census total is kept:
   * "my name vanished" means "the filter ate it" far more often than "the parse lost it", and a page that says only what is
   * on it cannot answer that.
   *
   * `noun` comes from the caller because the word is Resource vocabulary (the WPF assembly owns the strings; Core owns the
   * arithmetic). An empty census is an EMPTY string, not "Showing 0 …": the pane opens before there is a capture to count,
   * and a zero there reads as a failed read rather than a not-yet-started one.
   */
  internal static string CountText(long censusTotal, int shownCount, int pageSize, int pageIndex, bool narrowed, string noun)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(pageSize);

    if (censusTotal == 0) return string.Empty;
    if (shownCount == 0) return $"No matching {noun}, {censusTotal:N0} total";

    var from = (long)pageIndex * pageSize + 1;
    var to = Math.Min((long)shownCount, ((long)pageIndex + 1) * pageSize);
    var head = $"Showing {from:N0} to {to:N0} of {shownCount:N0} {noun}";
    return narrowed ? $"{head}, {censusTotal:N0} total" : head;
  }

  // The line beside the pager's own arrows: which page and how many. Empty when there is no page to stand on.
  internal static string PageText(int pageCount, int pageIndex)
    => pageCount <= 0 ? string.Empty : $"Page {pageIndex + 1:N0} of {pageCount:N0}";

  private static uint KindMask(IEnumerable<IdentityKind> kinds)
  {
    var mask = 0u;
    foreach (var kind in kinds) mask |= 1u << (int)kind;
    return mask;
  }
}

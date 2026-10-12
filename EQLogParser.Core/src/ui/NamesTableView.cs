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

  internal static PageDecision DecidePage(int totalCount, int pageSize, int currentPageIndex, int selectedIndex)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

    var pageCount = totalCount == 0 ? 0 : 1 + ((totalCount - 1) / pageSize);
    var page = Math.Clamp(currentPageIndex, 0, Math.Max(0, pageCount - 1));
    var jump = false;
    if (selectedIndex >= 0 && selectedIndex < totalCount)
    {
      var selectedPage = selectedIndex / pageSize;
      jump = selectedPage != page;
      page = selectedPage;
    }

    var start = page * pageSize;
    return new PageDecision(pageCount, page, jump, start, Math.Min(pageSize, totalCount - start));
  }

  private static uint KindMask(IEnumerable<IdentityKind> kinds)
  {
    var mask = 0u;
    foreach (var kind in kinds) mask |= 1u << (int)kind;
    return mask;
  }
}

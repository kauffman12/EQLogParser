// Annotations only; Core builds with Nullable=disable, and the eye rule's optional kind lookup needs to say "or nothing".
#nullable enable annotations

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

    /*
     * The table's UNIVERSE: which names get a row at all, decided before any filter.
     *
     * A name whose own spelling carries a summon word ("Kizant`s pet", "Beurun`s warder" — ClassificationRules.OwnerSuffixes) is settled by the log:
     * Pet at Certain, with the owner written out of the same string. Its row has no Type pencil, so listing ~4.7 % of a raid night's census to repeat
     * names the reader knows is noise; those rows are hidden here. DISPLAY ONLY — still classified, still scored, still in the ledger.
     *
     * Still listed on purpose: an OPERATOR VERDICT (a wrong click must stay correctable), a CUSTOM-NAMED pet ("Puksuu" — its owner is a real question),
     * and R21 spell-effect rows ("cannot be edited" is not "not worth a row"). A summon whose owner is an NPC is corrected at the OWNER's row.
     *
     * `Eye of <name>` is conditional: the shape proves nothing about the name inside it, so an eye keeps its row and loses it ONLY while that name reads
     * Player, returning when it stops reading that way. `kindByName` null means no eye resolves — the direction that keeps rows.
     */
  internal static bool IsListed(ClassificationReport.Row row, IReadOnlyDictionary<string, IdentityKind>? kindByName = null)
  {
    if (row.IsOperatorVerdict) return true;                              // a hand-written claim always keeps its row
    if (ClassificationRules.OwnerInName(row.Name) is not null) return false;   // a summon word in the name settles it

    var master = ClassificationRules.EyeSummonOwnerInName(row.Name);
    return master is null                                                // not an eye: nothing more to ask
           || kindByName is null
           || !kindByName.TryGetValue(master, out var masterKind)
           || masterKind != IdentityKind.Player;                          // only one of ours makes the row pointless
  }

  /*
   * The census filtered to its table's universe, in source order. One entry point because the eye rule needs the whole census to
   * answer for a single row, and the dictionary that costs (a name→kind map over up to 25,000 remembered names) is built only when a
   * census actually carries an eye shape — which measured captures say it essentially never does.
   */
  internal static List<ClassificationReport.Row> Universe(IReadOnlyList<ClassificationReport.Row> census)
  {
    var kindByName = KindLookupForEyes(census);
    var listed = new List<ClassificationReport.Row>(census.Count);
    foreach (var row in census)
      if (IsListed(row, kindByName)) listed.Add(row);
    return listed;
  }

  private static Dictionary<string, IdentityKind>? KindLookupForEyes(IReadOnlyList<ClassificationReport.Row> census)
  {
    var hasEye = false;
    foreach (var row in census)
      if (ClassificationRules.EyeSummonOwnerInName(row.Name) is not null) { hasEye = true; break; }
    if (!hasEye) return null;

    // Names are matched without case here because the master text is copied out of a sentence, not interned.
    var map = new Dictionary<string, IdentityKind>(census.Count, StringComparer.OrdinalIgnoreCase);
    foreach (var row in census) map[row.Name] = row.Kind;
    return map;
  }

  internal static bool Matches(string name, IdentityKind kind, Question question)
  {
    // All Types is unfiltered, including unplaced and taken-back rows. A narrowed set includes only its chosen kinds.
    var kindMatches = question.Kinds == AllKinds || (question.Kinds & (1u << (int)kind)) != 0;
    return kindMatches && (question.Query.Length == 0 || name.Contains(question.Query, StringComparison.OrdinalIgnoreCase));
  }

  internal static List<T> Build<T>(IReadOnlyList<T> completeCensus, Question question) where T : IIdentityCensusRow
    => FilterOrdered(completeCensus, row => Matches(row.Name, row.Kind, question),
      IdentityRowOrder.For<T>(question.Column, question.Descending));

  internal static List<T> Project<T>(IReadOnlyList<ClassificationReport.Row> census, Question question,
    Func<ClassificationReport.Row, T> project) where T : IIdentityCensusRow
  {
    var result = new List<T>();
    foreach (var row in census)
      if (Matches(row.Name, row.Kind, question)) result.Add(project(row));
    result.Sort(IdentityRowOrder.For<T>(question.Column, question.Descending));
    return result;
  }

  internal static List<T> FilterOrdered<T>(IReadOnlyList<T> rows, Func<T, bool> keep, Comparison<T> order)
  {
    var result = new List<T>(rows.Count);
    foreach (var row in rows)
      if (keep(row)) result.Add(row);
    result.Sort(order);
    return result;
  }

    /*
     * Where the pager stands after a rebuild: always clamped to pages that still exist, and moved to the selection's page ONLY when the question
     * changed (search, dial, sort). Following on every census reads as the table moving out from under the operator, so `followSelection` is an
     * argument rather than a rule inside this arithmetic.
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
     * The strip's count: what this page shows, how many rows the current question has, and — when a dial narrowed it — how big the capture still is.
     * That last one exists because "my name vanished" usually means "the filter ate it". An empty census is an EMPTY string, not "Showing 0 …": the pane
     * opens before there is anything to count.
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

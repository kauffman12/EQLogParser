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
   * The table's UNIVERSE: which names it lists at all, decided before any question is asked.
   *
   * A name whose own spelling carries a summon word — "Kizant`s pet", "Beorun`s warder" (ClassificationRules.OwnerSuffixes, the
   * one list) — is settled twice over by the log itself: the kind IS Pet at Certain, and R5 writes the owner out of the very
   * string it read the suffix from (`R5-owner:Kizant`). Nothing a person could type improves that, so such a row already has no
   * Type pencil (IdentityVocabulary.CanOverrule) — and listing 140 of them in a raid night (measured: 4.7% of that census, every
   * one verdicted Pet, zero surprises; docs/DesignNotes.md → "Identity Table paging") is a wall of rows repeating names the reader
   * already knows. An operator asked for exactly this: "thats just what the log says".
   *
   * DISPLAY ONLY, which is the whole point: the name is still classified, still scored on the boards, still remembered in the
   * ledger. What disappears is an answer nobody can change, not the answer. And the fixable half of the case stays on screen: a
   * summon whose owner is really an NPC ("A worry wraith`s pet") is corrected at the OWNER's row, which keeps its pencil.
   *
   * Two doors stay open by design:
   *   - **An operator verdict is always listed.** A wrong click has to remain correctable by another one — the same reason an
   *     override keeps its pencil on a name that spells otherwise (and it is the only way such a row can ever be taken back).
   *   - **A custom-named pet is listed.** "Puksuu" is a Pet by spell data or petslot evidence, so its owner genuinely is a
   *     question for a person (20 of that capture's 160 Pet rows). Only the summon-word shape is closed.
   *
   * NOT hidden here: R21 spell-effect rows have no pencil either, and they stay listed — a dot or proc name is worth looking up even
   * though nobody can type a better answer for it. "Cannot be edited" and "is not worth a row" are two different decisions.
   *
   * THE EYE HALF ASKS ABOUT ANOTHER ROW. `Eye of <name>` names its master the way ``Kizant`s pet`` does, but the shape proves nothing
   * about that name: NPCs are legitimately named like one, and an eye can only ever be NPC anyway (IdentityVocabulary.TypeOptionsFor).
   * So an eye keeps its row by default and loses it ONLY while the name inside it reads Player — "Eye of Kizant" goes while Kizant is
   * one of ours and comes back the moment he is not (an operator calling him NPC, or his name leaving the census). Measured: zero
   * eye-shaped rows in three captures (~1.5 GB), because an eye is refused as a combatant before its name is interned; this guards a
   * lane that exists in code rather than a population of rows, which is exactly why it is conditional instead of unconditional.
   *
   * `kindByName` is null when the caller offers no kinds, which reads "no eye can be resolved" — the direction that keeps rows.
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

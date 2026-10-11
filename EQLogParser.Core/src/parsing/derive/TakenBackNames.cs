using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): a click can legitimately name nobody,
 * and refusing silently is the answer this store gives rather than a warning.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * The names whose claim the operator took back in THIS session (2026-11).
   *
   * `ClassificationCommands.Recalculate` removes memory and writes no verdict — that is the verb, and nothing here changes it.
   * What this list answers is a narrower question the census had no way to ask: after the removal, should the name still be ON
   * the Identity Table? Before this list existed the answer was "only if something else lists it", so a name whose every claim
   * came from memory left the table entirely on the operator's click, and the window they were looking at lost the row they were
   * looking at (the ask: "if the button is clicked and the name isn't in the current log file at all, instead of removing it could
   * it be switched to Unknown?"). A take-back that makes the subject disappear reads as a delete, which is the one thing this
   * verb is not: it forgets, and forgetting has no answer to show, so the row shows the absence instead.
   *
   * Three laws.
   *
   *   1. **It is DISPLAY input, never an identity claim.** Nothing in the rule book reads this list, no store carries it, and it
   *      writes nothing: an asserted `Unknown` would ride at Manual (1000) above what this capture watched happen, which is the
   *      inversion the take-back exists to avoid (docs/DesignNotes.md → "The take-back icon takes everything back"). The report
   *      adds a row for these names ONLY when nothing else lists them, and `ClassificationReport` still asks the timeline first —
   *      so a name this log places keeps its kind, its rule's word and its facts, exactly as before. This list can only ever say
   *      "the operator un-wrote this and the capture has not answered yet".
   *   2. **Session-scoped, cleared when the capture it was clicked in goes away** (`DeriveEngine.Dispose`). A click belongs to the
   *      log that provoked it; last night's take-back must not pin a name onto tonight's identity list. The same lifetime as the
   *      owed-gesture slot on `UnaskedRefresh`, for the same reason: it remembers a HAND, not a fact.
   *   3. **One name at a time is not a limit here.** The overlay label is single-slot because state is one string; this list is
   *      the whole point of clicking several rows in a row — each stays visible and each reads what happened to it.
   */
  internal static class TakenBackNames
  {

    /// <summary>
    /// The provenance word a taken-back row carries (Row.Reason). It is not a rule code — no R-number, no dash, and nothing in the rule
    /// book writes it — but it rides the same maps, so it has a rank (IdentityVocabulary.ClaimRanks), a cell word ("Taken Back") and a
    /// hover sentence ("Claim Taken Back"). Display state travels through the SAME vocabulary so a row can never reach the screen wearing
    /// an untranslated identifier.
    /// </summary>
    internal const string Word = "TakenBack";

    private static readonly ConcurrentDictionary<string, byte> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Note that the operator withdrew this name's claim. Case-insensitive like every other identity key.</summary>
    internal static void Note(string? name)
    {
      if (!string.IsNullOrWhiteSpace(name)) _names[name!] = 0;
    }

    /// <summary>Was this name's claim taken back in this session?</summary>
    internal static bool Contains(string? name) => !string.IsNullOrEmpty(name) && _names.ContainsKey(name!);

    /*
     * Every name taken back this session — census input only (order means nothing; the pane sorts its own rows), so the small copy is
     * worth what it buys: a snapshot the census can walk while another click Notes into the live table.
     */
    internal static IReadOnlyCollection<string> All() => _names.Keys.ToArray();

    /// <summary>A new capture answers for itself: nothing was clicked in it yet.</summary>
    internal static void Clear() => _names.Clear();
  }
}

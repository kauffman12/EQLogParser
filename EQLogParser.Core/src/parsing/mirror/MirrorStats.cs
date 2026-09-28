/*
 * MirrorStats — one calculation, asked about a set of rows.
 *
 * Monitoring a log is storage: lines become facts, facts become rows. Nothing here runs while that happens, and
 * nothing here keeps a second tally. A surface that wants numbers asks this class about the rows it is showing —
 * the fight list's selection, or the damage overlay's current session — and gets the same arithmetic every other
 * surface gets: MirrorSummaryFights turns those rows into records aimed at their own npcs, DamageStatsBuilder does
 * the counting (its DamageValidator filters, its pet folding, its activity segments), and the seconds behind any DPS
 * come from StatsUtil.UpdateRaidTimeRanges rather than a caller's stopwatch. So "the overlay says 4,182,300" and "I
 * selected those ten fights and pressed summary says 4,182,300" is not a discipline two components keep, it is one
 * function called twice.
 *
 * THE ONE RULE: a scope builds on its OWN DamageStatsBuilder instance.
 * The boards read DamageStatsBuilder.Instance, whose last event is what the summary tabs, charts and copy-to-clipboard
 * paths show each other. An overlay refreshes about once a second; run that through the singleton and every refresh
 * would repaint the open summary with the overlay's window, and the two numbers this class exists to keep identical
 * would diverge by construction. A private instance costs one object and a set of dictionaries per scope — against
 * the millions of per-line accumulations it replaces on the parse path, noise — and leaves the shared one untouched
 * (pinned: ARefreshLeavesTheBoardsLastAnswerAlone).
 *
 * Runs on the caller's thread and returns the event that instance produced. It does not hop to a dispatcher: where
 * the hop happens is a property of the consumer (MainWindow.OnGenerationStatus queues at Background), and doing it
 * here would put a UI assumption inside code the overlay, the summary and a test all share.
 */
namespace EQLogParser.Mirror;

using System.Collections.Generic;

internal static class MirrorStats
{
  /*
   * Stats for exactly these rows: damage, tanking and per-player activity built from the facts the rows point at,
   * and healing windowed by the rows' own span the way the healing board has always worked (a heal belongs to no
   * fight, so it is windowed rather than selected).
   *
   * Returns null when there is nothing to ask about. That is different from "an empty event" on purpose: a caller
   * with no rows has no scope at all, and showing an empty board for that would be the same lie as showing stale
   * numbers — which of the two to display is the surface's decision, made with the session it owns.
   */
  internal static StatsGenerationEvent? For(IReadOnlyList<DerivedFight> rows, MirrorDamageIndex index,
    DamageFactTable facts, HealFactTable heals)
  {
    if (rows is not { Count: > 0 })
    {
      return null;
    }

    var input = MirrorSummaryFights.Build(rows, index, facts);
    var options = new GenerateStatsOptions
    {
      AllRanges = input.AllRanges,
      // An empty heal list means "this scope healed nothing", not "say nothing about healing" — see the note on
      // GenerateStatsOptions.Heals. Passing null here would leave a refresh showing the previous scope's numbers.
      Heals = MirrorSummaryHeals.Materialize(heals, input.AllRanges),
    };
    options.Npcs.AddRange(input.Fights);

    var scope = new DamageStatsBuilder();
    StatsGenerationEvent? result = null;
    scope.EventsGenerationStatus += generated => result = generated;
    scope.BuildTotalStats(options);

    return result;
  }
}

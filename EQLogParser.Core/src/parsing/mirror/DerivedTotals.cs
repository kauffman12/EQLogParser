/*
 * DerivedTotals — one calculation, asked about a set of rows.
 *
 * Monitoring a log is storage: lines become facts, facts become rows. Nothing here runs while that happens, and
 * nothing here keeps a second tally. A surface that wants numbers asks this class about the rows it is showing —
 * the fight list's selection, or the damage overlay's current session — and gets the same arithmetic every other
 * surface gets: FightSummarySource turns those rows into records aimed at their own npcs, DamageStatsBuilder does
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
// Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in
// optional rows/fights because a scope legitimately has nothing to show.
#nullable enable annotations
namespace EQLogParser.Mirror;

using System.Collections.Generic;

internal static class DerivedTotals
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
  /*
   * The optional window is the meter's reset. Zeroing a damage meter does not choose a different set of fights, it
   * asks the same fights about a slice of their seconds, and legacy models that by accumulating its own totals since
   * the reset (DamageOverlayStatsBuilder: per-player sums with an activity TimeRange, zeroed on reset, expired after
   * `mode` seconds — or FightTimeout when mode is 0, i.e. "on kill"). Here the slice is an argument to the one
   * calculation instead: same rows, facts whose seconds lie in [fromT, toT], and therefore damage, hit counts and
   * activity segments that all belong to the window together. A surface's zero point, its timeout and its blank-board
   * rule stay where legacy put them — in the overlay — because they are a display policy; what arrives here is the
   * window they decided on.
   *
   * Slicing is exact in the way that matters: For(rows, a, b) + For(rows, b, c) totals the same as For(rows, a, c),
   * because a fact is either inside the window or not, and nothing is counted twice or lost in the seam.
   */
  /*
   * The damage meter's shape for the same scope: damage and tanking halves, each built on its OWN builder instance,
   * returned in the container the overlay already paints. A window is expected here ("since I zeroed the meter") and
   * both halves get the same one, so a tank column cannot show seconds the damage column excluded.
   *
   * Null means "this scope has nothing to show", which is also legacy's contract for the overlay builder: it returns
   * null when neither half produced anything and the window blanks itself. One half being null is normal too — the
   * legacy builder does the same, a raid that only healed has no tank board.
   */
  internal static DamageOverlayStats? ForOverlay(IReadOnlyList<DerivedFight> rows, FightFactIndex index,
    DamageFactTable facts, HealFactTable heals, double fromT, double toT)
  {
    if (rows is not { Count: > 0 })
    {
      return null;
    }

    var input = FightSummarySource.Build(rows, index, facts, fromT, toT);
    if (input.Fights.Count == 0)
    {
      return null;
    }

    CombinedStats? damage = null;
    var damageScope = new DamageStatsBuilder();
    var damageOptions = new GenerateStatsOptions { AllRanges = input.AllRanges, MinSeconds = 0 };
    damageOptions.Npcs.AddRange(input.Fights);
    damageScope.EventsGenerationStatus += generated => damage = generated.CombinedStats;
    damageScope.BuildTotalStats(damageOptions);

    CombinedStats? tanking = null;
    var tankScope = new TankingStatsBuilder();
    var tankingOptions = new GenerateStatsOptions { AllRanges = input.AllRanges, MinSeconds = 0 };
    tankingOptions.Npcs.AddRange(input.Fights);
    tankScope.EventsGenerationStatus += generated => tanking = generated.CombinedStats;
    tankScope.BuildTotalStats(tankingOptions);

    if (damage is null && tanking is null)
    {
      return null;
    }

    // Healing deliberately not built here: the meter has never shown a heal column from this call, and building a
    // third board per refresh would be new work on a once-a-second path for a number nothing paints.
    return new DamageOverlayStats { DamageStats = damage, TankStats = tanking };
  }

  internal static StatsGenerationEvent? For(IReadOnlyList<DerivedFight> rows, FightFactIndex index,
    DamageFactTable facts, HealFactTable heals, double fromT = double.NegativeInfinity, double toT = double.PositiveInfinity)
  {
    if (rows is not { Count: > 0 })
    {
      return null;
    }

    var input = FightSummarySource.Build(rows, index, facts, fromT, toT);
    var options = new GenerateStatsOptions
    {
      AllRanges = input.AllRanges,
      // An empty heal list means "this scope healed nothing", not "say nothing about healing" — see the note on
      // GenerateStatsOptions.Heals. Passing null here would leave a refresh showing the previous scope's numbers.
      Heals = HealSummarySource.Materialize(heals, input.AllRanges),
    };
    options.Npcs.AddRange(input.Fights);

    var scope = new DamageStatsBuilder();
    StatsGenerationEvent? result = null;
    scope.EventsGenerationStatus += generated => result = generated;
    scope.BuildTotalStats(options);

    return result;
  }
}

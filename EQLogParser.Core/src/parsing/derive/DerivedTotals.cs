// One-shot scope queries and the full-build reference for meter equivalence tests. Private builders must never
// replace the summary panes' singleton answers or subscribe to process-static events.
// The live meter continues compact accounting through DamageMeterCalculation.
// Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in
// optional rows/fights because a scope legitimately has nothing to show.
#nullable enable annotations
namespace EQLogParser;

using System.Collections.Generic;

internal static class DerivedTotals
{
  // Reference answer for one already-selected scope. Null means neither direction has anything to show;
  // eligibility, expiration and reset policy belong to DamageOverlayLifecycle, not this calculation.
  internal static DamageOverlayStats? ForOverlay(IReadOnlyList<DerivedFight> rows, FightFactIndex index,
    DamageFactTable facts, double fromT, double toT)
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
    // Match the live meter's abbreviated title and omit chart-only hit amounts from this reference build.
    var damageOptions = new GenerateStatsOptions
    { Source = "damage meter overlay", AllRanges = input.AllRanges, MinSeconds = 0, CollectHitTotals = false, ShorthandCombinedCount = true };
    damageOptions.Npcs.AddRange(input.Fights);
    damageScope.EventsGenerationStatus += generated => damage = generated.CombinedStats;
    damageScope.BuildTotalStats(damageOptions);

    CombinedStats? tanking = null;
    var tankScope = new TankingStatsBuilder();
    var tankingOptions = new GenerateStatsOptions
    { Source = "damage meter overlay", AllRanges = input.AllRanges, MinSeconds = 0, CollectHitTotals = false, ShorthandCombinedCount = true };
    tankingOptions.Npcs.AddRange(input.Fights);
    tankScope.EventsGenerationStatus += generated => tanking = generated.CombinedStats;
    tankScope.BuildTotalStats(tankingOptions);

    if (damage is null && tanking is null)
    {
      return null;
    }

    // The meter's reference covers outgoing damage and tanking, not healing.
    return new DamageOverlayStats { DamageStats = damage, TankStats = tanking };
  }

  internal static StatsGenerationEvent? For(IReadOnlyList<DerivedFight>? rows, FightFactIndex index,
    DamageFactTable facts, HealFactTable heals, double fromT = double.NegativeInfinity, double toT = double.PositiveInfinity)
  {
    if (rows is not { Count: > 0 })
    {
      return null;
    }

    var input = FightSummarySource.Build(rows, index, facts, fromT, toT);
    var options = new GenerateStatsOptions
    {
      Source = "derived totals query",
      // Same law as ForOverlay: scoped rows outlive nobody's chart, so no raw amounts are kept (GenerateStatsOptions.CollectHitTotals).
      CollectHitTotals = false,
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

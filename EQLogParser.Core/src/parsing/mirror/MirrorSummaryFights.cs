using System;
using System.Collections.Generic;

namespace EQLogParser.Mirror
{
  /*
   * The derived fight list in the shape the existing damage summary already reads: the boss-directed
   * facts of one projected fight, rebuilt as DamageRecord objects inside Fight.DamageBlocks.
   *
   * Why this exists at all: the point of the mirror is that the current per-line pipeline and a
   * fact-table projection can be looked at side by side over the same log. The cheapest honest way to
   * compare them is to let the real summary tabs render the derived side — the same builder, the same
   * grids, so any disagreement is a classification difference and not two different pieces of UI
   * disagreeing with each other. Nothing here teaches the summary about the mirror; it is handed
   * ordinary Fight objects.
   *
   * What a materialized fight holds:
   *   DamageBlocks  — one ActionGroup per run of facts sharing a second (FightManager.AddAction's own
   *                   grouping rule), filled in fact order. Only facts aimed AT the row's own name land here,
   *                   which is what the legacy manager puts in DamageBlocks too: a mob hitting a raider belongs
   *                   to TankingBlocks, and mixing the directions would credit a boss with its own damage output.
   *                   "Aimed at" is the same comparison the row splits DamageToOwner/DamageByOwner by, not a fresh
   *                   verdict — so an unclassified attacker's hit on a known NPC sits in these blocks (legacy counts
   *                   it there as well) while its per-player credit keeps waiting for a classification pass.
   *   DamageSegments/DamageSubSegments — per-player (and per-spell) activity windows through
   *                   StatsUtil.UpdateTimeSegments, the same call FightManager makes. These drive the
   *                   per-player "activity" seconds, so without them DPS would be computed against the
   *                   whole raid window.
   *   BeginDamageTime/LastDamageTime/DamageTotal/DamageHits — from the facts themselves, not copied from
   *                   the row, so a fight whose blocks and roll-up ever drift apart is visible as a
   *                   mismatch instead of silently agreeing.
   *
   * What a materialized record does NOT hold, and what that costs:
   *   ModifiersMask is 0 — the fact table never captured it. DamageValidator therefore cannot exclude
   *   assassinate/headshot/slay-undead/... damage, so with any of those six settings turned OFF the
   *   derived total reads HIGHER than the legacy one. All six default to on (AppSettings), where the two
   *   agree exactly. Capturing parity means one more byte per damage fact (~10 MB on a 5 M-fact log), not
   *   a rebuild of anything — deliberately left for when parity is being chased for real.
   *   AttackerOwner comes from the line's own ownership word (ClassificationRules.OwnerInName), not from the
   *   registry: a pet the manager never mapped still lands under its owner here, which is a difference in what
   *   the two boards count, not an error in either. Pet roll-up ("X +Pets") works through the same field.
   *   PlayerDamageTotals stays empty — only DamageOverlayStatsBuilder reads it, and the overlay is fed
   *   straight from FightManager, never from a selection.
   *   SubType is filled in when the fact has none (RecordFrom's helper): the summary's melee counters look the
   *   subtype up in a ConcurrentDictionary, which throws on a null key — so a null there does not degrade the
   *   board, it empties it, with the exception swallowed inside DamageStatsBuilder's own catch.
   *
   * Label note: LabelTypes.LabelOf hands back the same Labels constants the parsers use, so
   * record.Type is the interned literal as always. "Reverse DS" is the one word the fact table knows and
   * HitLabel does not (it names an attacker, never a type); no damage line produces it as a type, and a
   * stray one would read back null exactly as an unknown word always does.
   */
  internal sealed class MirrorDamageIndex
  {
    private readonly object _gate = new();

    // fight -> ordinals into DamageFactTable.Facts, in table order (= arrival order), aimed at the owner only.
    private readonly Dictionary<DerivedFight, List<int>> _damageOrdinals = new();
    private readonly Dictionary<DerivedFight, Fight> _summaries = new();

    public long DamageFactCount { get; private set; }
    public int FightsWithDamage => _damageOrdinals.Count;


    // Four bytes of ordinal per captured fact: on a 5.3 M-fact log where roughly three quarters are
    // player-side, that is ~16 MB held for the life of the snapshot. It buys selection-time materializing
    // (no re-scan of the whole table on every click) and it dies with the snapshot it belongs to.
    public long EstimatedBytes => DamageFactCount * 4L;

    // Passed to FightProjection.Build as its owner sink — see the delegate's comment for why the answer
    // has to come from the projection rather than be recomputed here.
    internal void OnFact(DamageFact fact, int ordinal, DerivedFight owner, bool towardOwner)
    {
      if (!towardOwner) return;

      if (!_damageOrdinals.TryGetValue(owner, out var ordinals))
      {
        _damageOrdinals[owner] = ordinals = [];
      }

      ordinals.Add(ordinal);
      DamageFactCount++;
    }

    internal bool HasDamage(DerivedFight fight) => _damageOrdinals.ContainsKey(fight);

    // The ordinals behind a row, in table order — for the tests that hold this index to its contract, so
    // they can check a row against the fact table itself instead than against another copy of my bookkeeping.
    internal IReadOnlyList<int> DamageOrdinalsFor(DerivedFight fight)
      => _damageOrdinals.TryGetValue(fight, out var ordinals) ? ordinals : [];

    /*
     * The Fight the damage summary will be handed for this derived row, built on first request and kept
     * (re-clicking a row is free; the cache is dropped with the snapshot). Locked because selection events
     * arrive on the dispatcher while materializing happens on a worker task, and two clicks must not both
     * build the same fight.
     */
    internal Fight SummaryFightFor(DerivedFight fight, DamageFactTable facts)
    {
      lock (_gate)
      {
        if (_summaries.TryGetValue(fight, out var cached)) return cached;

        var built = BuildFight(fight, facts);
        _summaries[fight] = built;
        return built;
      }
    }

    private Fight BuildFight(DerivedFight fight, DamageFactTable facts)
    {
      var summary = new Fight
      {
        Id = fight.Id,
        Name = fight.Name,
        Dead = fight.Dead,
        GroupId = fight.GroupId,
        BeginTime = double.IsPositiveInfinity(fight.BeginTime) ? double.NaN : fight.BeginTime,
        LastTime = double.IsNegativeInfinity(fight.LastTime) ? double.NaN : fight.LastTime,
      };

      if (!_damageOrdinals.TryGetValue(fight, out var ordinals) || ordinals.Count == 0) return summary;

      var allFacts = facts.Facts;
      ActionGroup block = null;
      var lastBlockTime = double.NaN;
      var beginDamage = double.NaN;
      var lastDamage = double.NaN;

      foreach (var ordinal in ordinals)
      {
        var fact = allFacts[ordinal];
        var time = (double)fact.TimeS;

        // Same rule as FightManager.AddAction: consecutive actions sharing a timestamp share a block.
        if (block is null || !lastBlockTime.Equals(time))
        {
          block = new ActionGroup { BeginTime = time };
          summary.DamageBlocks.Add(block);
          lastBlockTime = time;
        }

        var attacker = facts.NameOf(fact.AtkIdx);
        var record = new DamageRecord
        {
          Attacker = attacker,
          /*
           * The owner a line-owned name carries inside itself ("Sancus`s pet" -> "Sancus"), which is what makes
           * DamageStatsBuilder fold a pet's damage under its raider when the registry never learned the pet. The
           * legacy manager asks the registry instead (and drops records whose attacker it cannot place at all), so
           * this one field is where the two boards part company: see MirrorSummaryFightsTest's mini-fight parity test.
           */
          AttackerOwner = fact.OwnerInLine ? ClassificationRules.OwnerInName(attacker) : null,
          Defender = facts.NameOf(fact.DefIdx),
          AttackerIsSpell = fact.AttackerIsSpell,
          Total = fact.Total,
          Type = LabelTypes.LabelOf(fact.TypeId),
          SubType = SubTypeOf(fact, facts),
        };

        block.Actions.Add(record);

        // The per-spell activity key comes from the same helper FightManager uses. It cannot be null here:
        // SubTypeOf guarantees a subtype, and CreateRecordKey returns at least that.
        StatsUtil.UpdateTimeSegments(summary.DamageSegments, summary.DamageSubSegments,
          StatsUtil.CreateRecordKey(record.Type, record.SubType), record.Attacker, time);

        if (LabelTypes.IsHit(fact.TypeId))
        {
          summary.DamageHits++;
          summary.DamageTotal += fact.Total;
        }

        /*
         * Both bounds need their own NaN test rather than a bare comparison: `time > NaN` is false, so seeding
         * lastDamage with NaN and comparing left every fight ending at NaN. TimeRange.Add drops a segment whose
         * bounds do not compare, and DamageStatsBuilder divides by an activity window of nothing — a derived
         * selection that produces no numbers at all, with no exception anywhere to say so.
         */
        if (double.IsNaN(beginDamage) || time < beginDamage) beginDamage = time;
        if (double.IsNaN(lastDamage) || time > lastDamage) lastDamage = time;
      }

      summary.BeginDamageTime = beginDamage;
      summary.LastDamageTime = lastDamage;

      return summary;
    }

    /*
     * A derived record's SubType is never null. The fact table stores "this line carried no modifier text" as
     * NoSubtype (-1) and plain melee is the ordinary case of that, but StatsUtil.UpdateDamageStats looks the
     * subtype up in a ConcurrentDictionary — which throws on a null key, inside DamageStatsBuilder's catch that
     * logs and carries on. So a null here is an empty board, not a rougher one. The type word stands in: the
     * activity window is identical either way, only the breakdown gets one sub-row per kind instead of one per
     * modifier message (and the same key reaches CreateRecordKey).
     */
    private static string SubTypeOf(DamageFact fact, DamageFactTable table)
      => table.SubtypeOf(fact.SubIdx) ?? LabelTypes.LabelOf(fact.TypeId) ?? Labels.OtherDmg;
  }

  // A derived selection turned into stats input: the materialized fights plus the AllRanges window the
  // summary expects (FightTable builds its own from the selected rows' BeginTime..LastTime, so the mirror
  // list has to hand over the same thing or DPS would be measured against a different clock).
  internal sealed record MirrorSummaryInput(IReadOnlyList<Fight> Fights, TimeRange AllRanges)
  {
    public int WithoutDamage { get; init; }
  }

  internal static class MirrorSummaryFights
  {
    public static MirrorSummaryInput Build(IReadOnlyList<DerivedFight> selected, MirrorDamageIndex index, DamageFactTable facts)
    {
      var result = new List<Fight>(selected.Count);
      var allRanges = new TimeRange();
      var withoutDamage = 0;

      foreach (var fight in selected)
      {
        if (!index.HasDamage(fight))
        {
          // A row nothing is aimed at — a charmed raider's own output, or an exchange whose facts all point
          // the other way — has nothing to show in a damage summary. Counting it keeps the status line
          // honest instead of the selected list silently shrinking.
          withoutDamage++;
          continue;
        }

        var built = index.SummaryFightFor(fight, facts);

        // Legacy's _allRanges: the wall-clock span of each selected fight, inactivity included.
        if (!double.IsNaN(built.BeginTime) && !double.IsNaN(built.LastTime))
        {
          allRanges.Add(new TimeSegment(built.BeginTime, built.LastTime));
        }

        result.Add(built);
      }

      return new MirrorSummaryInput(result, allRanges) { WithoutDamage = withoutDamage };
    }
  }
}

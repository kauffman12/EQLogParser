using System;
using System.Collections.Generic;

namespace EQLogParser
{
  // ZoneRecord intervals, including unnamed boundaries. The visit index keeps separate populations apart.
  internal sealed class NpcZoneTimeline
  {
    public const string UnknownZone = "Unknown";
    public const int UnknownVisit = -1;

    private readonly double[] _times;
    private readonly string[] _zones;

    public NpcZoneTimeline(IEnumerable<(double Time, string Zone)> entries)
    {
      var ordered = new List<(double Time, string Zone, int Order)>();
      if (entries is not null)
      {
        foreach (var (time, zone) in entries)
        {
          if (!double.IsFinite(time)) continue;
          var named = !string.IsNullOrEmpty(zone) && !zone.StartsWith("an area", StringComparison.OrdinalIgnoreCase);
          ordered.Add((time, named ? zone : null, ordered.Count));
        }
      }

      // A second can contain two entries; the later line must win even when sorting an unordered input.
      ordered.Sort(static (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));
      _times = new double[ordered.Count];
      _zones = new string[ordered.Count];
      for (var i = 0; i < ordered.Count; i++)
      {
        _times[i] = ordered[i].Time;
        _zones[i] = ordered[i].Zone;
      }
    }

    public int Count => _times.Length;

    /// <summary>Which zone interval t falls in, or UnknownVisit before the first zone line.</summary>
    public int VisitAt(double t)
    {
      if (_times.Length == 0 || !double.IsFinite(t)) return UnknownVisit;

      var lo = 0;
      var hi = _times.Length - 1;
      var found = UnknownVisit;
      while (lo <= hi)
      {
        var mid = (lo + hi) / 2;
        if (_times[mid] <= t)
        {
          found = mid;
          lo = mid + 1;
        }
        else
        {
          hi = mid - 1;
        }
      }

      return found;
    }

    public string ZoneAt(double t) => ZoneOf(VisitAt(t));
    public string ZoneOf(int visit) => visit < 0 ? UnknownZone : _zones[visit] ?? UnknownZone;
  }

  internal sealed class NpcCombatStatsRow
  {
    public string Zone { get; init; }
    public string Npc { get; init; }
    public double FirstSeen { get; init; }
    public int Deaths { get; init; }
    public long? AvgHp { get; init; }
    public long AvgMelee { get; init; }
    public long MaxMelee { get; init; }
  }

  /*
   * Estimate starting HP from closed same-name pools, weighted by killed lives, not by pools.
   * Selection changes the counted lives and melee, never the context needed to price a pool.
   * docs/DesignNotes.md → "NPC health is estimated from killed lives, and concurrent names pool".
   */
  internal static class NpcCombatStats
  {
    // rows is the COMPLETE projection from one snapshot; null selection means all, empty means none.
    public static List<NpcCombatStatsRow> Build(IReadOnlyList<DerivedFight> rows, DamageFactTable facts,
      FightFactIndex index, EntityTimeline timeline, HealFactTable heals, NpcZoneTimeline zones,
      IReadOnlyList<DerivedFight> selected = null)
      => Build(Capture(rows, facts, index, timeline, heals, zones, selected));

    // Call under the capture's derive gate. Only metadata and stream limits are copied; facts remain immutable.
    internal static Input Capture(IReadOnlyList<DerivedFight> rows, DamageFactTable facts, FightFactIndex index,
      EntityTimeline timeline, HealFactTable heals, NpcZoneTimeline zones, IReadOnlyList<DerivedFight> selected = null)
    {
      ArgumentNullException.ThrowIfNull(timeline);
      var lives = new List<Life>();
      var selection = selected is null ? null : new HashSet<DerivedFight>(selected);
      if (rows is not null && facts is not null && index is not null && selected is not { Count: 0 })
      {
        foreach (var row in rows)
        {
          lives.Add(new Life(row, row.Name, row.BeginTime, row.LastTime, row.DeathTime, row.Dead, row.EndReason,
            row.DamageToOwner, row.RaidPet, selection is null || selection.Contains(row),
            index.TankingOrdinalCount(row)));
        }
      }

      return new Input(lives, facts, index, timeline, heals, heals?.HealCount ?? 0, zones ?? new NpcZoneTimeline(null));
    }

    internal static List<NpcCombatStatsRow> Build(Input input)
    {
      var result = new List<NpcCombatStatsRow>();
      var (rows, facts, index, timeline, heals, healLimit, zones) = input;
      if (rows.Count == 0) return result;
      var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
      foreach (var row in rows)
      {
        if (row.RaidPet || string.IsNullOrEmpty(row.Name)) continue;
        var endT = row.Dead && !double.IsNaN(row.DeathTime) ? row.DeathTime : row.LastTime;
        if (!double.IsFinite(endT)) continue;
        if (timeline.IsConfirmedRaidPersonAt(row.Name, endT)) continue;

        var visit = zones.VisitAt(endT);
        if (!groups.TryGetValue(row.Name, out var group)) groups[row.Name] = group = new Group(row.Name);
        if (!group.Visits.TryGetValue(visit, out var lives)) group.Visits[visit] = lives = [];
        lives.Add(row);
      }

      var healTotals = CollectHeals(groups.Values, heals, healLimit);
      // Grouped melee walks revisit the same chunks across names and visits; retain decodes until the walk ends.
      using var damageRead = facts.RetainDecoded();
      foreach (var group in groups.Values)
      {
        foreach (var (visit, lives) in group.Visits)
        {
          var deaths = SelectedKills(lives);
          if (deaths == 0) continue;

          var hpSum = 0m;
          var pricedLives = 0;
          var meleeHits = 0L;
          var meleeTotal = 0m;
          var meleeMax = 0L;

          lives.Sort(static (a, b) => a.BeginTime.CompareTo(b.BeginTime));
          var run = new List<Life>();
          foreach (var life in lives)
          {
            if (run.Count > 0 && life.BeginTime - run[^1].LastTime > FightProjection.EngagementGapS)
            {
              PriceSelectedRun(run, group.Name, healTotals, ref hpSum, ref pricedLives);
              run.Clear();
            }
            run.Add(life);

            if (!life.Selected) continue;
            var ordinals = index.SnapshotTankingOrdinals(life.Source, life.TankingCount);
            for (var i = 0; i < ordinals.Length; i++)
            {
              ref readonly var fact = ref facts.RefAt(ordinals[i]);
              if (fact.TypeId != LabelTypes.Melee) continue;
              meleeHits++;
              meleeTotal += fact.Total;
              if (fact.Total > meleeMax) meleeMax = fact.Total;
            }
          }

          if (run.Count > 0)
            PriceSelectedRun(run, group.Name, healTotals, ref hpSum, ref pricedLives);

          result.Add(new NpcCombatStatsRow
          {
            Zone = zones.ZoneOf(visit),
            Npc = group.Name,
            FirstSeen = lives[0].BeginTime,
            Deaths = deaths,
            AvgHp = pricedLives > 0 ? (long)(hpSum / pricedLives) : null,
            AvgMelee = meleeHits > 0 ? (long)(meleeTotal / meleeHits) : 0,
            MaxMelee = meleeMax
          });
        }
      }

      return result;
    }

    internal sealed record Life(DerivedFight Source, string Name, double BeginTime, double LastTime,
      double DeathTime, bool Dead, DerivedFightEnd EndReason, long DamageToOwner, bool RaidPet, bool Selected,
      int TankingCount);

    internal sealed record Input(IReadOnlyList<Life> Rows, DamageFactTable Facts, FightFactIndex Index,
      EntityTimeline Timeline, HealFactTable Heals, int HealLimit, NpcZoneTimeline Zones);

    private static bool IsKill(Life row) => row.Dead && row.EndReason == DerivedFightEnd.Slain;

    private static int SelectedKills(List<Life> lives)
    {
      var deaths = 0;
      foreach (var life in lives)
      {
        if (IsKill(life) && life.Selected) deaths++;
      }
      return deaths;
    }

    private static void PriceSelectedRun(List<Life> run, string name,
      Dictionary<string, List<(double Time, long Total)>> healTotals,
      ref decimal hpSum, ref int pricedLives)
    {
      var damage = 0m;
      var selectedLives = 0;
      foreach (var life in run)
      {
        // Even an UNSELECTED unfinished life makes the pool unknowable.
        if (!IsKill(life)) return;
        damage += life.DamageToOwner;
        if (life.Selected) selectedLives++;
      }
      if (selectedLives == 0) return;

      var last = run[^1];
      var end = double.IsFinite(last.DeathTime) ? last.DeathTime : last.LastTime;
      if (healTotals.TryGetValue(name, out var healed)) damage -= HealedIn(healed, run[0].BeginTime, end);
      if (damage <= 0) return;

      // Keep fractional pool estimates until the final average; round neither a pool nor an individual life first.
      hpSum += damage * selectedLives / run.Count;
      pricedLives += selectedLives;
    }

    private static Dictionary<string, List<(double Time, long Total)>> CollectHeals(
      IEnumerable<Group> groups, HealFactTable heals, int healLimit)
    {
      var result = new Dictionary<string, List<(double Time, long Total)>>(StringComparer.Ordinal);
      if (heals is null || healLimit == 0) return result;

      var eligible = new HashSet<string>(StringComparer.Ordinal);
      var range = new TimeRange();
      foreach (var group in groups)
      {
        foreach (var lives in group.Visits.Values)
        {
          if (SelectedKills(lives) == 0) continue;
          eligible.Add(group.Name);
          var from = double.PositiveInfinity;
          var to = double.NegativeInfinity;
          foreach (var life in lives)
          {
            from = Math.Min(from, life.BeginTime);
            to = Math.Max(to, life.Dead && double.IsFinite(life.DeathTime) ? life.DeathTime : life.LastTime);
          }
          // A visit envelope includes the quiet gaps INSIDE pools; pricing still uses each run's exact bounds.
          range.Add(new TimeSegment(from, to));
        }
      }
      if (range.TimeSegments.Count == 0) return result;

      var ascending = heals.AscendingTime;
      var start = ascending ? heals.FirstIndexAtOrAfter(range.TimeSegments[0].BeginTime, healLimit) : 0;
      var lastEnd = range.TimeSegments[^1].EndTime;
      var reader = heals.Heals;
      for (var i = start; i < healLimit; i++)
      {
        ref readonly var heal = ref reader[i];
        var time = (double)heal.TimeS;
        if (ascending && time > lastEnd) break;
        if (!range.Contains(time)) continue;
        var name = heals.NameOf(heal.HealedIdx);
        if (name is null || !eligible.Contains(name)) continue;
        if (!result.TryGetValue(name, out var list)) result[name] = list = [];
        list.Add((time, heal.Total));
      }

      if (!ascending)
      {
        foreach (var list in result.Values) list.Sort(static (a, b) => a.Time.CompareTo(b.Time));
      }
      return result;
    }

    private static decimal HealedIn(List<(double Time, long Total)> healed, double from, double to)
    {
      var lo = 0;
      var hi = healed.Count;
      while (lo < hi)
      {
        var mid = (lo + hi) >>> 1;
        if (healed[mid].Time < from) lo = mid + 1;
        else hi = mid;
      }

      var total = 0m;
      for (var i = lo; i < healed.Count && healed[i].Time <= to; i++) total += healed[i].Total;
      return total;
    }

    private sealed class Group(string name)
    {
      public string Name { get; } = name;
      public Dictionary<int, List<Life>> Visits { get; } = [];
    }
  }
}

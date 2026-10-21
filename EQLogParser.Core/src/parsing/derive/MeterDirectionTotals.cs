using System;
using System.Collections.Generic;
using System.Globalization;

namespace EQLogParser;

internal sealed class MeterCoverage
{
  internal TimeRange Range { get; } = new();
  internal double Seconds { get; private set; }

  internal void Add(double begin, double end)
  {
    Seconds += Range.AddAndMeasure(new TimeSegment(begin, end));
  }
}

internal sealed class MeterActivity(double time)
{
  internal double Begin = time;
  internal double Last = time;
}

internal readonly record struct MeterFirst(string Name, string Owner, bool Valid, int Block, int Position);

internal readonly record struct MeterAmount(long Total, uint Hits)
{
  internal MeterAmount Add(long total, bool hit) => new(Total + total, Hits + (hit ? 1u : 0u));
  internal MeterAmount Add(MeterAmount other) => new(Total + other.Total, Hits + other.Hits);
}

// One life's removable contribution. There are no records, spell breakdowns, or per-hit amounts here.
internal sealed class MeterDirectionSlice
{
  internal readonly Dictionary<string, MeterActivity> Activity = new(StringComparer.Ordinal);
  internal readonly Dictionary<(string Name, string Owner), MeterAmount> Amounts = [];
  internal readonly Dictionary<string, (string Owner, double Time)> Owners = new(StringComparer.Ordinal);
  internal readonly List<double> Blocks = [];
  internal readonly Dictionary<(string Name, string Owner, bool Valid), List<MeterFirst>> First = [];
  /*
   * Six seconds of the same amounts, keyed the same way, so a withdrawn life loses both together; windowed against the
   * capture's newest second, never against the instant this update ran.
   */
  internal readonly MeterRecentWindow Recent = new();
  internal int OrderVersion;
  private int _position;
  internal double Begin = double.NaN;
  internal double Last = double.NaN;
  internal double Latest = double.NegativeInfinity;

  internal bool Add(MeterHit hit, bool tanking, MeterDamageFilter filter, long windowEdge)
  {
    var name = tanking ? hit.Defender : hit.Attacker;
    if (Blocks.Count == 0 || Blocks[^1] != hit.Time)
    {
      Blocks.Add(hit.Time);
      _position = 0;
      OrderVersion++;
    }
    var valid = tanking || filter.Allows(hit);
    var orderKey = (name, tanking ? string.Empty : hit.Owner ?? string.Empty, valid);
    if (!First.TryGetValue(orderKey, out var candidates)) First[orderKey] = candidates = [];
    if (candidates.Count > 0 && hit.Time < Blocks[candidates[0].Block]) candidates.Clear();
    if (candidates.Count == 0 || hit.Time == Blocks[candidates[0].Block] && candidates[^1].Block != Blocks.Count - 1)
    {
      candidates.Add(new MeterFirst(orderKey.Item1, orderKey.Item2, valid, Blocks.Count - 1, _position));
      OrderVersion++;
    }
    _position++;
    Latest = Math.Max(Latest, hit.Time);
    var backwards = Activity.TryGetValue(name, out var activity) && hit.Time < activity.Last;
    if (activity is null) Activity[name] = activity = new MeterActivity(hit.Time);
    activity.Last = hit.Time;
    Begin = double.IsNaN(Begin) ? hit.Time : tanking ? Begin : Math.Min(Begin, hit.Time);
    Last = double.IsNaN(Last) ? hit.Time : tanking ? hit.Time : Math.Max(Last, hit.Time);

    if (!tanking && !string.IsNullOrEmpty(hit.Owner) && !Labels.IsUnassignedOwner(hit.Owner)
      && (!Owners.TryGetValue(name, out var prior) || hit.Time >= prior.Time))
      Owners[name] = (hit.Owner, hit.Time);

    if (valid)
    {
      var ownerKey = tanking ? string.Empty : hit.Owner ?? string.Empty;
      var key = (name, ownerKey);
      Amounts.TryGetValue(key, out var amount);
      Amounts[key] = amount.Add(hit.Total, StatsUtil.CountsAsDamageHit(hit.Type));
      Recent.Add(name, ownerKey, hit.Time, hit.Total, windowEdge);
    }
    return backwards;
  }
}

internal readonly record struct MeterDamageFilter(bool Assassinate, bool Bane, bool DamageShield,
  bool FinishingBlow, bool Headshot, bool SlayUndead)
{
  internal static MeterDamageFilter Current => new(AppSettings.IsAssassinateDamageEnabled,
    AppSettings.IsBaneDamageEnabled, AppSettings.IsDamageShieldDamageEnabled,
    AppSettings.IsFinishingBlowDamageEnabled, AppSettings.IsHeadshotDamageEnabled,
    AppSettings.IsSlayUndeadDamageEnabled);

  internal bool Allows(MeterHit hit) => DamageValidator.IsDamageValid(hit.Modifiers, hit.Type,
    Assassinate, Bane, DamageShield, FinishingBlow, Headshot, SlayUndead);

  internal bool Allows(DamageFact fact) => DamageValidator.IsDamageValid(fact.ModMask, LabelTypes.LabelOf(fact.TypeId),
    Assassinate, Bane, DamageShield, FinishingBlow, Headshot, SlayUndead);
}

internal sealed class MeterDirectionTotals(bool tanking)
{
  private readonly Dictionary<string, MeterCoverage> _activity = new(StringComparer.Ordinal);
  private readonly Dictionary<(string Name, string Owner), MeterAmount> _amounts = [];
  private readonly Dictionary<string, (string Owner, double Time)> _owners = new(StringComparer.Ordinal);
  private readonly MeterCoverage _raid = new();
  private Dictionary<string, HashSet<string>> _groups = new(StringComparer.Ordinal);
  private readonly Dictionary<string, MeterCoverage> _groupTime = new(StringComparer.Ordinal);
  private readonly Dictionary<string, List<MeterCoverage>> _memberGroups = new(StringComparer.Ordinal);
  private double _last = double.NaN;
  private readonly MeterOrdering _ordering = new();

  // Recent damage the row resolver could not place on the last publish, reported on the build's Debug line. Zero is the
  // only sane answer: every counted recent amount comes from a hit that already earned its encounter row.
  internal long RecentUnresolved { get; private set; }

  internal void AddAmounts(MeterHit hit, MeterDamageFilter filter)
  {
    var name = tanking ? hit.Defender : hit.Attacker;
    if (!tanking && !string.IsNullOrEmpty(hit.Owner) && !Labels.IsUnassignedOwner(hit.Owner)
      && (!_owners.TryGetValue(name, out var prior) || hit.Time >= prior.Time))
      _owners[name] = (hit.Owner, hit.Time);
    if (!tanking && !filter.Allows(hit)) return;
    var key = (name, tanking ? string.Empty : hit.Owner ?? string.Empty);
    _amounts.TryGetValue(key, out var amount);
    _amounts[key] = amount.Add(hit.Total, StatsUtil.CountsAsDamageHit(hit.Type));
  }

  internal void AddActivity(string name, MeterActivity activity)
  {
    if (!_activity.TryGetValue(name, out var coverage)) _activity[name] = coverage = new MeterCoverage();
    coverage.Add(activity.Begin, activity.Last);
    if (_memberGroups.TryGetValue(name, out var groups))
      foreach (var group in groups) group.Add(activity.Begin, activity.Last);
  }

  internal void AddBounds(MeterDirectionSlice slice)
  {
    if (double.IsNaN(slice.Begin)) return;
    _raid.Add(slice.Begin, slice.Last);
    if (double.IsNaN(_last) || slice.Latest > _last) _last = slice.Latest;
  }

  internal void Merge(MeterDirectionSlice slice)
  {
    foreach (var pair in slice.Amounts)
    {
      _amounts.TryGetValue(pair.Key, out var old);
      _amounts[pair.Key] = old.Add(pair.Value);
    }
    foreach (var pair in slice.Owners)
      if (!_owners.TryGetValue(pair.Key, out var prior) || pair.Value.Time >= prior.Time)
        _owners[pair.Key] = pair.Value;
    foreach (var pair in slice.Activity) AddActivity(pair.Key, pair.Value);
    AddBounds(slice);
  }

  internal CombinedStats Publish(string target, TimeRange allRanges, IReadOnlyList<MeterDirectionSlice> slices,
    long windowEdge, out MeterDirectionRates rates)
  {
    rates = MeterDirectionRates.None;
    RecentUnresolved = 0;
    if (double.IsNaN(_last)) return null;
    var rows = new Dictionary<string, PlayerStats>(StringComparer.Ordinal);
    var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
    var owners = new HashSet<string>(StringComparer.Ordinal);
    if (!tanking)
    {
      foreach (var name in _activity.Keys)
      {
        var owner = _owners.TryGetValue(name, out var known) ? known.Owner : IdentityLookup.OwnerOf(name);
        if (string.IsNullOrEmpty(owner) || Labels.IsUnassignedOwner(owner)) continue;
        mapped[name] = owner;
        owners.Add(owner);
      }
    }

    long total = 0;
    foreach (var (key, amount) in _amounts)
    {
      var owner = !string.IsNullOrEmpty(key.Owner) ? key.Owner
        : mapped.TryGetValue(key.Name, out var assigned) ? assigned : null;
      var aggregate = !tanking && !Labels.IsUnassignedOwner(owner)
        && (owner is not null || owners.Contains(key.Name));
      var original = aggregate ? owner ?? key.Name : key.Name;
      var name = aggregate ? original + " +Pets" : original;
      if (!rows.TryGetValue(name, out var row))
      {
        rows[name] = row = StatsUtil.CreatePlayerStats(name, original);
        groups[name] = new HashSet<string>(StringComparer.Ordinal);
      }
      row.Total += amount.Total;
      row.Hits += amount.Hits;
      total += amount.Total;
      groups[name].Add(key.Name);
    }

    if (!SameGroups(groups))
    {
      _groups = groups;
      _groupTime.Clear();
      _memberGroups.Clear();
      foreach (var pair in groups)
      {
        var coverage = new MeterCoverage();
        foreach (var child in pair.Value)
        {
          foreach (var span in _activity[child].Range.TimeSegments) coverage.Add(span.BeginTime, span.EndTime);
          if (!_memberGroups.TryGetValue(child, out var memberships)) _memberGroups[child] = memberships = [];
          memberships.Add(coverage);
        }
        _groupTime[pair.Key] = coverage;
      }
    }

    var raid = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
    raid.Total = total;
    raid.TotalSeconds = _raid.Seconds;
    raid.Dps = StatsUtil.RatePerSecond(total, raid.TotalSeconds);
    raid.AllRanges.Add(allRanges);
    var combined = new CombinedStats
    {
      RaidStats = raid,
      TargetTitle = target,
      TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, raid.TotalSeconds),
      TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat,
        StatsUtil.FormatTotals(total), tanking ? " Tanked " : " Damage ", StatsUtil.FormatTotals(raid.Dps)),
      PlayerClasses = new Dictionary<string, string>(StringComparer.Ordinal)
    };
    foreach (var row in rows.Values)
    {
      row.TotalSeconds = _groupTime[row.Name].Seconds;
      StatsUtil.CalculateRates(row, raid, null);
      row.ClassName = PlayerRegistry.Instance.GetPlayerClass(row.OrigName, _last);
      combined.PlayerClasses.TryAdd(row.OrigName, row.ClassName);
      combined.StatsList.Add(row);
    }
    _ordering.Assign(combined, slices, groups, _amounts, tanking, Resolve);

    /*
     * Rolling RDPS: sum the six buckets of every selected life, resolve each actor through the SAME row resolver as the
     * encounter amounts (one pet grouping interpretation, never a second one), then divide once per row and once for the
     * whole scope — that total from its own sum, never added up from rounded rows.
     */
    var recentAmounts = new Dictionary<(string Name, string Owner), long>();
    for (var i = 0; i < slices.Count; i++) slices[i].Recent.SumInto(recentAmounts, windowEdge);

    var byRow = new Dictionary<string, long>(StringComparer.Ordinal);
    var recentTotal = 0L;
    foreach (var pair in recentAmounts)
    {
      var rowName = Resolve(pair.Key.Name, pair.Key.Owner);
      /*
       * Recent damage is a subset of counted damage by construction — same hit, same key, same admitted branch — so an
       * unplaceable amount is a bug the column would hide. Counted for the build's Debug line rather than hushed.
       */
      if (!rows.ContainsKey(rowName))
      {
        RecentUnresolved += pair.Value;
        continue;
      }
      byRow.TryGetValue(rowName, out var prior);
      byRow[rowName] = prior + pair.Value;
      recentTotal += pair.Value;
    }

    if (byRow.Count > 0)
    {
      // Dividing a value while enumerating the keys is legal; the copy only keeps "divide once, after the sum" obvious.
      var rowNames = new string[byRow.Count];
      byRow.Keys.CopyTo(rowNames, 0);
      foreach (var rowName in rowNames) byRow[rowName] = StatsUtil.RateOverWholeSeconds(byRow[rowName], MeterRecentWindow.WindowSeconds);
    }

    // Do not retain a separate empty map in every published result.
    IReadOnlyDictionary<string, long> rateRows = byRow.Count > 0 ? byRow : MeterDirectionRates.EmptyByRow;
    rates = new MeterDirectionRates(rateRows, StatsUtil.RateOverWholeSeconds(recentTotal, MeterRecentWindow.WindowSeconds), windowEdge);

    string Resolve(string name, string explicitOwner)
    {
      var owner = !string.IsNullOrEmpty(explicitOwner) ? explicitOwner
        : mapped.TryGetValue(name, out var assigned) ? assigned : null;
      return !tanking && !Labels.IsUnassignedOwner(owner) && (owner is not null || owners.Contains(name))
        ? (owner ?? name) + " +Pets" : name;
    }
    combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
    combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle);
    return combined;
  }

  private bool SameGroups(Dictionary<string, HashSet<string>> groups)
  {
    if (groups.Count != _groups.Count) return false;
    foreach (var pair in groups)
      if (!_groups.TryGetValue(pair.Key, out var prior) || !prior.SetEquals(pair.Value)) return false;
    return true;
  }
}

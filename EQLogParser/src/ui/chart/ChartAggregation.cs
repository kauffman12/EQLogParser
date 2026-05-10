using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /// <summary>
  /// Static utility class for chart data aggregation.
  /// Extracted from LineChart to enable standalone testing without WPF dependencies.
  /// </summary>
  internal static class ChartAggregation
  {
    /// <summary>
    /// Aggregates per-player data series into group-level series.
    /// Each group becomes a single line with values summed across all members at each time tick.
    /// </summary>
    /// <param name="playerData">Dictionary mapping player names to their data series</param>
    /// <param name="selectedGroups">List of groups to aggregate</param>
    /// <returns>List of aggregated series, one per group, sorted by total damage descending</returns>
    public static List<List<DataPoint>> AggregateGroups(Dictionary<string, List<DataPoint>> playerData, List<GroupEntry> selectedGroups)
    {
      var result = new List<List<DataPoint>>();

      if (selectedGroups == null || selectedGroups.Count == 0)
        return result;

      foreach (var group in selectedGroups)
      {
        // Collect all member data series
        var memberSeries = new List<List<DataPoint>>();
        foreach (var member in group.Members)
        {
          var matchName = member.Name;
          var origName = member.OrigName ?? member.Name;
          var found = false;

          // Try "Name +Pets" first (Player+Pets entries)
          if (!found && playerData.TryGetValue(matchName + " +Pets", out var petSeries))
          {
            memberSeries.Add(petSeries);
            found = true;
          }

          // Try "OrigName +Pets"
          if (!found && origName != matchName && playerData.TryGetValue(origName + " +Pets", out var origPetSeries))
          {
            memberSeries.Add(origPetSeries);
            found = true;
          }

          // Try exact name match
          if (!found && playerData.TryGetValue(matchName, out var exactSeries))
          {
            memberSeries.Add(exactSeries);
            found = true;
          }

          // Fallback: scan for PlayerName match
          if (!found)
          {
            foreach (var entry in playerData)
            {
              if (entry.Value.Count > 0 && entry.Value[0].PlayerName == origName)
              {
                memberSeries.Add(entry.Value);
                found = true;
                break;
              }
            }
          }
        }

        if (memberSeries.Count == 0)
          continue;

        // Merge all member series into a single aggregated series
        var aggregated = MergeSeries(group.Name, memberSeries);
        if (aggregated != null && aggregated.Count > 0)
        {
          result.Add(aggregated);
        }
      }

      // Sort by total damage descending
      result.Sort((a, b) => b[^1].Total.CompareTo(a[^1].Total));
      return result;
    }

    /// <summary>
    /// Merges multiple data series into one by summing values at each unique time tick.
    /// Cumulative fields (FightTotal, FightHits, etc.) carry forward across gaps.
    /// Rate/tick fields (TotalPerSecond, CritsPerSecond, RollingTotal, etc.) are set to 0
    /// when a player has no data — the group is treated as a single entity where gaps
    /// contribute nothing.
    /// </summary>
    public static List<DataPoint> MergeSeries(string groupName, List<List<DataPoint>> seriesList)
    {
      if (seriesList.Count == 0)
        return null;

      // Collect all unique time ticks
      var timeTicks = new SortedSet<double>();
      foreach (var series in seriesList)
      {
        foreach (var dp in series)
        {
          timeTicks.Add(dp.CurrentTime);
        }
      }

      if (timeTicks.Count == 0)
        return null;

      // Build lookup: for each series, map time -> DataPoint
      var seriesByTime = seriesList
        .Select(s => s.ToDictionary(dp => dp.CurrentTime))
        .ToList();

      // Track the last known DataPoint for each series (to carry forward values)
      var lastPoints = new List<DataPoint>(seriesList.Count);
      for (var i = 0; i < seriesList.Count; i++)
      {
        lastPoints.Add(null);
      }

      // Find the earliest tick time (fight start) across all players
      var minTime = timeTicks.Min();

      var merged = new List<DataPoint>();
      foreach (var time in timeTicks)
      {
        // Local accumulators for rate recalculation (sum of cumulative values across all series)
        long sumFightTotal = 0;
        uint sumFightHits = 0;
        uint sumFightCritHits = 0;
        uint sumFightTcHits = 0;

        // RollingTotal: sum of current values across all series at this tick
        long sumRollingTotal = 0;

        var point = new DataPoint
        {
          Name = groupName,
          PlayerName = groupName,
          CurrentTime = time,
          DateTime = DateUtil.FromDotNetSeconds(time)
        };

        // Sum all numeric fields across series that have data at this time.
        // When a series has no data at this tick, its contribution is 0 for
        // rate/tick fields — the group is treated as a single entity.
        // Note: percentage fields (CritRate, TcRate, Avg) are NOT summed —
        // they are recalculated below from the summed raw counts.
        for (var i = 0; i < seriesByTime.Count; i++)
        {
          if (seriesByTime[i].TryGetValue(time, out var dp))
          {
            // Update the last known point for this series
            lastPoints[i] = dp;

            // Sum current values across all series.
            // Per-second count fields (CritsPerSecond, AttemptsPerSecond, etc.) are
            // already per-second values (reset at tick boundaries in Aggregate),
            // so we sum them directly — no delta tracking needed.
            sumRollingTotal += dp.RollingTotal;

            point.TotalPerSecond += dp.TotalPerSecond;
            point.RollingDps += dp.RollingDps;
            point.CritsPerSecond += dp.CritsPerSecond;
            point.TcPerSecond += dp.TcPerSecond;
            point.AttemptsPerSecond += dp.AttemptsPerSecond;
            point.HitsPerSecond += dp.HitsPerSecond;
            // Accumulate full cumulative values for rate recalculation below
            sumFightTotal += dp.FightTotal;
            sumFightHits += dp.FightHits;
            sumFightCritHits += dp.FightCritHits;
            sumFightTcHits += dp.FightTcHits;
          }
          else if (lastPoints[i] is { } last)
          {
            // Player has no data at this tick — rate/tick fields contribute 0.
            // Only cumulative fields carry forward (they never decrease).
            sumFightTotal += last.FightTotal;
            sumFightHits += last.FightHits;
            sumFightCritHits += last.FightCritHits;
            sumFightTcHits += last.FightTcHits;
          }
        }

        // Total = cumulative fight damage across all series (never resets)
        // RollingTotal = sum of current rolling window values across all series
        point.Total = sumFightTotal;
        point.RollingTotal = sumRollingTotal;

        // Aggregate DPS: FightTotal / elapsedSeconds (same formula as per-player)
        var elapsedSeconds = time - minTime;
        if (elapsedSeconds > 0)
        {
          point.ValuePerSecond = (long)(sumFightTotal / elapsedSeconds);
        }

        // Recalculate averages from summed cumulative totals across all series
        // (not deltas) to get correct overall rates
        if (sumFightHits > 0)
        {
          point.Avg = (long)Math.Round(Convert.ToDecimal(sumFightTotal) / sumFightHits, 2);
          point.CritRate = Math.Round(Convert.ToDouble(sumFightCritHits) / sumFightHits * 100, 2);
          point.TcRate = Math.Round(Convert.ToDouble(sumFightTcHits) / sumFightHits * 100, 2);
        }

        merged.Add(point);
      }

      return merged;
    }
  }
}

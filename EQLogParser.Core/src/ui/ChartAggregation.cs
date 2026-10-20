using System;
using System.Collections.Generic;

namespace EQLogParser;

/// <summary>Combines sparse chart series and calculates their rolling rates.</summary>
internal static class ChartAggregation
{
  internal const int RollingSeconds = 6;

  public static void PopulateRolling(Dictionary<string, List<DataPoint>> data)
  {
    foreach (var points in data.Values) PopulateRolling(points);
  }

  public static List<List<DataPoint>> AggregateGroups(Dictionary<string, List<DataPoint>> playerData,
    List<GroupEntry> selectedGroups)
  {
    var result = new List<List<DataPoint>>();
    if (selectedGroups is null) return result;
    foreach (var group in selectedGroups)
    {
      var memberSeries = new List<List<DataPoint>>();
      foreach (var member in group.Members)
      {
        var matchName = member.Name;
        var origName = member.OrigName ?? member.Name;
        if (playerData.TryGetValue(matchName + " +Pets", out var series)
          || origName != matchName && playerData.TryGetValue(origName + " +Pets", out series)
          || playerData.TryGetValue(matchName, out series))
        {
          memberSeries.Add(series);
        }
        else
        {
          foreach (var entry in playerData.Values)
          {
            if (entry.Count > 0 && entry[0].PlayerName == origName)
            {
              memberSeries.Add(entry);
              break;
            }
          }
        }
      }
      if (MergeSeries(group.Name, memberSeries) is { Count: > 0 } merged) result.Add(merged);
    }
    result.Sort(static (a, b) => b[^1].Total.CompareTo(a[^1].Total));
    return result;
  }

  // Member lists already ascend. Cursors avoid a sorted timestamp set and a second dictionary per member.
  public static List<DataPoint> MergeSeries(string groupName, List<List<DataPoint>> seriesList)
  {
    var cursors = new int[seriesList.Count];
    var previous = new DataPoint[seriesList.Count];
    var clock = new ChartActivityClock();
    var merged = new List<DataPoint>();
    var total = 0L;
    var hits = 0u;
    var crits = 0u;
    var twincasts = 0u;
    while (true)
    {
      var time = double.PositiveInfinity;
      for (var i = 0; i < seriesList.Count; i++)
      {
        if (cursors[i] < seriesList[i].Count)
          time = Math.Min(time, seriesList[i][cursors[i]].CurrentTime);
      }
      if (double.IsPositiveInfinity(time)) break;

      var point = new DataPoint
      {
        Name = groupName,
        PlayerName = groupName,
        CurrentTime = time,
        DateTime = DateUtil.FromDotNetSeconds(time)
      };
      for (var i = 0; i < seriesList.Count; i++)
      {
        var series = seriesList[i];
        if (cursors[i] >= series.Count || series[cursors[i]].CurrentTime != time) continue;
        var current = series[cursors[i]++];
        total += current.FightTotal - (previous[i]?.FightTotal ?? 0);
        hits += current.FightHits - (previous[i]?.FightHits ?? 0);
        crits += current.FightCritHits - (previous[i]?.FightCritHits ?? 0);
        twincasts += current.FightTcHits - (previous[i]?.FightTcHits ?? 0);
        previous[i] = current;
        point.TotalPerSecond += current.TotalPerSecond;
        point.AttemptsPerSecond += current.AttemptsPerSecond;
        point.HitsPerSecond += current.HitsPerSecond;
        point.CritsPerSecond += current.CritsPerSecond;
        point.TcPerSecond += current.TcPerSecond;
      }

      // Every real bucket has an attempt, even a miss or zero heal. Synthetic gap markers have none.
      if (point.AttemptsPerSecond > 0) clock.Add(time);
      point.Total = point.FightTotal = total;
      point.FightHits = hits;
      point.FightCritHits = crits;
      point.FightTcHits = twincasts;
      ChartSeriesBuilder.SetRates(point, clock.Seconds);
      merged.Add(point);
    }
    if (merged.Count == 0) return null;
    PopulateRolling(merged);
    return merged;
  }

  private static void PopulateRolling(List<DataPoint> points)
  {
    if (points.Count == 0) return;
    var left = 0;
    var windowTotal = 0L;
    var start = points[0].CurrentTime;
    for (var right = 0; right < points.Count; right++)
    {
      var point = points[right];
      windowTotal += point.TotalPerSecond;
      while (left < right && point.CurrentTime - points[left].CurrentTime > RollingSeconds - 1)
      {
        windowTotal -= points[left].TotalPerSecond;
        left++;
      }

      // Missing seconds count as zero; only the series' initial warm-up shortens the six-bucket denominator.
      var seconds = (long)Math.Min(RollingSeconds, point.CurrentTime - start + 1);
      point.RollingTotal = windowTotal;
      point.RollingDps = windowTotal / seconds;
    }
  }
}

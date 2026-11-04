using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser;

/// <summary>The benchmark's class-normalized values; filtering the display never changes this export source.</summary>
internal static class BenchmarkSeries
{
  internal static List<BenchmarkBar> Prepare(IEnumerable<PlayerStats> players, Func<string, bool> include)
  {
    var columns = new List<BenchmarkBar>();
    var baseTotal = 0L;
    string theClass = null;
    foreach (var stats in players.OrderBy(row => row.ClassName, StringComparer.OrdinalIgnoreCase)
      .ThenByDescending(row => row.Total))
    {
      var name = stats.OrigName;
      if (!include(name)) continue;
      var isFirst = false;
      if (string.IsNullOrEmpty(theClass) || stats.ClassName != theClass)
      {
        isFirst = true;
        baseTotal = stats.Total;
        theClass = string.IsNullOrEmpty(stats.ClassName) ? "Unknown" : stats.ClassName;
      }
      if (baseTotal > 0)
        columns.Add(new BenchmarkBar((int)(stats.Total / (double)baseTotal * 100), name,
          StatsUtil.FormatTotals(stats.Total, 1), theClass, isFirst, stats.Name.Contains(" +Pets")));
    }
    return columns;
  }
}

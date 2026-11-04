using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser;

internal readonly record struct BenchmarkBar(int Percent, string Name, string Total, string ClassName,
  bool IsFirst, bool HasPets);
internal readonly record struct BarChartColumn(double Value, string Above, string Primary, string Secondary,
  string Group, string ColorKey, string Tooltip);

/// <summary>The two supported presentations; strings are formatted only for realized columns.</summary>
internal sealed class BarChartData
{
  private readonly HitFrequencyBar[] _frequency = [];
  private readonly BenchmarkBar[] _benchmark = [];
  private readonly bool _percent;

  internal int Count => _percent ? _benchmark.Length : _frequency.Length;
  internal BarChartAxis Axis { get; }
  internal bool HasSecondary { get; }
  internal string Title => _percent ? "Percent" : "Frequency";
  internal string EmptyText => _percent ? "No players match these filters" : "No hits match these filters";

  private BarChartData(IReadOnlyList<HitFrequencyBar> bars)
  {
    _frequency = bars.ToArray();
    Axis = BarChartLayout.FrequencyAxis(_frequency.Length == 0 ? 0 : _frequency.Max(bar => bar.Frequency));
    HasSecondary = true;
  }

  private BarChartData(IReadOnlyList<BenchmarkBar> bars)
  {
    _benchmark = bars.ToArray();
    _percent = true;
    Axis = BarChartLayout.PercentAxis;
    HasSecondary = _benchmark.Any(bar => bar.HasPets);
  }

  internal static BarChartData Frequency(IReadOnlyList<HitFrequencyBar> bars) => new(bars);
  internal static BarChartData Benchmark(IReadOnlyList<BenchmarkBar> bars) => new(bars);
  internal static string ClassColorKey(string name) => "ColumnChartClass" + name + "Brush";

  internal BarChartColumn Column(int index)
  {
    if (_percent)
    {
      var bar = _benchmark[index];
      var above = $"{bar.Percent}% ({bar.Total})";
      var secondary = bar.HasPets ? "+Pets" : "";
      return new(bar.Percent, above, bar.Name, secondary, bar.IsFirst ? bar.ClassName : "",
        ClassColorKey(bar.ClassName), $"{bar.Name}{(bar.HasPets ? " +Pets" : "")}\n{bar.ClassName}\n{above}");
    }
    var hit = _frequency[index];
    var amount = hit.Amount.ToString("N0", CultureInfo.CurrentCulture);
    var frequency = hit.Frequency.ToString("N0", CultureInfo.CurrentCulture);
    var difference = hit.Difference == 0 ? "" : "+" + hit.Difference.ToString("N0", CultureInfo.CurrentCulture);
    return new(hit.Frequency, frequency, amount, difference, "", "PrimaryColor",
      $"Hit Value: {amount}\nFrequency: {frequency}\nDifference: {hit.Difference.ToString("N0", CultureInfo.CurrentCulture)}"
      + "\nDifference from the preceding distinct hit value, before filtering");
  }

  // A stable budget for this whole data question. Geometry/scrolling never formats off-screen columns.
  internal IEnumerable<string> WidthSamples()
  {
    if (_percent)
    {
      foreach (var bar in _benchmark)
      {
        yield return bar.Name;
        yield return $"{bar.Percent}% ({bar.Total})";
      }
      yield return "+Pets";
    }
    else if (_frequency.Length > 0)
    {
      var maximum = _frequency.Max(bar => bar.Amount);
      var sample = maximum.ToString("N0", CultureInfo.CurrentCulture);
      // Use a wide digit for every position, not whichever digits happened to make the maximum amount.
      yield return "+" + new string(sample.Select(character => char.IsDigit(character) ? '8' : character).ToArray());
      yield return Axis.Maximum.ToString("N0", CultureInfo.CurrentCulture);
    }
  }
}

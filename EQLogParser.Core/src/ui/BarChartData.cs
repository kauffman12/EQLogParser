using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Annotations only, no null-flow analysis: DI metadata is optional and never changes the histogram.
#nullable enable annotations

namespace EQLogParser;

internal readonly record struct BenchmarkBar(int Percent, string Name, string Total, string ClassName,
  bool IsFirst, bool HasPets);
internal readonly record struct BarChartColumn(double Value, string Above, string Primary, string Secondary,
  string Group, string ColorKey, string Tooltip, string DiLabel = "");

/// <summary>The two supported presentations; strings are formatted only for realized columns.</summary>
internal sealed class BarChartData
{
  private readonly HitFrequencyBar[] _frequency = [];
  private readonly BenchmarkBar[] _benchmark = [];
  private readonly bool _percent;
  private readonly MeleeDiFit? _diFit;

  internal int Count => _percent ? _benchmark.Length : _frequency.Length;
  // The whole-question label budget stays fixed while the drawn frequency axis follows the visible range.
  internal BarChartAxis Axis { get; }
  internal bool HasSecondary { get; }
  internal bool HasDiLabels { get; }
  internal string Title => _percent ? "Percent" : "Frequency";
  internal string EmptyText => _percent ? "No players match these filters" : "No hits match these filters";

  private BarChartData(IReadOnlyList<HitFrequencyBar> bars, MeleeDiFit? diFit)
  {
    _frequency = bars.ToArray();
    _diFit = diFit;
    HasDiLabels = diFit is not null && _frequency.Any(bar => diFit.LevelFor(bar.Amount) > 0);
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

  internal static BarChartData Frequency(IReadOnlyList<HitFrequencyBar> bars, MeleeDiFit? diFit = null)
    => new(bars, diFit);
  internal static BarChartData Benchmark(IReadOnlyList<BenchmarkBar> bars) => new(bars);
  internal static string ClassColorKey(string name) => "ColumnChartClass" + name + "Brush";

  internal double ColumnStride(double labelWidth) => BarChartLayout.ColumnStride(labelWidth, _percent);

  internal BarChartAxis AxisFor(BarChartRange visible)
  {
    if (_percent) return Axis;
    var maximum = 0;
    var first = Math.Clamp(visible.First, 0, Count);
    var end = Math.Clamp(visible.End, first, Count);
    for (var index = first; index < end; index++) maximum = Math.Max(maximum, _frequency[index].Frequency);
    return BarChartLayout.FrequencyAxis(maximum);
  }

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
    var level = _diFit?.LevelFor(hit.Amount) ?? 0;
    var diLabel = level > 0 ? $"DI {level}" : "";
    var tooltip = $"Hit Value: {amount}\nFrequency: {frequency}"
      + $"\nDifference: {hit.Difference.ToString("N0", CultureInfo.CurrentCulture)}"
      + "\nDifference from the preceding distinct hit value, before filtering";
    if (_diFit is { } fit)
    {
      tooltip += level > 0 ? $"\nEstimated DI {level}" : "\nOutside Estimated DI Ladder";
      tooltip += $"\nLevels Observed: {fit.ObservedLevels}/{MeleeDiEstimator.LevelCount}"
        + $"\nFit Support: {fit.HitCount:N0}/{fit.TotalHitCount:N0} Hits"
        + $"\nEffective Interval: {fit.Interval:0.###}\nMaximum Fit Error: {fit.MaximumResidual:0.###}"
        + $"\nFit Allowance: {fit.AllowedResidual:0.###}";
    }
    return new(hit.Frequency, frequency, amount, difference, "", "EQMenuIconBrush", tooltip, diLabel);
  }

  internal string FrequencyTsv()
  {
    if (_percent) throw new InvalidOperationException("A benchmark has no hit-frequency export");
    var rows = new List<List<object>>(_frequency.Length);
    foreach (var hit in _frequency)
    {
      var level = _diFit?.LevelFor(hit.Amount) ?? 0;
      rows.Add([hit.Amount, hit.Frequency, hit.Difference, level > 0 ? level : ""]);
    }
    return TextUtils.BuildTsv(["Hit Value", "Frequency", "Difference", "DI"], rows);
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
      if (HasDiLabels) yield return $"DI {MeleeDiEstimator.LevelCount}";
    }
  }
}

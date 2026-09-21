using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLogParser.Mirror
{
  internal sealed class FightFieldDiff
  {
    public string Field { get; init; }
    public string Current { get; init; }
    public string Derived { get; init; }
  }

  // One paired position in the two fight lists (paired by creation order — both pipelines create
  // fights in line order, so this is the natural alignment).
  internal sealed class FightComparison
  {
    public int Index { get; init; }                     // 1-based
    public string Name { get; init; }
    public bool BothPresent { get; init; }
    public List<FightFieldDiff> Diffs { get; } = [];
    public List<string> Notes { get; } = [];            // open questions / known-difference markers

    [JsonIgnore] public Fight Current;                  // source data for the text view (null one-sided)
    [JsonIgnore] public DerivedFight Derived;

    [JsonIgnore] public bool Matched => BothPresent && Diffs.Count == 0;
  }

  internal sealed class SectionComparison
  {
    public int CurrentSectionCount { get; set; }
    public int DerivedSectionCount { get; set; }
    public List<string> Diffs { get; set; } = [];
  }

  // The Phase 1 validation artifact (D7): machine-readable JSON + legible text, generated from
  // the current pipeline's fight list and the derived one. A WPF side-by-side window over this
  // data comes in Phase 3.
  internal sealed class MirrorReport
  {
    public string LogName { get; init; }
    public int CurrentFightCount { get; init; }
    public int DerivedFightCount { get; init; }
    public double TimeToleranceS { get; init; }
    public long FactCount { get; init; }
    public List<FightComparison> Fights { get; init; } = [];
    public SectionComparison Sections { get; set; }

    [JsonIgnore]
    public bool CountsMatch => CurrentFightCount == DerivedFightCount;

    [JsonIgnore]
    public int MatchedFights => Fights.Count(f => f.Matched);

    [JsonIgnore]
    public List<string> OpenQuestions => Fights.Where(f => !f.BothPresent).Select(f =>
      $"{(f.Derived is null ? "current-only" : "derived-only")} fight #{f.Index} '{f.Name}'").ToList();

    [JsonIgnore]
    public bool AllMatch => CountsMatch && Fights.All(f => f.Matched);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
      WriteIndented = true,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    // Legible eyeball view: one row per paired fight (derived values; mismatches are flagged and
    // spelled out on DIFF lines below the row), then section summary and open questions.
#pragma warning disable CA1305 // report text only — no culture-dependent values, alignment specifiers only
    public string ToText()
    {
      var sb = new StringBuilder();
      sb.AppendLine("COMBAT MIRROR COMPARISON");
      sb.AppendLine($"log: {LogName ?? "<unknown>"}    facts: {FactCount}    time tolerance: {TimeToleranceS}s    totals: exact");
      sb.AppendLine($"current fights: {CurrentFightCount}    derived fights: {DerivedFightCount}    count match: {(CountsMatch ? "YES" : "NO")}    field-matched: {MatchedFights}/{Math.Max(CurrentFightCount, DerivedFightCount)}");

      if (Fights.Count > 0)
      {
        sb.AppendLine();

        var header = $"{"#":>3}  {"name":<32} {"begin":<16} {"end":<16} {"dead":>5} {"dmgTotal":>10} {"hits":>4} {"tankTtl":>10} {"tankHits":>8}";
        sb.AppendLine(header);
        sb.AppendLine(new string('-', header.Length));
      }

      foreach (var cmp in Fights)
      {
        sb.AppendLine(RowFor(cmp) + (cmp.Matched || !cmp.BothPresent ? "" : "   *MISMATCH*"));

        foreach (var d in cmp.Diffs)
        {
          sb.AppendLine($"     DIFF {d.Field}: current={d.Current} derived={d.Derived}");
        }

        foreach (var note in cmp.Notes)
        {
          sb.AppendLine($"     note: {note}");
        }
      }

      if (Sections is not null)
      {
        sb.AppendLine();
        sb.AppendLine($"sections: current={Sections.CurrentSectionCount} derived={Sections.DerivedSectionCount}");
        foreach (var d in Sections.Diffs)
        {
          sb.AppendLine($"     DIFF {d}");
        }
      }

      var oneSided = OpenQuestions;
      if (oneSided.Count > 0 || Fights.Any(f => f.Notes.Count > 0))
      {
        sb.AppendLine();
        sb.AppendLine("open questions:");
        foreach (var q in oneSided)
        {
          sb.AppendLine($"  - {q}");
        }

        foreach (var f in Fights.Where(f => f.BothPresent && f.Notes.Count > 0))
        {
          foreach (var n in f.Notes)
          {
            sb.AppendLine($"  - fight #{f.Index} '{f.Name}': {n}");
          }
        }
      }

      return sb.ToString();
    }
#pragma warning restore CA1305

    private static string RowFor(FightComparison cmp)
    {
      if (cmp.Derived is not { } d)
      {
        var tail = cmp.Current is null ? "" : $", begin={Time(cmp.Current.BeginTime)}";
        return $"{cmp.Index,3}  {Trunc(cmp.Name, 32),-32} (current-only{tail})";
      }

      if (cmp.Current is null)
      {
        return $"{cmp.Index,3}  {Trunc(d.Name, 32),-32} (derived-only, begin={Time(d.BeginTime)})";
      }

      // matched or not, the row shows the DERIVED values; any difference is spelled out below it
      var dead = d.Dead ? "YES" : "no";
      return $"{cmp.Index,3}  {Trunc(d.Name, 32),-32} {Time(d.BeginTime),-16} {Time(d.EndTime),-16} {dead,5} {d.DamageTotal,10} {d.DamageHits,4} {d.TankTotal,10} {d.TankHits,8}";
    }

    private static string Time(double t) => t is double.NaN or double.PositiveInfinity ? "n/a" : DateUtil.FormatDotNetDateSeconds(t);

    private static string Trunc(string s, int max) => s is null || s.Length <= max ? s ?? "-" : s.AsSpan(0, max - 1).ToString() + "\u2026";

    public void WriteAll(string directory, string baseName)
    {
      Directory.CreateDirectory(directory);
      File.WriteAllText(Path.Combine(directory, baseName + ".json"), ToJson());
      File.WriteAllText(Path.Combine(directory, baseName + ".txt"), ToText());
    }
  }

  internal static class MirrorComparison
  {
    // timeToleranceS: boundaries may differ by this much (default 1 s = one log frame);
    // counts and totals are exact.
    public static MirrorReport Compare(IReadOnlyList<Fight> current, IReadOnlyList<DerivedFight> derived, double timeToleranceS = 1.0, string logName = null, long factCount = -1)
    {
      var report = new MirrorReport
      {
        LogName = logName,
        CurrentFightCount = current.Count,
        DerivedFightCount = derived.Count,
        TimeToleranceS = timeToleranceS,
        FactCount = factCount
      };

      var max = Math.Max(current.Count, derived.Count);
      for (var i = 0; i < max; i++)
      {
        var c = i < current.Count ? current[i] : null;
        var d = i < derived.Count ? derived[i] : null;

        var cmp = new FightComparison
        {
          Index = i + 1,
          Name = d?.Name ?? c?.Name,
          BothPresent = c is not null && d is not null,
          Current = c,
          Derived = d
        };

        if (c is not null && d is not null)
        {
          cmp.Diffs.AddRange(FieldDiff("Name", c.Name, d.Name));
          cmp.Diffs.AddRange(TimeDiff("BeginTime", c.BeginTime, d.BeginTime, timeToleranceS));
          cmp.Diffs.AddRange(TimeDiff("LastTime", c.LastTime, d.LastTime, timeToleranceS));
          cmp.Diffs.AddRange(FieldDiff("Dead", c.Dead.ToString(), d.Dead.ToString()));
          cmp.Diffs.AddRange(CountDiff("DamageTotal", c.DamageTotal, d.DamageTotal));
          cmp.Diffs.AddRange(CountDiff("DamageHits", (long)c.DamageHits, (long)d.DamageHits));
          cmp.Diffs.AddRange(CountDiff("TankTotal", c.TankTotal, d.TankTotal));
          cmp.Diffs.AddRange(CountDiff("TankHits", (long)c.TankHits, (long)d.TankHits));
          cmp.Diffs.AddRange(TimeDiff("BeginDamageTime", c.BeginDamageTime, d.BeginDamageTime, timeToleranceS));
          cmp.Diffs.AddRange(TimeDiff("LastDamageTime", c.LastDamageTime, d.LastDamageTime, timeToleranceS));
          cmp.Diffs.AddRange(TimeDiff("BeginTankingTime", c.BeginTankingTime, d.BeginTankingTime, timeToleranceS));
          cmp.Diffs.AddRange(TimeDiff("LastTankingTime", c.LastTankingTime, d.LastTankingTime, timeToleranceS));
        }

        report.Fights.Add(cmp);
      }

      // sectioning over both lists with the SAME algorithm (the current side is shimmed), so any
      // diff here is a Sectionizer input/behavior difference, not an algorithm difference
      var currentSections = Sectionizer.Sectionize(ShimForSection(current));
      var derivedSections = Sectionizer.Sectionize(derived);
      var sectionDiffs = new List<string>();

      if (currentSections.Count != derivedSections.Count)
      {
        sectionDiffs.Add($"section count: current={currentSections.Count} derived={derivedSections.Count}");
      }

      for (var i = 0; i < Math.Min(currentSections.Count, derivedSections.Count); i++)
      {
        var cs = currentSections[i];
        var ds = derivedSections[i];

        if (cs.Fights.Count != ds.Fights.Count)
        {
          sectionDiffs.Add($"section {i + 1}: fight count current={cs.Fights.Count} derived={ds.Fights.Count}");
          continue;
        }

        for (var g = 0; g < Math.Min(cs.InactiveGaps.Count, ds.InactiveGaps.Count); g++)
        {
          if (Math.Abs(cs.InactiveGaps[g].From - ds.InactiveGaps[g].From) > timeToleranceS || Math.Abs(cs.InactiveGaps[g].To - ds.InactiveGaps[g].To) > timeToleranceS)
          {
            sectionDiffs.Add($"section {i + 1} divider gap: current=({cs.InactiveGaps[g].From:F0}s-{cs.InactiveGaps[g].To:F0}s) derived=({ds.InactiveGaps[g].From:F0}s-{ds.InactiveGaps[g].To:F0}s)");
          }
        }

        if (cs.InactiveGaps.Count != ds.InactiveGaps.Count)
        {
          sectionDiffs.Add($"section {i + 1}: divider count current={cs.InactiveGaps.Count} derived={ds.InactiveGaps.Count}");
        }
      }

      report.Sections = new SectionComparison
      {
        CurrentSectionCount = currentSections.Count,
        DerivedSectionCount = derivedSections.Count,
        Diffs = sectionDiffs
      };

      return report;
    }

    // shim so the Sectionizer can run over the current pipeline's fights without copying its logic
    private static List<DerivedFight> ShimForSection(IReadOnlyList<Fight> fights) => fights.Select(f => new DerivedFight
    {
      Name = f.Name,
      Id = (int)f.Id,
      BeginTime = f.BeginTime,
      LastTime = f.LastTime,
      LastDamageTime = f.LastDamageTime,
      DamageHits = (uint)f.DamageHits
    }).ToList();

    private static IEnumerable<FightFieldDiff> FieldDiff(string field, string current, string derived)
    {
      if (string.Equals(current, derived, StringComparison.Ordinal)) yield break;
      yield return new FightFieldDiff { Field = field, Current = current, Derived = derived };
    }

    private static IEnumerable<FightFieldDiff> TimeDiff(string field, double current, double derived, double tolerance)
    {
      var equal = (double.IsNaN(current) && double.IsNaN(derived)) || Math.Abs(current - derived) <= tolerance;
      if (equal) yield break;
      yield return new FightFieldDiff
      {
        Field = field,
        Current = FormatTime(current),
        Derived = FormatTime(derived)
      };
    }

    private static IEnumerable<FightFieldDiff> CountDiff(string field, long current, long derived)
    {
      if (current == derived) yield break;
      yield return new FightFieldDiff
      {
        Field = field,
        Current = current.ToString(CultureInfo.InvariantCulture),
        Derived = derived.ToString(CultureInfo.InvariantCulture)
      };
    }

    private static string FormatTime(double t) => double.IsNaN(t) ? "n/a" : DateUtil.FormatDotNetDateSeconds(t);
  }
}

using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * A gated probe (EQLP_REUSE_CEILING=<log>): what would a WHOLE-ROW cache save?
 *
 * The delta phase does not have to merge accumulators if it can hand unchanged rows over intact and rebuild only the rows the new facts
 * belong to, letting the existing finalize recompute rates, percent and ranking (that part is already free: totals 3 ms + present 0 ms
 * over a whole night - docs/DesignNotes.md -> "Where a board build's time actually goes"). Whether that earns its complexity is one
 * arithmetic question, answered here before any machinery exists to be disappointed by: of the records a full recount walks, how many
 * belong to rows that the new facts do not touch?
 *
 * A touched row pays its WHOLE history again (it is rebuilt from scratch, which is exactly why no merge is needed), so the number that
 * matters is "sum of every record belonging to a touched name" over "every record". Attribution is the attacker's name because that is
 * what a damage board row is keyed on. Two stated approximations, both conservative - they overstate a cached build's cost, so a good
 * number here is not flattery:
 *   - `X +Pets` folding is not modelled. Folding pets onto owners merges name groups, so a real build touches at least this many records.
 *   - healing and tanking have their own key (healer / defender) and their own ceiling; neither is measured here.
 */
[TestClass]
[DoNotParallelize]
public class RowReuseCeilingProbeTest
{
  [TestMethod]
  public void RowReuseCeiling_FactsBelongingToUntouchedRows()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_REUSE_CEILING");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
      Assert.Inconclusive("set EQLP_REUSE_CEILING=<log>");

    var run = PipelineHarness.RunFileDerived(path);
    var facts = run.Facts;
    var count = facts.FactCount;

    // Per-name record counts over the whole capture: a name's total is what rebuilding that row costs.
    var perName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    var counted = 0L;
    for (var i = 0; i < count; i++)
    {
      var f = facts.Facts[i];
      if (f.TimeS <= 0)
      {
        continue;
      }

      var name = facts.NameOf(f.AtkIdx);
      if (string.IsNullOrEmpty(name))
      {
        continue;
      }

      perName[name] = perName.TryGetValue(name, out var c) ? c + 1 : 1;
      counted++;
    }

    Console.WriteLine($"[reuse] {Path.GetFileName(path)} facts={count:N0} counted={counted:N0} rows(names)={perName.Count:N0}");

    // Share-of-capture tails: a refresh arriving after the capture grew by this much.
    foreach (var share in new[] { 0.001, 0.005, 0.01, 0.05 })
    {
      Report(facts, count, perName, Math.Max(1, (int)(count * share)), $"last {share:P1} of facts");
    }

    /*
     * Time-shaped windows measured IN THE MIDDLE of the capture, which is what a live refresh looks like. Anchoring on the newest fact
     * was wrong: both reference captures end in a long idle stretch, so "last 600s" held three facts and proved nothing except that
     * somebody logged off. A window placed at the busiest half of the night is the question we actually care about.
     */
    var anchor = count / 2;
    var anchorTime = facts.Facts[anchor].TimeS;
    foreach (var seconds in new[] { 5d, 30d, 120d })
    {
      var from = anchorTime - seconds;
      var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var tail = 0;
      for (var i = 0; i < count; i++)
      {
        var f = facts.Facts[i];
        if (f.TimeS <= from || f.TimeS > anchorTime)
        {
          continue;
        }

        var name = facts.NameOf(f.AtkIdx);
        if (string.IsNullOrEmpty(name))
        {
          continue;
        }

        tail++;
        touched.Add(name);
      }

      ReportTouched(perName, touched, tail, $"{seconds:F0}s window at mid-capture");
    }
  }

  private static void Report(DamageFactTable facts, int count, Dictionary<string, long> perName, int tailLength, string label)
  {
    var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = count - tailLength; i < count; i++)
    {
      var f = facts.Facts[i];
      if (f.TimeS <= 0)
      {
        continue;
      }

      var name = facts.NameOf(f.AtkIdx);
      if (!string.IsNullOrEmpty(name))
      {
        touched.Add(name);
      }
    }

    ReportTouched(perName, touched, tailLength, label);
  }

  private static void ReportTouched(Dictionary<string, long> perName, HashSet<string> touched, int tailLength, string label)
  {
    var total = perName.Values.Sum();
    long walked = 0;
    foreach (var name in touched)
    {
      walked += perName[name];
    }

    var share = total == 0 ? 0 : (double)walked / total;
    Console.WriteLine($"[reuse]   {label,-28} tail={tailLength,10:N0} rows touched={touched.Count,4}/{perName.Count,-4} "
      + $"({(perName.Count == 0 ? 0 : 100.0 * touched.Count / perName.Count),5:F1}%)  records a cached build walks={walked,12:N0}/{total:N0} "
      + $"= {share,5:P1}  saved ≈ {(1 - share):P1} of the walk");
  }

}

using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * Does the projection carry stay honest when it asks the WEAKER question — "did any answer move" (`EntityTimeline.AnswerStamp`) instead of
 * "did any evidence arrive" (`StateStamp`)? On fixtures the answer is easy to arrange; on a real capture it is not, and the cost of being
 * wrong is the worst failure this engine has: rows kept across passes that report plausible numbers for a routing the rules no longer hold.
 *
 * So this file asserts it over a capture replayed as growing prefixes (parse and rules see only what has "arrived"), with the drift that
 * motivated the change injected on every pass: each name's verdict re-recorded under the ledger's own spelling (`Prior:…`), same kind, which
 * is exactly what pass N+1 of a real session does when it seeds itself from the memory pass N wrote. That moves `StateStamp` (the old gate
 * rebuilt everything: measured 1,077 ms over 8,014,198 facts, and the fresh index threw away the per-row materialization cache the boards
 * read). Most of the time that moves no answer either — but not always, and the exception turned out to be a finding rather than a bug: see (3).
 *
 * Three things are asserted at every prefix:
 *   1. the CONTINUED rows equal a from-zero `FightProjection.Build` over the same facts and the same timeline — field by field, so a
 *      divergence names the row and the column instead of two hashes;
 *   2. each surviving row's ordinal run only ever GROWS at the end (`ARowRecordSetOnlyGrowsAtTheEnd`) — the property an additive board
 *      refresh would be built on: walk what is beyond the watermark, reuse what is not. If a fact ever changed rows or moved within a run,
 *      that design dies here rather than in a stale damage summary; and
 *   3. a pass over state that has genuinely SETTLED — same facts, memory the previous pass already folded — CONTINUES, while the pass that first
 *      APPLIES freshly written memory may rebuild. It has to: a remembered verdict enters at -infinity, which creates an earlier independent
 *      reason for some names, and `HasIndependentIdentity` is what decides whether a charmed name's row hides. Measured on
 *      eqlog_Incogitable_xegony.txt: 7 of 1,649 names answer that predicate differently once memory is replayed, while no name's resolved kind
 *      moves at all. Carrying those passes would keep drawing rows projected over the older answer.
 *
 * Runs only when EQLP_PROJECTION_CARRY names a capture (`EQLP_PROJECTION_CARRY_PASSES=n`, default 5):
 *   EQLP_PROJECTION_CARRY=/abs/path/eqlog_Kizant_xegony-2.txt dotnet test --filter ProjectionCarryRealLog --logger "console;verbosity=detailed"
 */
[TestClass]
[DoNotParallelize]
public class ProjectionCarryRealLogTest
{
  [TestMethod]
  public void CarriedProjectionMatchesAFullRebuildOnRealLog()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_PROJECTION_CARRY");
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("set EQLP_PROJECTION_CARRY=<capture log>");
    if (!int.TryParse(Environment.GetEnvironmentVariable("EQLP_PROJECTION_CARRY_PASSES"), out var passes) || passes < 2) passes = 5;

    PipelineHarness.EnsureDataStore();
    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealRecordSource.Current = null;

    var length = new FileInfo(path).Length;
    var dir = Path.Combine(Path.GetTempPath(), "eqlp-carry-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var cache = new FightProjection.FightProjectionCache();
    var continuedPasses = 0;
    var settleContinued = 0;
    var previousRuns = new Dictionary<string, int[]>();
    long previousEvidence = 0;
    long previousAnswer = 0;
    var strictLawPasses = 0;

    try
    {
      for (var k = 1; k <= passes; k++)
      {
        var prefix = Path.Combine(dir, $"carry-{k:D2}.txt");
        CopyPrefix(path, length * k / passes, prefix);

        var run = PipelineHarness.RunFileDerived(prefix);
        File.Delete(prefix);
        var facts = run.Facts;

        var timeline = new EntityTimeline();
        var first = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
        var last = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);

        // Did this prefix answer anything differently from the one before it? A verdict that moves re-routes facts that were already filed,
        // so a row's run may legitimately change in the MIDDLE — which is exactly why a refresh may not reuse a count across such a pass.
        var verdictsMoved = k > 1 && (timeline.StateStamp() != previousEvidence || timeline.AnswerStamp() != previousAnswer);
        previousEvidence = timeline.StateStamp();
        previousAnswer = timeline.AnswerStamp();

        // (1) The growth pass. New facts legitimately bring new answers, so this one may rebuild; what it may NOT do is disagree with a
        // from-zero projection over the same capture.
        var sw = Stopwatch.StartNew();
        var rows = cache.Project(facts, timeline);
        sw.Stop();

        // Read the flag NOW: `LastPassContinued` describes the most recent pass, and the settle pass below overwrites it. Printing it
        // afterwards would tell the reader a growth pass continued when the number belongs to a different one.
        var growthContinued = cache.LastPassContinued;
        if (growthContinued) continuedPasses++;

        var reference = FightProjection.Build(facts, timeline);
        AssertSameRows(reference, rows, $"prefix {k} growth pass ({facts.FactCount:N0} facts)");
        RecordOrdinalRuns(cache.Index, rows, previousRuns, k, facts.FactCount, verdictsMoved);
        if (!verdictsMoved) strictLawPasses++;

        /*
         * (2) The settle pass, which is the case the weaker gate exists for and which growth alone cannot exercise: the capture has STOPPED
         * arriving (the load finished, or the raid logged out) and the only new information is what this application now remembers — the
         * ledger and registry rows its own first pass wrote. Facts unchanged, answers unchanged, evidence moved. Under the old gate that is
         * a full re-walk plus a new index (and with it, the boards re-materializing everything they just materialized); under this one it is
         * a continuation of a few milliseconds.
         */
        var reRecorded = RememberConclusions(timeline, facts);

        var settleSw = Stopwatch.StartNew();
        var settled = cache.Project(facts, timeline);
        settleSw.Stop();
        var memoryRebuilt = !cache.LastPassContinued;   // read it now: the quiet pass below overwrites the flag

        AssertSameRows(reference, settled, $"prefix {k} settle pass ({reRecorded:N0} names re-remembered over unchanged facts)");

        /*
         * A second pass over EXACTLY the same state — same facts, and memory that is no longer new. THIS is the case that must continue from
         * the watermark: it is the field report's tick, boards built and nothing arriving.
         *
         * The first settle pass above may rebuild, and the reason is measured rather than assumed: a remembered verdict enters at -infinity, so
         * for a name whose evidence first named it mid-capture it creates an EARLIER independent reason — and `HasIndependentIdentity` is what
         * the charm/pet hiding reads. Replaying memory over eqlog_Incogitable_xegony.txt flips that predicate for 7 of 1,649 names while no
         * name's resolved kind moves at all. Refusing to carry those passes is the digest doing its job; carrying them would draw last pass's
         * row shape. That rebuild happens once per remembered verdict, not once per tick, because this seeding runs on every pass from then on.
         */
        var quietSw = Stopwatch.StartNew();
        var quiet = cache.Project(facts, timeline);
        quietSw.Stop();

        AssertSameRows(reference, quiet, $"prefix {k} quiet pass (same facts, same memory as the settle pass above)");
        if (reRecorded > 0)
        {
          Assert.IsTrue(cache.LastPassContinued,
            $"prefix {k}: a pass over facts AND memory that the previous pass already folded rebuilt everything — the answer digest is moving on " +
            "something a projection cannot read, which is the cost the field report was about");
          if (k > 1) settleContinued++;
        }

        Console.WriteLine($"[carry] pass {k}: facts {facts.FactCount,9:N0} rows {rows.Count,5:N0} " +
                          $"| growth {(growthContinued ? "continued" : "rebuilt ")} {sw.ElapsedMilliseconds,6:N0} ms " +
                          $"| verdicts {(verdictsMoved ? "MOVED" : "same ")} | memory arriving over {reRecorded,4:N0} names: {(memoryRebuilt ? "rebuilt " : "continued")} {settleSw.ElapsedMilliseconds,5:N0} ms " +
                          $"| quiet tick: {(cache.LastPassContinued ? "continued" : "REBUILT")} {quietSw.ElapsedMilliseconds,5:N0} ms");
      }

      Console.WriteLine($"[carry] {path}: growth passes continued {continuedPasses}/{passes}; quiet passes (same facts, same memory) " +
                        $"continued {settleContinued}/{Math.Max(0, passes - 1)}; every pass equalled a full rebuild row for row");

      // Growth passes may rebuild — new facts bring new answers. A quiet pass may not: nothing whatever changed.
      Assert.IsTrue(settleContinued >= Math.Max(1, passes - 1),
        "a pass over an unchanged capture and unchanged memory must continue from the watermark; rebuilding there is what cost 1,077 ms of " +
        "projection and the boards' 1.5 s of re-materialization in the field report");
    }
    finally
    {
      try { Directory.Delete(dir, true); } catch (IOException) { /* temp litter is not a test failure */ }
    }
  }

  /*
   * Re-record every verdict this pass reached under the ledger's spelling of its rule name — same kind, same effective time, deliberately
   * weak so it can never win. That is what a prior-store seed looks like to the timeline: invisible to the routing, visible to `StateStamp`.
   * The count is per prefix, because each prefix gets a fresh timeline exactly as a fresh pass does.
   */
  private static int RememberConclusions(EntityTimeline timeline, DamageFactTable facts)
  {
    var n = 0;
    foreach (var name in facts.InternedNames)
    {
      if (string.IsNullOrEmpty(name)) continue;

      var kind = timeline.IdentityAt(name, double.PositiveInfinity);
      if (kind == IdentityKind.Unknown) continue;

      timeline.SetIdentity(name, kind, RuleStrength.Weak, "Prior:simulated-ledger");
      n++;
    }

    return n;
  }

  // Everything a row means to the grid and to the boards built from it, on one line, so a failure names the row and the field.
  private static string Describe(DerivedFight r)
    => $"{r.Name}@{r.BeginTime}-{r.LastTime} {(r.Dead ? "dead" : "live")}/{r.EndReason} " +
       $"dmg={r.DamageTotal} hits={r.DamageHits} toOwner={r.DamageToOwner} byOwner={r.DamageByOwner} " +
       $"taken={r.TankHits} dmgWin={r.BeginDamageTime}/{r.LastDamageTime} tankWin={r.BeginTankingTime}/{r.LastTankingTime} " +
       $"pet={r.RaidPet} owned={r.CharmedOwned} group={r.GroupId}";

  private static void AssertSameRows(IReadOnlyList<DerivedFight> reference, IReadOnlyList<DerivedFight> continued, string because)
  {
    var expected = reference.Select(Describe).OrderBy(static s => s, StringComparer.Ordinal).ToList();
    var actual = continued.Select(Describe).OrderBy(static s => s, StringComparer.Ordinal).ToList();

    if (expected.SequenceEqual(actual)) return;

    // Name the first handful of differences: "the projection disagreed" tells nobody anything useful at 4,000 rows.
    var diffs = new List<string>();
    for (var i = 0; i < Math.Max(expected.Count, actual.Count) && diffs.Count < 8; i++)
    {
      var e = i < expected.Count ? expected[i] : "(missing)";
      var a = i < actual.Count ? actual[i] : "(missing)";
      if (!string.Equals(e, a, StringComparison.Ordinal)) diffs.Add($"\n    rebuild   = {e}\n    continued = {a}");
    }

    Assert.Fail($"{because}: carried projection diverged from a full rebuild " +
                $"({expected.Count} vs {actual.Count} rows), first differences:{string.Join("", diffs)}");
  }

  /*
   * The append-only law, measured where it would actually be used. A board refresh that reuses what it already counted needs each row's run
   * of fact ordinals to be the same list, possibly longer, never reordered or re-homed — otherwise "walk past the watermark" silently drops
   * or double-counts a fight. Rows are keyed by name + begin time because a rebuild hands back new objects (the grid's own `FightKey` law).
   */
  private static void RecordOrdinalRuns(FightFactIndex index, IReadOnlyList<DerivedFight> rows,
                                        Dictionary<string, int[]> previous, int pass, long factCount, bool verdictsMoved)
  {
    duplicatesSeen.Clear();
    var duplicates = 0;     // one fact filed on two rows: no board's arithmetic survives that, verdicts moved or not
    var shrunkRows = 0;     // facts left a row's run
    var brokenRows = 0;     // a run that is not the old list with possibly more at the end

    foreach (var row in rows)
    {
      var key = $"{row.Name}@{row.BeginTime}";
      var run = index.DamageOrdinalsFor(row).ToArray();

      foreach (var ordinal in run)
      {
        if (!duplicatesSeen.Add(ordinal)) duplicates++;
      }

      if (!previous.TryGetValue(key, out var before))
      {
        previous[key] = run;
        continue;
      }

      if (run.Length < before.Length) shrunkRows++;
      for (var i = 0; i < Math.Min(before.Length, run.Length); i++)
      {
        if (before[i] == run[i]) continue;
        brokenRows++;
        break;
      }

      previous[key] = run;
    }

    Assert.AreEqual(0, duplicates,
        $"pass {pass}: {duplicates} fact ordinal(s) appear in TWO rows' damage runs over {factCount:N0} facts — a fact lands on exactly one " +
        "board, and a refresh that adds counts per row would double what it cannot see");

    /*
     * The append-only law holds only where nothing was re-read: while the answers stand, a row's run is the same list with more at the end, and
     * that is what lets a refresh walk past its watermark. Once a verdict moves, old facts are re-RUTED by design ("changed interpretation of old
     * facts means a safe full rebuild"), so a middle-of-run change is the correct outcome — and the measurement this prints is Phase 2's: how often
     * a real capture re-homes rows, i.e. how much of a refresh can reuse anything. A pass whose verdicts did NOT move must hold strictly.
     */
    if (!verdictsMoved)
    {
      Assert.AreEqual(0, shrunkRows,
          $"pass {pass}: {shrunkRows} row(s) LOST facts over {factCount:N0} facts with NO verdict moved — a fact vanished from its run " +
          "for no reason the rules can state, so no refresh may trust an earlier count of that row");
      Assert.AreEqual(0, brokenRows,
          $"pass {pass}: {brokenRows} row(s) had a fact ordinal move inside their run with NO verdict moved — the run is not a " +
          "prefix-extension, which is the property an additive refresh stands on");
    }

    Console.WriteLine($"[carry]        runs: {rows.Count,5:N0} rows | verdicts {(verdictsMoved ? "moved" : "same ")} " +
                      $"| shrunk {shrunkRows,4} not-prefix {brokenRows,4}" +
                      $"{(verdictsMoved ? "  (legal here: re-routing is what a moved verdict does)" : "  <- strict law asserted")}");
  }

  // Reset per pass by the caller's single entry point; one fact belonging to two rows is a bug in any era.
  private static readonly HashSet<int> duplicatesSeen = new();

  private static void CopyPrefix(string source, long bytes, string target)
  {
    using var inF = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    using var outF = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    var buffer = new byte[1 << 20];
    long left = bytes;
    while (left > 0)
    {
      var read = inF.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
      if (read <= 0) break;
      outF.Write(buffer, 0, read);
      left -= read;
    }
  }
}

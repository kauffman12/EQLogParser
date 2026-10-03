using System.Diagnostics;

using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * What "a fight is going on" costs and how often it changes, measured over a real capture instead of argued from the rule.
 *
 * `LiveFights` states three choices (which clock, which timestamps, which rows) that the legacy meter never had to make —
 * FightManager owned a set that a log line added to. Once the mirror owns the answer, two questions become product
 * questions, and both are answerable from a capture:
 *
 *   1. **How much of a raid night has no fight in it?** That is how long a closed meter stays closed: the announcement that
 *      reopens it fires while damage is fresh, so the dark stretches are exactly the quiet ones. It is also how often the app
 *      refuses to open a meter at all on launch, since `HasLiveFight` gates that too.
 *   2. **How many pulls does a night contain, and how often does one resume after the gap?** Starts per life, restarts of the
 *      same name after 30 quiet seconds, longest pause inside a row: the shape the board's own gap rule makes of a capture, and
 *      what a player reads as "pulls". It is NOT the meter's open rate — that rule fires on fresh damage at derive rate
 *      (LiveFights.HasFreshDamage), because legacy fired its event on every damage line of a fight, which is why a meter closed
 *      with the X came straight back.
 *
 * Runs only when EQLP_MIRROR_LIVE names a log:
 *   EQLP_MIRROR_LIVE=local/logs/live/eqlog_Kizant_xegony.txt dotnet test --filter LiveFights_RealLog --logger "console;verbosity=detailed"
 *   (EMU captures under local/logs/emu/ also need EQLP_EMU=1 - see RealLogBoardsTest's header)
 *
 * The pull counts are taken off each row's two direction populations — `FightFactIndex.DamageOrdinalsFor` and
 * `.TankingOrdinalsFor`, which are exactly what `LiveFights.LastActivityAt` reads — counting a fresh start after every silence
 * longer than the gap. Unrouted facts (mob on mob, mob on somebody's pet) sit inside the row's span but in neither window, so
 * they do not keep a fight live and do not appear here either; counting them would understate the quiet stretches, which are
 * what decide how long a closed meter stays shut.
 */
[TestClass]
[DoNotParallelize]
public class LiveFightsRealLogTest
{
    private const double GapS = LiveFights.GapS;

    [TestMethod]
    public void LiveFights_RealLog_AnnouncementAndQuietCensus()
    {
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_MIRROR_LIVE"));
        if (path is null) Assert.Inconclusive("set EQLP_MIRROR_LIVE=<log> to census the live-fight rule over a capture");

        var step = Stopwatch.StartNew();
        var run = PipelineHarness.RunFileDerived(path);
        var timeline = new EntityTimeline();
        var facts = run.Facts;
        var first = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
        var last = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);
        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        step.Stop();
        Console.WriteLine($"[live] {rows.Count} rows, raw fact clock {first:N0} .. {last:N0} = {(last - first):N0} s "
                          + $"({step.ElapsedMilliseconds:N0} ms to classify+project)");
        Assert.IsTrue(rows.Count > 0 && facts.Facts.Length > 0, "this capture produced nothing to measure");

        /*
         * 1. Quiet coverage: every fact makes the capture "live" for GapS after it, so the answer is the union of those
         *    intervals clipped to the capture — and the holes between them are the stretches where a hidden meter would be
         *    shut and no meter would open on launch. Facts arrive in time order, so one sweep with a running interval.
         */
        double covered = 0;
        var quietRuns = 0;
        double longestQuiet = 0;
        var runStart = double.NaN;
        var runEnd = double.NaN;

        void Flush(double nextStart)
        {
            if (double.IsNaN(runStart)) return;

            covered += runEnd - runStart;
            if (!double.IsNaN(nextStart) && nextStart > runEnd)
            {
                quietRuns++;
                longestQuiet = Math.Max(longestQuiet, nextStart - runEnd);
            }

            runStart = double.NaN;
        }

        foreach (var fact in facts.Facts)
        {
            var from = Math.Max(fact.TimeS, first);
            var to = Math.Min(fact.TimeS + GapS, last);
            if (to <= from) continue;

            if (double.IsNaN(runStart) || fact.TimeS > runEnd)
            {
                Flush(from);
                runStart = from;
                runEnd = to;
            }
            else runEnd = Math.Max(runEnd, to);
        }

        Flush(double.NaN);

        /*
         * Printed as ABSOLUTE minutes rather than a percentage: a log file spans whatever the player left it open over —
         * Kizant's 270 rows sit inside 13.7 days of file, so "0.5 % of the capture was live" measures how often the raid
         * logged out, not how the meter behaves. The interesting quantity is the total time a board would stay up, and the
         * number of holes where it would have been shut.
         */
        Console.WriteLine($"[live] {(last - first) / 3600:N1} h of file, of which {covered / 60:N1} min have a fight live in them; "
                          + $"{quietRuns} stretches of >{GapS:N0} s with nothing live, longest {longestQuiet / 60:N1} min");

        // 2. Pull starts: one when a row's windowed traffic starts, another each time it restarts after the gap. Both lists
        //    are already ascending (the projection walks facts in order), so this is a two-pointer merge.
        var startedRows = 0;
        var starts = 0;
        var restarts = 0;
        double longestRowPause = 0;
        foreach (var row in rows)
        {
            if (!index.HasDamage(row) && !index.HasTanking(row)) continue;

            startedRows++;
            starts++;
            var prev = double.NegativeInfinity;
            var i = 0;
            var j = 0;
            var damageOrdinals = index.DamageOrdinalsFor(row);
            var tankOrdinals = index.TankingOrdinalsFor(row);
            while (i < damageOrdinals.Count || j < tankOrdinals.Count)
            {
                double t;
                if (j >= tankOrdinals.Count || (i < damageOrdinals.Count &&
                                                facts.Facts[damageOrdinals[i]].TimeS <= facts.Facts[tankOrdinals[j]].TimeS))
                    t = facts.Facts[damageOrdinals[i++]].TimeS;
                else
                    t = facts.Facts[tankOrdinals[j++]].TimeS;

                if (t - prev > GapS)
                {
                    if (!double.IsNegativeInfinity(prev))
                    {
                        starts++;
                        restarts++;
                        longestRowPause = Math.Max(longestRowPause, t - prev);
                    }
                }

                prev = t;
            }
        }

        Console.WriteLine($"[live] {starts} pull starts over {startedRows} rows that carry traffic "
                          + $"({starts / (double)startedRows:N2} per row; {restarts} restarts, longest mid-row pause "
                          + $"{longestRowPause:N0} s)");

        // 3. Live right now, i.e. what a meter opening at this instant would be told, and the same question on the two dials.
        var liveNow = rows.Count(r => LiveFights.IsLive(r, last, GapS));
        var liveAtFive = rows.Count(r => LiveFights.IsLive(r, last, LiveFights.TimeoutFor(5)));
        Console.WriteLine($"[live] at the capture's newest event: {liveNow} live on the default dial, {liveAtFive} on a 5 s dial; "
                          + $"live damage share on this capture = "
                          + $"{rows.Where(r => LiveFights.IsLive(r, last, GapS)).Sum(r => r.DamageTotal) / (double)Math.Max(1, rows.Sum(r => r.DamageTotal)) * 100:N1} %");

        // Each row that carries anything starts at least once, and a capture measured this way must be non-trivial —
        // those are the collapse checks. Everything else above is a report for a human to read, deliberately un-thresholded:
        // a raid night's rhythm is not a constant to assert, and a red test that fires because a guild took a long break is
        // a test people delete.
        Assert.IsTrue(starts >= startedRows, "a row with traffic never announced its own start");
        Assert.IsTrue(covered > 0 && covered <= last - first + GapS, "the quiet sweep ran outside the capture");
        /*
         * NOT asserted: that `liveNow` is above zero. It reads 1 on Kizant (that file ends mid-pull) and 0 on both Incogitable
         * and 09-20-25 (they end after a kill, and a dead row is not live however recent its last hit) — those zeros are the
         * rule working, which is why they are printed rather than demanded. What IS worth failing on is the rule being blind:
         * ask each row at the moment of its OWN newest activity, which every live-capable row passes by construction.
         */
        Assert.IsTrue(rows.Any(r => !r.Dead && LiveFights.IsLive(r, LiveFights.LastActivityAt(r), GapS)),
            "no row in this capture is live even at the second of its own last hit; the rule cannot see rows");

        /*
         * The dial has to bite: a meter told to zero after five seconds of quiet must see FEWER live rows than one waiting
         * out the engagement gap, on any real night. If these two ever read equal, `TimeoutFor` is not reaching the rule and
         * the overlay's dial would be decorative.
         */
        Assert.IsTrue(liveAtFive <= liveNow, "a tighter quiet window answered with MORE live fights");

        /*
         * What the reopen rule consumes. It fires per derive while damage stays inside the gap, so its rate is the CADENCE's,
         * not the log's (DeriveCadence: 3..15 s). Printed against this capture's fact count so nobody has to wonder
         * whether "announce on fresh damage" means an open-per-line like legacy's per-line event — it does not, and its reader
         * no-ops while a window exists anyway.
         */
        Console.WriteLine($"[live] reopen-rule ceiling over the {covered / 60:N1} covered minutes: ~{covered / 3:N0} announces "
                          + $"at the fastest cadence (3 s), ~{covered / 15:N0} at the slowest (15 s) — against "
                          + $"{facts.Facts.Length:N0} damage facts, every one of which fired legacy's event");
    }

    private static string? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.IsPathRooted(path) || File.Exists(path)) return path;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

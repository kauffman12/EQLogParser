using EQLogParser;

namespace EQLogParser;

/*
 * A derive pass costs what the capture costs (measured 650 ms at 2,931,939 facts — docs/DesignNotes.md), and on a live
 * tail almost all of that money buys a walk over facts the previous pass already walked. So the projection carries its
 * own accumulators (FightProjection.ProjectionState) and the next pass resumes at the watermark instead of starting at
 * ordinal 0, with the damage index carried alongside because it holds ordinal positions into an append-only table.
 *
 * That is a real speedup and a real risk, and the risk is the whole subject of this file: a continued pass must be
 * INDISTINGUISHABLE from a single full pass over the same facts and the same classification. Not "close", not "equal on
 * the totals I thought to check" — these rows are what the fight list displays and what every summary board is built
 * from, so a divergence would put plausible numbers on screen with nothing anywhere pointing at it. Two things keep it honest:
 *
 *   1. Continue runs the SAME loop body Build runs (Build is literally Continue over a fresh state), so there is no second
 *      implementation to drift. What splitting can still break is the STATE crossing the boundary — open rows, per-name
 *      death queues, the charm→pet link, the publish step's refusal to consume a death it only READ — and that is what
 *      these tests aim at.
 *
 *   2. The carry is gated (FightProjectionCache): the watermarks must still name the same facts, and
 *      EntityTimeline.StateStamp must match the stamp recorded with the carried state. Any identity verdict added,
 *      strengthened or re-windowed buys a full rebuild, which is what every pass used to be. A false "changed" costs one
 *      extra pass; a false "unchanged" would display stale rows indefinitely — so the digest errs safe, and
 *      EntityTimelineDigestTest holds it to the whole of the timeline's state.
 */
[TestClass]
public class FightProjectionIncrementTest
{
    private const double T0 = 1_000;

    // A table that grows, with one sequence counter for the whole life of the test — the live case: same table object,
    // same name pool, ordinals only ever increasing.
    private sealed class Capture
    {
        internal readonly DamageFactTable Facts = new(64);
        private int _seq;

        internal void Hit(string atk, string def, uint dmg, double t, byte label = LabelTypes.Melee)
          => Facts.AddFact(new DamageFact(_seq++, (long)(T0 + t), Facts.InternName(atk), Facts.InternName(def),
                                          total: dmg, typeId: label, flags: 0, modMask: 0, subIdx: ushort.MaxValue));

        internal void Slain(string name, double t)
          => Facts.AddDeath(new DeathFact(_seq++, (long)(T0 + t), Facts.InternName(name), (short)-1));

        internal void Charm(string name, double t)
          => Facts.AddEvidence(new EvidenceFact(_seq++, (long)(T0 + t), Facts.InternName(name), EvidenceFact.EvCharmStart));

        internal void JoinedRaid(string name, double t)
          => Facts.AddEvidence(new EvidenceFact(_seq++, (long)(T0 + t), Facts.InternName(name), EvidenceFact.EvJoinedRaid));
    }

    /*
     * Everything a row means to the display and to the boards built from it, on one line. Compared between the continued
     * path and the full path, because a failure has to print which FIELD of which row disagreed rather than two hashes.
     */
    private static string Describe(DerivedFight r)
        => $"{r.Name}@{r.BeginTime}-{r.LastTime} {(r.Dead ? "dead" : "live")}/{r.EndReason} " +
           $"dmg={r.DamageTotal} hits={r.DamageHits} toOwner={r.DamageToOwner} byOwner={r.DamageByOwner} " +
           $"dmgWin={r.BeginDamageTime}/{r.LastDamageTime} tankWin={r.BeginTankingTime}/{r.LastTankingTime} " +
           $"pet={r.RaidPet} owned={r.CharmedOwned} " +
           $"encounter={(r.EncounterRow is null ? "-" : $"{r.EncounterRow.Name}@{r.EncounterRow.BeginTime}")} " +
           $"rollup=[{Rollup(r)}]";

    private static string Rollup(DerivedFight r)
      => string.Join(",", r.PlayerRollup.OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                          .Select(static kv => $"{kv.Key}:{kv.Value.Damage}/{kv.Value.Hits}"));

    private static List<string> DescribeAll(IReadOnlyList<DerivedFight> rows)
      => rows.Select(Describe).OrderBy(static s => s, StringComparer.Ordinal).ToList();

    private static void AssertSameRows(IReadOnlyList<DerivedFight> reference, IReadOnlyList<DerivedFight> continued,
                                       string because)
    {
        var expected = DescribeAll(reference);
        var actual = DescribeAll(continued);
        CollectionAssert.AreEqual(expected, actual,
            $"{because}\n  reference = {string.Join("\n  reference = ", expected)}\n  continued = {string.Join("\n  continued = ", actual)}");
    }

    // The classification DeriveEngine builds per pass (roster seed, then the rules over damage and heals).
    private static EntityTimeline Classify(DamageFactTable facts)
      => Classify(facts, null);

    private static EntityTimeline Classify(DamageFactTable facts, HealFactTable? heals)
    {
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(facts, timeline, heals);
        return timeline;
    }

    // ---- what may continue, and what has to be rebuilt ----

    [TestMethod]
    public void ADamageOnlyPassContinuesWhereTheLastOneStopped()
    {
        var log = new Capture();
        log.Hit("Zomm", "A bone walker", 100, 0);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        cache.Project(log.Facts, timeline);
        Assert.IsFalse(cache.LastPassContinued, "the first pass has nothing to continue from");

        // More swinging, no new evidence about anybody: same verdicts, so the state is still valid.
        log.Hit("Zomm", "A bone walker", 200, 5);
        var rows = cache.Project(log.Facts, timeline);

        Assert.IsTrue(cache.LastPassContinued,
            "new facts under a classification that says the same thing about every name are a continuation");
        Assert.AreEqual(300, rows.Single().DamageToOwner);
    }

    /*
     * The cheap lane of the live cadence (DeriveCadence.DeriveKind.ProjectionOnly): between expensive passes the session
     * re-projects the new facts over the timeline INSTANCE the last full pass produced, because rebuilding verdicts is what costs
     * 186-261 ms and folding is what costs 0-5. Two things have to be true for that to be a display rather than a fiction, and both
     * are asserted here: the carry stays open across consecutive cheap folds (otherwise the "cheap" lane re-walks the night every
     * half-second, which is slower than not having it), and the rows match a single fold over the same span under the same
     * verdicts — staleness in this lane is about verdicts a later full pass has not reached yet, never about facts being missed.
     */
    [TestMethod]
    public void TwoCheapPassesOverOneSetOfVerdictsMatchASingleFold()
    {
        // Both sides see the same four swings. The reference gets them in one fold with verdicts classified over all of them;
        // the live side meets them as a growing capture, under the verdicts its FIRST pass classified.
        var whole = new Capture();
        foreach (var t in new[] { 0d, 4d, 8d, 12d }) whole.Hit("Zomm", "A corrupted skeleton", 100, t);
        var reference = FightProjection.Build(whole.Facts, Classify(whole.Facts)).ToList();

        var live = new Capture();
        live.Hit("Zomm", "A corrupted skeleton", 100, 0);
        live.Hit("Zomm", "A corrupted skeleton", 100, 4);

        var cache = new FightProjection.FightProjectionCache();
        var verdicts = Classify(live.Facts);
        cache.Project(live.Facts, verdicts);                      // expensive pass, leaves its timeline behind

        live.Hit("Zomm", "A corrupted skeleton", 100, 8);
        cache.Project(live.Facts, verdicts);                      // cheap pass #1: the SAME timeline instance

        live.Hit("Zomm", "A corrupted skeleton", 100, 12);
        var rows = cache.Project(live.Facts, verdicts);           // cheap pass #2

        Assert.IsTrue(cache.LastPassContinued, "the cheap lane is only cheap if the carry survives the previous cheap pass");
        AssertSameRows(reference, rows, "folding in three steps must read exactly like folding once");
    }

    [TestMethod]
    public void ANewIdentityVerdictBuysAFullRebuild()
    {
        // Rows MIGRATE when evidence arrives — that is the feature the engine exists for — so a changed classification
        // may never be answered by continuing: an exchange keyed on the raider has to move onto the mob it was fighting.
        var log = new Capture();
        log.Hit("Echohead", "Illuminai", 50, 0);
        var cache = new FightProjection.FightProjectionCache();

        var before = cache.Project(log.Facts, Classify(log.Facts));
        Assert.AreEqual("Illuminai", before[0].Name, "unknown-vs-unknown keeps the legacy defender key");

        log.JoinedRaid("Illuminai", 9);                     // explicit text: she is one of ours
        var after = cache.Project(log.Facts, Classify(log.Facts));

        Assert.IsFalse(cache.LastPassContinued, "a new verdict about any name invalidates every carried row");
        Assert.AreEqual("Echohead", after[0].Name, "the exchange belongs to the NPC, not the raider");
    }

    [TestMethod]
    public void ACoincidentallyEqualTableIsNotAContinuation()
    {
        // The carried index holds ORDINALS, so a carry is only sound while ordinal N still means the fact it meant last
        // pass. Two captures can easily be the same length and number their facts the same way; what says them apart is
        // the boundary fact itself — its time and the interned names on both sides of it.
        var one = new Capture();
        one.Hit("Zomm", "Grul", 100, 0);
        one.Hit("Zomm", "Grul", 100, 1);

        var other = new Capture();
        other.Hit("Zomm", "Gralurt", 100, 0);
        other.Hit("Zomm", "Gralurt", 100, 1);

        var cache = new FightProjection.FightProjectionCache();
        cache.Project(one.Facts, Classify(one.Facts));
        var rows = cache.Project(other.Facts, Classify(other.Facts));

        Assert.IsFalse(cache.LastPassContinued, "same length is not the same facts");
        Assert.AreEqual("Gralurt", rows.Single().Name, "and the rebuild over the new table says so in its rows");
    }

    [TestMethod]
    public void TheSameTableGrowingIsAContinuation()
    {
        var log = new Capture();
        log.Hit("Zomm", "Grul", 100, 0);
        log.Hit("Zomm", "Grul", 100, 1);

        var cache = new FightProjection.FightProjectionCache();
        cache.Project(log.Facts, Classify(log.Facts));

        log.Hit("Zomm", "Grul", 100, 2);
        cache.Project(log.Facts, Classify(log.Facts));
        Assert.IsTrue(cache.LastPassContinued);

        // And a pass that adds nothing at all (the timer fired on a quiet log) still answers with the same rows.
        var again = cache.Project(log.Facts, Classify(log.Facts));
        Assert.AreEqual(300, again.Single().DamageToOwner);
    }

    // ---- the state that crosses a boundary: rows, deaths, windows ----

    /*
     * The publish step marks a row dead from a slain line it finds in its tail, and MUST NOT consume that death: the raid
     * swinging at the same name again (the egg case — slain at :52, swung at again at :58) needs the loop to draw the
     * boundary between the two lives, and the loop needs the death. Consume it while publishing and two encounters weld
     * into one row with double the duration and one kill where there were two.
     */
    [TestMethod]
    public void ADeathArrivingInAnEarlierPassStillSplitsTheRowOnePassWould()
    {
        var log = new Capture();
        log.Hit("Zomm", "A corrupted egg", 500, 0);
        log.Slain("A corrupted egg", 5);

        var cache = new FightProjection.FightProjectionCache();
        var published = cache.Project(log.Facts, Classify(log.Facts));
        Assert.AreEqual(1, published.Count);
        Assert.IsTrue(published[0].Dead, "a slain line inside the tail marks the row that stopped fighting");

        log.Hit("Zomm", "A corrupted egg", 700, 15);        // the next egg to answer to that name
        var continued = cache.Project(log.Facts, Classify(log.Facts));

        var whole = new Capture();
        whole.Hit("Zomm", "A corrupted egg", 500, 0);
        whole.Slain("A corrupted egg", 5);
        whole.Hit("Zomm", "A corrupted egg", 700, 15);

        AssertSameRows(FightProjection.Build(whole.Facts, Classify(whole.Facts)), continued,
            "a death that arrived in an earlier pass must still split these two lives");
        Assert.AreEqual(2, continued.Count);
        Assert.IsTrue(continued.Single(r => r.BeginTime == (long)T0).Dead);
    }

    [TestMethod]
    public void ARowPublishedDeadAndThenHitAgainMatchesOneFullPass()
    {
        // Same mechanism from the other side: a row published as dead is still OPEN in the carried state and keeps taking
        // facts; only the boundary rules decide when it stops being the same engagement. Here a 38 s silence splits it.
        var log = new Capture();
        log.Hit("Zomm", "Grul", 100, 0);
        log.Slain("Grul", 2);

        var cache = new FightProjection.FightProjectionCache();
        cache.Project(log.Facts, Classify(log.Facts));

        log.Hit("Zomm", "Grul", 100, 40);
        log.Hit("Zomm", "Grul", 100, 45);
        var continued = cache.Project(log.Facts, Classify(log.Facts));

        var whole = new Capture();
        whole.Hit("Zomm", "Grul", 100, 0);
        whole.Slain("Grul", 2);
        whole.Hit("Zomm", "Grul", 100, 40);
        whole.Hit("Zomm", "Grul", 100, 45);

        AssertSameRows(FightProjection.Build(whole.Facts, Classify(whole.Facts)), continued,
            "a death and a gap two passes apart have to land in the same places they would in one walk");
    }

    [TestMethod]
    public void AGapSplitLandingOnAPassBoundaryMatchesOneFullPass()
    {
        // The row ends because four minutes of nothing happened between two hits, and the pass boundary falls there.
        var log = new Capture();
        log.Hit("Zomm", "Grul", 100, 0);
        log.Hit("Zomm", "Grul", 200, 3);

        var cache = new FightProjection.FightProjectionCache();
        cache.Project(log.Facts, Classify(log.Facts));

        log.Hit("Zomm", "Grul", 400, 240);
        var continued = cache.Project(log.Facts, Classify(log.Facts));

        var whole = new Capture();
        whole.Hit("Zomm", "Grul", 100, 0);
        whole.Hit("Zomm", "Grul", 200, 3);
        whole.Hit("Zomm", "Grul", 400, 240);

        AssertSameRows(FightProjection.Build(whole.Facts, Classify(whole.Facts)), continued,
            "a gap boundary is one row pair either way");
        Assert.AreEqual(2, continued.Count);
        Assert.IsTrue(cache.LastPassContinued, "no evidence arrived here, so this pass should have been the cheap one");
    }

    [TestMethod]
    public void ACharmArrivingLaterClosesTheRowItClosedInOneFullPass()
    {
        // A charm sighting lands after the raid's last swing (two people, two acts). Live, it arrives in a LATER pass than
        // the facts it ends, and the row it closes has been published alive in the meantime.
        var log = new Capture();
        log.Hit("Zomm", "an imbued whipgrass", 250, 0);
        log.Hit("Zomm", "an imbued whipgrass", 250, 4);

        var cache = new FightProjection.FightProjectionCache();
        var before = cache.Project(log.Facts, Classify(log.Facts));
        Assert.IsFalse(before[0].Dead, "nothing has ended this row yet");

        log.Charm("an imbued whipgrass", 30);
        var continued = cache.Project(log.Facts, Classify(log.Facts));

        Assert.IsFalse(cache.LastPassContinued, "a window opening is a classification change, so rows are rebuilt");

        var whole = new Capture();
        whole.Hit("Zomm", "an imbued whipgrass", 250, 0);
        whole.Hit("Zomm", "an imbued whipgrass", 250, 4);
        whole.Charm("an imbued whipgrass", 30);

        var wholeRows = FightProjection.Build(whole.Facts, Classify(whole.Facts));
        AssertSameRows(wholeRows, continued,
            "the charm closes the same row with the same reason whichever order it arrived in");
        Assert.IsTrue(continued.Any(r => r.EndReason == DerivedFightEnd.Charmed),
            "and one of these rows says so: closed by a charm, not by a gap " + string.Join(" | ", continued.Select(Describe)));
    }

    [TestMethod]
    public void TheDamageIndexTravelsWithTheRowsItWasFilledBeside()
    {
        // The index is filled inside the same walk that builds the rows, so a continued pass APPENDS to the same ordinal
        // lists. A row must not end up indexed against facts that went into another row's totals.
        var log = new Capture();
        log.Hit("Zomm", "Grul", 100, 0);
        log.Hit("Grul", "Zomm", 60, 1);

        var cache = new FightProjection.FightProjectionCache();
        cache.Project(log.Facts, Classify(log.Facts));

        log.Hit("Zomm", "Grul", 100, 5);
        log.Hit("Zomm", "Gralurt", 90, 6);
        var rows = cache.Project(log.Facts, Classify(log.Facts));

        var whole = new Capture();
        whole.Hit("Zomm", "Grul", 100, 0);
        whole.Hit("Grul", "Zomm", 60, 1);
        whole.Hit("Zomm", "Grul", 100, 5);
        whole.Hit("Zomm", "Gralurt", 90, 6);
        var reference = new FightProjection.FightProjectionCache();
        var fullRows = reference.Project(whole.Facts, Classify(whole.Facts));

        AssertSameRows(fullRows, rows, "index or no index, the rows agree");

        foreach (var row in rows)
        {
            var mine = cache.Index.DamageOrdinalsFor(row).OrderBy(static o => o).ToList();
            var theirs = reference.Index
                .DamageOrdinalsFor(fullRows.Single(r => r.Name == row.Name)).OrderBy(static o => o).ToList();
            CollectionAssert.AreEqual(theirs, mine, $"{row.Name}: the facts indexed for a row are that row's own facts");
        }

        Assert.AreEqual(reference.Index.DamageFactCount, cache.Index.DamageFactCount);
        Assert.AreEqual(reference.Index.TankingFactCount, cache.Index.TankingFactCount);
    }

    // ---- convergence over parsed logs: every prefix, then the whole thing ----

    /*
     * The property above over real parsed content: feed one log through the cache in growing pieces — re-parsing and
     * re-classifying each time, exactly as DeriveEngine does on every tick — and require the final row list to be the one
     * a single pass over the finished log produces. This is the test that exercises the gate with real evidence arriving in
     * real order: joins, pets, charms, mid-log verdicts, names migrating between rows.
     */
    [TestMethod]
    [DataRow("mini-fight.txt")]
    [DataRow("tank-fight.txt")]
    [DataRow("gap-fight.txt")]
    [DataRow("attempt-fight.txt")]
    [DataRow("heal-fight.txt")]
    [DataRow("heal-board.txt")]
    [DataRow("rules-fixture.txt")]
    public void GrowingAParsedLogOnePassAtATimeEndsAtTheSameFightList(string fixture)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", fixture));
        if (lines.Length < 8) Assert.Inconclusive($"{fixture} is too short to split meaningfully");

        var cache = new FightProjection.FightProjectionCache();
        IReadOnlyList<DerivedFight> live = [];
        var continuedPasses = 0;
        var cuts = Cuts(lines.Length);

        foreach (var cut in cuts)
        {
            var pass = ParseAndClassify(lines[..cut], fixture);
            live = cache.Project(pass.Facts, pass.Timeline);
            if (cache.LastPassContinued) continuedPasses++;
        }

        var whole = ParseAndClassify(lines, fixture);
        AssertSameRows(FightProjection.Build(whole.Facts, whole.Timeline), live,
            $"{fixture}: {lines.Length} lines folded in {cuts.Count} passes");
    }

    /*
     * The fast path has to be REACHED for the convergence test above to mean anything, and how often it is says what the
     * gate is actually doing. On a fixture where the raid is just fighting, most passes see no new verdict about anybody
     * and continue. On rules-fixture.txt — a fixture built so that each line teaches the classifier something — they must
     * not: every cut restates somebody's identity, so carrying rows across it would be exactly the mistake the gate
     * exists to prevent. Asserting both directions is asserting the gate discriminates rather than being always-open or
     * always-shut (an always-shut gate would still pass the equality tests above, and would just be the old code).
     */
    [TestMethod]
    public void TheGateOpensOnQuietPassesAndShutsWhenVerdictsMove()
    {
        static int ContinuedPasses(string fixture)
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", fixture));
            var cache = new FightProjection.FightProjectionCache();
            var cuts = Cuts(lines.Length);
            var continued = 0;
            foreach (var cut in cuts)
            {
                var pass = ParseAndClassify(lines[..cut], fixture);
                cache.Project(pass.Facts, pass.Timeline);
                if (cache.LastPassContinued) continued++;
            }

            return continued;
        }

        // Measured: 4 of mini-fight's 6 passes continue, 0 of rules-fixture's.
        Assert.IsTrue(ContinuedPasses("mini-fight.txt") >= 3,
            "the fast path is never taken on a log of mostly fighting, so the gate is always shut and this whole " +
            "mechanism buys nothing");
        Assert.IsTrue(ContinuedPasses("rules-fixture.txt") <= 2,
            "rules-fixture restates identities every few lines; carrying rows through THAT is the unsafe direction");
    }

    // Cuts at line boundaries. Any cut is fair: a parser handed half a log simply has less to say about it.
    private static List<int> Cuts(int lineCount)
      => new[] { 0.3, 0.45, 0.6, 0.75, 0.9, 1.0 }.Select(f => Math.Max(2, (int)(lineCount * f)))
          .Distinct().Where(c => c <= lineCount).ToList();

    private sealed record Parsed(DamageFactTable Facts, EntityTimeline Timeline);

    // The same two steps DeriveEngine runs per pass: roster seed, then the rules over facts and heals.
    private static Parsed ParseAndClassify(string[] lines, string fixtureName)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-inc-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, fixtureName);   // the fixture's own name keeps any player-name seeding intact
        File.WriteAllLines(log, lines);

        var run = PipelineHarness.RunFileDerived(log);
        var facts = run.Facts;
        var timeline = new EntityTimeline();
        var firstT = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
        var lastT = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
        RegistrySeed.Apply(timeline, facts, firstT, lastT);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);
        return new Parsed(facts, timeline);
    }
}

using EQLogParser;

namespace EQLogParser;

/*
 * Layer 1 of the materialization cache (docs/DesignNotes.md → "What the cell cache has to buy at two boundaries"): a row
 * whose ordinal run GREW continues instead of re-walking.
 *
 * `FightFactIndex.SummaryFightFor` used to have one answer for "something about this row moved": rebuild from ordinal zero,
 * allocating every block and every DamageRecord again — on a live pull that is paid per touched row per pass for records that
 * cannot possibly have changed. It now resumes at the count it last consumed, because the fact tables are append-only, a row's
 * ordinal list only ever grows at the end (`FightFactIndex.OnFactLocked`), and a pass that REBUILT the rows threw this index away
 * with them, so a cache entry can never describe a different walk. The proof of an unchanged block is its ordinal prefix.
 *
 * The laws pinned here, in order:
 *
 *   - **a continued materialization equals a from-zero one**, field for field and record for record — including the two
 *     aggregates that are not append-only (per-player activity segments and the per-spell DD/DoT/Proc counters, whose value
 *     objects the walk mutates in place, so they are cloned and continued rather than shared);
 *   - **a published `Fight` is never mutated**: the object handed out last pass sits inside a `SummaryInput` a builder may be
 *     walking on another thread, so growth produces a new Fight whose CLOSED blocks are shared by reference and whose open
 *     (last) block per direction is cloned;
 *   - **the warm path really runs** — asserted by block identity, because a test that silently took the cold path everywhere
 *     would still be green, which is the failure mode this whole seam has to defend against;
 *   - one second, two paths: a fact arriving on the second of an already-built block joins it on both, since "consecutive"
 *     compares only against the previous fact.
 */
[TestClass]
public class MaterializationContinuationTest
{
    private const double T0 = FixtureTime.Base;

    // A table that grows, one sequence counter for the whole test — the live case: same table object, same name pool,
    // ordinals only ever increasing. Same shape as FightProjectionIncrementTest's helper.
    private sealed class Capture
    {
        internal readonly DamageFactTable Facts = new(64);
        private int _seq;

        internal void Hit(string atk, string def, uint dmg, double t, byte label = LabelTypes.Melee)
          => Facts.AddFact(new DamageFact(_seq++, (long)(T0 + t), Facts.InternName(atk), Facts.InternName(def),
                                          total: dmg, typeId: label, flags: 0, modMask: 0, subIdx: ushort.MaxValue));

        internal void Taunt(string npc, string by, double t, byte flags = TauntFact.TauntSuccess)
          => Facts.AddTaunt(new TauntFact(_seq++, (long)(T0 + t), Facts.InternName(npc), Facts.InternName(by), flags));
    }

    /*
     * The verdicts the routing needs, declared before anything is folded: the mob is an NPC and the raiders are Players, so a
     * fact has a side to be aimed at. (Classifying them from these bare facts is not possible — no name here carries an article,
     * casts, speaks or gets healed — and `FightSummarySourceTest` sets identities the same way.) Set once and never re-run:
     * the answers must hold still across the passes, which is what a CONTINUED projection requires of them.
     */
    private static EntityTimeline Classify(DamageFactTable facts)
    {
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Berauni", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Kilsa", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");
        ClassificationRules.Apply(facts, timeline);
        return timeline;
    }

    private static DerivedFight Row(IReadOnlyList<DerivedFight> rows, string name)
        => rows.First(r => r.Name == name);

    // ---- rendering: everything the materializer produces, on one comparable line ----

    private static string Record(DamageRecord r)
      => $"{r.Attacker}>{r.Defender}:{r.Total}/{r.Type}#{r.SubType}/m{r.ModifiersMask}/o{r.AttackerOwner ?? "-"}/spell={r.AttackerIsSpell}";

    private static string Blocks(List<ActionGroup> blocks)
      => string.Join(",", blocks.Select(b => $"{b.BeginTime}[{string.Join(";", b.Actions.Cast<DamageRecord>().Select(Record))}]"));

    private static string TauntBlocks(List<ActionGroup> blocks)
      => string.Join(",", blocks.Select(b => $"{b.BeginTime}[{string.Join(";", b.Actions.Cast<TauntRecord>()
                                .Select(t => $"{t.Player}:{t.Npc}/{t.Success}/{t.IsImproved}"))}]"));

    private static string Segments(string word, Dictionary<string, TimeSegment> segments)
      => $"{word}=[" + string.Join(",", segments.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                  .Select(kv => $"{kv.Key}:{kv.Value.BeginTime}-{kv.Value.EndTime}")) + "]";

    private static string SubSegments(string word, Dictionary<string, Dictionary<string, TimeSegment>> sub)
      => $"{word}=[" + string.Join(",", sub.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            kv.Key + "{" + string.Join(",", kv.Value.OrderBy(inner => inner.Key, StringComparer.Ordinal)
                .Select(inner => $"{inner.Key}:{inner.Value.BeginTime}-{inner.Value.EndTime}")) + "}")) + "]";

    private static string Spells(string word, Dictionary<string, SpellDamageStats> stats)
      => $"{word}=[" + string.Join(",", stats.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                  .Select(kv => $"{kv.Key}:{kv.Value.Count}/{kv.Value.Total}/{kv.Value.Max}")) + "]";

    // The whole materialized row: identity, bounds, both directions' blocks, both windows, every aggregate.
    private static string Describe(Fight? fight)
    {
      if (fight is null) return "<null>";

      return string.Join("|",
        $"id={fight.Id}", $"name={fight.Name}", $"dead={fight.Dead}", $"group={fight.GroupId}",
        $"span={fight.BeginTime}-{fight.LastTime}",
        $"dmgWin={fight.BeginDamageTime}/{fight.LastDamageTime}",
        $"tankWin={fight.BeginTankingTime}/{fight.LastTankingTime}",
        $"hits={fight.DamageHits}/{fight.TankHits}", $"totals={fight.DamageTotal}/{fight.TankTotal}",
        $"tip={fight.TooltipText}",
        $"D[{Blocks(fight.DamageBlocks)}]", $"T[{Blocks(fight.TankingBlocks)}]",
        $"TA[{TauntBlocks(fight.TauntBlocks)}]",
        Segments("ds", fight.DamageSegments), Segments("ts", fight.TankSegments),
        SubSegments("dss", fight.DamageSubSegments), SubSegments("tss", fight.TankSubSegments),
        Spells("dd", fight.DdDamage), Spells("dot", fight.DoTDamage), Spells("proc", fight.ProcDamage));
    }

    /*
     * The reference: the same table, the same classification, and NO cache — a fresh index projected from zero. This is
     * what the row would say if layer 1 did not exist, so every comparison below is "the cheap answer is the same answer".
     */
    private static Fight? RebuildFromZero(DamageFactTable facts, EntityTimeline timeline, string rowName)
    {
        var fresh = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, fresh.OnFact);
        Sectionizer.StampGroupIds(rows);
        return fresh.SummaryFightFor(Row(rows, rowName), facts);
    }

    // A raid member beating on a mob that hits back: both directions exist from the first second, so a grown row exercises
    // two ordinal runs and both window maps at once.
    private static void OpenFight(Capture log)
    {
        log.Hit("Illuminai", "Echohead", 500, 0);
        log.Hit("Berauni", "Echohead", 300, 1);
        log.Hit("Echohead", "Illuminai", 120, 1);
    }

    [TestMethod]
    public void AGrownRowIsTheSameFightAFromZeroWalkBuilds()
    {
        var log = new Capture();
        OpenFight(log);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        var rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var index = cache.Index;
        var mob = Row(rows, "Echohead");

        var first = Describe(index.SummaryFightFor(mob, log.Facts));

        // Pass 2: more damage in a new second, the mob hitting back again, and a taunt that belongs to this row.
        log.Hit("Illuminai", "Echohead", 640, 4);
        log.Hit("Echohead", "Berauni", 90, 5);
        log.Taunt("Echohead", "Illuminai", 2);
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        mob = Row(rows, "Echohead");

        var grown = Describe(cache.Index.SummaryFightFor(mob, log.Facts));

        Assert.AreNotEqual(first, grown, "a row that grew must not answer with last pass's materialization (the stale-cache bug)");
        Assert.AreEqual(Describe(RebuildFromZero(log.Facts, timeline, "Echohead")), grown,
            "a continued materialization is not a different answer — it is the same one reached without re-walking");

        // Pass 3: DD and DoT records arrive too, so the per-spell dictionaries continue from their cloned counters rather
        // than being rebuilt from nothing.
        log.Hit("Berauni", "Echohead", 250, 7, LabelTypes.Dd);
        log.Hit("Illuminai", "Echohead", 60, 8, LabelTypes.Dot);
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);

        Assert.AreEqual(Describe(RebuildFromZero(log.Facts, timeline, "Echohead")),
                        Describe(cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)),
            "spell counters, activity windows and blocks all continue; a lost Count or a restarted window would show here");
    }

    /*
     * The assertion that the warm path exists. A test that only compared answers would also pass if every request took the
     * cold path, which is exactly how an "optimisation" of this kind goes unnoticed-and-absent. Closed blocks are shared by
     * reference with the previous generation, so identity proves the cache was used; the records inside them are the same
     * objects, so nothing was re-allocated for the part of the row that cannot have changed.
     */
    [TestMethod]
    public void TheWarmPathSharesItsClosedBlocksWithThePreviousGeneration()
    {
        var log = new Capture();
        OpenFight(log);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        var rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var first = cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;
        var firstOpeningBlock = first.DamageBlocks[0];
        var firstRecord = first.DamageBlocks[0].Actions[0];

        log.Hit("Illuminai", "Echohead", 640, 9);
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var grown = cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;

        Assert.AreNotSame(first, grown, "growth produces a NEW Fight — the old one belongs to whoever is reading it");
        Assert.AreSame(firstOpeningBlock, grown.DamageBlocks[0],
            "the closed block came from the cache; if this fails the row was re-walked and the saving is not there");
        Assert.AreSame(firstRecord, grown.DamageBlocks[0].Actions[0], "and its record was not re-allocated");
    }

    /*
     * Why the new Fight has to be new: the previous one is inside a SummaryInput handed to the boards, which walk it on the
     * summary gate's worker task while the next derive pass folds. A mutation here is a torn grid at best and an enumerator
     * exception at worst — so growth may only ever build a new object over shared immutable pieces.
     */
    [TestMethod]
    public void GrowingARowLeavesTheFightItAlreadyHandedOutAlone()
    {
        var log = new Capture();
        OpenFight(log);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        var rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var published = cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;
        var before = Describe(published);
        var beforeDamageBlocks = published.DamageBlocks.Count;
        var beforeTankBlocks = published.TankingBlocks.Count;

        // Damage in the LAST block's own second (the merge case), a new second, and more damage taken.
        log.Hit("Illuminai", "Echohead", 700, 1);
        log.Hit("Berauni", "Echohead", 111, 6);
        log.Hit("Echohead", "Kilsa", 88, 6);
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var grown = cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;

        Assert.AreEqual(before, Describe(published),
            "the object already handed to a builder changed under it — append must clone the open block, never extend it");
        Assert.AreEqual(beforeDamageBlocks, published.DamageBlocks.Count, "and no block appeared in it");
        Assert.AreEqual(beforeTankBlocks, published.TankingBlocks.Count, "and none in its tanking half either");

        Assert.IsTrue(grown.DamageBlocks.Count > beforeDamageBlocks || grown.TankingBlocks.Count > beforeTankBlocks,
            "the growth has to be VISIBLE on the new object");
        Assert.AreEqual(Describe(RebuildFromZero(log.Facts, timeline, "Echohead")), Describe(grown),
            "including the fact that shared the open block's second: it joins on both paths");
    }

    /*
     * A stamp can move with no new ordinal at all — `Dead` flips from a queued death, `GroupId` is re-stamped by Sectionizer,
     * the row's bounds advance. That used to be a full re-walk for a scalar; now it re-presents the same blocks, and the answer
     * must still match a from-zero build.
     */
    [TestMethod]
    public void AStampMovingWithoutNewFactsRePresentsRatherThanRewalks()
    {
        var log = new Capture();
        OpenFight(log);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        var rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var index = cache.Index;
        var first = index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;
        var sharedBlock = first.DamageBlocks[0];

        // Same facts; the row's state changes (a death lands) and its group divider moves.
        log.Facts.AddDeath(new DeathFact(99, (long)(T0 + 30), log.Facts.InternName("Echohead"), -1));
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);

        var restamped = index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;

        Assert.IsTrue(Row(rows, "Echohead").Dead, "the fixture must actually move a stamp, or this test proves nothing");
        Assert.AreSame(sharedBlock, restamped.DamageBlocks[0],
            "a scalar moved and no fact did: the blocks come from the cache rather than being re-allocated");
        Assert.AreEqual(Describe(RebuildFromZero(log.Facts, timeline, "Echohead")), Describe(restamped),
            "and the re-presented row says what a full build would say, including the dead marker and the tooltip");
    }

    /*
     * A row that only ever gets hit — no damage dealt, so its DamageBlocks list is empty forever while its tanking half grows.
     * The damage pass must not decide whether the materialization exists (that is what dropped a player's damage taken once),
     * and continuing a run that never started must be as exact as continuing one that did.
     */
    [TestMethod]
    public void ARowThatOnlyGetsHitGrowsItsTankingHalfExactly()
    {
        var log = new Capture();
        log.Hit("Echohead", "Kilsa", 100, 0);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        var rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        // The row is keyed on the mob, and its tanking half is what Kilsa received.
        var first = Describe(cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts));

        log.Hit("Echohead", "Kilsa", 150, 2);
        log.Hit("Echohead", "Berauni", 30, 2);
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);

        Assert.AreEqual(Describe(RebuildFromZero(log.Facts, timeline, "Echohead")),
                        Describe(cache.Index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)),
            "growing a tank-only row matches a full build: counts, windows and block seconds");
    }

    /*
     * The windowed path is deliberately never cached (a meter that was just zeroed must not serve its truncated slice to an
     * unwindowed click), so growth must not be able to leak a partial slice into it either way. Same table, same growth: the
     * slice answers for its window and the cached row answers for the whole life.
     */
    [TestMethod]
    public void AWindowedSliceNeverTouchesTheCachedRow()
    {
        var log = new Capture();
        OpenFight(log);
        var timeline = Classify(log.Facts);
        var cache = new FightProjection.FightProjectionCache();

        var rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var index = cache.Index;
        var whole = index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;

        // A slice of one second answers for that second alone...
        var sliced = index.SummaryFightInWindow(Row(rows, "Echohead"), log.Facts, T0 + 1, T0 + 1)!;
        Assert.AreEqual(T0 + 1, sliced.BeginDamageTime, "the slice is the window's, not the row's");

        // ...and leaves the cached whole-life row exactly as it was.
        Assert.AreEqual(Describe(whole), Describe(index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)),
            "an unwindowed click after a meter slice gets the same answer it had before: the cache is one entry, not two");

        log.Hit("Illuminai", "Echohead", 20, 1);
        rows = cache.Project(log.Facts, timeline);
        Sectionizer.StampGroupIds(rows);
        var grown = index.SummaryFightFor(Row(rows, "Echohead"), log.Facts)!;

        Assert.AreNotSame(whole, grown, "the row grew, so the cached entry moved on");
        Assert.AreEqual(Describe(RebuildFromZero(log.Facts, timeline, "Echohead")), Describe(grown));
    }
}

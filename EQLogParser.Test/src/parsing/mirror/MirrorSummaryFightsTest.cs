using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * The seam that lets the existing damage summary render a derived selection: MirrorDamageIndex learns
 * from FightProjection which facts belong to which row and in which direction, and MirrorSummaryFights
 * rebuilds them as DamageRecord objects inside Fight.DamageBlocks.
 *
 * What these tests hold:
 *   - the index holds ONLY damage dealt TO the row's owner (that is what legacy puts in DamageBlocks;
 *     a mob hitting a raider belongs to the tanking side, and mixing them would credit a boss with its
 *     own damage output);
 *   - materialized records are their facts, field for field — nothing re-spelled, nothing re-timed;
 *   - blocks are maximal runs of one second, which is FightManager.AddAction's grouping rule, because the
 *     builder merges same-BeginTime blocks across fights and a split run would double-count an instant;
 *   - misses stay in the blocks (legacy counts them there), so block count is not hit count;
 *   - the real DamageStatsBuilder, given nothing but materialized fights, reports the same total the
 *     derived row shows. That is the whole point: one board, two engines, comparable numbers.
 */
[TestClass]
public class MirrorSummaryFightsTest
{
    private const double T0 = 1_000;
    private static string MiniFightPath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "mini-fight.txt");

    // Same fixture shape FightProjectionTest uses: facts by name/amount/second/label, table-ordinal order.
    private static DamageFactTable BuildFacts(params (string Atk, string Def, uint Dmg, double T, byte Label)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t, label) in rows)
        {
            var a = facts.InternName(atk);
            var d = facts.InternName(def);
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: dmg, typeId: label,
              flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    // The pass MirrorSession runs: rules over the facts, then the projection with its index sink.
    private static (List<DerivedFight> Fights, MirrorDamageIndex Index, DamageFactTable Facts) Derive(DamageFactTable facts, EntityTimeline? timeline = null)
    {
        var line = timeline ?? new EntityTimeline();
        ClassificationRules.Apply(facts, line);

        var index = new MirrorDamageIndex();
        var fights = FightProjection.Build(facts, line, index.OnFact);
        Sectionizer.StampGroupIds(fights);
        return (fights, index, facts);
    }

    private static DerivedFight Row(List<DerivedFight> rows, string name)
        => rows.First(r => r.Name == name);

    private static List<DamageRecord> Records(Fight fight)
        => fight.DamageBlocks.SelectMany(b => b.Actions).Cast<DamageRecord>().ToList();

    // ---- what the index is allowed to hold ----

    [TestMethod]
    public void TheIndexHoldsOnlyDamageAimedAtTheRowOwner()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 500, 0, LabelTypes.Melee),   // verified player hits the mob
            ("Echohead", "Illuminai", 120, 1, LabelTypes.Melee),   // the mob hitting back: tanking side, never here
            ("Grimling", "Echohead", 70, 2, LabelTypes.Dot));      // unclassified attacker: still aimed at the mob

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var echo = Row(rows, "Echohead");

        // Both hits ON Echohead, neither of its own output. The direction is the row's own split, so an
        // unclassified attacker counts here exactly as FightManager's blocks count it — while its per-player
        // credit stays withheld (see TheIndexOfASplitExchangeSumsToTheRowsDamageToOwner).
        CollectionAssert.AreEqual(new List<int> { 0, 2 }, index.DamageOrdinalsFor(echo).ToList(),
            "damage blocks hold what is aimed at the row's name, not what a registry happened to recognise");
        Assert.IsTrue(index.HasDamage(echo), "a row with damage aimed at it must be selectable");

        // Every ordinal the index hands out must name this row's owner as its defender — the direction
        // rule stated as a fact about the table, not as a comment.
        foreach (var ordinal in index.DamageOrdinalsFor(echo))
        {
            Assert.AreEqual("Echohead", table.NameOf(table.Facts[ordinal].DefIdx));
        }
    }

    [TestMethod]
    public void TheIndexOfASplitExchangeSumsToTheRowsDamageToOwner()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 500, 0, LabelTypes.Melee),
            ("Illuminai", "Echohead", 0, 1, LabelTypes.Miss),      // a miss: in the blocks, worth nothing
            ("Echohead", "Illuminai", 120, 2, LabelTypes.Melee),
            ("Bystander", "Echohead", 999, 3, LabelTypes.Melee));  // never credited while unclassified

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var echo = Row(rows, "Echohead");

        var fromFacts = index.DamageOrdinalsFor(echo)
            .Where(o => LabelTypes.IsHit(table.Facts[o].TypeId))
            .Sum(o => (long)table.Facts[o].Total);

        Assert.AreEqual(echo.DamageToOwner, fromFacts, "the row's number and the facts behind its blocks must be one number");
        /*
         * The row's Hits column counts the whole exchange, both directions (3: Illuminai's hit, Echohead's
         * answer, Bystander's); the materialized fight counts only what its blocks hold, which is legacy's own
         * meaning for DamageHits (2). Different numbers on purpose — which is why the damage summary has to be
         * fed blocks and not the row's roll-up.
         */
        Assert.AreEqual(3u, echo.DamageHits);
        Assert.AreEqual(2u, index.SummaryFightFor(echo, table).DamageHits);

        // Three ordinals for one credited hit: the miss (legacy keeps misses in the blocks, and so does the
        // summary's to-hit column) and Bystander's 999, which lands in the blocks while being nobody's credit.
        Assert.AreEqual(3, index.DamageOrdinalsFor(echo).Count);
        Assert.IsFalse(echo.PlayerRollup.ContainsKey("Bystander"),
            "an unclassified attacker may damage the row but must not appear as a raider's damage");
        Assert.IsTrue(echo.PlayerRollup.TryGetValue("Illuminai", out var credit) && credit.Damage == 500);
    }

    [TestMethod]
    public void TheSinkSpeaksOncePerFactThatOwnsARow()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 500, 0, LabelTypes.Melee),
            ("Illuminai", "Illuminai", 10, 1, LabelTypes.Melee),   // self damage: dropped before any row
            ("Illuminai", "Bystander", 10, 2, LabelTypes.Melee));  // friendly fire: dropped

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bystander", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        ClassificationRules.Apply(facts, timeline);

        var calls = new List<string>();
        var rows = FightProjection.Build(facts, timeline,
            (fact, ordinal, owner, credited) => calls.Add($"{ordinal}:{owner.Name}:{credited}"));

        CollectionAssert.AreEqual(new List<string> { "0:Echohead:True" }, calls,
            "one call per row-owning fact, and only for facts that own a row");
        Assert.AreEqual(1, rows.Count);
    }

    // ---- materialization: records are their facts ----

    [TestMethod]
    public void MaterializedRecordsAreTheirOwnFacts()
    {
        var facts = new DamageFactTable(64);
        var a = facts.InternName("Illuminai");
        var d = facts.InternName("Echohead");
        var subFire = (ushort)facts.InternSubtype("Greater Summoning: Fire");
        facts.AddFact(new DamageFact(0, (long)T0, a, d, total: 500, typeId: LabelTypes.Dd,
          flags: 0, modMask: 0, subIdx: subFire));
        facts.AddFact(new DamageFact(1, (long)T0 + 1, a, d, total: 250, typeId: LabelTypes.Dot,
          flags: DamageFact.FlagAttackerIsSpell, modMask: 0, subIdx: ushort.MaxValue));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var summary = index.SummaryFightFor(Row(rows, "Echohead"), table);

        var records = Records(summary);
        Assert.AreEqual(2, records.Count);

        for (var i = 0; i < records.Count; i++)
        {
            var fact = table.Facts[i];
            var record = records[i];
            Assert.AreEqual(table.NameOf(fact.AtkIdx), record.Attacker);
            Assert.AreEqual(table.NameOf(fact.DefIdx), record.Defender);
            Assert.AreEqual(fact.Total, record.Total);
            Assert.AreEqual(LabelTypes.LabelOf(fact.TypeId), record.Type, "the label word is the same interned literal the parsers write");
            // A fact whose line carried no modifier text gets the type word as its subtype: the summary's melee
            // counters key a ConcurrentDictionary on it and throw on a null key (MirrorSummaryFights.SubTypeOf).
            Assert.AreEqual(table.SubtypeOf(fact.SubIdx) ?? LabelTypes.LabelOf(fact.TypeId), record.SubType);
            Assert.AreEqual(fact.AttackerIsSpell, record.AttackerIsSpell);
        }

        Assert.AreEqual("Echohead", summary.Name);
        Assert.AreEqual(Row(rows, "Echohead").Id, summary.Id);
        Assert.AreEqual(500 + 250, summary.DamageTotal);
        Assert.AreEqual(T0, summary.BeginDamageTime);
        Assert.AreEqual(T0 + 1, summary.LastDamageTime);
    }

    [TestMethod]
    public void BlocksAreMaximalRunsOfOneSecond()
    {
        // Three raiders, one of them twice inside a second: legacy would hand the builder two blocks here,
        // not four — a run split across blocks is the same instant counted twice.
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 10, 0, LabelTypes.Melee),
            ("Baruk", "Echohead", 20, 0, LabelTypes.Melee),
            ("Illuminai", "Echohead", 30, 1, LabelTypes.Melee),
            ("Baruk", "Echohead", 40, 3, LabelTypes.Melee),
            ("Illuminai", "Echohead", 50, 3, LabelTypes.Melee),
            ("Illuminai", "Echohead", 60, 3, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Baruk", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var summary = index.SummaryFightFor(Row(rows, "Echohead"), table);

        CollectionAssert.AreEqual(new List<double> { T0, T0 + 1, T0 + 3 },
            summary.DamageBlocks.Select(b => b.BeginTime).ToList());
        CollectionAssert.AreEqual(new List<int> { 2, 1, 3 },
            summary.DamageBlocks.Select(b => b.Actions.Count).ToList());
    }

    [TestMethod]
    public void PlayerTimeSegmentsCoverEveryRaiderInTheBlocks()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 10, 0, LabelTypes.Melee),
            ("Baruk", "Echohead", 20, 2, LabelTypes.Dd),
            ("Baruk", "Echohead", 30, 5, LabelTypes.Dd),
            ("Echohead", "Illuminai", 40, 6, LabelTypes.Melee));   // not credited: must not open a segment

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Baruk", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var summary = index.SummaryFightFor(Row(rows, "Echohead"), table);

        // Melee carries no modifier text on its line, so the sub-segment key falls back to the type word
        // (MirrorDamageIndex) rather than handing the dictionary a null key.
        CollectionAssert.Contains(summary.DamageSubSegments["Illuminai"].Keys.ToList(), Labels.Melee);

        CollectionAssert.AreEqual(new[] { "Baruk", "Illuminai" }, summary.DamageSegments.Keys.OrderBy(k => k).ToList(),
            "activity seconds are per attacker in the blocks — without them the summary would divide by the raid window");
        Assert.IsFalse(summary.DamageSegments.ContainsKey("Echohead"));

        foreach (var segment in summary.DamageSegments.Values)
        {
            Assert.IsTrue(segment.BeginTime >= summary.BeginDamageTime && segment.EndTime <= summary.LastDamageTime);
        }

        // Sub-segments keep the per-spell breakdown, keyed by the same helper FightManager uses: a DD and a
        // DoT sharing a spell name stay two entries because that key carries the type.
        Assert.IsTrue(summary.DamageSubSegments.TryGetValue("Baruk", out var barukSubs), "no per-spell activity for Baruk");
        // Baruk's DD carries no spell name in this fixture, so its subtype is the type word and the internal
        // key doubles ("Direct Damage=Direct Damage"). That doubling is invisible: the grids bind Name/Type,
        // never Key — and one unlabeled kind still gets exactly one breakdown row instead of none.
        CollectionAssert.Contains(barukSubs.Keys.ToList(), StatsUtil.CreateRecordKey(Labels.Dd, Labels.Dd));
        Assert.IsFalse(summary.DamageSubSegments.ContainsKey("Echohead"));
    }

    // ---- selection ----

    [TestMethod]
    public void ARowNothingIsAimedAtIsCountedNotSilentlyDropped()
    {
        // A classified mob hitting a classified raider: the row exists (that is the mirror's identity-aware
        // list doing its job) and every fact in it points the other way, so a damage summary over it has
        // nothing to show. The count is what stops the grid from looking like it ignored the click.
        var facts = BuildFacts(("Echohead", "Illuminai", 100, 0, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.IsFalse(index.HasDamage(rows[0]));

        var input = MirrorSummaryFights.Build(rows, index, table);

        Assert.AreEqual(0, input.Fights.Count);
        Assert.AreEqual(1, input.WithoutDamage);
        Assert.AreEqual(0, input.AllRanges.TimeSegments.Count);
    }

    [TestMethod]
    public void ASelectionReportsTheWallClockWindowOfWhatWasSelected()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 10, 0, LabelTypes.Melee),
            ("Illuminai", "Grimling", 20, 40, LabelTypes.Melee));   // second fight, same raiders

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");
        timeline.SetIdentity("Grimling", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        Assert.AreEqual(2, rows.Count);

        var input = MirrorSummaryFights.Build(rows, index, table);

        Assert.AreEqual(2, input.Fights.Count);
        Assert.AreEqual(0, input.WithoutDamage);

        // One span per selected fight, handed to TimeRange (which bridges runs closer than its own 6-second
        // rule) — the shape FightTable builds for a legacy selection, so DPS divides the same way.
        CollectionAssert.AreEqual(new List<double> { T0, T0 + 40 }, input.AllRanges.TimeSegments.Select(s => s.BeginTime).ToList());
    }

    [TestMethod]
    public void ASelectionOfOneFightIsOneWindowAndNothingElse()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 10, 0, LabelTypes.Melee),
            ("Illuminai", "Grimling", 20, 400, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");
        timeline.SetIdentity("Grimling", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var input = MirrorSummaryFights.Build([Row(rows, "Echohead")], index, table);

        Assert.AreEqual(1, input.Fights.Count);
        Assert.AreEqual(1, input.AllRanges.TimeSegments.Count);
        Assert.AreEqual(T0, input.AllRanges.TimeSegments[0].BeginTime);
    }

    // ---- the point of all of it: the real summary over derived fights ----

    [TestMethod]
    public void TheDamageBuilderReportsWhatTheDerivedRowShows()
    {
        PipelineHarness.EnsureDataStore();

        var facts = BuildFacts(
            ("Illuminai", "Echohead", 500, 0, LabelTypes.Melee),
            ("Baruk", "Echohead", 300, 1, LabelTypes.Dd),
            ("Echohead", "Illuminai", 900, 2, LabelTypes.Melee));   // tanking side: not in this total

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Baruk", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var input = MirrorSummaryFights.Build(rows, index, table);

        /*
         * No damage-filter dial is pinned here and none needs it: a materialized record carries ModifiersMask 0,
         * and each of the six filters drops a record only when its modifier bit is set. Nothing can be excluded
         * from a derived selection — which is also the honest limit of this comparison (MirrorSummaryFights).
         */
        StatsGenerationEvent? done = null;
        DamageStatsBuilder.Instance.EventsGenerationStatus += OnStatus;
        try
        {
            GenerateStatsOptions options = new();
            options.Npcs.AddRange(input.Fights);
            options.AllRanges = input.AllRanges;
            options.MinSeconds = 0;

            DamageStatsBuilder.Instance.BuildTotalStats(options);
        }
        finally
        {
            DamageStatsBuilder.Instance.EventsGenerationStatus -= OnStatus;
        }

        Assert.IsNotNull(done, "the builder never reported back — it did not accept the derived fights");
        Assert.AreEqual("COMPLETED", done.State);
        Assert.AreEqual(800, done.CombinedStats.RaidStats.Total,
            "the board must show the damage the derived row counted, and none of the mob's own output");

        var names = done.CombinedStats.StatsList.Select(s => s.Name).ToList();
        CollectionAssert.Contains(names, "Illuminai");
        CollectionAssert.Contains(names, "Baruk");
        Assert.AreEqual(500, done.CombinedStats.StatsList.First(s => s.Name == "Illuminai").Total);

        void OnStatus(StatsGenerationEvent e)
        {
            if (e.State is "COMPLETED" or "NONPC" or "NODATA") done = e;
        }
    }

    [TestMethod]
    public void ARealLogMaterializesEveryFightItShows()
    {
        Assert.IsTrue(File.Exists(MiniFightPath), $"missing fixture: {MiniFightPath}");

        var run = PipelineHarness.RunFileWithMirror(MiniFightPath);

        // The same pass order MirrorSession runs, so this walks the production path and not a shortcut.
        ClassificationRules.Apply(run.Facts, run.Timeline);

        var index = new MirrorDamageIndex();
        var fights = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(fights);

        Assert.IsTrue(fights.Count > 0, "the fixture derives no fights");

        foreach (var fight in fights)
        {
            var fromFacts = index.DamageOrdinalsFor(fight)
                .Where(o => LabelTypes.IsHit(run.Facts.Facts[o].TypeId))
                .Sum(o => (long)run.Facts.Facts[o].Total);
            Assert.AreEqual(fight.DamageToOwner, fromFacts, $"{fight.Name}: index and row disagree");

            if (!index.HasDamage(fight)) continue;

            var summary = index.SummaryFightFor(fight, run.Facts);
            Assert.AreEqual(fight.DamageToOwner, summary.DamageTotal, $"{fight.Name}: blocks and row disagree");
            Assert.IsTrue(summary.DamageBlocks.Count > 0, $"{fight.Name}: no damage blocks for a selectable row");
            Assert.IsTrue(Records(summary).All(r => r.Defender == fight.Name), $"{fight.Name}: a block holds something aimed elsewhere");
            Assert.IsTrue(summary.DamageSegments.Keys.All(k => Records(summary).Any(r => r.Attacker == k)),
                $"{fight.Name}: an activity window for someone who never swung at it");
        }
    }

    /*
     * The claim the mirror exists to test, at the level of a board: same log, same fight, two engines. The
     * legacy list's Fight objects and the derived rows each feed DamageStatsBuilder separately and the two boards
     * are compared. Measured on this fixture (mini-fight.txt), with all six modifier filters on:
     *
     *   raid total          legacy 604,857   derived 722,926   (+19.5 %)
     *   Rune / Ammeren / Triumph / Soell / Puksu / Kuma    identical to the point, on both boards
     *   Sancus +Pets 114,811 and Jazrakhan +Pets 3,258     derived only
     *
     * The whole difference is two pets. PlayerRegistry never learned them from this log ("Sancus`s pet" answers
     * IsPetOrPlayerOrMerc false, IsPossiblePlayerName false, GetPlayerFromPet null at the end of the run), so
     * FightManager's IsValidAttack refused every one of their records and their damage reached NO fight at all —
     * not the boss's row, not a pet row. The line itself says whose pet it is, which is the evidence R5 stores on
     * the fact and ClassificationRules.OwnerInName cuts out of the name; the derived record carries it in
     * AttackerOwner, so the builder folds both pets under their owners. Same log, same fight, one number, and the
     * gap is a rule difference rather than arithmetic — which is precisely the thing worth being able to see.
     *
     * All six filters are switched on because a materialized record carries no modifier bits: with any of them off
     * the two sides differ for a second, unrelated and documented reason (MirrorSummaryFights), and this test would
     * be measuring that gap instead of this one.
     */
    [TestMethod]
    public void TheMiniFightSummarizesToTheSameNumbersFromEitherList()
    {
        Assert.IsTrue(File.Exists(MiniFightPath), $"missing fixture: {MiniFightPath}");
        PipelineHarness.EnsureDataStore();

        var run = PipelineHarness.RunFileWithMirror(MiniFightPath);

        ClassificationRules.Apply(run.Facts, run.Timeline);
        var index = new MirrorDamageIndex();
        var derived = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(derived);

        // Pair by name — that the names and boundaries agree at all is MirrorComparisonTest's claim; here it
        // only lets each side hand the builder its own Fight objects.
        var rows = derived.Where(row => index.HasDamage(row) && run.Fights.Any(f => f.Name == row.Name)).ToList();
        Assert.IsTrue(rows.Count > 0, "no fight of this fixture appears on both sides");

        var names = rows.Select(r => r.Name).ToHashSet();
        var legacy = run.Fights.Where(f => names.Contains(f.Name)).ToList();

        var fromMirror = MirrorSummaryFights.Build(rows, index, run.Facts);
        var fromManager = new TimeRange();
        foreach (var fight in legacy) fromManager.Add(new TimeSegment(fight.BeginTime, fight.LastTime));

        var prior = (Assassinate: AppSettings.IsAssassinateDamageEnabled, Bane: AppSettings.IsBaneDamageEnabled,
          DamageShield: AppSettings.IsDamageShieldDamageEnabled, FinishingBlow: AppSettings.IsFinishingBlowDamageEnabled,
          Headshot: AppSettings.IsHeadshotDamageEnabled, SlayUndead: AppSettings.IsSlayUndeadDamageEnabled);

        CombinedStats mirrorStats, managerStats;
        try
        {
            AppSettings.IsAssassinateDamageEnabled = AppSettings.IsBaneDamageEnabled = AppSettings.IsDamageShieldDamageEnabled
              = AppSettings.IsFinishingBlowDamageEnabled = AppSettings.IsHeadshotDamageEnabled = AppSettings.IsSlayUndeadDamageEnabled = true;

            mirrorStats = Board(fromMirror.Fights, fromMirror.AllRanges);
            managerStats = Board(legacy, fromManager);
        }
        finally
        {
            AppSettings.IsAssassinateDamageEnabled = prior.Assassinate;
            AppSettings.IsBaneDamageEnabled = prior.Bane;
            AppSettings.IsDamageShieldDamageEnabled = prior.DamageShield;
            AppSettings.IsFinishingBlowDamageEnabled = prior.FinishingBlow;
            AppSettings.IsHeadshotDamageEnabled = prior.Headshot;
            AppSettings.IsSlayUndeadDamageEnabled = prior.SlayUndead;
        }

        // (1) Every raider the legacy board names must read the same on the derived board. Not one point of
        // what the old engine managed to place may move.
        foreach (var entry in managerStats.StatsList)
        {
            var other = mirrorStats.StatsList.FirstOrDefault(s => s.Name == entry.Name);
            Assert.IsNotNull(other, $"{entry.Name}: on the legacy board only");
            Assert.AreEqual(entry.Total, other.Total, $"{entry.Name}: per-raider total differs between the two lists");
        }

        // (2) The derived board may add only line-owned pets, folded under their owners.
        var extra = mirrorStats.StatsList.Where(s => !managerStats.StatsList.Any(m => m.Name == s.Name)).ToList();
        Assert.IsTrue(extra.Count > 0, "this fixture is supposed to show the difference the mirror exists for");
        foreach (var entry in extra)
        {
            Assert.IsTrue(entry.Name.EndsWith(" +Pets", StringComparison.Ordinal),
                $"{entry.Name}: the derived board added a raider that is not a line-owned pet");
        }

        // (3) The delta IS those pets, computed from the facts rather than from either board.
        var petDamage = rows.Sum(row => index.DamageOrdinalsFor(row)
            .Where(o => run.Facts.Facts[o].OwnerInLine && LabelTypes.IsHit(run.Facts.Facts[o].TypeId))
            .Sum(o => (long)run.Facts.Facts[o].Total));

        Assert.AreEqual(petDamage, mirrorStats.RaidStats.Total - managerStats.RaidStats.Total,
            "the two boards differ by something other than the pets legacy's registry never mapped");
        Assert.AreEqual(extra.Sum(s => (long)s.Total), petDamage, "the pet rows and the fact table disagree");
    }

    // One board, built the way MainWindow builds it: options in, completion event out.
    private static CombinedStats Board(IReadOnlyList<Fight> fights, TimeRange allRanges)
    {
        StatsGenerationEvent? done = null;
        void OnStatus(StatsGenerationEvent e)
        {
            if (e.State is "COMPLETED" or "NONPC" or "NODATA") done = e;
        }

        DamageStatsBuilder.Instance.EventsGenerationStatus += OnStatus;
        try
        {
            GenerateStatsOptions options = new();
            options.Npcs.AddRange(fights);
            options.AllRanges = allRanges;
            options.MinSeconds = 0;

            DamageStatsBuilder.Instance.BuildTotalStats(options);
        }
        finally
        {
            DamageStatsBuilder.Instance.EventsGenerationStatus -= OnStatus;
        }

        Assert.IsNotNull(done, "the builder never reported back");
        Assert.AreEqual("COMPLETED", done.State, "the builder refused the selection");
        return done.CombinedStats;
    }

    // ---- grouping: the number a stats run reads as GroupId ----

    [TestMethod]
    public void AGroupIdMatchesTheDividerAboveIt()
    {
        var first = new DerivedFight { Name = "First", BeginTime = 100, LastTime = 120 };
        var sameSection = new DerivedFight { Name = "Second", BeginTime = 200, LastTime = 210 };
        var afterGap = new DerivedFight { Name = "Third", BeginTime = 100 + Sectionizer.DefaultGroupTimeout * 3, LastTime = 500 };

        var fights = new List<DerivedFight> { first, sameSection, afterGap };
        var sectionCount = Sectionizer.StampGroupIds(fights);

        Assert.AreEqual(2, sectionCount);
        Assert.AreEqual(1, first.GroupId);
        Assert.AreEqual(1, sameSection.GroupId);
        Assert.AreEqual(2, afterGap.GroupId);

        // The stamped numbers must change exactly where the grid draws a divider — one rule, two consumers.
        var dividers = Sectionizer.ToDisplayRows(fights).Count(r => r.IsDivider);
        Assert.AreEqual(sectionCount - 1, dividers);
    }

    [TestMethod]
    public void MaterializingIsCachedPerRow()
    {
        var facts = BuildFacts(("Illuminai", "Echohead", 500, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var echo = Row(rows, "Echohead");

        Assert.AreSame(index.SummaryFightFor(echo, table), index.SummaryFightFor(echo, table),
            "a row clicked twice must not allocate its records twice");
    }
}

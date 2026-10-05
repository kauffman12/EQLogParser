using EQLogParser;

namespace EQLogParser;

/*
 * The seam that lets the existing damage summary render a derived selection: FightFactIndex learns
 * from FightProjection which facts belong to which row and in which direction, and FightSummarySource
 * rebuilds them as DamageRecord objects inside Fight.DamageBlocks.
 *
 * What these tests hold:
 *   - the index splits each row's facts by direction: damage dealt TO the row's owner lands on the damage
 *     side (that is what legacy puts in DamageBlocks) and everything else on the tanking side, and NO fact is
 *     on both — a boss must never be credited with its own damage output, and a selected set of rows must
 *     neither lose the hits its raiders took nor count one twice;
 *   - materialized records are their facts, field for field — nothing re-spelled, nothing re-timed;
 *   - blocks are maximal runs of one second, which is FightManager.AddAction's grouping rule, because the
 *     builder merges same-BeginTime blocks across fights and a split run would double-count an instant;
 *   - misses stay in the blocks (legacy counts them there), so block count is not hit count;
 *   - the real DamageStatsBuilder, given nothing but materialized fights, reports the same total the
 *     derived row shows. That is the whole point: one board, two engines, comparable numbers.
 */
[TestClass]
public class FightSummarySourceTest
{
    private const double T0 = 1_000;
    private static string MiniFightPath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "mini-fight.txt");

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

    // The pass DeriveEngine runs: rules over the facts, then the projection with its index sink.
    private static (List<DerivedFight> Fights, FightFactIndex Index, DamageFactTable Facts) Derive(DamageFactTable facts, EntityTimeline? timeline = null)
    {
        var line = timeline ?? new EntityTimeline();
        ClassificationRules.Apply(facts, line);

        // The index carries the classification, exactly as FightProjectionCache does in the application: the materialized
        // record asks that timeline whether a caster-less spell line hit one of ours (FightSummarySource.RecordFrom).
        var index = new FightFactIndex(line);
        var fights = FightProjection.Build(facts, line, index.OnFact);
        Sectionizer.StampGroupIds(fights);
        return (fights, index, facts);
    }

    private static DerivedFight Row(List<DerivedFight> rows, string name)
        => rows.First(r => r.Name == name);

    private static List<DamageRecord> Records(Fight fight)
        => fight.DamageBlocks.SelectMany(b => b.Actions).Cast<DamageRecord>().ToList();

    private static List<DamageRecord> TankRecords(Fight fight)
        => fight.TankingBlocks.SelectMany(b => b.Actions).Cast<DamageRecord>().ToList();

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
            (fact, ordinal, owner, target) => calls.Add($"{ordinal}:{owner.Name}:{target}"));

        CollectionAssert.AreEqual(new List<string> { $"0:Echohead:{FightProjection.FactTarget.AtOwner}" }, calls,
            "one call per row-owning fact, and only for facts that own a row");
        Assert.AreEqual(1, rows.Count);
    }

    /*
     * The tanking half means "damage one of our people received", which is what the tank report lists — it groups by
     * record.Defender. Three shapes that all used to be filed as tanking because they merely pointed away from an
     * NPC row, and only one of them survives the question:
     *
     *   a mob hitting a raider            → RaidSide, on the row the exchange opened
     *   a mob hitting somebody's pet      → Neither. Legacy's unfiltered board carries 428,146,449 of it across 75 pet
     *                                       names on Incogitable (and 4.82 B on NPC names); keeping it made the raid's
     *                                       damage-taken unauditable.
     *   a mob hitting another mob/corpse  → Neither, same reason.
     *
     * A charmed raider is the fourth shape and deliberately not RaidSide: while the window holds, identity says Npc,
     * she is fighting against us, and the hits landing on her belong to that encounter instead of to her meter.
     */
    [TestMethod]
    public void TheTankingHalfIsDamageOurPeopleReceived()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 500, 0, LabelTypes.Melee),   // raid on the mob: AtOwner
            ("Echohead", "Illuminai", 120, 1, LabelTypes.Melee),   // mob on a raider: RaidSide
            ("Echohead", "Swarmik`s pet", 90, 2, LabelTypes.Melee),   // mob on a pet: nobody's damage taken
            ("Echohead", "A gnawed corpse", 70, 3, LabelTypes.Melee), // mob on a mob: same
            ("Illuminai", "Mysteryhost", 30, 4, LabelTypes.Melee));   // raider on an UNCLASSIFIED name

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");
        timeline.SetIdentity("Swarmik", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Swarmik`s pet", IdentityKind.Pet, RuleStrength.Strong, "R5-summon");
        timeline.AddAffiliation(AffiliationKind.PetOfPlayer, "Swarmik`s pet", T0 - 10, T0 + 100, RuleStrength.Strong, "R5-summon", "Swarmik");

        var (rows, index, table) = Derive(facts, timeline);
        var echo = Row(rows, "Echohead");

        var tanking = index.TankingOrdinalsFor(echo)
            .Select(o => $"{table.Facts[o].Total}->{table.NameOf(table.Facts[o].DefIdx)}")
            .ToList();
        CollectionAssert.AreEqual(new List<string> { "120->Illuminai" }, tanking,
            "the tanking half carries the hits that landed on a person, and nothing else");

        Assert.AreEqual(120L, index.SummaryFightFor(echo, table).TankTotal, "the row's damage-taken is the same one number as its blocks");
        // One unrouted, not two: mob-on-mob never reaches a row at all (the projection drops it before the sink),
        // while a mob hitting OUR pet does — it is part of the encounter — and is then refused by both boards.
        Assert.AreEqual(1L, index.UnroutedFactCount, "the pet hit is captured but belongs to nobody's board");
        Assert.AreEqual(4L, index.DamageFactCount + index.TankingFactCount + index.UnroutedFactCount,
            "every fact that reached a row is accounted for exactly once, in one of three places");

        // The fourth shape, asserted at the predicate rather than through a row: a charmed raider's identity reads
        // Npc while the window holds, and this deliberately does not undo the flip to hand her a tank number.
        timeline.SetIdentity("Turncoat", IdentityKind.Npc, RuleStrength.Strong, "R9-charm");
        Assert.IsFalse(timeline.IsRaidVictimAt("Turncoat", T0 + 4),
            "a raider fighting for the other side is not one of us getting hit");
        Assert.IsTrue(timeline.IsRaidVictimAt("Illuminai", T0 + 1));

        /*
         * The precedence, which is the trap in this whole rule: "Mysteryhost" has no identity at all, so it passes
         * IsRaidVictimAt — and it is the thing a raider is hitting, i.e. the raid's own damage. Aimed at the row wins,
         * so her 30 is on the damage side of her row and this row has no tanking half whatsoever.
         */
        var mystery = Row(rows, "Mysteryhost");
        Assert.IsNotNull(mystery, "an unclassified exchange still opens a row, keyed on the defender as legacy does");
        Assert.AreEqual(1, index.DamageOrdinalsFor(mystery).Count);
        Assert.AreEqual(0, index.TankingOrdinalsFor(mystery).Count,
            "a raid swing at an unknown name is not someone receiving damage, however unknown that name is");
        Assert.IsFalse(timeline.IsRaidVictimAt("Swarmik`s pet", T0 + 2), "a pet is not a person");
        Assert.IsFalse(timeline.IsRaidVictimAt(null, T0 + 2));
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

        // The same spell shape aimed at ONE OF OURS instead of at a mob. It gets its own row (legacy keys that exchange on
        // the spell name too) and it is where the other half of the record-name rule shows: the noun stays.
        var s = facts.InternName("Dread Pheromones");
        facts.AddFact(new DamageFact(2, (long)T0 + 2, s, a, total: 60, typeId: LabelTypes.Dot,
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

            /*
             * One deliberate exception to "a materialized record is its fact, verbatim", and it is spelled out below rather
             * than derived here so this loop cannot go tautological: a line that named NO caster (`… has taken 335500 damage
             * from Curse XVII Rk. III by .`) carries the spell where an attacker belongs, the damage grid puts one row per
             * Attacker, and copying that noun through hands a curse a damage column beside real raiders. Legacy answers with
             * Labels.Unk (FightManager's AttackerIsSpell re-decision for a non-player target), so the total is kept and nobody
             * is invented.
             */
            if (!fact.AttackerIsSpell) Assert.AreEqual(table.NameOf(fact.AtkIdx), record.Attacker);

            Assert.AreEqual(table.NameOf(fact.DefIdx), record.Defender);
            Assert.AreEqual(fact.Total, record.Total);
            Assert.AreEqual(LabelTypes.LabelOf(fact.TypeId), record.Type, "the label word is the same interned literal the parsers write");
            // A fact whose line carried no modifier text gets the type word as its subtype: the summary's melee
            // counters key a ConcurrentDictionary on it and throw on a null key (FightSummarySource.SubTypeOf).
            Assert.AreEqual(table.SubtypeOf(fact.SubIdx) ?? LabelTypes.LabelOf(fact.TypeId), record.SubType);
            Assert.AreEqual(fact.AttackerIsSpell, record.AttackerIsSpell);
        }

        // The spell-flagged record: damage kept, caster name replaced by legacy's word for "we were not told".
        var spellRecord = records[1];
        Assert.IsTrue(spellRecord.AttackerIsSpell, "the line's own evidence still rides on the record");
        Assert.AreEqual(250u, spellRecord.Total, "replacing a name never loses the damage");
        Assert.AreEqual(Labels.Unk, spellRecord.Attacker,
            "an unattributed dot must not become a damage-dealer row named after a spell");

        // And the same shape hitting one of OURS keeps its noun: that exchange is the tanking side of a fight AGAINST the
        // spell, which is what legacy keys the row on and what the operator reads there.
        var spellRow = Row(rows, "Dread Pheromones");
        var spellSide = index.SummaryFightFor(spellRow, table);
        var tankRecord = TankRecords(spellSide).Single();
        Assert.AreEqual("Illuminai", tankRecord.Defender, "the exchange is the raid's damage-taken half");
        Assert.AreEqual("Dread Pheromones", tankRecord.Attacker,
            "a boss dot beating on a raider stays attributed to the thing the line named");

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
        // (FightFactIndex) rather than handing the dictionary a null key.
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
    public void ARowNothingIsAimedAtStillFeedsTheTankingBoard()
    {
        // A classified mob hitting a classified raider: the row exists (that is the engine's identity-aware
        // list doing its job) and every fact in it points the other way, so a DAMAGE summary over it has nothing
        // to show. Skipping the row outright — which is what this used to do — deleted the damage those players
        // took from the tanking board too, and narrowed AllRanges while it was at it. Legacy hands its own
        // hit-only rows to both builders; so does this.
        var facts = BuildFacts(("Echohead", "Illuminai", 100, 0, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.IsFalse(index.HasDamage(rows[0]), "nothing was aimed AT this row");
        Assert.IsTrue(index.HasTanking(rows[0]), "and yet this row is where the damage taken lives");

        var input = FightSummarySource.Build(rows, index, table);

        Assert.AreEqual(1, input.Fights.Count, "a hit-only row still reaches the boards");
        Assert.AreEqual(0, input.WithoutDamage);
        Assert.AreEqual(0, input.Fights[0].DamageBlocks.Count, "but it contributes no damage-dealt records");

        var taken = TankRecords(input.Fights[0]);
        Assert.AreEqual(1, taken.Count);
        Assert.AreEqual("Echohead", taken[0].Attacker);
        Assert.AreEqual("Illuminai", taken[0].Defender, "the raider who took it, which is what the board rolls up by");
        Assert.AreEqual(100L, input.Fights[0].TankTotal);
        Assert.AreEqual(1L, input.Fights[0].TankHits);
        Assert.AreEqual(T0, input.Fights[0].BeginTankingTime, "the builder takes its raid window from these bounds");
        Assert.AreEqual(T0, input.Fights[0].LastTankingTime);
        CollectionAssert.AreEqual(new List<double> { T0, T0 }, input.AllRanges.TimeSegments.Select(s => new List<double> { s.BeginTime, s.EndTime }).First());
    }

    [TestMethod]
    public void EveryFactOfARowLandsOnExactlyOneOfItsTwoBoards()
    {
        // The partition law, and the reason one selection can feed two boards without double counting:
        // FightProjection gives each fact one row AND one direction, OnFact files it in exactly one list.
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 500, 0, LabelTypes.Melee),
            ("Echohead", "Illuminai", 120, 1, LabelTypes.Melee),
            ("Echohead", "Kilsa", 80, 2, LabelTypes.Dot),
            ("Kilsa", "Echohead", 60, 3, LabelTypes.Dd));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Kilsa", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var echo = Row(rows, "Echohead");

        CollectionAssert.AreEqual(
            index.DamageOrdinalsFor(echo).Concat(index.TankingOrdinalsFor(echo)).OrderBy(x => x).ToList(),
            index.DamageOrdinalsFor(echo).Union(index.TankingOrdinalsFor(echo)).OrderBy(x => x).ToList(),
            "no ordinal sits on both sides of its row");

        var input = FightSummarySource.Build(rows, index, table);
        var built = input.Fights.Single();

        Assert.AreEqual(4, Records(built).Count + TankRecords(built).Count,
            "the row's materialized records are its facts: nothing dropped, nothing counted twice");
        Assert.AreEqual(2, Records(built).Count);
        Assert.AreEqual(2, TankRecords(built).Count);
        Assert.AreEqual(index.DamageFactCount + index.TankingFactCount,
            input.Fights.Sum(f => f.DamageBlocks.Sum(b => b.Actions.Count) + f.TankingBlocks.Sum(b => b.Actions.Count)),
            "across a whole selection: one materialized record per captured fact");
    }

    /*
     * Damage taken is credited per RAIDER, and the row keeps both directions. The fixture's own truth, in
     * numbers rather than engine terms: 234 + 88 onto Rune (the miss line writes no record), 512 onto Kilsa,
     * and the priest took 300 + 210. A mob hitting Rune does not open a row named Rune: rows are keyed on the
     * encounter's mob, so her 322 lives inside the PRIEST's row as damage-by-owner, and TankingStatsBuilder
     * re-sums it by record.Defender. The pinned negatives are as load-bearing as the totals — if a future
     * change ever grows a raider row for a mob's victim, or loses one direction of the split, this notices.
     */
    [TestMethod]
    public void DamageTakenIsPerRaiderAndTheRowKeepsBothDirections()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "tank-fight.txt");
        Assert.IsTrue(File.Exists(path), $"missing fixture: {path}");

        var run = PipelineHarness.RunFileDerived(path);

        ClassificationRules.Apply(run.Facts, run.Timeline);
        var index = new FightFactIndex();
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);

        var input = FightSummarySource.Build(rows, index, run.Facts);
        Assert.IsTrue(input.Fights.Any(static f => f.TankingBlocks.Count > 0),
            "the fixture produced no damage taken at all — this test would prove nothing");
        Assert.IsTrue(rows.All(static r => r.Name != "Rune"),
            "a raider who only got hit must not become a fight row; her damage belongs to the encounter");

        var fromBlocks = input.Fights.SelectMany(static f => f.TankingBlocks)
          .SelectMany(static b => b.Actions).Cast<DamageRecord>()
          .GroupBy(static r => r.Defender)
          .ToDictionary(static g => g.Key, static g => (long)g.Sum(static r => r.Total));

        Assert.AreEqual(322L, fromBlocks["Rune"], "Rune's damage taken");
        Assert.AreEqual(512L, fromBlocks["Kilsa"], "Kilsa's damage taken");
        Assert.AreEqual(834L, fromBlocks.Values.Sum(), "the raid-wide damage taken");

        var priest = Row(rows, "An ice giant priest");
        Assert.AreEqual(510L, priest.DamageToOwner, "the same row still carries what the raid did to it");
        Assert.AreEqual(834L, priest.DamageByOwner, "and what it did to the raid: the projection's own split");
    }

    [TestMethod]
    public void DamageTakenGivesEachRaiderTheirOwnActivityWindow()
    {
        /*
         * TankSegments/TankSubSegments are the tank board's denominator: TankingStatsBuilder feeds them to
         * StatsUtil.UpdateRaidTimeRanges and each player's SDPS divides by THEIR window, not the raid's. An
         * empty dictionary here does not fail loudly — every raider reads the raid-wide seconds and looks fine.
         * So this asserts names and bounds, and asserts them non-empty.
         */
        var facts = BuildFacts(
            ("Echohead", "Illuminai", 100, 5, LabelTypes.Melee),
            ("Echohead", "Illuminai", 100, 9, LabelTypes.Melee),
            ("Echohead", "Kilsa", 50, 20, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Kilsa", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var built = FightSummarySource.Build(rows, index, table).Fights.Single();

        Assert.AreEqual(2, built.TankSegments.Count, "one entry per raider who was hit");
        Assert.IsTrue(built.TankSegments.ContainsKey("Illuminai"));
        Assert.IsTrue(built.TankSegments.ContainsKey("Kilsa"));
        Assert.AreEqual(T0 + 5, built.TankSegments["Illuminai"].BeginTime,
            "Illuminai's window starts at the first hit she took, not at the fight's start");
        Assert.AreEqual(T0 + 9, built.TankSegments["Illuminai"].EndTime);
        Assert.AreEqual(T0 + 20, built.TankSegments["Kilsa"].BeginTime);
        Assert.IsTrue(built.TankSubSegments.Count > 0, "and the per-spell sub-windows the breakdown reads");
        Assert.IsFalse(built.DamageSegments.ContainsKey("Echohead"),
            "being hit is not activity for the damage board's denominators");
    }

    [TestMethod]
    public void EveryOutcomeTakenCountsAsAHitEvenWithNoDamage()
    {
        // FightManager's tank branch increments TankHits/TankTotal unconditionally, while its damage branch
        // counts only IsHitType records. A resisted or zero-total outcome therefore counts as a hit a player
        // took, and the derived side has to be just as lopsided or the # Hits column disagrees for no reason.
        var facts = BuildFacts(
            ("Echohead", "Illuminai", 0, 1, LabelTypes.Rs),
            ("Echohead", "Illuminai", 120, 1, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var built = FightSummarySource.Build(rows, index, table).Fights.Single();

        Assert.AreEqual(2L, built.TankHits, "a zero-total outcome is still a swing that found somebody");
        Assert.AreEqual(120L, built.TankTotal);
        Assert.AreEqual(2, built.TankingBlocks[0].Actions.Count,
            "both outcomes share one second-run block, the same grouping rule the damage side uses");
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

        var input = FightSummarySource.Build(rows, index, table);

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
        var input = FightSummarySource.Build([Row(rows, "Echohead")], index, table);

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
        var input = FightSummarySource.Build(rows, index, table);

        /*
         * No damage-filter dial is pinned here and none needs it: a materialized record carries ModifiersMask 0,
         * and each of the six filters drops a record only when its modifier bit is set. Nothing can be excluded
         * from a derived selection — which is also the honest limit of this comparison (FightSummarySource).
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

        var run = PipelineHarness.RunFileDerived(MiniFightPath);

        // The same pass order DeriveEngine runs, so this walks the production path and not a shortcut.
        ClassificationRules.Apply(run.Facts, run.Timeline);

        var index = new FightFactIndex();
        var fights = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(fights);

        Assert.IsTrue(fights.Count > 0, "the fixture derives no fights");

        foreach (var fight in fights)
        {
            var fromFacts = index.DamageOrdinalsFor(fight)
                .Where(o => LabelTypes.IsHit(run.Facts.Facts[o].TypeId))
                .Sum(o => (long)run.Facts.Facts[o].Total);
            Assert.AreEqual(fight.DamageToOwner, fromFacts, $"{fight.Name}: index and row disagree");

            if (!index.HasDamage(fight) && !index.HasTanking(fight)) continue;

            var summary = index.SummaryFightFor(fight, run.Facts);
            Assert.AreEqual(fight.DamageToOwner, summary.DamageTotal, $"{fight.Name}: blocks and row disagree");
            if (index.HasDamage(fight))
            {
                Assert.IsTrue(summary.DamageBlocks.Count > 0, $"{fight.Name}: no damage blocks for a selectable row");
                Assert.IsTrue(Records(summary).All(r => r.Defender == fight.Name), $"{fight.Name}: a block holds something aimed elsewhere");
                Assert.IsTrue(summary.DamageSegments.Keys.All(k => Records(summary).Any(r => r.Attacker == k)),
                    $"{fight.Name}: an activity window for someone who never swung at it");
            }
            else
            {
                Assert.AreEqual(0, summary.DamageBlocks.Count, $"{fight.Name}: nothing aimed at it, yet it carries damage");
            }

            /*
             * The tanking half of the same row. Held against the FACTS rather than against DerivedFight.TankTotal:
             * FightProjection sums both directions into DamageTotal and splits them into DamageToOwner /
             * DamageByOwner, and it does not fill TankTotal/TankHits/TankRollup at all (those belong to the older
             * row (they belonged to the retired legacy engine) - so comparing a materialized tank total to the row's
             * TankTotal would compare a real number to a zero that nothing writes, and pass on any log with no
             * damage taken in it. Which is exactly how this test stayed green before the tanking side existed.
             */
            var takenFromFacts = index.TankingOrdinalsFor(fight).Sum(o => (long)run.Facts.Facts[o].Total);
            Assert.AreEqual(takenFromFacts, summary.TankTotal, $"{fight.Name}: tank blocks and facts disagree");
            Assert.AreEqual(index.TankingOrdinalsFor(fight).Count, TankRecords(summary).Count,
                $"{fight.Name}: every fact aimed away from the row must reach a block");
            Assert.IsTrue(TankRecords(summary).All(r => r.Attacker == fight.Name),
                $"{fight.Name}: a tanking block holds somebody else's swing");
            Assert.IsTrue(summary.TankSegments.Keys.All(k => TankRecords(summary).Any(r => r.Defender == k)),
                $"{fight.Name}: a damage-taken window for someone who was never hit by it");
        }
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

    // ---- what the fight-range readers (spell/taunt boards) read off a materialized fight ----

    [TestMethod]
    public void AMaterializedFightCarriesItsSpellBoards()
    {
        var facts = BuildFacts(
            ("Illuminai", "Echohead", 400, 0, LabelTypes.Dd),
            ("Illuminai", "Echohead", 150, 3, LabelTypes.Dot));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var summary = index.SummaryFightFor(Row(rows, "Echohead"), table);

        // The key shape is FightManager's live one: caster ++ subtype word. These facts carry no modifier text,
        // so the subtype falls back to the kind word itself (SubTypeOf's documented floor).
        var ddKey = "Illuminai++" + LabelTypes.LabelOf(LabelTypes.Dd);
        var dotKey = "Illuminai++" + LabelTypes.LabelOf(LabelTypes.Dot);

        Assert.IsTrue(summary.DdDamage.TryGetValue(ddKey, out var dd), "the direct-damage entry is under its own key");
        Assert.AreEqual("Illuminai", dd.Caster);
        Assert.AreEqual(1u, dd.Count);
        Assert.AreEqual(400ul, dd.Total);
        Assert.AreEqual(400u, dd.Max);

        Assert.IsTrue(summary.DoTDamage.TryGetValue(dotKey, out var dot), "the dot entry is under its own key");
        Assert.AreEqual(150ul, dot.Total);

        // Counting is per kind: one Dd fact and one Dot fact never land in the other dictionary.
        Assert.AreEqual(1, summary.DdDamage.Count);
        Assert.AreEqual(1, summary.DoTDamage.Count);
    }

    [TestMethod]
    public void TauntsLandOnTheirNpcRowWithTheOutcomeWords()
    {
        // The row's life runs from its first fact to its last: the taunts must land INSIDE it, because a taunt is
        // routed to the one open row its name has at its second (legacy's GetFight), and this row is only open on
        // the seconds it actually fought.
        var facts = BuildFacts(("Illuminai", "Echohead", 100, 2, LabelTypes.Melee),
                               ("Illuminai", "Echohead", 100, 9, LabelTypes.Melee));

        // Two taunts on this row - a plain success at second 2 and an improved one at second 9 - plus one aimed
        // at a name that is not this row, which must stay out of it.
        var npc = facts.InternName("Echohead");
        var player = facts.InternName("Illuminai");
        var other = facts.InternName("Waxwork");
        facts.AddTaunt(new TauntFact(900, (long)(T0 + 2), npc, player, TauntFact.TauntSuccess));
        facts.AddTaunt(new TauntFact(901, (long)(T0 + 9), npc, player, TauntFact.TauntImproved));
        facts.AddTaunt(new TauntFact(902, (long)(T0 + 3), other, player, TauntFact.TauntSuccess));

        // And one that names the row on a second it is not alive: no row of that name is open at T0+400, so the
        // fact belongs to nobody here. Without the lifetime clamp it would land on this row - and so would every
        // other same-named row, which is how a 4,364-line evening multiplied into tens of thousands.
        facts.AddTaunt(new TauntFact(903, (long)(T0 + 400), npc, player, TauntFact.TauntSuccess));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var (rows, index, table) = Derive(facts, timeline);
        var echo = Row(rows, "Echohead");
        var summary = index.SummaryFightFor(echo, table);

        var taunts = summary.TauntBlocks.SelectMany(b => b.Actions).Cast<TauntRecord>().ToList();
        Assert.AreEqual(2, taunts.Count,
            "only the two taunts that name the row while it is alive: the other name's taunt belongs to its own row, "
            + "and the out-of-lifetime one belongs to no row at all");
        Assert.AreEqual("Illuminai", taunts[0].Player);
        Assert.AreEqual("Echohead", taunts[0].Npc);
        Assert.IsTrue(taunts[0].Success);
        Assert.IsFalse(taunts[0].IsImproved);
        Assert.IsTrue(taunts[1].IsImproved, "the improved word rides the fact back to the board");

        // One second, one block - the same grouping rule the damage and tank passes live by.
        Assert.AreEqual(2, summary.TauntBlocks.Count);
        Assert.AreEqual(T0 + 2, summary.TauntBlocks[0].BeginTime);
        Assert.AreEqual(T0 + 9, summary.TauntBlocks[1].BeginTime);

        // And a windowed materialization keeps only what is inside the window: the second-9 taunt is out.
        var bounded = FightSummarySource.Build([echo], index, table, T0, T0 + 5).Fights.Single();
        Assert.AreEqual(1, bounded.TauntBlocks.SelectMany(b => b.Actions).Count(), "the windowed row keeps one taunt");
    }
}

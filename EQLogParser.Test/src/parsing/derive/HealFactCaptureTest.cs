using System.Runtime.CompilerServices;

using EQLogParser;

namespace EQLogParser;

/*
 * Stage A of "parse all damage and heals in one consistent way with nothing lost": the heal stream is
 * captured, and the capture is checked against the one thing it must agree with — the records the healing
 * board itself reads.
 *
 * The comparison is worth stating precisely, because it is the strongest form this test could take.
 * HealingLineParser fires EventsHealProcessed immediately after RecordsStore.Instance.Add, for every heal
 * it stored, and nothing between those two statements can classify or drop anything (HealingLineParser.cs:46).
 * So "mirror heals" and "heals the healing board has available" are the same population by construction, and
 * any difference here is a capture bug rather than a disagreement about interpretation. A test that compared
 * heal facts against another sum of itself would pass forever and prove nothing.
 *
 * What is NOT covered here, deliberately: whether a heal counts toward a fight. That is the projection's
 * question (healing belongs to a fight's time window the way HealingStatsBuilder has always treated it), not
 * the tap's — and a capture test that pre-judged it would be unable to tell me the projection lost something.
 */
[TestClass]
[DoNotParallelize]
public class HealFactCaptureTest
{
    private static string HealFixture => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "heal-fight.txt");

    [TestInitialize]
    public void Setup()
    {
        // Both stores are process globals. RecordsStore holds the records every other test class has parsed;
        // the heal RepeatStore decides instance sharing by sighting history. Without clearing, whichever test
        // ran first decides what "the same population" means (the trap LineParsersTest documents).
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    // ---- the size of the thing, because a fact table is memory you pay for up front ----

    /*
     * DamageFact gained ModifiersMask (the six modifier filters' input) inside its existing 32 bytes by
     * deleting OverTotal, which damage never wrote. If this ever reads 40, that was paid for instead of
     * recycled: on a 5 M-damage-fact log that is 40 MB for a field nothing reads. HealFact genuinely costs
     * 40 — it carries what landed AND what was asked for, which is the number healing tabs are built from.
     */
    [TestMethod]
    public void AFactIsItsMeasuredSizeNotWishes()
    {
        Assert.AreEqual(32, Unsafe.SizeOf<DamageFact>(), "damage facts are the most numerous thing the mirror holds");
        Assert.AreEqual(40, Unsafe.SizeOf<HealFact>());

        /*
         * The heal table's estimate is its own buffer at 40 B a fact. The damage table's EstimatedBytes covers
         * every stream that class holds (deaths, taunts, identities, evidence as well as facts) because D2's
         * revisit trigger is a process peak, not one array — so it is asserted for growth rather than for an
         * exact number, which would have to restate capacities the table keeps to itself.
         */
        var facts = new DamageFactTable(1_000);
        var heals = new HealFactTable(facts, 1_000);
        Assert.AreEqual(40_000L, heals.EstimatedBytes, "40 B a heal fact, at capacity, no per-record allocation");

        /*
         * The estimate prices the BUFFER, not the row count — which is the honest statement for D2's memory
         * trigger, since a doubling leaves the old array alive until the garbage collector takes it and always
         * rounds capacity up past what is stored. 16 → 32 → 64 → 128 across 65 adds: eight times the bytes for
         * four times the facts.
         */
        var grown = new HealFactTable(new DamageFactTable(1), 16);
        var before = grown.EstimatedBytes;
        for (var i = 0; i < 65; i++) grown.AddHeal(default);
        Assert.AreEqual(65, grown.HealCount);
        Assert.AreEqual(before * 8, grown.EstimatedBytes, "the estimate follows the doubling the buffer actually did");
    }

    [TestMethod]
    public void TheHealWordsAreInTheVocabularyAndAreNotHits()
    {
        // One byte has to hold both streams' words. Ids 16/17 are the two heal words; nothing about them may
        // read as a damage hit, because a caller that sums IsHit facts into DamageTotal would otherwise be
        // adding healing the moment both streams share a table.
        Assert.AreEqual(LabelTypes.Heal, LabelTypes.IdOf(Labels.Heal));
        Assert.AreEqual(LabelTypes.Hot, LabelTypes.IdOf(Labels.Hot));
        Assert.AreEqual(Labels.Heal, LabelTypes.LabelOf(LabelTypes.Heal));
        Assert.AreEqual(Labels.Hot, LabelTypes.LabelOf(LabelTypes.Hot));

        Assert.IsFalse(LabelTypes.IsHit(LabelTypes.Heal));
        Assert.IsFalse(LabelTypes.IsHit(LabelTypes.Hot));
        Assert.IsTrue(LabelTypes.IsHeal(LabelTypes.Heal));
        Assert.IsTrue(LabelTypes.IsHeal(LabelTypes.Hot));
        Assert.IsFalse(LabelTypes.IsHeal(LabelTypes.Melee));

        // The damage side of the vocabulary is unchanged by sharing it.
        Assert.IsTrue(LabelTypes.IsHit(LabelTypes.Melee));
        Assert.IsFalse(LabelTypes.IsHit(LabelTypes.Miss));
    }

    // ---- the ledger: captured heals vs the records the healing board reads ----

    [TestMethod]
    public void EveryStoredHealIsAHealFactAndBack()
    {
        var run = PipelineHarness.RunFileDerived(HealFixture);
        var stored = RecordsStore.Instance.GetAllHeals().ToList();

        Assert.IsTrue(stored.Count > 0, "the fixture must produce healing at all, or this test proves nothing");
        Assert.AreEqual(stored.Count, run.HealFacts.HealCount,
            "a heal the healing board can see and the mirror did not capture is data lost at the door");

        long storedTotal = 0, storedAsked = 0;
        for (var i = 0; i < stored.Count; i++)
        {
            var rec = stored[i].Item2;
            storedTotal += rec.Total;
            storedAsked += rec.OverTotal == 0 ? rec.Total : rec.OverTotal;
        }

        long factTotal = 0, factAsked = 0;
        var facts = run.HealFacts.Heals;
        for (var i = 0; i < facts.Length; i++)
        {
            factTotal += facts[i].Total;
            factAsked += facts[i].AskedFor;
        }

        Assert.AreEqual(storedTotal, factTotal);
        Assert.AreEqual(storedAsked, factAsked);

        // Field for field, in the order both sides saw them. Values only, never instance identity: heals are
        // shared by value (RepeatStore), and a mirror fact is a copy of the record's contents by design.
        for (var i = 0; i < stored.Count; i++)
        {
            var rec = stored[i].Item2;
            var fact = facts[i];

            Assert.AreEqual(rec.Healer, run.HealFacts.NameOf(fact.HealerIdx), $"healer of heal {i}");
            Assert.AreEqual(rec.Healed, run.HealFacts.NameOf(fact.HealedIdx), $"healed of heal {i}");
            Assert.AreEqual(rec.Total, fact.Total);
            Assert.AreEqual(rec.OverTotal, fact.OverTotal, "the parenthesised amount, verbatim including its zero");
            Assert.AreEqual(rec.Type, LabelTypes.LabelOf(fact.TypeId));
            Assert.AreEqual(rec.SubType, run.HealFacts.SpellOf(fact.SubIdx));
            Assert.AreEqual(rec.ModifiersMask, fact.ModMask);
            Assert.AreEqual(CombatCapture.ToTimeS(stored[i].Item1), fact.TimeS);
        }
    }

    [TestMethod]
    public void DamageAndHealFactsShareOneSequenceSoOrderSurvives()
    {
        // One counter across both streams: within a single second the log's own order is the only information
        // about what happened first, and a heal that arrives between two hits has to stay between them.
        var run = PipelineHarness.RunFileDerived(HealFixture);

        var seqs = new List<(int Seq, bool IsHeal)>();
        var dmg = run.Facts.Facts;
        for (var i = 0; i < dmg.Length; i++) seqs.Add((dmg[i].Seq, false));
        var heals = run.HealFacts.Heals;
        for (var i = 0; i < heals.Length; i++) seqs.Add((heals[i].Seq, true));

        Assert.AreEqual(dmg.Length + heals.Length, seqs.Count);

        // No duplicate sequence numbers: one counter means every fact of either kind is uniquely placed.
        Assert.AreEqual(seqs.Count, seqs.Select(s => s.Seq).Distinct().Count());

        // And both streams really did feed the same counter — a heal sits between two damage lines inside
        // 18:50:02 in the fixture. If either stream had its own numbering, ordering the merged list would be
        // meaningless and a reader could not walk the log's order at all.
        var ordered = seqs.OrderBy(s => s.Seq).ToList();
        Assert.IsTrue(ordered.Any(s => !s.IsHeal) && ordered.Any(s => s.IsHeal), "both streams captured");
        var healPositions = ordered.Select((s, i) => (s.IsHeal, i)).Where(x => x.IsHeal).Select(x => x.i).ToList();
        Assert.IsTrue(healPositions.Min() > 0, "the fixture's first damage line precedes its first heal");
        Assert.IsTrue(ordered.Skip(healPositions.Min() + 1).Any(s => !s.IsHeal),
            "damage follows the fixture's first heal, so one stream cannot end up sorted entirely to one side of the other");

        // Sequence order must agree with time order — a fact about the tap, not the file: it is the shared
        // counter that lets a reader walk both streams in log order without sorting.
        var lastTime = long.MinValue;
        foreach (var s in ordered)
        {
            var t = s.IsHeal ? heals[SeqIndex(heals, s.Seq)].TimeS : dmg[SeqIndex(dmg, s.Seq)].TimeS;
            Assert.IsTrue(t >= lastTime, "sequence order may never run backwards against the clock");
            lastTime = t;
        }
    }

    private static int SeqIndex(ReadOnlySpan<HealFact> heals, int seq)
    {
        for (var i = 0; i < heals.Length; i++) if (heals[i].Seq == seq) return i;
        throw new InvalidOperationException($"no heal fact with seq {seq}");
    }

    private static int SeqIndex(ReadOnlySpan<DamageFact> facts, int seq)
    {
        for (var i = 0; i < facts.Length; i++) if (facts[i].Seq == seq) return i;
        throw new InvalidOperationException($"no damage fact with seq {seq}");
    }

    [TestMethod]
    public void AHealersNameResolvesToTheSameIndexTheDamageTableGivesHer()
    {
        // Shared interning is the design decision that keeps a join between the streams an index compare and
        // the name table paid for once. Two tables with their own spaces would pass every test above and still
        // make "the same raider" two different numbers.
        var run = PipelineHarness.RunFileDerived(HealFixture);

        var heals = run.HealFacts.Heals;
        Assert.IsTrue(heals.Length > 0);

        for (var i = 0; i < heals.Length; i++)
        {
            var healer = run.HealFacts.NameOf(heals[i].HealerIdx);
            Assert.AreEqual(run.Facts.InternName(healer), heals[i].HealerIdx,
                $"{healer} is one entity, so she is one index");
        }
    }

    // ---- what a heal line actually says, including the shape that means "unknown" ----

    [TestMethod]
    public void TheOverhealNumberKeepsBothMeaningsApart()
    {
        var run = PipelineHarness.RunFileDerived(HealFixture);
        var facts = run.HealFacts.Heals;

        var withAsk = First(run, "Vexmora", "Corunist");      // "for 3100 (9900)"
        Assert.AreEqual(3100u, withAsk.Total);
        Assert.AreEqual(9900u, withAsk.OverTotal);
        Assert.AreEqual(9900u, withAsk.AskedFor);
        Assert.AreEqual(6800u, withAsk.Overheal);

        // The zero that means "the line never said", not "nothing was asked". Normalising it to Total at
        // capture would be tidier and would double StatsUtil's MaxPotentialHit on every plain heal line —
        // so the fact keeps the parser's answer and AskedFor is where intent gets read.
        var noAsk = First(run, "Vexmora", "Rellam");          // "for 4200" (no parentheses)
        Assert.AreEqual(4200u, noAsk.Total);
        Assert.AreEqual(0u, noAsk.OverTotal);
        Assert.AreEqual(4200u, noAsk.AskedFor);
        Assert.AreEqual(0u, noAsk.Overheal);

        // A heal can ask for exactly what it landed and still write the parentheses.
        var exact = First(run, "Vexmora", "Vexmora");         // "for 1200 (1200)"
        Assert.AreEqual(1200u, exact.AskedFor);
        Assert.AreEqual(0u, exact.Overheal);
    }

    [TestMethod]
    public void HoTLinesAndModifierMasksComeAcrossAsTheSameWordsTheRecordUses()
    {
        var run = PipelineHarness.RunFileDerived(HealFixture);

        var hot = First(run, "Corunist", "Vexmora");          // "... healed Vexmora over time for 1750 (2600)"
        Assert.AreEqual(LabelTypes.Hot, hot.TypeId);
        Assert.AreEqual(Labels.Hot, LabelTypes.LabelOf(hot.TypeId), "the board matches on this exact interned word");

        // "(Critical)" on a heal: the mask is what the healing filters read, and -1 is the parser's own
        // "no modifier text" answer rather than a zero it would have to mean something else by.
        var crit = First(run, "Vexmora", "Vexmora");
        Assert.AreNotEqual((short)-1, crit.ModMask, "a critical heal must carry the bit its line carried");

        var plain = First(run, "Vexmora", "Rellam");
        Assert.AreEqual((short)-1, plain.ModMask);
    }

    [TestMethod]
    public void ADamageRecordRematerializesWithTheMaskThatMadeItsFiltersHonest()
    {
        // The gap this closes was documented in FightSummarySource for a whole increment: derived records had
        // ModifiersMask 0, so DamageValidator excluded nothing and every derived total read HIGH the moment one
        // of the six modifier settings was switched off. Off the record into the fact, out of the fact into the
        // rebuilt record — one hop each way, no interpretation.
        var run = PipelineHarness.RunFileDerived(HealFixture);

        var index = new FightFactIndex();
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline);
        var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
        // Named by what the parser fired, not by the fixture's spelling: the capitalisation is normalization
        // upstream of the mirror, and this row's name is data — a hardcoded lowercase would silently select
        // nothing and every assertion below it would run on an empty block list.
        var boss = rows.OrderByDescending(r => r.DamageTotal).First();
        Assert.IsTrue(boss.Name.EndsWith("frostbound sentinel", StringComparison.OrdinalIgnoreCase), $"unexpected top row {boss.Name}");

        var summary = index.SummaryFightFor(boss, run.Facts);
        var records = summary.DamageBlocks.SelectMany(b => b.Actions).Cast<DamageRecord>().ToList();
        Assert.IsTrue(records.Count > 0);

        var facts = run.Facts.Facts;
        var ordinals = index.DamageOrdinalsFor(boss);
        for (var i = 0; i < ordinals.Count; i++)
        {
            Assert.AreEqual(facts[ordinals[i]].ModMask, records[i].ModifiersMask,
                "the rebuilt record carries the mask the parser put on the original");
        }

        // At least one line in the fixture carries a modifier (Critical / Crippling Blow / Lucky Critical), so
        // the loop above is not comparing a column of identical values.
        var masks = new HashSet<short>();
        for (var i = 0; i < facts.Length; i++) masks.Add(facts[i].ModMask);
        Assert.IsTrue(masks.Count > 1, "fixture must contain masked and unmasked damage to prove the copy works");
    }

    /*
     * Two heal shapes the current parser refuses. Pinned as measurements, not as guesses about whether they
     * matter — counted on local/eqlog_Incogitable_xegony.txt (344 MB, 3.6M lines, ~448,600 heal-action lines):
     *
     *   "`s pet healed itself ..."         10,194 lines  refused — the healer word is a pet name, and the
     *                                              extraction special-cases "`s ward" but not "`s pet"
     *   "`s pet has been healed ..."        2,688 lines  refused — same cause, the subject side this time
     *   "has been healed over time for"       120 lines  refused — the amount offset assumes "for <n>" follows
     *                                              "healed" immediately, and "over time" is in between
     *
     * 13,002 of ~448,600 heal-action lines: 2.9 %. Which is small enough to leave alone ONLY because of what
     * the composition turns out to be — every one of those 10,194 active pet lines is a pet healing ITSELF
     * (measured: 10,194 of 10,194), so no raider's output moves; and the passive ones name no healer at all
     * (they read "has been healed for N by <effect>"), so on the board they would have landed under an unknown
     * healer either way. What would change this from footnote to bug is a capture showing pet lines healing
     * RAIDERS — so the count of each shape belongs in this comment, and a fix should arrive with it re-measured.
     *
     * Every one of those 10,194 is a pet healing ITSELF (measured: 10,194 of 10,194), so no raider's healing
     * output and no raid total on the board changes — which is why this is a pinned fact and not a parser fix.
     * By contrast `X`s ward healed <raider>` — 10,620 lines on the same capture — DOES parse, credited to the
     * ward's owner, so real pet/ward healing of the raid is already in both pipelines.
     *
     * This test loses its value the day someone fixes either shape, and that is the point: it fires when the
     * numbers above stop being true, and the fix should then come with a re-measured comment rather than a
     * deleted assertion.
     */
    [TestMethod]
    public void TheTwoRefusedHealShapesAreMeasuredRatherThanAssumed()
    {
        Assert.IsFalse(Parse("Nniki`s pet healed itself for 7500 hit points by Venom Claw VII."),
            "pet self-heal lines are refused today (10,194 of them on the reference capture, all self-targeted)");

        Assert.IsFalse(Parse("Rellam has been healed over time for 900 hit points by Roar of the Lion IX."),
            "passive over-time heals are refused today (120 lines on the reference capture)");

        // The shape that does work, and which is why "`s pet" needs no new branch to make the healing board
        // correct: wards already credit their owner.
        var ward = Parse("Steadman`s ward healed Broddy for 10250 hit points by Ancient Restoration XX.");
        Assert.IsTrue(ward);
        var rec = RecordsStore.Instance.GetAllHeals().Last().Item2;
        Assert.AreEqual("Steadman", rec.Healer, "a ward's healing belongs to the raider who cast it");

        /*
         * A pet on the RECEIVING end of a passive heal is refused too — same extraction gap, opposite side of
         * the sentence. These are the life-drain effects that top a pet off ("has been healed for 40000 ... by
         * Enhanced Theft of Essence Effect XV"), and they name no healer, so the board never saw them either
         * way; the damage side of the same shape is not lost, since "X`s pet" as an attacker or defender parses.
         */
        Assert.IsFalse(Parse("Bulgar`s pet has been healed for 40000 hit points by Enhanced Theft of Essence Effect XV."),
            "2,688 lines on the reference capture, all healer-less passive effects");
    }

    private static bool Parse(string action)
    {
        var before = RecordsStore.Instance.GetAllHeals().Count();
        var ok = HealingLineParser.Process(new LineData
        {
            Action = action,
            BeginTime = 7_000,
            LineNumber = 1,
            Split = action.Split(' '),
        });

        Assert.AreEqual(ok ? before + 1 : before, RecordsStore.Instance.GetAllHeals().Count(),
            "a refused line must store nothing, or the ledger test would double-count it");
        return ok;
    }

    private static HealFact First(PipelineHarness.DeriveRunResult run, string healer, string healed)
    {
        var facts = run.HealFacts.Heals;
        for (var i = 0; i < facts.Length; i++)
        {
            if (run.HealFacts.NameOf(facts[i].HealerIdx) == healer && run.HealFacts.NameOf(facts[i].HealedIdx) == healed)
            {
                return facts[i];
            }
        }

        throw new AssertFailedException($"no heal fact for {healer} -> {healed}; the fixture line does not parse or is not captured");
    }
}

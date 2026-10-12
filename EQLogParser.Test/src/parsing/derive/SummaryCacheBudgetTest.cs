using EQLogParser;

namespace EQLogParser;

/*
 * The materialization cache is a budgeted accelerator (docs/DesignNotes.md → "DPS and active-reader retention review at
 * 35420fcf"). Unbounded it retained every row of every selection for the life of the carried index — measured 2.27M
 * materialized damage records beside a capture whose settled fact tables cost 19.5 MiB, and a deselect-all never brought
 * the count down because the index outlives any one selection.
 *
 * Laws pinned here:
 *   - the cache stays at or under its record budget no matter how many rows are asked for;
 *   - a row that is still THE ANSWER when re-asked survives eviction (recency), which is also what keeps a row someone
 *     is watching from being rebuilt under their clicks;
 *   - an evicted row answers again on demand with the same numbers, in a fresh object — the cache is acceleration, not
 *     the data;
 *   - ordinal-list slack compaction reclaims the doubling slack of DEAD rows only: an open row's list is about to be
 *     written, and the swap (never an in-place resize) leaves a list already in somebody's hands intact.
 */
[TestClass]
public class SummaryCacheBudgetTest
{
    private const int Fights = 120;
    private const int HitsPerFight = 3000;
    private const uint PerHit = 100;

    private static DamageFactTable BuildFacts()
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        for (var mob = 0; mob < Fights; mob++)
        {
            // One second per row: all its hits land in one block, which keeps the materialization cost down without
            // changing what is under test (the cache bookkeeping, not the grouping).
            var t = 1_000_000 + mob;
            for (var hit = 0; hit < HitsPerFight; hit++)
            {
                facts.AddFact(new DamageFact(seq++, (long)t, facts.InternName("RaiderA"), facts.InternName($"Mob{mob:D3}"),
                                             total: PerHit, typeId: LabelTypes.Melee, flags: 0, modMask: 0, subIdx: ushort.MaxValue));
            }
        }

        return facts;
    }

    private static EntityTimeline KindsOnly()
    {
        // Routing needs to know which side each name is on; a timeline with the kinds set is exactly what the projection reads.
        var timeline = new EntityTimeline();
        timeline.SetIdentity("RaiderA", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        for (var mob = 0; mob < Fights; mob++)
        {
            timeline.SetIdentity($"Mob{mob:D3}", IdentityKind.Npc, RuleStrength.Certain, "R1-target");
        }

        return timeline;
    }

    private static DerivedFight Row(IReadOnlyList<DerivedFight> rows, string name) => rows.First(r => r.Name == name);

    [TestMethod]
    public void SummaryCache_StaysAtBudget_KeepsRecentlyAnsweredRows_RebuildsEvictedOnDemand()
    {
        var facts = BuildFacts();
        var index = new FightFactIndex(KindsOnly());
        var rows = FightProjection.Build(facts, KindsOnly(), index.OnFact);

        // Fill the cache to its edge: 83 rows x 3000 records = 249k, just under the 250k budget.
        var firstAsked = new Fight?[Fights];
        for (var i = 0; i < 83; i++)
        {
            firstAsked[i] = index.SummaryFightFor(rows[i], facts);
        }

        // Row 0 now gets re-asked while it is still the answer: that ask moves it to the tail of the eviction order,
        // which is what saves it below. Without the touch it would be among the first victims.
        var row0 = index.SummaryFightFor(rows[0], facts);
        Assert.AreSame(firstAsked[0], row0, "an unchanged re-ask must hit the cache");

        for (var i = 83; i < Fights; i++)
        {
            firstAsked[i] = index.SummaryFightFor(rows[i], facts);
        }

        // 120 rows x 3000 records against a 250k-record budget: 83 entries fit, 37 were evicted - and row 0 was NOT
        // among them because it answered an ask at the very moment the tail started growing.
        Assert.IsTrue(index.CachedSummaryRecords <= FightFactIndex.SummaryRecordBudget,
            $"budget is {FightFactIndex.SummaryRecordBudget:N0}, cache holds {index.CachedSummaryRecords:N0}");
        Assert.AreEqual(FightFactIndex.SummaryRecordBudget / HitsPerFight, index.CachedSummaryCount);

        var row0Again = index.SummaryFightFor(rows[0], facts);
        Assert.AreSame(row0, row0Again, "the recently answered row must have survived eviction");

        // The evicted rows come back on demand: same numbers, fresh object - the cache accelerated them, it is not their storage.
        var row1Rebuilt = index.SummaryFightFor(rows[1], facts);
        Assert.IsNotNull(row1Rebuilt);
        Assert.AreEqual(HitsPerFight * PerHit, row1Rebuilt.DamageTotal);
        Assert.AreNotSame(firstAsked[1], row1Rebuilt, "an evicted row is rebuilt, not resurrected");
    }

    [TestMethod]
    public void CompactOrdinalSlack_ReclaimsDeadRowsOnly_AndKeepsTheOrdinalsIntact()
    {
        var facts = BuildFacts();
        var index = new FightFactIndex(KindsOnly());
        var rows = FightProjection.Build(facts, KindsOnly(), index.OnFact);

        var dead = Row(rows, "Mob000");
        var open = Row(rows, "Mob001");
        var deadList = (List<int>)index.DamageOrdinalsFor(dead);
        var openList = (List<int>)index.DamageOrdinalsFor(open);
        Assert.AreEqual(HitsPerFight, deadList.Count);
        Assert.IsTrue(deadList.Capacity > HitsPerFight, "doubling must leave slack on the dead row too");
        Assert.IsTrue(openList.Capacity > HitsPerFight, "doubling must leave slack on the open row too");

        var slotsBefore = index.OrdinalSlots;
        var expectedAfter = slotsBefore - (deadList.Capacity - HitsPerFight);
        Assert.IsTrue(expectedAfter < slotsBefore, "the arithmetic assumes there was slack to reclaim");

        dead.Dead = true;   // a death closes the row; its ordinal run is done growing
        index.CompactOrdinalSlack();

        var swappedDeadList = (List<int>)index.DamageOrdinalsFor(dead);
        Assert.AreEqual(HitsPerFight, swappedDeadList.Count);
        Assert.AreEqual(0, swappedDeadList[0]);
        Assert.AreEqual(HitsPerFight - 1, swappedDeadList[^1]);
        Assert.AreEqual(swappedDeadList.Count, swappedDeadList.Capacity, "the dead row's list is swapped to an exact-size copy");

        // The open row's list is untouched (it may still grow): same instance. The total came down by exactly the dead
        // row's excess - reclaimed, not lost.
        Assert.AreSame(openList, index.DamageOrdinalsFor(open));
        Assert.AreEqual(expectedAfter, index.OrdinalSlots);
    }
}

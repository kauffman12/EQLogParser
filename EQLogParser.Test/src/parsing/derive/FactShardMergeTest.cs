using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using EQLogParser;

namespace EQLogParser;

/*
 * FactShardMerge is the seam that decides whether a sharded load can hold a content standard at all:
 * two ingest lanes filled two table sets with their OWN name pools and their OWN sequence counters, and
 * the merge must hand the engine tables indistinguishable from what one lane would have produced over
 * the same lines in file order. Everything asserted here is CONTENT — row by row, name resolved to its
 * string — because "the code remaps indices" compiles no matter which index space you got right.
 *
 * The fixture mixes all four queues the sequential capture shares a clock across (damage, heal, death,
 * taunt) plus both spell-subtype lanes (damage subtype pool, heal spell pool), and deliberately avoids
 * evidence/registry-verification lines: those depend on memory that legitimately differs when a stream
 * is cut mid-file, which the merge does not own and does not fix.
 */
[TestClass]
public class FactShardMergeTest
{
  private sealed class NullSinks : IChatSink, ITriggerHook
  {
    public void Init()
    {
    }

    public void Add(ChatType chat)
    {
    }

    public void CheckQuickShare(ChatType chat, string action, double beginTime)
    {
    }
  }

  [TestInitialize]
  public void Setup()
  {
    ParseLane.BindCurrentThread(null);
    DamageLineParser.ResetProcessState();
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup()
  {
    ParseLane.BindCurrentThread(null);
    DamageLineParser.ResetProcessState();
    PlayerRegistry.Instance.Clear();
  }

  /*
   * One complete ingest stream, exactly as production runs it: its own lane, its own capture over its
   * own tables, its own LogProcessor consumer. The shard under test and the sequential control are the
   * SAME code path with different byte ranges — there is no second implementation to agree with.
   */
  private static (DamageFactTable Facts, HealFactTable Heals) RunStream(IEnumerable<string> lines)
  {
    var facts = new DamageFactTable(64);
    var heals = new HealFactTable(facts, 64);
    var lane = new ParseLane();
    var capture = new CombatCapture(facts, heals, lane);
    capture.Start();
    try
    {
      using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>());
      using var processor = new LogProcessor("shard.txt", new NullSinks(), new NullSinks(), lane);
      processor.LinkTo(items);

      foreach (var line in lines)
      {
        var dt = DateUtil.ParseStandardDate(line);
        Assert.AreNotEqual(DateTime.MinValue, dt, $"fixture line has no standard timestamp: {line}");
        items.Add(new LogReaderItem(line, DateUtil.ToDotNetSeconds(dt), false));
      }

      items.CompleteAdding();
      Assert.IsTrue(processor.Completion!.Wait(TimeSpan.FromSeconds(60)));
    }
    finally
    {
      capture.Stop();
    }

    return (facts, heals);
  }

  private static readonly string[] Lines =
  [
    "[Sat Sep 05 18:41:01 2026] Astralx crushes Sontalak for 126225 points of damage. (Strikethrough Critical)",
    "[Sat Sep 05 18:41:02 2026] Useless crushes an abyssal terror for 9022 points of damage.",
    "[Sat Sep 05 18:41:03 2026] Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.",
    "[Sat Sep 05 18:41:04 2026] Sontalak has been slain by Astralx.",
    "[Sat Sep 05 18:41:05 2026] Foob taunts Doomshade.",
    "[Sat Sep 05 18:41:06 2026] Your ward heals you as it breaks! You healed Niktaza for 8970 (86306) hit points by Healing Ward. (Critical)",
  ];

  private static string Describe(DamageFactTable t, in DamageFact f) =>
    $"{f.Seq}|{f.TimeS}|{t.NameOf(f.AtkIdx)}->{t.NameOf(f.DefIdx)}|{f.Total}|{f.TypeId}|{f.Flags}|{f.ModMask}|{t.SubtypeOf(f.SubIdx)}";

  private static string Describe(DamageFactTable t, HealFactTable h, in HealFact f) =>
    $"{f.Seq}|{f.TimeS}|{t.NameOf(f.HealerIdx)}->{t.NameOf(f.HealedIdx)}|{f.Total}|{f.OverTotal}|{f.TypeId}|{f.Flags}|{f.ModMask}|{h.SpellOf(f.SubIdx)}";

  [TestMethod]
  public void MergedShardsMatchOneSequentialStreamRowForRow()
  {
    var (seqFacts, seqHeals) = RunStream(Lines);

    PlayerRegistry.Instance.Clear();   // a fresh process-equivalent for the split run
    var shardA = RunStream(new ArraySegment<string>(Lines, 0, 3));
    var shardB = RunStream(new ArraySegment<string>(Lines, 3, 3));

    var mergedFacts = new DamageFactTable(64);
    var mergedHeals = new HealFactTable(mergedFacts, 64);
    var lastSeq = FactShardMerge.MergeInto(mergedFacts, mergedHeals, [new FactShard(shardA.Facts, shardA.Heals), new FactShard(shardB.Facts, shardB.Heals)]);

    // Counts first — a missing row with matching totals would be the quiet bug.
    Assert.AreEqual(seqFacts.FactCount, mergedFacts.FactCount, "damage count");
    Assert.AreEqual(seqHeals.HealCount, mergedHeals.HealCount, "heal count");
    Assert.AreEqual(seqFacts.DeathCount, mergedFacts.DeathCount, "death count");
    Assert.AreEqual(seqFacts.TauntCount, mergedFacts.TauntCount, "taunt count");
    Assert.AreEqual(seqFacts.IdentityEventCount, mergedFacts.IdentityEventCount, "identity count");
    Assert.AreEqual(seqFacts.Evidence.Length, mergedFacts.Evidence.Length, "evidence count");

    for (var i = 0; i < seqFacts.FactCount; i++)
    {
      Assert.AreEqual(Describe(seqFacts, seqFacts.RefAt(i)), Describe(mergedFacts, mergedFacts.RefAt(i)), $"damage[{i}]");
    }

    for (var i = 0; i < seqHeals.HealCount; i++)
    {
      Assert.AreEqual(Describe(seqFacts, seqHeals, seqHeals.RefAt(i)), Describe(mergedFacts, mergedHeals, mergedHeals.RefAt(i)), $"heal[{i}]");
    }

    var seqDeaths = seqFacts.Deaths;
    var mDeaths = mergedFacts.Deaths;
    for (var i = 0; i < seqDeaths.Length; i++)
    {
      Assert.AreEqual($"{seqDeaths[i].Seq}|{seqFacts.NameOf(seqDeaths[i].KilledIdx)}|{(seqDeaths[i].KillerIdx < 0 ? "<none>" : seqFacts.NameOf(seqDeaths[i].KillerIdx))}",
                      $"{mDeaths[i].Seq}|{mergedFacts.NameOf(mDeaths[i].KilledIdx)}|{(mDeaths[i].KillerIdx < 0 ? "<none>" : mergedFacts.NameOf(mDeaths[i].KillerIdx))}",
                      $"death[{i}]");
    }

    var seqTaunts = seqFacts.Taunts;
    var mTaunts = mergedFacts.Taunts;
    for (var i = 0; i < seqTaunts.Length; i++)
    {
      Assert.AreEqual($"{seqTaunts[i].Seq}|{seqFacts.NameOf(seqTaunts[i].NpcIdx)}",
                      $"{mTaunts[i].Seq}|{mergedFacts.NameOf(mTaunts[i].NpcIdx)}", $"taunt[{i}]");
    }

    // The shared clock: renumbering must reproduce the sequential sequence EXACTLY for a boundary at a
    // line break (the whole point of interleaving by local seq instead of concatenating queues).
    Assert.AreEqual(seqFacts.FactCount + seqHeals.HealCount + seqFacts.DeathCount + seqFacts.TauntCount +
                    seqFacts.IdentityEventCount + seqFacts.Evidence.Length, lastSeq);
  }

  [TestMethod]
  public void SameNameAcrossShardsMergesToOneId()
  {
    // The "one entity, two ids" bug: Astralx is name 0 in shard A and 0 in shard B — different tables,
    // same ordinal, different MEANING until the pool remap runs. Assert by resolved string across BOTH
    // queues that need it (attacker index and subtype index are separate pools with the same hazard).
    var shardA = RunStream([Lines[0]]);
    var shardB = RunStream([Lines[1], Lines[0]]);

    var mergedFacts = new DamageFactTable(64);
    FactShardMerge.MergeInto(mergedFacts, new HealFactTable(mergedFacts, 64), [new FactShard(shardA.Facts, shardA.Heals), new FactShard(shardB.Facts, shardB.Heals)]);

    Assert.AreEqual(3, mergedFacts.FactCount);
    Assert.AreEqual("Astralx", mergedFacts.NameOf(mergedFacts.RefAt(0).AtkIdx));
    Assert.AreEqual("Useless", mergedFacts.NameOf(mergedFacts.RefAt(1).AtkIdx));
    Assert.AreEqual("Astralx", mergedFacts.NameOf(mergedFacts.RefAt(2).AtkIdx));

    // And the subtype pool remapped too: the crit line's ability rides in the ordinal-compared pool.
    Assert.AreEqual(mergedFacts.SubtypeOf(seqControlSubtype(0)), mergedFacts.SubtypeOf(mergedFacts.RefAt(2).SubIdx));
  }

  private static ushort seqControlSubtype(int factIndex)
  {
    // Small control stream used only by the subtype assertion above.
    var (facts, _) = RunStream([Lines[0]]);
    return facts.RefAt(factIndex).SubIdx;
  }

  [TestMethod]
  public void MonitorPhaseResumesNumberingAboveTheMergedHistory()
  {
    var shardA = RunStream(new ArraySegment<string>(Lines, 0, 3));
    var shardB = RunStream(new ArraySegment<string>(Lines, 3, 3));

    var facts = new DamageFactTable(64);
    var heals = new HealFactTable(facts, 64);
    var lastSeq = FactShardMerge.MergeInto(facts, heals, [new FactShard(shardA.Facts, shardA.Heals), new FactShard(shardB.Facts, shardB.Heals)]);

    // The hand-off: the session capture is built over the merged tables with the merge's last sequence.
    var capture = new CombatCapture(facts, heals, ParseLane.Main, lastSeq);
    capture.Start();
    try
    {
      using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>());
      using var processor = new LogProcessor("tail.txt", new NullSinks(), new NullSinks());   // default lane, as the monitor reader is
      processor.LinkTo(items);

      const string line = "[Sat Sep 05 18:42:00 2026] Astralx crushes Sontalak for 7 points of damage.";
      items.Add(new LogReaderItem(line, DateUtil.ToDotNetSeconds(DateUtil.ParseStandardDate(line)), true));
      items.CompleteAdding();
      Assert.IsTrue(processor.Completion!.Wait(TimeSpan.FromSeconds(60)));
    }
    finally
    {
      capture.Stop();
    }

    // shard A contributes both `crushes` rows (lines 0-2 are damage, damage, heal); one monitor line
    // lands on top of the merged two.
    Assert.AreEqual(3, facts.FactCount);
    ref readonly var tail = ref facts.RefAt(facts.FactCount - 1);
    Assert.AreEqual(7u, tail.Total);
    Assert.IsTrue(tail.Seq > lastSeq, $"a monitor row numbered {tail.Seq} would interleave with the merged history at {lastSeq}");
    Assert.AreEqual(lastSeq + 1, tail.Seq);
  }
}

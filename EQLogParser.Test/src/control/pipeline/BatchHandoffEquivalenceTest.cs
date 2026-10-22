using EQLogParser;
using System.Collections.Concurrent;

namespace EQLogParser;

/*
 * The reader hands the parser lines in batches (ILogProcessor), because the handoff across threads — not the parsing —
 * is what costs: one Add per line measured at ~21,460 ms of CPU and 885 MB allocated for 4.5 M items against ~10,721 ms
 * and 108 MB for the same work in batches of 5,000 (docs/DesignNotes.md → "The reader hands the parser one line at a
 * time…").
 *
 * That trade is only free if the batch shape never reaches a parser. So this suite feeds the SAME lines through the real
 * consumer loop in three different shapes and requires the same parsed stream out of all of them — same records, in the
 * same order — plus one test that puts a batch boundary BETWEEN two lines that a carry joins, which is the only way a
 * bundle could matter: PreviousAction writes on every line and a death reads it back as its last sentence, so if any
 * part of that carry were scoped to a batch instead of to the stream, the death at the head of batch two would come back
 * quoting nothing.
 *
 * What is NOT asserted here: wall time or allocation. Both are measurement, recorded in the section named above; a
 * threshold that passes on this box would be a coin flip on another.
 */
[TestClass]
[DoNotParallelize]
public class BatchHandoffEquivalenceTest
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

  // Twelve swings with a different amount each, so the ORDER of what came out is observable and not just the count,
  // and two deaths whose "Previous" is the line the parser saw immediately before them.
  private static string[] Fixture() =>
  [
    "[Wed Oct 01 12:00:01 2025] Kizant hits a gnawed ghoul for 101 points of damage.",
    "[Wed Oct 01 12:00:02 2025] Kizant hits a gnawed ghoul for 102 points of damage.",
    "[Wed Oct 01 12:00:03 2025] Kizant has been slain by a gnawed ghoul.",
    "[Wed Oct 01 12:00:04 2025] Bithika hits a gnawed ghoul for 104 points of damage.",
    "[Wed Oct 01 12:00:05 2025] Bithika hits a gnawed ghoul for 105 points of damage.",
    "[Wed Oct 01 12:00:06 2025] Vex hits a gnawed ghoul for 106 points of damage.",
    "[Wed Oct 01 12:00:07 2025] Vex hits a gnawed ghoul for 107 points of damage.",
    "[Wed Oct 01 12:00:08 2025] Kizant hits a gnawed ghoul for 108 points of damage.",
    "[Wed Oct 01 12:00:09 2025] Vex has been slain by a gnawed ghoul.",
    "[Wed Oct 01 12:00:10 2025] Bithika hits a gnawed ghoul for 110 points of damage.",
    "[Wed Oct 01 12:00:11 2025] Bithika hits a gnawed ghoul for 111 points of damage.",
    "[Wed Oct 01 12:00:12 2025] Bithika hits a gnawed ghoul for 112 points of damage.",
  ];

  private sealed record ParsedStream(string[] Damage, string[] Deaths);

  /*
   * Feed `lines` through one real LogProcessor (Main lane, exactly like the app's reader) with the queue bundling
   * `batchSize` lines per handoff. The last batch is whatever is left over, which is the shape a real file ends with.
   */
  private static ParsedStream Feed(int batchSize, string[] lines)
  {
    var damage = new List<string>();
    var deaths = new List<string>();
    Action<DamageProcessedEvent> onDamage = e => damage.Add(e.Action);
    Action<DeathEvent> onDeath = e => deaths.Add($"{e.Record.Killed}|{e.Record.Previous}");
    DamageLineParser.EventsDamageProcessed += onDamage;
    DamageLineParser.EventsNewDeath += onDeath;

    try
    {
      using var items = new BlockingCollection<LogReaderItem[]>(new ConcurrentQueue<LogReaderItem[]>());
      using var processor = new LogProcessor(new NullSinks(), new NullSinks());
      processor.LinkTo(items);

      for (var start = 0; start < lines.Length; start += batchSize)
      {
        var count = Math.Min(batchSize, lines.Length - start);
        var batch = new LogReaderItem[count];
        for (var i = 0; i < count; i++)
        {
          var line = lines[start + i];
          var dt = DateUtil.ParseStandardDate(line);
          Assert.AreNotEqual(DateTime.MinValue, dt, $"fixture line has no standard timestamp: {line}");
          batch[i] = new LogReaderItem(line, DateUtil.ToDotNetSeconds(dt), false);
        }

        items.Add(batch);
      }

      items.CompleteAdding();
      Assert.IsTrue(processor.Completion!.Wait(TimeSpan.FromSeconds(60)), "consumer did not drain");
      return new ParsedStream([.. damage], [.. deaths]);
    }
    finally
    {
      DamageLineParser.EventsDamageProcessed -= onDamage;
      DamageLineParser.EventsNewDeath -= onDeath;
    }
  }

  [TestMethod]
  public void EveryBatchShapeYieldsTheSameParsedStream()
  {
    var lines = Fixture();

    // 1 = the old per-line contract (one handoff per line); 7 = a bundle that does not divide the file; 20 = the whole
    // file in one batch, where a consumer that trusted "a batch is full" would fall over.
    var perLine = Feed(1, lines);
    var ragged = Feed(7, lines);
    var single = Feed(lines.Length, lines);

    Assert.IsTrue(perLine.Damage.Length >= 10,
      $"the fixture stopped producing damage records ({perLine.Damage.Length}); the comparison would be vacuous");
    Assert.AreEqual(perLine.Damage.Length, ragged.Damage.Length, "a different number of damage records per batch shape");
    Assert.AreEqual(perLine.Damage.Length, single.Damage.Length, "a different number of damage records per batch shape");

    // Sequence equality, not set equality: the amounts encode arrival order, and a bundle that reordered lines would
    // show up here as 108 before 104.
    CollectionAssert.AreEqual(perLine.Damage, ragged.Damage, "batching changed the ORDER damage reached the pipeline");
    CollectionAssert.AreEqual(perLine.Damage, single.Damage, "one big batch parsed differently than many small ones");

    Assert.AreEqual(2, perLine.Deaths.Length, "the fixture's two death lines stopped producing death records");
    CollectionAssert.AreEqual(perLine.Deaths, ragged.Deaths, "batching changed what the death records carried");
    CollectionAssert.AreEqual(perLine.Deaths, single.Deaths, "batching changed what the death records carried");
  }

  [TestMethod]
  public void ACarryCrossesABatchBoundary()
  {
    // The one shape that could matter: the sentence before a death is written while parsing the last line of batch one,
    // and read by the first line of batch two. A carry scoped to a batch would leave this death quoting nothing, while
    // every same-batch test stayed green.
    var split = Feed(1,
    [
      "[Wed Oct 01 12:00:01 2025] Ghoul hits Kizant for 12 points of damage.",
      "[Wed Oct 01 12:00:02 2025] Kizant has been slain by a gnawed ghoul.",
    ]);

    Assert.AreEqual(1, split.Deaths.Length, "the death line produced no record");
    Assert.AreEqual("Kizant|Ghoul hits Kizant for 12 points of damage.", split.Deaths[0],
      "a carry did not survive the boundary between two batches — carries belong to the stream, not to a bundle");

    // Control: the same two lines inside ONE batch must read identically, so the assertion above measures the boundary
    // and not something about these lines.
    var together = Feed(20,
    [
      "[Wed Oct 01 12:00:01 2025] Ghoul hits Kizant for 12 points of damage.",
      "[Wed Oct 01 12:00:02 2025] Kizant has been slain by a gnawed ghoul.",
    ]);

    CollectionAssert.AreEqual(split.Deaths, together.Deaths, "a carry behaved differently across a batch boundary");
  }

  [TestMethod]
  public void APartialFinalBatchStillCrossesWhole()
  {
    // A reader flushes what it holds at end of file, however little that is; nothing may require a full bundle.
    var result = Feed(5, Fixture()[..7]);

    Assert.AreEqual(6, result.Damage.Length, "the leftover partial batch (2 lines past the last full one) did not arrive");
  }
}

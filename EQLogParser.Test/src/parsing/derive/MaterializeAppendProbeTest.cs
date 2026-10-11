using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using EQLogParser;

namespace EQLogParser;

/*
 * A GATED COST PROBE, not a CI assertion: what layer 1 of the materialization cache actually saves on a real capture
 * (docs/DesignNotes.md → "Layer 1 is built", where the measured numbers live; the decision it implemented was
 * docs/DesignNotes.md → "What the cell cache has to buy at two boundaries").
 *
 * `FightFactIndex.SummaryFightFor` used to answer "this row moved" by re-walking every ordinal the row ever held, so a live
 * pull paid for records that cannot have changed. It now continues from the count it last consumed. `RowReuseCeilingProbeTest`
 * measured the CEILING of whole-row reuse (~20 % of a recount); this measures the REALIZED saving of what was built, on the
 * same two arms over one growing table:
 *
 *   COLD - every row materialized through a FRESH `FightFactIndex` (a brand-new index is exactly what a rebuilt pass had, and
 *          a fresh index has nothing to continue from): this is pre-layer-1 behaviour.
 *   WARM - the SAME rows materialized through the index the carry keeps alive: what a live pass does now.
 *
 * Both arms answer the identical question ("what does every row's fight look like as it stands"), only the cache differs, and
 * ONLY the per-row materialization loop is timed in each — projection cost is not what this changed, so it must not ride in the
 * number. Bytes are printed beside milliseconds because the saving is mostly allocation (blocks and DamageRecords that are no
 * longer rebuilt), and `ws` is not `heap` (AGENTS).
 *
 * The tail is simulated honestly: ONE fact table, fed in chunks, with a projection between chunks. The timeline is classified
 * once, on the first chunk, and reused — which is what the CHEAP lane does (it runs no rule book at all), and it is also the
 * conservative arm: a classification that keeps moving would rebuild rows and therefore materializations, narrowing the win.
 * Quiescence between chunks is observed by polling the captured count, same idea as DeriveCadence.
 *
 * Run it (an ABSOLUTE path: the test host's working directory is the bin folder, and `EnsureDataStore` moves it):
 *   EQLP_MATERIALIZE_APPEND="$PWD/local/logs/live/eqlog_Incogitable_xegony.txt" EQLP_MATERIALIZE_CHUNKS=6 \
 *     dotnet test EQLogParser.Test/EQLogParser.Test.csproj --no-build --filter MaterializeAppendProbe --logger "console;verbosity=detailed"
 *
 * It asserts one thing besides running: the two arms agree on what they materialized, every pass — a saving measured on an arm that
 * produced a different answer is not a saving. Kept as the NAMED re-measure command for this seam (the way `RowReuseCeilingProbeTest`
 * and `MeterBoardCostRealLogTest` are kept) because layer 2's gate asks the same question again — "how much of a live pass is left after
 * appending stopped being re-walked" — and that number needs the same harness, not a reconstruction.
 */
[TestClass]
[DoNotParallelize]
public class MaterializeAppendProbeTest
{
  [TestMethod]
  public void MaterializeAppend_WarmAgainstCold()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_MATERIALIZE_APPEND");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
      Assert.Inconclusive("set EQLP_MATERIALIZE_APPEND=<log>");

    PipelineHarness.EnsureDataStore();
    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear(false);

    var chunks = int.TryParse(Environment.GetEnvironmentVariable("EQLP_MATERIALIZE_CHUNKS"), out var c) && c > 1 ? c : 5;
    /*
     * Streaming, not ToList(): a night is millions of lines and this probe exists to talk about bytes. The enumerator is pulled
     * chunk by chunk between projections, which is also the only way the tail arrives like a tail - one batch at a time.
     */
    using var lines = File.ReadLines(path).GetEnumerator();
    var lineCount = 0;

    var facts = new DamageFactTable(100_000);
    var heals = new HealFactTable(facts);
    var capture = new CombatCapture(facts, heals);
    capture.Start();

    using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), 100_000);
    // Side channels inert, exactly as PipelineHarness runs them headless.
    using var processor = new LogProcessor(path, new InertSinks(), new InertSinks());
    processor.LinkTo(items);

    // One cheap count pass (page-cached, no parsing) so "chunks" means chunks of the capture rather than of a guess.
    var totalLines = File.ReadLines(path).Count(static l => l.Length > 28);
    var perChunk = Math.Max(1, totalLines / chunks);
    EntityTimeline? timeline = null;
    FightProjection.FightProjectionCache? cache = null;
    double firstTs = double.NaN, lastTs = double.NaN;

    var coldMs = 0.0; var warmMs = 0.0;
    var coldBytes = 0L; var warmBytes = 0L;
    long coldRecords = 0, warmRecords = 0, coldBlocks = 0, warmBlocks = 0;
    var rowsPerPass = 0;

    var pass = 0;
    while (true)
    {
      pass++;
      var fed = 0;
      while (fed < perChunk && lines.MoveNext())
      {
        var line = lines.Current;
        if (line.Length <= 28) continue;
        var dt = DateUtil.ParseStandardDate(line);
        if (dt == DateTime.MinValue) continue;

        var ts = DateUtil.ToDotNetSeconds(dt);
        if (double.IsNaN(firstTs)) firstTs = ts;
        lastTs = ts;
        items.Add(new LogReaderItem(line, ts, false));
        lineCount++;
        fed++;
      }

      if (fed == 0) break;

      WaitForIngest(facts, heals);

      // First chunk: the rule book runs and the projection is born. Every later pass rides the carry with those verdicts
      // (the cheap lane), which is what keeps AnswerStamp still enough for a continuation to be possible at all.
      if (timeline is null)
      {
        timeline = new EntityTimeline();
        RegistrySeed.Apply(timeline, facts, firstTs, lastTs);
        ClassificationRules.Apply(facts, timeline, heals);
        cache = new FightProjection.FightProjectionCache();
      }

      var rows = cache!.Project(facts, timeline);
      Sectionizer.StampGroupIds(rows);
      var visible = CharmPetRows.Visible(rows);
      rowsPerPass = Math.Max(rowsPerPass, visible.Count);

      /*
       * ---- WARM: the index the carry keeps alive. ----
       * Timed on its own; the string that compares the arms is built afterwards, because a comparison cost sitting inside both
       * numbers would hide the difference it is supposed to reveal.
       */
      var warmAlloc = GC.GetAllocatedBytesForCurrentThread();
      var warmSw = Stopwatch.StartNew();
      var warmFights = Materialize(visible, facts, cache.Index);
      warmSw.Stop();
      warmMs += warmSw.Elapsed.TotalMilliseconds;
      var warmAllocDelta = GC.GetAllocatedBytesForCurrentThread() - warmAlloc;
      warmBytes += warmAllocDelta;

      /*
       * ---- COLD: a fresh index over the same table. ----
       * A new FightFactIndex is what any pass that REBUILT had (the cache died with the rows it belonged to), so this arm is
       * the pre-layer-1 code path rather than an approximation of it. Not timed: building its ordinals is projection work, and
       * layer 1 changed none of that.
       */
      var fresh = new FightFactIndex(timeline);
      var freshRows = FightProjection.Build(facts, timeline, fresh.OnFact);
      Sectionizer.StampGroupIds(freshRows);

      var coldAlloc = GC.GetAllocatedBytesForCurrentThread();
      var coldSw = Stopwatch.StartNew();
      var coldFights = Materialize(CharmPetRows.Visible(freshRows), facts, fresh);
      coldSw.Stop();
      coldMs += coldSw.Elapsed.TotalMilliseconds;
      var coldAllocDelta = GC.GetAllocatedBytesForCurrentThread() - coldAlloc;
      coldBytes += coldAllocDelta;

      var coldRender = TextOf(coldFights);
      var warmRender = TextOf(warmFights);
      Assert.AreEqual(coldRender.Text, warmRender.Text,
        $"pass {pass}: the continued arm materialized a different answer than the full one");

      coldRecords += coldRender.Records; warmRecords += warmRender.Records;
      coldBlocks += coldRender.Blocks; warmBlocks += warmRender.Blocks;

      Console.WriteLine($"[materialize] pass {pass}: facts={facts.FactCount:N0} rows={visible.Count:N0} " +
                        $"cold={coldSw.Elapsed.TotalMilliseconds:F0} ms warm={warmSw.Elapsed.TotalMilliseconds:F0} ms | " +
                        $"alloc cold={FormatBytes(coldAllocDelta)} warm={FormatBytes(warmAllocDelta)}");
    }

    items.CompleteAdding();
    capture.Stop();

    Console.WriteLine($"[materialize] TOTAL {Path.GetFileName(path)} passes={pass - 1} lines={lineCount:N0} rows(max)={rowsPerPass:N0}");
    Console.WriteLine($"[materialize]   cold (fresh index, pre-layer-1): {coldMs:F0} ms, {FormatBytes(coldBytes)}, " +
                      $"{coldBlocks:N0} blocks, {coldRecords:N0} records");
    Console.WriteLine($"[materialize]   warm (continued, layer 1)      : {warmMs:F0} ms, {FormatBytes(warmBytes)}, " +
                      $"{warmBlocks:N0} blocks, {warmRecords:N0} records");
    Console.WriteLine(coldMs > 0
      ? $"[materialize]   saving: x{(coldMs / Math.Max(warmMs, 0.001)):F2} time, " +
        $"{100.0 * (1.0 - warmBytes / (double)Math.Max(coldBytes, 1)):F1}% bytes"
      : "[materialize]   no cold work measured");
  }

  // The timed question: every visible row's Fight, as it stands right now. Nothing else happens in here.
  private static List<Fight> Materialize(IReadOnlyList<DerivedFight> rows, DamageFactTable facts, FightFactIndex index)
  {
    var built = new List<Fight>(rows.Count);
    foreach (var row in rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.BeginTime))
    {
      var fight = index.SummaryFightFor(row, facts);
      if (fight is not null) built.Add(fight);
    }
    return built;
  }

  // Everything the materializer produced, as one string (compared exactly between arms) plus the two counts that say how much
  // work it was: blocks opened and records built.
  private static (string Text, long Blocks, long Records) TextOf(List<Fight> fights)
  {
    var sb = new StringBuilder();
    long blocks = 0, records = 0;
    foreach (var fight in fights)
    {

      sb.Append(fight.Name).Append('@').Append(fight.BeginTime).Append('-').Append(fight.LastTime)
        .Append("|h").Append(fight.DamageHits).Append('/').Append(fight.TankHits)
        .Append("|t").Append(fight.DamageTotal).Append('/').Append(fight.TankTotal)
        .Append("|w").Append(fight.BeginDamageTime).Append('/').Append(fight.LastDamageTime)
        .Append(fight.BeginTankingTime).Append('/').Append(fight.LastTankingTime)
        .Append("|b").Append(fight.DamageBlocks.Count).Append('/').Append(fight.TankingBlocks.Count);

      blocks += fight.DamageBlocks.Count + fight.TankingBlocks.Count;

      foreach (var block in fight.DamageBlocks.Concat(fight.TankingBlocks))
        foreach (var action in block.Actions.Cast<DamageRecord>())
        {
          records++;
          sb.Append(";").Append(action.Attacker).Append('>').Append(action.Defender)
            .Append(':').Append(action.Total).Append(action.Type).Append('#').Append(action.SubType);
        }

      sb.Append('\n');
    }
    return (sb.ToString(), blocks, records);
  }

  // Chat and triggers do nothing in a headless probe (same posture as PipelineHarness's sinks).
  private sealed class InertSinks : IChatSink, ITriggerHook
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

  private static string FormatBytes(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

  /*
   * Wait until the captured count holds still: the parse lane is a background consumer, so projecting mid-chunk would measure a
   * different amount of facts than the arm that follows. Polling the count (not sleeping a fixed time) keeps this honest on a
   * machine that is slower or faster than the one that wrote it.
   */
  private static void WaitForIngest(DamageFactTable facts, HealFactTable heals)
  {
    long previous = -1;
    var stable = 0;
    var deadline = DateTime.UtcNow.AddMinutes(5);

    while (DateTime.UtcNow < deadline && stable < 3)
    {
      var current = (long)facts.FactCount + heals.HealCount;
      stable = current == previous ? stable + 1 : 0;
      previous = current;
      Thread.Sleep(100);
    }

    Assert.IsTrue(stable >= 3, $"ingest did not settle within 5 minutes ({facts.FactCount:N0} facts)");
  }
}

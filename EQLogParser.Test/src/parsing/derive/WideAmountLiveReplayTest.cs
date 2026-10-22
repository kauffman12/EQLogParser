using System.Collections.Concurrent;

namespace EQLogParser;

[TestClass]
public class WideAmountLiveReplayTest
{
  [TestMethod]
  public void EightHoursOfOrderedGrowthAcceptsLateWideDamageAndHealing()
  {
    PipelineHarness.SharedDataStore();
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear(false);
    DamageLineParser.ResetProcessState();
    var priorEmu = AppSettings.IsEmuParsingEnabled;
    AppSettings.IsEmuParsingEnabled = false;
    var facts = new DamageFactTable(0);
    var heals = new HealFactTable(facts, 0);
    var timeline = new EntityTimeline();
    var capture = new CombatCapture(facts, heals);
    var sink = new Sink(capture);
    using var queue = new BlockingCollection<LogReaderItem[]>();
    using var processor = new LogProcessor(sink, sink);
    var origin = new DateTime(2026, 10, 11, 0, 0, 0);
    long sent = 0;
    var refreshes = 0;
    capture.Start();
    processor.LinkTo(queue);
    try
    {
      Send(origin, "Kilsef has joined the raid.", "Reisil has joined the raid.");
      for (var minute = 0; minute < 480; minute++)
      {
        var last = minute == 479;
        var amount = last ? TextUtils.MaxAmount : 100;
        Send(origin.AddMinutes(minute),
          $"Kilsef slashes a training dummy for {amount} points of damage.",
          $"A training dummy hits Kilsef for {amount} points of damage.",
          $"Reisil healed Kilsef for {amount} ({amount}) hit points by Greater Renewal.");
        if ((minute + 1) % 30 != 0) continue;
        Assert.IsTrue(SpinWait.SpinUntil(() => processor.ProcessedLines == sent, TimeSpan.FromSeconds(30)));
        ClassificationRules.Apply(facts, timeline, heals);
        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        var input = FightSummarySource.Build(rows.TakeLast(1).ToArray(), index, facts);
        var options = new GenerateStatsOptions
        {
          Source = "wide live replay", AllRanges = input.AllRanges,
          Heals = HealSummarySource.Materialize(heals, input.AllRanges), CollectHitTotals = false
        };
        options.Npcs.AddRange(input.Fights);
        DamageStatsBuilder.Instance.BuildTotalStats(options);
        HealingStatsBuilder.Instance.BuildTotalStats(options);
        Assert.AreEqual(amount, DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats.Total);
        Assert.AreEqual(amount, HealingStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats.Total);
        refreshes++;
      }
      Assert.AreEqual(16, refreshes);
      Assert.AreEqual(960, facts.FactCount);
      Assert.AreEqual(480, heals.HealCount);
      Assert.AreEqual(TextUtils.MaxAmount, facts.At(959).Total);
      Assert.AreEqual(TextUtils.MaxAmount, heals.At(479).OverTotal);
      Assert.AreEqual(100L, facts.At(0).Total, "refreshes must not replace historical rows");
    }
    finally
    {
      if (!queue.IsAddingCompleted) queue.CompleteAdding();
      processor.Completion.GetAwaiter().GetResult();
      capture.Stop();
      AppSettings.IsEmuParsingEnabled = priorEmu;
      DamageLineParser.ResetProcessState();
      RecordsStore.Instance.Clear(false);
      PlayerRegistry.Instance.Clear();
      HealTap.Clear();
    }

    void Send(DateTime date, params string[] actions)
    {
      var prefix = date.ToString("[ddd MMM dd HH:mm:ss yyyy] ", System.Globalization.CultureInfo.InvariantCulture);
      var time = DateUtil.ToDotNetSeconds(date);
      queue.Add(actions.Select(action => new LogReaderItem(prefix + action, time, true)).ToArray());
      sent += actions.Length;
    }
  }

  private sealed class Sink(CombatCapture capture) : IChatSink, ITriggerHook
  {
    public void Init() { }
    public void Add(ChatType chat) => capture.HandleChat(chat);
    public void CheckQuickShare(ChatType chat, string action, double beginTime) { }
  }
}

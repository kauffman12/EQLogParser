using EQLogParser.Audio;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace EQLogParser.Wpf.Test;

[TestClass]
public class TimerResetAnnouncementTest
{
  // Exercise the processor's scheduling, restart and stop paths with the completed row absent from _timerLists.
  // Countdown retirement is performed at its record seam so these tests need neither a dispatcher nor overlay windows.
  [TestMethod]
  [DataRow(1)]
  [DataRow(2)]
  public async Task RestartAfterCountdownCompletionCancelsOldResetAndRearms(int restartOption)
  {
    await using var fixture = new ProcessorFixture(restartOption);
    var old = fixture.StartAndFinishCountdown("old", 60);
    var token = old.ResetSource.Token;

    Assert.IsTrue(await fixture.PrepareStartAsync("timer"));
    Assert.IsTrue(token.IsCancellationRequested, "Restart must find the reset even though its countdown row is gone.");
    Assert.AreEqual(0, fixture.PendingCount);

    fixture.StartAndFinishCountdown("replacement", 0.02);
    await WaitUntilAsync(() => fixture.Speech.Count == 1 && fixture.PendingCount == 0);
    Assert.AreEqual("replacement", fixture.Speech.Single().Action);
    Assert.AreEqual(1, fixture.Logs.Count(log => log.Type == "Timer Reset"));
  }

  [TestMethod]
  [DataRow(0)]
  [DataRow(3)]
  [DataRow(4)]
  public async Task NonRestartOptionsLeaveTheOriginalResetAlone(int option)
  {
    await using var fixture = new ProcessorFixture(option);
    var old = fixture.StartAndFinishCountdown("original", 60);
    var token = old.ResetSource.Token;

    Assert.IsTrue(await fixture.PrepareStartAsync("timer"));
    Assert.IsFalse(token.IsCancellationRequested);
    Assert.AreEqual(1, fixture.PendingCount);
  }

  [TestMethod]
  public async Task RestartSameNameLeavesOtherNamesPending()
  {
    await using var fixture = new ProcessorFixture(2);
    var old = fixture.StartAndFinishCountdown("original", 60);
    var token = old.ResetSource.Token;

    Assert.IsTrue(await fixture.PrepareStartAsync("different name"));
    Assert.IsFalse(token.IsCancellationRequested);
    Assert.AreEqual(1, fixture.PendingCount);
  }

  [TestMethod]
  [DataRow("stop")]
  [DataRow("disable")]
  [DataRow("dispose")]
  public async Task CleanupCancelsResetAfterCountdownCompletion(string action)
  {
    await using var fixture = new ProcessorFixture(1);
    var old = fixture.StartAndFinishCountdown("old", 60);
    var token = old.ResetSource.Token;
    switch (action)
    {
      case "stop":
        await fixture.Processor.StopTriggersAsync();
        break;
      case "disable":
        await fixture.DisableAsync();
        break;
      case "dispose":
        await fixture.Processor.DisposeAsync();
        break;
    }

    Assert.IsTrue(token.IsCancellationRequested);
    Assert.AreEqual(0, fixture.PendingCount);
    Assert.AreEqual(0, fixture.Speech.Count);
  }

  [TestMethod]
  public async Task OverdueResetUsesTheStartDeadlineInsteadOfWaitingAnotherDuration()
  {
    await using var fixture = new ProcessorFixture(1);
    fixture.StartAndFinishCountdown("overdue", 60, DateTime.UtcNow.AddMinutes(-2).Ticks);

    await WaitUntilAsync(() => fixture.Speech.Count == 1 && fixture.PendingCount == 0);
    Assert.AreEqual("overdue", fixture.Speech.Single().Action);
    Assert.AreEqual(1, fixture.Logs.Count(log => log.Type == "Timer Reset"));
  }

  [TestMethod]
  public async Task StopCancelsADueResetWaitingBehindAnExecutingTrigger()
  {
    await using var fixture = new ProcessorFixture(1);
    await fixture.Gate.WaitAsync();
    Task stopping;
    try
    {
      // Stop queues behind the running trigger before the due reset can claim the same gate.
      stopping = fixture.Processor.StopTriggersAsync();
      fixture.StartAndFinishCountdown("due", 60, DateTime.UtcNow.AddMinutes(-2).Ticks);
    }
    finally
    {
      fixture.Gate.Release();
    }

    await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.AreEqual(0, fixture.PendingCount);
    Assert.AreEqual(0, fixture.Speech.Count);
  }

  [TestMethod]
  public async Task ZeroResetOrBlankActionsScheduleNothing()
  {
    await using var fixture = new ProcessorFixture(1);
    fixture.StartAndFinishCountdown("zero", 0);
    fixture.Wrapper.TriggerData.ResetTextToSpeak = null;
    fixture.Wrapper = new TriggerWrapper { Id = "trigger", TriggerData = fixture.Wrapper.TriggerData };
    fixture.StartAndFinishCountdown("blank", 60);
    Assert.AreEqual(0, fixture.PendingCount);
    Assert.AreEqual(0, fixture.Speech.Count);
  }

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
      Assert.IsLessThan(TimeSpan.FromSeconds(5), clock.Elapsed, "The reset worker did not answer.");
      await Task.Delay(10);
    }
  }

  private static T Field<T>(TriggerProcessor processor, string name) =>
    typeof(TriggerProcessor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(processor) is T value
      ? value : throw new InvalidOperationException($"Missing processor field {name}");

  private static object? Invoke(TriggerProcessor processor, string name, params object?[] arguments) =>
    (typeof(TriggerProcessor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException($"Missing processor method {name}")).Invoke(processor, arguments);

  private sealed class ProcessorFixture : IAsyncDisposable
  {
    internal readonly TriggerProcessor Processor;
    internal TriggerWrapper Wrapper;
    internal readonly SemaphoreSlim Gate;
    internal readonly BlockingCollection<Speak> Speech;
    internal readonly BlockingCollection<TriggerLogItem> Logs;
    private readonly TriggerStateDB _store = new(null, applyLegacyUpgrades: false);
    private readonly List<TimerData> _pending;
    private readonly List<TimerData> _countdowns = [];

    internal ProcessorFixture(int option)
    {
      var id = Guid.NewGuid().ToString("N");
      Processor = new(id, id, id, "", 0, 0, null, null, null, null,
        new Mock<IAudioManager>().Object, _store);
      Wrapper = new TriggerWrapper
      {
        Id = "trigger",
        TriggerData = new Trigger { TriggerAgainOption = option, ResetTextToSpeak = "ready" },
        ModifiedResetSpeak = "ready"
      };
      Field<Dictionary<string, TriggerWrapper>>(Processor, "_activeTriggersById").Add(Wrapper.Id, Wrapper);
      Field<ConcurrentDictionary<string, List<TimerData>>>(Processor, "_timerLists").TryAdd(Wrapper.Id, _countdowns);
      _pending = Field<List<TimerData>>(Processor, "_pendingTimerResets");
      Speech = Field<BlockingCollection<Speak>>(Processor, "_speakCollection");
      Logs = Field<BlockingCollection<TriggerLogItem>>(Processor, "_triggerLogCollection");
      Gate = Field<SemaphoreSlim>(Processor, "_activeTriggerSemaphore");
    }

    internal int PendingCount
    {
      get { lock (_pending) return _pending.Count; }
    }

    internal TimerData StartAndFinishCountdown(string action, double resetSeconds, long? begin = null)
    {
      Wrapper.TriggerData.ResetDurationSeconds = resetSeconds;
      var data = new TimerData
      {
        TriggerId = Wrapper.Id,
        DisplayName = "timer",
        ResetTicks = TimerLifecycle.ResetTicks(begin ?? DateTime.UtcNow.Ticks, resetSeconds),
        CancelSource = new CancellationTokenSource()
      };
      lock (_countdowns) _countdowns.Add(data);
      Invoke(Processor, "ScheduleTimerReset", Wrapper, data, new LineData { Action = action }, null);
      lock (_countdowns)
      {
        TimerDataUtil.CleanupTimerData(data);
        _countdowns.Remove(data);
      }
      return data;
    }

    internal async Task<bool> PrepareStartAsync(string name)
    {
      await Gate.WaitAsync();
      try
      {
        return await (Invoke(Processor, "PrepareTimerStartAsync", Wrapper, name) as Task<bool>
          ?? throw new InvalidOperationException("Timer preparation did not return a task"));
      }
      finally
      {
        Gate.Release();
      }
    }

    internal Task DisableAsync() => Invoke(Processor, "SetActiveTriggersAsync",
      new Dictionary<string, TriggerWrapper>(), new HashSet<string>()) as Task
      ?? throw new InvalidOperationException("Trigger disable did not return a task");

    public async ValueTask DisposeAsync()
    {
      await Processor.DisposeAsync();
      await _store.Dispose();
    }
  }
}

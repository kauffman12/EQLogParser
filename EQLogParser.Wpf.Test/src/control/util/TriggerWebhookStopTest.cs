using EQLogParser.Audio;
using Moq;
using System.Reflection;

namespace EQLogParser.Wpf.Test;

[TestClass]
public class TriggerWebhookStopTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task StopClearsMessagesEnqueuedWhileWaitingForTheActiveTrigger(bool disposeWhileWaiting)
  {
    await using var fixture = new ProcessorFixture();
    await fixture.Gate.WaitAsync();
    var beforeStop = fixture.Enqueue("already waiting");
    var stopping = fixture.Processor.StopTriggersAsync();
    var late = Task.CompletedTask;
    var next = Task.CompletedTask;
    var gateHeld = true;
    try
    {
      await beforeStop.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.IsFalse(stopping.IsCompleted, "Stop must wait for the executing trigger, not skip its cleanup.");
      late = fixture.Enqueue("the executing trigger finished its action");
      Assert.IsFalse(late.IsCompleted);
      if (disposeWhileWaiting) await fixture.Processor.DisposeAsync();
      gateHeld = false;
      fixture.Gate.Release();
      await stopping.WaitAsync(TimeSpan.FromSeconds(5));
      await late.WaitAsync(TimeSpan.FromSeconds(5));
      fixture.Audio.Verify(audio => audio.StopAudio(fixture.Character, false),
        disposeWhileWaiting ? Times.Never() : Times.Once());
      Assert.AreEqual(1, fixture.Gate.CurrentCount, "Every return path must release the trigger gate.");
      Assert.IsNull(MainActions.DiscordWebhooks.ErrorFor(fixture.Url, fixture.Character, "loot"));

      next = fixture.Enqueue("next match");
      Assert.AreEqual(disposeWhileWaiting, next.IsCompleted,
        "Ordinary Stop keeps future matches usable; disposal rejects the retired source.");
      fixture.Processor.CancelPendingWebhooks();
      await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally
    {
      fixture.Processor.CancelPendingWebhooks();
      if (gateHeld) fixture.Gate.Release();
      await stopping.WaitAsync(TimeSpan.FromSeconds(5));
      await Task.WhenAll(beforeStop, late, next).WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task AllCharactersAreCancelledBeforeTheFirstCharactersCleanupCanWait(bool cleanupFails)
  {
    await using var first = new ProcessorFixture();
    await using var second = new ProcessorFixture();
    if (cleanupFails)
      first.Audio.Setup(audio => audio.StopAudio(first.Character, false)).Throws<InvalidOperationException>();
    await first.Gate.WaitAsync();
    var firstPending = first.Enqueue("first character");
    var secondPending = second.Enqueue("second character");
    var stopping = TriggerManager.StopProcessorsAsync([first.Processor, second.Processor]);
    var late = Task.CompletedTask;
    var gateHeld = true;
    try
    {
      await Task.WhenAll(firstPending, secondPending).WaitAsync(TimeSpan.FromSeconds(5));
      Assert.IsFalse(stopping.IsCompleted, "The first character is still executing a trigger.");
      first.Audio.Verify(audio => audio.StopAudio(first.Character, false), Times.Never());
      second.Audio.Verify(audio => audio.StopAudio(second.Character, false), Times.Never());
      late = first.Enqueue("first character's final action");
      gateHeld = false;
      first.Gate.Release();
      if (cleanupFails)
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
      else
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
      await late.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.AreEqual(1, first.Gate.CurrentCount, "A cleanup failure must not strand the trigger gate.");
      first.Audio.Verify(audio => audio.StopAudio(first.Character, false), Times.Once());
      second.Audio.Verify(audio => audio.StopAudio(second.Character, false), cleanupFails ? Times.Never() : Times.Once());
    }
    finally
    {
      first.Processor.CancelPendingWebhooks();
      second.Processor.CancelPendingWebhooks();
      if (gateHeld) first.Gate.Release();
      try
      {
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
      }
      catch (InvalidOperationException) when (cleanupFails)
      {
        // The failure itself is asserted above; teardown must still wait for every queued task.
      }
      await Task.WhenAll(firstPending, secondPending, late).WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  private static T Field<T>(TriggerProcessor processor, string name) =>
    typeof(TriggerProcessor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(processor) is T value
      ? value : throw new InvalidOperationException($"Missing processor field {name}");

  private sealed class ProcessorFixture : IAsyncDisposable
  {
    internal readonly string Character = Guid.NewGuid().ToString("N");
    internal readonly Mock<IAudioManager> Audio = new(MockBehavior.Strict);
    internal readonly TriggerProcessor Processor;
    internal readonly SemaphoreSlim Gate;
    internal readonly string Url;
    private readonly TriggerStateDB _store = new(null, applyLegacyUpgrades: false);
    private readonly WebhookSource _source;

    public async ValueTask DisposeAsync()
    {
      await Processor.DisposeAsync();
      await _store.Dispose();
      Gate.Dispose();
    }

    internal ProcessorFixture()
    {
      Audio.Setup(audio => audio.Add(Character, ""));
      Audio.Setup(audio => audio.StopAudio(Character, false));
      Processor = new(Character, Character, Character, "", 0, 0, null, null, null, null, Audio.Object, _store);
      Gate = Field<SemaphoreSlim>(Processor, "_activeTriggerSemaphore");
      var owner = Field<string>(Processor, "_webhookOwner");
      _source = MainActions.DiscordWebhooks.UpdateSources(owner, Character, [("loot", "Loot")])["loot"];
      // Even a broken test that leaves this window alive cannot send to a real HTTP endpoint.
      Url = $"invalid-{Character}";
    }

    internal Task Enqueue(string content) => MainActions.DiscordWebhooks.EnqueueAsync(_source, Url, content, 30);
  }
}

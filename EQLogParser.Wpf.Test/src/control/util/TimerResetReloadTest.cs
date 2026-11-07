using Moq;
using System.Reflection;

namespace EQLogParser.Wpf.Test;

public partial class TimerResetAnnouncementTest
{
  [TestMethod]
  [DataRow(false, false)]
  [DataRow(false, true)]
  [DataRow(true, false)]
  [DataRow(true, true)]
  public async Task ReloadReadinessDoesNotDiscardDueResetAudio(bool sound, bool reloadStartsBeforeDeadline)
  {
    await using var fixture = new ProcessorFixture(1);
    ConfigureReset(fixture, "ready", sound: sound ? "ready.wav" : null);
    EnableSpeech(fixture);
    if (reloadStartsBeforeDeadline) SetReady(fixture.Processor, false);

    fixture.StartAndFinishCountdown("match", 0.05,
      reloadStartsBeforeDeadline ? null : DateTime.UtcNow.AddMinutes(-2).Ticks);
    await WaitUntilAsync(() => fixture.Speech.Count == 1 && fixture.PendingCount == 0);
    // GetActiveTriggersAsync holds this state while rebuilding the catalog. Also cover reload starting AFTER enqueue.
    SetReady(fixture.Processor, false);
    Assert.IsTrue(fixture.Speech.TryTake(out var speak));
    Invoke(fixture.Processor, "HandleSpeech", speak);
    SetReady(fixture.Processor, true);

    Assert.AreEqual(0, fixture.Speech.Count, "The consumer must deliver the reset, not require a requeue or retry.");
    if (sound)
    {
      fixture.Audio.Verify(audio => audio.SpeakFileAsync(fixture.Processor.CurrentCharacterId,
        TriggerUtil.ResolveSoundPath("ready.wav"), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once());
      fixture.Audio.Verify(audio => audio.SpeakTtsAsync(It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never());
    }
    else
    {
      fixture.Audio.Verify(audio => audio.SpeakTtsAsync(fixture.Processor.CurrentCharacterId, "ready",
        It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once());
      fixture.Audio.Verify(audio => audio.SpeakFileAsync(It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never());
    }
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task AllowingResetAudioDuringReloadDoesNotAllowAudioAfterDisposal(bool sound)
  {
    await using var fixture = new ProcessorFixture(1);
    ConfigureReset(fixture, "ready", sound: sound ? "ready.wav" : null);
    EnableSpeech(fixture);
    fixture.StartAndFinishCountdown("match", 60, DateTime.UtcNow.AddMinutes(-2).Ticks);
    await WaitUntilAsync(() => fixture.Speech.Count == 1 && fixture.PendingCount == 0);
    Assert.IsTrue(fixture.Speech.TryTake(out var speak));

    await fixture.Processor.DisposeAsync();
    Invoke(fixture.Processor, "HandleSpeech", speak);

    fixture.Audio.Verify(audio => audio.SpeakTtsAsync(It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never());
    fixture.Audio.Verify(audio => audio.SpeakFileAsync(It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never());
  }

  [TestMethod]
  public async Task OrdinarySpeechRetainsItsReadinessGuard()
  {
    await using var fixture = new ProcessorFixture(1);
    EnableSpeech(fixture);
    SetReady(fixture.Processor, false);
    Invoke(fixture.Processor, "HandleSpeech", new Speak
    {
      IsPrimary = true,
      Wrapper = fixture.Wrapper,
      TtsOrSound = "ordinary speech"
    });
    fixture.Audio.Verify(audio => audio.SpeakTtsAsync(It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never());
  }

  private static void SetReady(TriggerProcessor processor, bool ready) =>
    (typeof(TriggerProcessor).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("Missing readiness field")).SetValue(processor, ready);
}

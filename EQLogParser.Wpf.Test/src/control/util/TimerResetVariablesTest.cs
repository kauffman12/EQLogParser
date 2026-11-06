using Moq;
using System.Collections.Concurrent;
using System.Reflection;

namespace EQLogParser.Wpf.Test;

public partial class TimerResetAnnouncementTest
{
  [TestMethod]
  public async Task ResetSpeechAndDisplayResolveTheSameCodesFromOneResetTimeSnapshot()
  {
    await using var fixture = new ProcessorFixture(1);
    const string template = "{C};{TIMER-WARN-TIME-VALUE};{L};{S1.upper};{n1.number};{p1.lower};{orig.capitalize};"
      + "${GABILITY.upper};{gPad.padleft:4};{gPad.padright:4};{gPad.center:5};{counter};{repeated};{logtime}";
    fixture.Wrapper.TriggerData.WarningSeconds = 7;
    ConfigureReset(fixture, template);
    var variables = Field<ConcurrentDictionary<string, string>>(fixture.Processor, "_variables");
    variables["gAbility"] = "old";
    variables["gPad"] = "Q";
    variables["gExpired"] = "must not survive";
    Field<Dictionary<string, long>>(fixture.Processor, "_variableExpiryTimes")["gExpired"] = DateTime.UtcNow.AddSeconds(-1).Ticks;
    var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["s1"] = "Alice", ["n1"] = "123.6" };
    var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["p1"] = "PREVIOUS" };
    var original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["orig"] = "origin" };
    var line = new LineData
    {
      Action = "starting line",
      BeginTime = DateUtil.ToDotNetSeconds(new DateTime(2026, 1, 1, 13, 14, 15))
    };

    await fixture.Gate.WaitAsync();
    TimerData data;
    try
    {
      data = fixture.StartAndFinishCountdown(line.Action, 60, DateTime.UtcNow.AddMinutes(-2).Ticks,
        matches, previous, original, line, counter: 5, repeated: 3);
      variables["gAbility"] = "ready"; // changed after timer start but before reset delivery
    }
    finally
    {
      fixture.Gate.Release();
    }
    await WaitUntilAsync(() => fixture.Speech.Count == 1 && fixture.PendingCount == 0);
    var speak = fixture.Speech.Single();
    Assert.AreEqual("ready", speak.Variables["gAbility"]);
    Assert.IsFalse(speak.Variables.ContainsKey("gExpired"));
    Assert.IsFalse(variables.ContainsKey("gExpired"), "TTL expiry must run before either message reads variables.");
    variables["gAbility"] = "changed after enqueue";
    variables["gPad"] = "changed after enqueue";

    var expected = fixture.Processor.CurrentCharacterId
      + ";7;starting line;ALICE;124;previous;Origin;READY;   Q;Q   ;  Q  ;5;3;13:14:15";
    EnableSpeech(fixture);
    Invoke(fixture.Processor, "HandleSpeech", speak);
    fixture.Audio.Verify(audio => audio.SpeakTtsAsync(fixture.Processor.CurrentCharacterId,
      expected, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once());

    // Resolve the actual display preparation seam without opening an overlay window.
    ConfigureReset(fixture, template, template);
    var display = InvokeStatic("PrepareTimerResetDisplay", fixture.Wrapper, data, line, matches, speak.Variables);
    Assert.AreEqual(expected, display);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task ResetOnlyCounterAndRepeatedCodesCountAtTriggerTimeNotResetTime(bool displayOnly)
  {
    await using var fixture = new ProcessorFixture(1);
    fixture.Wrapper.TriggerData.ResetDurationSeconds = 60;
    ConfigureReset(fixture, displayOnly ? null : "{counter} {repeated}",
      displayOnly ? "{counter} {repeated}" : null);
    EnableSpeech(fixture);
    var line = new LineData { Action = "match" };
    var begin = DateTime.UtcNow.Ticks;
    for (var i = 0; i < 2; i++)
    {
      // No overlay: this drives the same firing/counting path that precedes StartTimerAsync.
      var task = Invoke(fixture.Processor, "HandleTriggerAsync", fixture.Wrapper, line, null, null,
        double.NaN, 0L, begin, 0) as Task
        ?? throw new InvalidOperationException("Trigger firing did not return a task");
      await task;
    }
    var counters = Field<Dictionary<string, ConcurrentDictionary<string, RepeatedData>>>(fixture.Processor, "_counterTimes");
    var repeats = Field<Dictionary<string, ConcurrentDictionary<string, RepeatedData>>>(fixture.Processor, "_repeatedTimerTimes");
    Assert.AreEqual(2L, counters[fixture.Wrapper.Id]["trigger-count"].Count);
    Assert.AreEqual(2L, repeats[fixture.Wrapper.Id]["timer"].Count);

    // The callback uses its originating run's numbers even after later matches advance the stores.
    Assert.AreEqual("1 1", InvokeStatic("ProcessTimerCodes", "{COUNTER} {REPEATED}", 1L, 1L, 0d));
    Assert.AreEqual(2L, counters[fixture.Wrapper.Id]["trigger-count"].Count);
    Assert.AreEqual(2L, repeats[fixture.Wrapper.Id]["timer"].Count);
  }

  [TestMethod]
  public async Task ResetSoundUsesTheExistingFilePathAndAudioLaneInsteadOfSpeakingItsName()
  {
    await using var fixture = new ProcessorFixture(1);
    ConfigureReset(fixture, "ignored when a sound is selected", sound: "ready.wav");
    fixture.StartAndFinishCountdown("match", 60, DateTime.UtcNow.AddMinutes(-2).Ticks);
    await WaitUntilAsync(() => fixture.Speech.Count == 1 && fixture.PendingCount == 0);
    var speak = fixture.Speech.Single();
    Assert.IsTrue(speak.IsSound);
    Assert.AreEqual("ready.wav", speak.TtsOrSound);
    EnableSpeech(fixture);
    Invoke(fixture.Processor, "HandleSpeech", speak);
    fixture.Audio.Verify(audio => audio.SpeakFileAsync(fixture.Processor.CurrentCharacterId,
      TriggerUtil.ResolveSoundPath("ready.wav"), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once());
    fixture.Audio.Verify(audio => audio.SpeakTtsAsync(It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never());
  }

  [TestMethod]
  public async Task NullAndUnsetResetTextKeepTheExistingOutputRules()
  {
    await using var fixture = new ProcessorFixture(1);
    ConfigureReset(fixture, "{NuLl}");
    var data = fixture.StartAndFinishCountdown("match", 60, DateTime.UtcNow.AddMinutes(-2).Ticks);
    await WaitUntilAsync(() => fixture.Logs.Count == 1 && fixture.PendingCount == 0);
    Assert.AreEqual(0, fixture.Speech.Count);
    ConfigureReset(fixture, null, "{NULL}");
    Assert.IsNull(InvokeStatic("PrepareTimerResetDisplay", fixture.Wrapper, data,
      new LineData(), null, new Dictionary<string, string>()));
    ConfigureReset(fixture, null, "{unset}");
    Assert.AreEqual("{unset}", InvokeStatic("PrepareTimerResetDisplay", fixture.Wrapper, data,
      new LineData(), null, new Dictionary<string, string>()));
  }

  private static void ConfigureReset(ProcessorFixture fixture, string? speak, string? display = null, string? sound = null)
  {
    var trigger = fixture.Wrapper.TriggerData;
    trigger.ResetTextToSpeak = speak;
    trigger.ResetTextToDisplay = display;
    trigger.ResetSoundToPlay = sound;
    fixture.Wrapper = new TriggerWrapper
    {
      Id = fixture.Wrapper.Id,
      TriggerData = trigger,
      ModifiedTimerName = "timer",
      ModifiedResetSpeak = Invoke(fixture.Processor, "PreProcessCodes", speak, trigger) as string,
      ModifiedResetDisplay = Invoke(fixture.Processor, "PreProcessCodes", display, trigger) as string
    };
  }

  private static void EnableSpeech(ProcessorFixture fixture)
  {
    (typeof(TriggerProcessor).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("Missing readiness field")).SetValue(fixture.Processor, true);
    (typeof(TriggerProcessor).GetField("_lexicon", BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("Missing lexicon field")).SetValue(fixture.Processor, new Dictionary<string, string>());
  }

  private static object? InvokeStatic(string name, params object?[] arguments) =>
    (typeof(TriggerProcessor).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException($"Missing processor method {name}")).Invoke(null, arguments);
}

using System.Text;
using System.Text.Json;

namespace EQLogParser
{
  /// <summary>
  /// Tests for NagUtil.ConvertTriggers — NAG trigger import functionality.
  /// Covers: basic conversion, conditions, multi-phrase capture, null duration,
  /// action type handling, deduplication, and import result tracking.
  /// </summary>
  [TestClass]
  public class NagUtilTriggerImportTest
  {
    /// <summary>
    /// Calls ConvertTriggers and unwraps the root wrapper node so tests can access trigger data directly.
    /// </summary>
    private static (List<ExportTriggerNode> nodes, List<NagImportResult> results, Dictionary<string, NagTriggerMetadata> metadata) ConvertTriggersUnwrapped(string json)
    {
      var (nodes, results, metadata) = NagUtil.ConvertTriggers(json);
      // ConvertTriggers wraps all nodes in a root ExportTriggerNode for the Import() flow.
      // Unwrap it for test assertions.
      if (nodes.Count == 1 && nodes[0].TriggerData is null && nodes[0].Nodes != null)
      {
        return (nodes[0].Nodes, results, metadata);
      }
      return (nodes, results, metadata);
    }

    #region Basic Trigger Conversion

    [TestMethod]
    public void ConvertTriggers_SingleTextOverlay_ReturnsNode()
    {
      var json = CreateTriggerJson("Test Trigger", "spellcast:Fireball", actions:
      [
        CreateAction(0, displayText: "Fireball cast!", duration: 5.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Test Trigger", nodes[0].Name);
      Assert.IsFalse(nodes[0].TriggerData.UseRegex);
      Assert.AreEqual("Fireball cast!", nodes[0].TriggerData.TextToDisplay);
      // NAG's DisplayText duration is only the on-screen lifetime of the text — it must NOT
      // become an EQLP timer, or every text notification would show a countdown.
      Assert.IsFalse(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(0.0, nodes[0].TriggerData.DurationSeconds);
      // Pattern must be set — this is the critical bug that was previously missed
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("spellcast:Fireball", nodes[0].TriggerData.Pattern);
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_MissingName_Skipped()
    {
      var json = CreateTriggerJson(null, "pattern", actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.HasCount(1, results);
      Assert.AreEqual("Skipped", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_DevOnly_Skipped()
    {
      var json = CreateTriggerJson("Dev Trigger", "pattern", onlyExecuteInDev: true, actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.AreEqual("Skipped", results[0].Status);
      Assert.Contains("Dev-only", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_NoCapturePhrases_Skipped()
    {
      var json = CreateTriggerJson("No Phrases", "pattern", capturePhrases: Array.Empty<JsonElement>(), actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.AreEqual("Skipped", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_NoSupportedActions_Skipped()
    {
      var json = CreateTriggerJson("No Actions", "pattern", actions:
      [
        CreateAction(5, displayText: "set variable") // Action type 5 is unsupported
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.AreEqual("Skipped", results[0].Status);
    }

    #endregion

    #region Action Type Handling

    [TestMethod]
    public void ConvertTriggers_ActionType0_TextOverlay_Imported()
    {
      var json = CreateTriggerJson("Text Trigger", "pattern", actions:
      [
        CreateAction(0, displayText: "Hello World", duration: 10.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Hello World", nodes[0].TriggerData.TextToDisplay);
      // Text display duration must not turn the notification into a countdown.
      Assert.IsFalse(nodes[0].TriggerData.EnableTimer);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType1_Audio_MissingFile_TrackedAsPartial()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions:
      [
        CreateAction(1, audioFileId: "audio-file-123")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Without files-database.json, audioFileId is used as-is
      Assert.AreEqual("audio-file-123", nodes[0].TriggerData.SoundToPlay);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      // Missing audio file → Partial status
      Assert.AreEqual("Partial", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType2_TTS_Imported()
    {
      var json = CreateTriggerJson("TTS Trigger", "pattern", actions:
      [
        CreateAction(2, displayText: "Spell ready")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Spell ready", nodes[0].TriggerData.TextToSpeak);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType3_Timer_MappedToProgress()
    {
      // NAG actionType 3 ("Timer") fills up over time — must map to EQLP Progress (3),
      // not Countdown (1) which drains.
      var json = CreateTriggerJson("Timer Trigger", "pattern", actions:
      [
        CreateAction(3, displayText: "Cooldown", duration: 30.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(30.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual(3, nodes[0].TriggerData.TimerType);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType4_Countdown_MappedToCountdown()
    {
      // NAG actionType 4 without repeatTimer is a plain countdown (drains) — EQLP Countdown (1).
      var json = CreateTriggerJson("Countdown Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Channeling", duration: 15.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(15.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual(1, nodes[0].TriggerData.TimerType);
      Assert.AreEqual(0L, nodes[0].TriggerData.TimesToLoop);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType4_RepeatTimerWithCount_MappedToLooping()
    {
      var json = CreateTriggerJson("Loop Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Aura", duration: 60.0, repeatTimer: true, repeatCount: 3)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(4, nodes[0].TriggerData.TimerType);
      Assert.AreEqual(3L, nodes[0].TriggerData.TimesToLoop);
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType4_RepeatTimerUnlimited_MappedToLargeLoopCount()
    {
      // NAG repeatTimer with no repeatCount repeats forever — approximate with a large
      // loop count and report the approximation in dropped features.
      var json = CreateTriggerJson("Infinite Loop", "pattern", actions:
      [
        CreateAction(4, displayText: "Aura", duration: 60.0, repeatTimer: true)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual(4, nodes[0].TriggerData.TimerType);
      Assert.IsTrue(nodes[0].TriggerData.TimesToLoop > 1000);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("unlimited timer repeat (approximated)", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_RestartBehavior_MappedToEqlpOption()
    {
      // NAG: 0=StartNewTimer, 1=RestartOnDuplicate, 2=RestartTimer, 3=DoNothing
      // EQLP: 0=new entry, 1=clear all, 2=stop same display name then start, 3=skip if any
      var cases = new (int nag, int eqlp)[]
      {
        (0, 0),
        (1, 2),
        (2, 1),
        (3, 3)
      };

      foreach (var (nag, eqlp) in cases)
      {
        var json = CreateTriggerJson($"RB {nag}", "pattern", actions:
        [
          CreateAction(3, displayText: "Timer", duration: 10.0, restartBehavior: nag)
        ]);

        var (nodes, _, _) = ConvertTriggersUnwrapped(json);

        Assert.AreEqual(eqlp, nodes[0].TriggerData.TriggerAgainOption, $"NAG restartBehavior {nag}");
      }
    }

    [TestMethod]
    public void ConvertTriggers_ActionType8_Counter_NoTimer_IdleResetMapped()
    {
      // NAG counters are invisible in-memory tallies — no timer should be created.
      // The counter duration is an idle-reset window → EQLP RepeatedResetTime.
      var json = CreateTriggerJson("Counter Trigger", "pattern", actions:
      [
        CreateAction(8, displayText: "Physical", duration: 300.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsFalse(nodes[0].TriggerData.EnableTimer, "NAG counters do not create a visible timer");
      Assert.AreEqual(300.0, nodes[0].TriggerData.RepeatedResetTime);
      Assert.AreEqual("Physical", nodes[0].TriggerData.TextToDisplay);
      var step = nodes[0].TriggerData.VariableActions.FirstOrDefault();
      Assert.IsNotNull(step);
      Assert.AreEqual("Physical", step.VariableName);
      Assert.AreEqual("Imported", results[0].Status);
    }

    #region Multi-Timer Fan-Out & Timer Labels

    [TestMethod]
    public void ConvertTriggers_Countdown_DisplayText_BecomesAltTimerName()
    {
      // NAG labels its timer bar with the action's displayText (NAG overlay: displayText ||
      // trigger name). EQLP renders the timer-bar label from AltTimerName; TextToDisplay is a
      // separate text notification, so the label must not be imported as display text.
      var json = CreateTriggerJson("Label Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Channeling ${Cast}", duration: 15.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual("Channeling {Cast}", nodes[0].TriggerData.AltTimerName);
      Assert.AreEqual("", nodes[0].TriggerData.TextToDisplay,
        "Timer label must not be imported as display text");
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_TwoCountdownActions_OneNodeEach_ValuesPreserved()
    {
      // A NAG trigger with two countdown actions must produce two EQLP triggers — the last
      // action used to overwrite the first's duration, label, and restart behavior.
      var json = CreateTriggerJson("Two Timers", "pattern", actions:
      [
        CreateAction(4, displayText: "Long cooldown", duration: 180.0, restartBehavior: 0),
        CreateAction(4, displayText: "Short cooldown", duration: 60.0, restartBehavior: 1)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      // The first action's values must survive on its own node
      var first = nodes.First(n => n.Name.EndsWith("(Timer 1)"));
      Assert.AreEqual(180.0, first.TriggerData.DurationSeconds);
      Assert.AreEqual("Long cooldown", first.TriggerData.AltTimerName);
      Assert.AreEqual(0, first.TriggerData.TriggerAgainOption);

      var second = nodes.First(n => n.Name.EndsWith("(Timer 2)"));
      Assert.AreEqual(60.0, second.TriggerData.DurationSeconds);
      Assert.AreEqual("Short cooldown", second.TriggerData.AltTimerName);
      // NAG restartBehavior 1 (RestartOnDuplicate) → EQLP option 2 (stop same name then start)
      Assert.AreEqual(2, second.TriggerData.TriggerAgainOption);

      Assert.IsTrue(nodes.All(n => n.TriggerData.EnableTimer));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_TimerWithEndingDuration_WarnTimeRemainingMapped()
    {
      // NAG's endingDuration (seconds before the end at which warning text/sound fire) is EQLP's
      // "Warn With Time Remaining". It was silently dropped before.
      var json = CreateTriggerJson("Warn Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Cooldown", duration: 120.0, extraJson: "\"endingDuration\":30")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual(30L, nodes[0].TriggerData.WarningSeconds);
      Assert.AreEqual("Imported", results[0].Status);

      // No endingDuration -> no warning threshold (model default 0).
      var jsonNoWarn = CreateTriggerJson("Plain Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Cooldown", duration: 120.0)
      ]);

      var (nodesNoWarn, _, _) = ConvertTriggersUnwrapped(jsonNoWarn);

      Assert.AreEqual(0L, nodesNoWarn[0].TriggerData.WarningSeconds);
    }

    [TestMethod]
    public void ConvertTriggers_TimerWithEndingAndEndedAudio_SoundSlotsMapped()
    {
      // NAG can play an audio clip when the timer enters its ending state (warning sound) and/or
      // when it ends (end sound). Both were silently dropped before.
      var json = CreateTriggerJson("Audio Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Cooldown", duration: 60.0,
          extraJson: "\"endingPlayAudio\":true,\"endingPlayAudioFileId\":\"warn-sfx.wav\",\"endedPlayAudio\":true,\"endedPlayAudioFileId\":\"end-sfx.wav\"")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Without files-database.json, file ids are used as-is (same convention as regular audio).
      Assert.AreEqual("warn-sfx.wav", nodes[0].TriggerData.WarningSoundToPlay);
      Assert.AreEqual("end-sfx.wav", nodes[0].TriggerData.EndSoundToPlay);
      // Unresolvable files are reported, same as regular action audio.
      CollectionAssert.Contains(results[0].MissingAudioFiles, "warn-sfx.wav");
      CollectionAssert.Contains(results[0].MissingAudioFiles, "end-sfx.wav");

      // The audio flags stay off unless the NAG enable booleans are set.
      var jsonNoAudio = CreateTriggerJson("Quiet Timer", "pattern", actions:
      [
        CreateAction(4, displayText: "Cooldown", duration: 60.0,
          extraJson: "\"endingPlayAudio\":false,\"endedPlayAudio\":false")
      ]);

      var (nodesNoAudio, _, _) = ConvertTriggersUnwrapped(jsonNoAudio);

      Assert.AreEqual("", nodesNoAudio[0].TriggerData.WarningSoundToPlay);
      Assert.AreEqual("", nodesNoAudio[0].TriggerData.EndSoundToPlay);
    }

    [TestMethod]
    public void ConvertTriggers_CaseSensitiveEndEarlyPhrases_SensitivityPreserved()
    {
      // NAG end-early phrases opt out of case-insensitivity per phrase; EQLP compiles every regex
      // pattern with IgnoreCase, so a (?-i) prefix restores it (same convention as capture phrases).
      var json = CreateTriggerJson("Sensitive End", "pattern",
        endEarlyPhrases:
        [
          CreateCapturePhrase("^Wears off text.", useRegEx: true, ignoreCase: false),
          CreateCapturePhrase("you are ready", useRegEx: true, ignoreCase: true)
        ],
        actions: [CreateAction(0, displayText: "text")]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("(?-i)^Wears off text.", nodes[0].TriggerData.EndEarlyPattern);
      Assert.IsTrue(nodes[0].TriggerData.EndUseRegex);
      Assert.AreEqual("you are ready", nodes[0].TriggerData.EndEarlyPattern2, "Case-insensitive regex phrases get no prefix");

      // Non-regex case-sensitive cannot be expressed in EQLP — reported, not guessed at.
      var jsonNonRegex = CreateTriggerJson("Sensitive End 2", "pattern",
        endEarlyPhrases: [CreateCapturePhrase("Wears off exactly", useRegEx: false, ignoreCase: false)],
        actions: [CreateAction(0, displayText: "text")]);

      var (_, resultsNonRegex, _) = ConvertTriggersUnwrapped(jsonNonRegex);

      CollectionAssert.Contains(resultsNonRegex[0].DroppedFeatures, "case-sensitive non-regex end-early phrase(s) imported as case-insensitive");
    }

    [TestMethod]
    public void ConvertTriggers_ActionLevelCaseSensitiveEndEarly_SensitivityPreserved()
    {
      // End-early phrases on the timer action itself (they stop this timer only) need the same
      // treatment as trigger-level ones.
      var json = CreateTriggerJson("Timer Sensitive End", "pattern", actions:
      [
        CreateAction(4, displayText: "Channeling", duration: 60.0,
          extraJson: "\"endEarlyPhrases\":[{\"phrase\":\"^You (have been slain|died)\",\"useRegEx\":true,\"ignoreCase\":false}]")
      ]);

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("(?-i)^You (have been slain|died)", nodes[0].TriggerData.EndEarlyPattern);
    }

    [TestMethod]
    public void ConvertTriggers_TextAndTimerActions_DoNotOverwriteEachOther()
    {
      // Text overlay and timer actions used to clobber each other's displayText — the timer's
      // label now goes to AltTimerName, so both features coexist on one node.
      var json = CreateTriggerJson("Text And Timer", "pattern", actions:
      [
        CreateAction(0, displayText: "Status text"),
        CreateAction(4, displayText: "Cooldown", duration: 30.0)
      ]);

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Status text", nodes[0].TriggerData.TextToDisplay);
      Assert.AreEqual("Cooldown", nodes[0].TriggerData.AltTimerName);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
    }

    [TestMethod]
    public void ConvertTriggers_TwoPhrasesTwoTimers_FourNodesNamedPerPhraseAndTimer()
    {
      var json = CreateTriggerJson("Grid", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^You cast \w+", useRegEx: true, phraseId: "p1"),
        CreateCapturePhrase(@"^You cast 2 \w+", useRegEx: true, phraseId: "p2")
      ], actions:
      [
        CreateAction(4, displayText: "A", duration: 10.0),
        CreateAction(4, displayText: "B", duration: 20.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(4, nodes);
      Assert.AreEqual("Grid #1 (Timer 1)", nodes[0].Name);
      Assert.AreEqual("Grid #1 (Timer 2)", nodes[1].Name);
      Assert.AreEqual("Grid #2 (Timer 1)", nodes[2].Name);
      Assert.AreEqual("Grid #2 (Timer 2)", nodes[3].Name);
      // Each phrase × timer combination keeps its own duration/label
      Assert.AreEqual(10.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual(20.0, nodes[1].TriggerData.DurationSeconds);
      Assert.AreEqual(10.0, nodes[2].TriggerData.DurationSeconds);
      Assert.AreEqual(20.0, nodes[3].TriggerData.DurationSeconds);
      Assert.AreEqual("A", nodes[0].TriggerData.AltTimerName);
      Assert.AreEqual("B", nodes[1].TriggerData.AltTimerName);
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionEndEarlyPhrases_RoutedToOwnTimerNodeOnly()
    {
      // An action's endEarlyPhrases stop that timer only in NAG — in EQLP they must land on
      // that timer's nodes and not leak into sibling timer nodes.
      var actionJson = CreateActionString(4, displayText: "Timed", duration: 30.0);
      actionJson = actionJson.Replace("}", ",\"endEarlyPhrases\":[{\"phrase\":\"Spell faded\"}]}");

      var json = CreateTriggerJson("Scoped EEP", "pattern", endEarlyPhrases:
      [
        CreateEndEarlyPhrase("Channel broken")
      ], actions:
      [
        JsonDocument.Parse(actionJson).RootElement,
        CreateAction(4, displayText: "Untimed", duration: 15.0)
      ]);

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      var first = nodes.First(n => n.Name.EndsWith("(Timer 1)"));
      // Trigger-level phrase plus this action's own phrase, both on its node
      Assert.AreEqual("Channel broken", first.TriggerData.EndEarlyPattern);
      Assert.AreEqual("Spell faded", first.TriggerData.EndEarlyPattern2);

      var second = nodes.First(n => n.Name.EndsWith("(Timer 2)"));
      // Sibling timer only gets the trigger-level phrase
      Assert.AreEqual("Channel broken", second.TriggerData.EndEarlyPattern);
      Assert.IsTrue(string.IsNullOrEmpty(second.TriggerData.EndEarlyPattern2),
        "Sibling timer node must not receive the other action's end-early phrases");
    }

    [TestMethod]
    public void ConvertTriggers_DotTimerAndBeneficialTimer_MatchNagDrawDirection()
    {
      // Verified against the NAG runtime (renderer.js): DotTimers (6) are drawn filling up like
      // Timers, and BeneficialTimers (10) deplete like Countdowns. Both are per-target in NAG,
      // which EQLP cannot represent — the divergence must be reported on the node's comments.
      var json = CreateTriggerJson("Dot And Buff", "pattern", actions:
      [
        CreateAction(6, displayText: "Dot", duration: 20.0),
        CreateAction(10, displayText: "Buff", duration: 30.0)
      ]);

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      var dot = nodes.First(n => n.Name.EndsWith("(Timer 1)"));
      Assert.AreEqual(3, dot.TriggerData.TimerType, "NAG DotTimers fill up — must import as Progress");
      StringAssert.Contains(dot.TriggerData.Comments ?? "", "dot timer approximated");

      var buff = nodes.First(n => n.Name.EndsWith("(Timer 2)"));
      Assert.AreEqual(1, buff.TriggerData.TimerType, "NAG BeneficialTimers deplete — must import as Countdown");
      StringAssert.Contains(buff.TriggerData.Comments ?? "", "per-target buff timer");
    }

    [TestMethod]
    public void ConvertTriggers_SharedNonTimerActions_FirstTimerVariantOnly()
    {
      // NAG fires non-timer actions once per trigger execution, but every fan-out node matches
      // the same line — shared actions must live on the first timer variant only or they would
      // double-fire (TTS spoken twice, counter incremented twice).
      var json = CreateTriggerJson("Shared Actions", "pattern", comments: "author note", actions:
      [
        CreateAction(2, displayText: "Spell ready"),
        CreateAction(8, displayText: "Cooldowns", duration: 30.0),
        CreateAction(4, displayText: "T1", duration: 60.0),
        CreateAction(4, displayText: "T2", duration: 90.0)
      ]);

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      var first = nodes.First(n => n.Name.EndsWith("(Timer 1)"));
      var second = nodes.First(n => n.Name.EndsWith("(Timer 2)"));

      // Shared actions live on the first variant only...
      Assert.AreEqual("Spell ready", first.TriggerData.TextToSpeak);
      Assert.AreEqual("Cooldowns", first.TriggerData.TextToDisplay, "Counter label is a shared text action");
      Assert.AreEqual(1, first.TriggerData.VariableActions.Count, "Counter increment must fire exactly once");

      // ...and are absent from the sibling variant, which carries only its own timer.
      Assert.IsNull(second.TriggerData.TextToSpeak);
      Assert.IsNull(second.TriggerData.TextToDisplay);
      Assert.AreEqual(0, second.TriggerData.VariableActions.Count, "Counter must not increment on the sibling node");

      // The NAG author's comment is inert metadata — it describes the trigger as a whole and
      // must stand on each split node (unlike the shared actions above, it cannot double-fire).
      StringAssert.Contains(first.TriggerData.Comments ?? "", "author note");
      StringAssert.Contains(second.TriggerData.Comments ?? "", "author note");
      Assert.IsTrue(second.TriggerData.EnableTimer);
      Assert.AreEqual(90.0, second.TriggerData.DurationSeconds);
      Assert.AreEqual("T2", second.TriggerData.AltTimerName);
    }

    #endregion

    [TestMethod]
    public void ConvertTriggers_OrphanedFolderId_PlacedInOrphanedTriggers()
    {
      // Triggers whose folderId doesn't exist in the folder list go to "Orphaned Triggers"
      // (mirroring NAG's own startup re-filing), not flattened to the root.
      var json = @"{
        ""folders"": [{""folderId"": ""F1"", ""name"": ""Raids"", ""children"": []}],
        ""triggers"": [
          {""name"": ""In Folder"", ""triggerId"": ""t1"", ""folderId"": ""F1"", ""onlyExecuteInDev"": false,
           ""capturePhrases"": [{""phrase"": ""p1"", ""useRegEx"": false}], ""actions"": [{""actionType"": 0, ""displayText"": ""text""}]},
          {""name"": ""Orphaned"", ""triggerId"": ""t2"", ""folderId"": ""MISSING-FOLDER"", ""onlyExecuteInDev"": false,
           ""capturePhrases"": [{""phrase"": ""p2"", ""useRegEx"": false}], ""actions"": [{""actionType"": 0, ""displayText"": ""text""}]}
        ]
      }";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // Both triggers are wrapped in a folder node at the top level.
      Assert.HasCount(2, nodes);
      var raidNode = nodes.FirstOrDefault(n => n.Name == "Raids");
      Assert.IsNotNull(raidNode);
      Assert.IsTrue(raidNode.Nodes.Any(n => n.Name == "In Folder"));

      var orphanNode = nodes.FirstOrDefault(n => n.Name == "Orphaned Triggers");
      Assert.IsNotNull(orphanNode, "Dead folderId should be refiled into Orphaned Triggers");
      Assert.IsTrue(orphanNode.Nodes.Any(n => n.Name == "Orphaned"));

      Assert.AreEqual("Raids", results.First(r => r.TriggerId == "t1").FolderPath);
      Assert.AreEqual("Orphaned Triggers", results.First(r => r.TriggerId == "t2").FolderPath);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType5_SetVariable_NoName_Dropped()
    {
      var json = CreateTriggerJson("Var Trigger", "pattern", actions:
      [
        CreateAction(5, displayText: "set some var"), // No variableName — can't map
        CreateAction(0, displayText: "text overlay")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("set variable", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType5_SetVariable_WithPhraseId_NamedGroup()
    {
      // NAG set-variable action stores a capture group into a named variable.
      // The corresponding phrase uses numbered groups (.*) which should be converted
      // to named groups (?<varName>.*) so EQLP can reference the captured value.
      var json = CreateTriggerJson("Spell Trigger", "placeholder", capturePhrases:
      [
        CreateCapturePhrase("^You begin casting (.*)\\.", useRegEx: true, phraseId: "phrase-spell")
      ], actions:
      [
        CreateAction(0, displayText: "Spell: ${SpellBeingCast}"), // text overlay using the variable
        CreateAction(5, variableName: "SpellBeingCast", phraseId: "phrase-spell") // set variable from capture group
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Pattern should use a simple named group (s1), not the variable name
      Assert.Contains("?<s1>", nodes[0].TriggerData.Pattern);
      Assert.DoesNotContain("(*)", nodes[0].TriggerData.Pattern); // no unnamed groups
      // A VariableAction should be set to store the captured value globally
      var setVarAction = nodes[0].TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "SpellBeingCast");
      Assert.IsNotNull(setVarAction);
      Assert.AreEqual("{s1}", setVarAction.Value);
      Assert.IsTrue(setVarAction.IsSetAction);
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType12_ScreenGlow_Dropped()
    {
      // NAG v0.2.26 names actionType 12 "Screen Glow" (older notes said "screen flash").
      var json = CreateTriggerJson("Glow Trigger", "pattern", actions:
      [
        CreateAction(12),
        CreateAction(0, displayText: "text overlay")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("screen glow", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType11_DeathRecap_Dropped()
    {
      // NAG v0.2.26 names actionType 11 "DisplayDeathRecap" (older notes said "hotkey").
      var json = CreateTriggerJson("DeathRecap Trigger", "pattern", actions:
      [
        CreateAction(11),
        CreateAction(0, displayText: "text overlay")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("death recap display", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType14_Stopwatch_Dropped()
    {
      var json = CreateTriggerJson("Stopwatch Trigger", "pattern", actions:
      [
        CreateAction(14),
        CreateAction(0, displayText: "text overlay")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("stopwatch timer", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType7_ClearVariable_MappedToEndTimerClearVariables()
    {
      // The trigger needs a real timer action for EndTimerClearVariables to fire at timer
      // end — a DisplayText duration no longer creates one.
      var json = CreateTriggerJson("Var Trigger", "pattern", actions:
      [
        CreateAction(0, displayText: "text overlay"),
        CreateAction(4, displayText: "channeling", duration: 5.0),
        CreateAction(7, variableName: "SpellBeingCast")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.IsNotNull(nodes[0].TriggerData.EndTimerClearVariables);
      Assert.Contains("SpellBeingCast", nodes[0].TriggerData.EndTimerClearVariables);
      Assert.AreEqual("Imported", results[0].Status);
    }

    #endregion

    #region Null Duration Handling

    [TestMethod]
    public void ConvertTriggers_NullDuration_UsesDefault60()
    {
      var json = CreateTriggerJson("Null Dur Trigger", "pattern", actions:
      [
        CreateAction(3, displayText: "Dynamic Timer", durationNull: true)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(60.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("indefinite timer duration", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_NullDuration_WithEndEarlyPhrases_Merged()
    {
      // Null-duration timers rely on endEarlyPhrases to terminate early.
      // Trigger-level and action-level phrases should be merged (max 3 slots).
      var actionJson = CreateActionString(3, displayText: "Timer", durationNull: true);
      actionJson = actionJson.Replace("}", ",\"endEarlyPhrases\":[{\"phrase\":\"Spell faded\"}]}");

      var json = CreateTriggerJson("Null Dur EEP", "pattern", endEarlyPhrases:
      [
        CreateEndEarlyPhrase("Channel broken")
      ], actions:
      [
        JsonDocument.Parse(actionJson).RootElement
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual(60.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual("Channel broken", nodes[0].TriggerData.EndEarlyPattern);
      Assert.AreEqual("Spell faded", nodes[0].TriggerData.EndEarlyPattern2);
    }

    #endregion

    #region Conditions Parsing

    [TestMethod]
    public void ConvertTriggers_ConditionOperator16_ContainsCheck()
    {
      // NAG operatorType 16 = Contains (case-insensitive substring — verified against the
      // v0.2.26 engine, models/trigger.js + log-watcher.js storeContainsValue).
      var json = CreateTriggerJson("Zone Trigger", "pattern", conditions:
      [
        CreateCondition("CurrentZone", 16, "Norg")
      ], actions:
      [
        CreateAction(0, displayText: "In Norg")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("{CurrentZone} contains \"Norg\"", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator1_EqualityCheck_SingleValue()
    {
      // NAG operatorType 1 = Equals (exact match — verified against v0.2.26 engine).
      var json = CreateTriggerJson("Equality Trigger", "pattern", conditions:
      [
        CreateCondition("SpellBeingCast", 1, "Fireball")
      ], actions:
      [
        CreateAction(0, displayText: "Fire spell")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("{SpellBeingCast} = \"Fireball\"", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator1_Equality_PipeSeparatedValues()
    {
      var json = CreateTriggerJson("Equality Trigger", "pattern", conditions:
      [
        CreateCondition("SpellBeingCast", 1, "Fireball|Flame Strike")
      ], actions:
      [
        CreateAction(0, displayText: "Fire spell")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // NAG pipe-separated values should become OR'd equality clauses; parenthesized so the
      // OR cannot leak across a larger "&&" join (EQLP condition grammar: AND binds tighter).
      Assert.AreEqual("({SpellBeingCast} = \"Fireball\" || {SpellBeingCast} = \"Flame Strike\")", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator16_Contains_PipeSeparatedValues()
    {
      var json = CreateTriggerJson("Contains Trigger", "pattern", conditions:
      [
        CreateCondition("SpellBeingCast", 16, "Fire|Flame")
      ], actions:
      [
        CreateAction(0, displayText: "Fire spell")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("({SpellBeingCast} contains \"Fire\" || {SpellBeingCast} contains \"Flame\")", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator0_IsNull()
    {
      // NAG operatorType 0 = IsNull — passes while the variable has no stored value.
      var json = CreateTriggerJson("IsNull Trigger", "pattern", conditions:
      [
        CreateCondition("SpellBeingCast", 0, null)
      ], actions:
      [
        CreateAction(0, displayText: "No spell tracked")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("!{SpellBeingCast}", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator2_WithValues_NegatedEquality()
    {
      // NAG operatorType 2 = DoesNotEqual: no stored value may equal a condition value.
      var json = CreateTriggerJson("NotEq Trigger", "pattern", conditions:
      [
        CreateCondition("SpellBeingCast", 2, "Fireball|Flame Strike")
      ], actions:
      [
        CreateAction(0, displayText: "Other spell")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("!({SpellBeingCast} = \"Fireball\" || {SpellBeingCast} = \"Flame Strike\")", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator2_NoValue_MustBeSet()
    {
      // NAG DoesNotEqual without a value passes only when the variable has at least one
      // stored value — EQLP truthy check.
      var json = CreateTriggerJson("Exists Trigger", "pattern", conditions:
      [
        CreateCondition("EbItemZone", 2, null)
      ], actions:
      [
        CreateAction(0, displayText: "Item check")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("{EbItemZone}", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator_MultiConditionWithPipeValues_Parenthesized()
    {
      // A multi-value (OR) condition joined with another via && must stay parenthesized.
      var json = CreateTriggerJson("Multi Cond Trigger", "pattern", conditions:
      [
        CreateCondition("CurrentZone", 16, "Norg"),
        CreateCondition("SpellBeingCast", 1, "Fireball|Flame Strike")
      ], actions:
      [
        CreateAction(0, displayText: "Specific check")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("{CurrentZone} contains \"Norg\" && ({SpellBeingCast} = \"Fireball\" || {SpellBeingCast} = \"Flame Strike\")", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_Condition_UnknownOperator_DroppedAndReported()
    {
      // Operators outside {0, 1, 2, 16} (e.g. 8 = numeric GreaterThan, used by NAG only for
      // counter conditions) cannot be expressed — the condition must be dropped and reported,
      // not silently lost.
      var json = CreateTriggerJson("Unknown Op Trigger", "pattern", conditions:
      [
        CreateCondition("SomeCounter", 8, "50")
      ], actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsNull(nodes[0].TriggerData.MatchVariableCondition);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains($"condition operator 8 on SomeCounter", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_Condition_NonVariableType_Reported()
    {
      // conditionType 3 (counter value) has no EQLP equivalent — must be reported.
      var json = "{\"triggers\":[{\"name\":\"Counter Cond Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"capturePhrases\":[{\"phrase\":\"pattern\",\"useRegEx\":false}],\"conditions\":[{\"conditionType\":3,\"variableName\":\"Physical\",\"operatorType\":8,\"variableValue\":\"50\"}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsNull(nodes[0].TriggerData.MatchVariableCondition);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("counter condition", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator2_Existence()
    {
      var json = CreateTriggerJson("Exists Trigger", "pattern", conditions:
      [
        CreateCondition("EbItemZone", 2, null)
      ], actions:
      [
        CreateAction(0, displayText: "Item check")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("{EbItemZone}", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_MultipleConditions_JoinedWithAnd()
    {
      var json = CreateTriggerJson("Multi Cond Trigger", "pattern", conditions:
      [
        CreateCondition("CurrentZone", 16, "Norg"),
        CreateCondition("SpellBeingCast", 16, "Fireball")
      ], actions:
      [
        CreateAction(0, displayText: "Specific check")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      var cond = nodes[0].TriggerData.MatchVariableCondition;
      Assert.Contains("{CurrentZone} contains", cond);
      Assert.Contains("{SpellBeingCast} contains", cond);
      Assert.Contains("&&", cond);
    }

    [TestMethod]
    public void ConvertTriggers_NullOperatorType_HandledGracefully()
    {
      // NAG data has 4 conditions with null operatorType — must not crash, and the
      // unevaluable condition is dropped + reported (Partial) rather than silently lost.
      var json = "{\"triggers\":[{\"name\":\"Null Op Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"capturePhrases\":[{\"phrase\":\"pattern\",\"useRegEx\":false}],\"conditions\":[{\"conditionType\":1,\"variableName\":\"SomeVar\",\"operatorType\":null,\"variableValue\":\"val\"}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsNull(nodes[0].TriggerData.MatchVariableCondition);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("condition operator -1 on SomeVar", results[0].DroppedFeatures);
    }

    #endregion

    #region Multi-Phrase Capture

    [TestMethod]
    public void ConvertTriggers_MultiplePhrases_OneTriggerPerPhrase()
    {
      var json = CreateTriggerJson("Multi Phrase", "pattern", capturePhrases:
      [
        CreateCapturePhrase("You cast Fireball"),
        CreateCapturePhrase("You cast Flame Strike")
      ], actions:
      [
        CreateAction(0, displayText: "Fire spell")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      // Each phrase becomes its own EQLP trigger (no alternation combining)
      Assert.AreEqual("You cast Fireball", nodes[0].TriggerData.Pattern);
      Assert.AreEqual("You cast Flame Strike", nodes[1].TriggerData.Pattern);
    }

    [TestMethod]
    public void ConvertTriggers_MultiplePhrasesWithRegex_Preserved()
    {
      var json = CreateTriggerJson("Regex Phrase", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"You cast (?<spellName>\w+)", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "{spellName} ready!")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.UseRegex);
    }

    [TestMethod]
    public void ConvertTriggers_IgnoreCaseFalse_RegexPhrase_GetsNegativeInlineModifier()
    {
      // NAG phrases are case-insensitive by default but can opt out per phrase. EQLP compiles
      // all patterns with RegexOptions.IgnoreCase, so the import must prepend (?-i) to restore
      // case sensitivity (verified: .NET lets an inline (?-i) override the compile flag).
      var json = CreateTriggerJson("Sensitive Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^Fireball hits for \d+ damage$", useRegEx: true, ignoreCase: false)
      ], actions:
      [
        CreateAction(0, displayText: "Fireball")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(nodes[0].TriggerData.Pattern.StartsWith("(?-i)"), $"Pattern should start with (?-i): {nodes[0].TriggerData.Pattern}");
      // Case sensitivity restored: the pattern must not match a differently-cased line.
      var regex = new System.Text.RegularExpressions.Regex(nodes[0].TriggerData.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
      Assert.IsTrue(regex.IsMatch("Fireball hits for 50 damage"));
      Assert.IsFalse(regex.IsMatch("FIREBALL hits for 50 damage"));
      // No dropped-feature note — the restriction is fully preserved for regex phrases.
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_IgnoreCaseDefault_RegexPhrase_NoPrefix()
    {
      var json = CreateTriggerJson("Insensitive Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^Fireball hits$", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "Fireball")
      ]);

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsFalse(nodes[0].TriggerData.Pattern.StartsWith("(?-i)"));
    }

    [TestMethod]
    public void ConvertTriggers_IgnoreCaseFalse_NonRegexPhrase_Reported()
    {
      // Non-regex phrases use EQLP's always-case-insensitive literal matching — there is no
      // per-trigger override, so the divergence is reported instead of silently imported.
      var json = CreateTriggerJson("Sensitive Literal Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase("Fireball hits for damage", useRegEx: false, ignoreCase: false)
      ], actions:
      [
        CreateAction(0, displayText: "Fireball")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Fireball hits for damage", nodes[0].TriggerData.Pattern);
      Assert.IsFalse(nodes[0].TriggerData.UseRegex);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("case-sensitive non-regex phrase(s) imported as case-insensitive", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_DollarVarInRegexPhrase_RestrictionReported()
    {
      // NAG treats ${var} in a phrase as a match-time restriction (only matches stored values
      // of that variable, never when the variable is empty). EQLP cannot express it — the
      // import matches any text and must report the divergence.
      var json = CreateTriggerJson("Var Condition Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^Your ${SpellBeingCast} spell fizzles.$", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "Fizzled")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("(?<SpellBeingCast>.+?)", nodes[0].TriggerData.Pattern);
      Assert.AreEqual("Partial", results[0].Status);
      // Assert.Contains on a collection is an exact match — use the full note string.
      Assert.Contains("phrase ${var} restriction (NAG only matches stored variable values; import matches any text)", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_DollarCharacterOnly_NoRestrictionNote()
    {
      // ${Character} maps to EQLP's native {c} replacement — that is exact, not a relaxation,
      // so no restriction note may be reported.
      var json = CreateTriggerJson("Char Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^${Character} casts a spell.$", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "Cast")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Imported", results[0].Status);
      Assert.IsFalse(results[0].DroppedFeatures.Any(f => f.StartsWith("phrase ${var} restriction")));
    }

    #endregion

    #region End Early Phrases

    [TestMethod]
    public void ConvertTriggers_EndEarlyPhrases_AppliedToTrigger()
    {
      var json = CreateTriggerJson("End Early Trigger", "pattern", endEarlyPhrases:
      [
        CreateEndEarlyPhrase("Spell ended"),
        CreateEndEarlyPhrase("Channel broken")
      ], actions:
      [
        CreateAction(3, displayText: "Channeling", duration: 30.0)
      ]);
      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Spell ended", nodes[0].TriggerData.EndEarlyPattern);
      Assert.AreEqual("Channel broken", nodes[0].TriggerData.EndEarlyPattern2);
    }

    [TestMethod]
    public void ConvertTriggers_ActionLevelEndEarlyPhrases_MergedWithTriggerLevel()
    {
      // Action-level endEarlyPhrases (537 timer actions in real data have these)
      var actionJson = CreateActionString(3, displayText: "Timer", duration: 30.0);
      // Inject endEarlyPhrases into the action JSON
      actionJson = actionJson.Replace("}", ",\"endEarlyPhrases\":[{\"phrase\":\"Spell faded\"}]}");

      var json = CreateTriggerJson("Merged EEP", "pattern", endEarlyPhrases:
      [
        CreateEndEarlyPhrase("Channel broken")
      ], actions:
      [
        JsonDocument.Parse(actionJson).RootElement
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Both trigger-level and action-level should be present (max 3 slots)
      Assert.AreEqual("Channel broken", nodes[0].TriggerData.EndEarlyPattern);
      Assert.AreEqual("Spell faded", nodes[0].TriggerData.EndEarlyPattern2);
    }

    #endregion

    #region Comments and Metadata

    [TestMethod]
    public void ConvertTriggers_Comments_PreservedInNagComment()
    {
      var json = "{\"triggers\":[{\"name\":\"Commented Trigger\",\"triggerId\":\"test-123\",\"onlyExecuteInDev\":false,\"comments\":\"User's note here\",\"capturePhrases\":[{\"phrase\":\"pattern\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // NAG comments are preserved as-is (no "Original:" prefix).
      // The NAG trigger ID is tracked via OriginalId and the metadata dictionary.
      Assert.DoesNotContain("Original:", nodes[0].TriggerData.Comments);
      Assert.Contains("User's note here", nodes[0].TriggerData.Comments);
    }

    [TestMethod]
    public void ConvertTriggers_DroppedFeatures_ListedInComment()
    {
      var json = CreateTriggerJson("Partial Trigger", "pattern", actions:
      [
        CreateAction(5, displayText: "set var"), // Unsupported
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("EQLP Import Notes:", nodes[0].TriggerData.Comments);
      Assert.Contains("set variable", nodes[0].TriggerData.Comments);
    }

    #endregion

    #region Template Conversion

    [TestMethod]
    public void ConvertTriggers_NagTemplates_ConvertedToEqlp()
    {
      // NAG uses ${var} for variables and (?<name>...) for regex groups.
      // EQLP display text uses {$name} for variable references (the $ prefix is optional).
      var json = CreateTriggerJson("Template Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"You cast (?<spellName>\w+)", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "{spellName} ready!")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // NAG's {groupName} preserved as EQLP's {groupName} (no leading $)
      Assert.Contains("{spellName}", nodes[0].TriggerData.TextToDisplay);
    }

    [TestMethod]
    public void ConvertTriggers_NagDollarVar_ConvertedToEqlp()
    {
      // NAG ${var} → EQLP {var} (in display text, not regex phrases)
      var json = CreateTriggerJson("Dollar Var Trigger", "pattern", actions:
      [
        CreateAction(0, displayText: "${caster} casts!")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("{caster}", nodes[0].TriggerData.TextToDisplay);
      Assert.DoesNotContain("${caster}", nodes[0].TriggerData.TextToDisplay);
    }

    #endregion

    #region NAG Variable References in Regex Phrases

    [TestMethod]
    public void ConvertTriggers_TsPlaceholder_PreservedInRegexPhrase()
    {
      // NAG uses {TS} as a duration placeholder in regex phrases.
      // EQLP's CheckOptions() at runtime converts {TS} → (?<TS>(?:\d+[dhms]?:?){1,4}).
      // Must NOT be converted to {$TS} by ConvertTemplates.
      var json = CreateTriggerJson("TS Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase("^${Character} starts a {TS} timer\\.$", useRegEx: true)
      ], actions:
      [
        CreateAction(3, displayText: "Timer started", durationNull: true)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // {TS} must be preserved as-is (not {$TS})
      Assert.Contains("{TS}", nodes[0].TriggerData.Pattern);
      Assert.DoesNotContain("{$TS}", nodes[0].TriggerData.Pattern);
      // ${Character} → {c} (EQLP native replacement, not a capture group)
      Assert.Contains("{c}", nodes[0].TriggerData.Pattern);
    }

    [TestMethod]
    public void ConvertTriggers_DollarVar_ReplacedWithCaptureGroup()
    {
      // NAG ${varName} in regex phrases → (.+?) since EQLP doesn't support {$var} in patterns
      var json = CreateTriggerJson("DollarVar Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase("^${Character} casts a spell\\.$", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "Spell cast")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // ${Character} → {c} (EQLP native replacement)
      Assert.Contains("{c}", nodes[0].TriggerData.Pattern);
      Assert.DoesNotContain("${Character}", nodes[0].TriggerData.Pattern);
    }

    [TestMethod]
    public void ConvertTriggers_EqlpHandledVars_PreservedInRegexPhrase()
    {
      // EQLP handles {S}, {N} at runtime via CheckOptions(). Must not be converted.
      var json = CreateTriggerJson("EqlpVars Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase("^You cast {S} for {N} damage\\.$", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "Cast")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // {S} and {N} should be preserved as-is for EQLP runtime conversion
      Assert.Contains("{S}", nodes[0].TriggerData.Pattern);
      Assert.Contains("{N}", nodes[0].TriggerData.Pattern);
      Assert.DoesNotContain("{$S}", nodes[0].TriggerData.Pattern);
      Assert.DoesNotContain("{$N}", nodes[0].TriggerData.Pattern);
    }

    [TestMethod]
    public void ConvertTriggers_UnhandledVar_ReplacedWithCaptureGroup()
    {
      // {target} is an unhandled NAG variable — must be replaced with (?<target>.+?) for regex matching
      var json = CreateTriggerJson("UnhandledVar Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase("^A yellow cloud forms above {target}'s head\\.$", useRegEx: true)
      ], actions:
      [
        CreateAction(0, displayText: "Cloud formed")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // {target} should be replaced with a named capture group
      Assert.Contains("?<target>", nodes[0].TriggerData.Pattern);
      Assert.DoesNotContain("{target}", nodes[0].TriggerData.Pattern);
    }

    [TestMethod]
    public void ConvertTriggers_NonRegexPhraseWithVar_EnablesRegexMode()
    {
      // 49 single-phrase non-regex triggers use {C} — EQLP replaces {C} at runtime via PreProcessCodes,
      // so regex mode is NOT needed. The pattern stays as a literal string with {C} preserved.
      var json = CreateTriggerJson("NonRegex Var Trigger", "pattern", capturePhrases:
      [
        CreateCapturePhrase("A yellow cloud forms above {C}'s head.", useRegEx: false)
      ], actions:
      [
        CreateAction(0, displayText: "Cloud")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // {C} is preserved as-is — EQLP replaces it at runtime via PreProcessCodes
      Assert.Contains("{C}", nodes[0].TriggerData.Pattern);
      Assert.IsFalse(nodes[0].TriggerData.UseRegex);
    }

    [TestMethod]
    public void ConvertTriggers_NonRegexPhraseWithoutVars_StayNonRegex()
    {
      // Non-regex phrases without NAG variables should stay non-regex (literal match)
      var json = CreateTriggerJson("Plain NonRegex", "pattern", capturePhrases:
      [
        CreateCapturePhrase("You cast Fireball.", useRegEx: false)
      ], actions:
      [
        CreateAction(0, displayText: "Cast")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsFalse(nodes[0].TriggerData.UseRegex);
      Assert.AreEqual("You cast Fireball.", nodes[0].TriggerData.Pattern);
    }

    #endregion

    #region Import Result Tracking

    [TestMethod]
    public void ConvertTriggers_Results_TrackImportedAndSkipped()
    {
      var json = @"{
        ""triggers"": [
          {""name"": ""Good"", ""triggerId"": ""t1"", ""onlyExecuteInDev"": false, ""capturePhrases"": [{""phrase"": ""p1"", ""useRegEx"": false}], ""actions"": [{""actionType"": 0, ""displayText"": ""text""}]},
          {""name"": ""Bad"", ""triggerId"": ""t2"", ""onlyExecuteInDev"": true, ""capturePhrases"": [{""phrase"": ""p1"", ""useRegEx"": false}], ""actions"": [{""actionType"": 0, ""displayText"": ""text""}]},
          {""name"": ""Partial"", ""triggerId"": ""t3"", ""onlyExecuteInDev"": false, ""capturePhrases"": [{""phrase"": ""p1"", ""useRegEx"": false}], ""actions"": [{""actionType"": 5}, {""actionType"": 0, ""displayText"": ""text""}]}
        ]
      }";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes); // Good + Partial
      Assert.HasCount(3, results);

      var imported = results.FirstOrDefault(r => r.TriggerId == "t1");
      var skipped = results.FirstOrDefault(r => r.TriggerId == "t2");
      var partial = results.FirstOrDefault(r => r.TriggerId == "t3");

      Assert.IsNotNull(imported);
      Assert.IsNotNull(skipped);
      Assert.IsNotNull(partial);
      Assert.AreEqual("Imported", imported!.Status);
      Assert.AreEqual("Skipped", skipped!.Status);
      Assert.AreEqual("Partial", partial!.Status);
    }

    [TestMethod]
    public void ConvertTriggers_Results_TrackFolderPath()
    {
      var results = new List<NagImportResult>
      {
        new() { TriggerName = "Test1", TriggerId = "t1", Status = "Imported", Reason = null, ActionsSummary = "Text", FolderPath = "/root/sub" },
        new() { TriggerName = "Test2", TriggerId = "t2", Status = "Skipped", Reason = "Dev-only trigger", ActionsSummary = null, FolderPath = "/root" }
      };

      var tempFile = Path.GetTempFileName();
      try
      {
        NagUtil.WriteImportReportHtml(results, tempFile);
        Assert.IsTrue(File.Exists(tempFile));

        var html = File.ReadAllText(tempFile);
        Assert.Contains("/root/sub", html);
        Assert.Contains("/root", html);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    #endregion

    #region HTML Report Generation

    [TestMethod]
    public void WriteImportReportHtml_ValidResults_CreatesHtml()
    {
      var results = new List<NagImportResult>
      {
        new() { TriggerName = "Test1", TriggerId = "t1", Status = "Imported", Reason = null, FolderPath = "NAG Import - 2024-01-15 10:30/Orphaned Triggers", ActionsSummary = "Text", MissingAudioFiles = [] },
        new() { TriggerName = "Test2", TriggerId = "t2", Status = "Skipped", Reason = "Dev-only trigger", FolderPath = "(root)", ActionsSummary = null, MissingAudioFiles = [] },
        new() { TriggerName = "Has,Comma", TriggerId = "t3", Status = "Partial", Reason = "set variable", FolderPath = "NAG Import - 2024-01-15 10:30/Raids/Kunark", ActionsSummary = "Text, Audio", MissingAudioFiles = new List<string> { "missing.wav" } }
      };

      var tempFile = Path.GetTempFileName();
      try
      {
        NagUtil.WriteImportReportHtml(results, tempFile);
        Assert.IsTrue(File.Exists(tempFile));

        var html = File.ReadAllText(tempFile);
        // Verify HTML structure
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        // Verify summary stats (display text uses 'Success' instead of 'Imported')
        Assert.Contains("Success", html);
        Assert.Contains("Partial", html);
        Assert.Contains("Skipped", html);
        // Verify trigger names appear
        Assert.Contains("Test1", html);
        Assert.Contains("Has,Comma", html);
        // Verify folder paths appear
        Assert.Contains("Orphaned Triggers", html);
        Assert.Contains("Kunark", html);
        // Verify missing audio file is listed
        Assert.Contains("missing.wav", html);
        // Verify badge classes and light theme CSS
        Assert.Contains("badge-imported", html);
        Assert.Contains("badge-partial", html);
        Assert.Contains("badge-skipped", html);
        Assert.Contains("background: #f5f5f5", html);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    [TestMethod]
    public void WriteImportReportHtml_EmptyResults_CreatesHtml()
    {
      var tempFile = Path.GetTempFileName();
      try
      {
        NagUtil.WriteImportReportHtml([], tempFile);
        Assert.IsTrue(File.Exists(tempFile));

        var html = File.ReadAllText(tempFile);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    [TestMethod]
    public void WriteImportReportHtml_SpecialCharacters_AreHtmlEncoded()
    {
      var results = new List<NagImportResult>
      {
        new() { TriggerName = "Trigger <with> & \"quotes\"", TriggerId = "t1", Status = "Imported", FolderPath = "(root)", ActionsSummary = "" }
      };

      var tempFile = Path.GetTempFileName();
      try
      {
        NagUtil.WriteImportReportHtml(results, tempFile);
        Assert.IsTrue(File.Exists(tempFile));

        var html = File.ReadAllText(tempFile);
        // Raw HTML special chars must NOT appear unencoded
        Assert.DoesNotContain("<with>", html);
        Assert.Contains("&lt;with&gt;", html);
        Assert.Contains("&amp;", html);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    [TestMethod]
    public void WriteImportReportHtml_RootFolder_ShowsEmRoot()
    {
      var results = new List<NagImportResult>
      {
        new() { TriggerName = "Root Trig", TriggerId = "t1", Status = "Imported", FolderPath = "(root)", ActionsSummary = "" }
      };

      var tempFile = Path.GetTempFileName();
      try
      {
        NagUtil.WriteImportReportHtml(results, tempFile);
        Assert.IsTrue(File.Exists(tempFile));

        var html = File.ReadAllText(tempFile);
        Assert.Contains("<em>(root)</em>", html);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    [TestMethod]
    public void WriteImportReportHtml_ResultsSortedByStatus_SkippedFirst()
    {
      var results = new List<NagImportResult>
      {
        new() { TriggerName = "ImportedOne", TriggerId = "t1", Status = "Imported", FolderPath = "(root)", ActionsSummary = "" },
        new() { TriggerName = "SkippedOne", TriggerId = "t2", Status = "Skipped", Reason = "dev", FolderPath = "(root)", ActionsSummary = null },
        new() { TriggerName = "PartialOne", TriggerId = "t3", Status = "Partial", Reason = "dropped", FolderPath = "(root)", ActionsSummary = "" },
        new() { TriggerName = "ImportedTwo", TriggerId = "t4", Status = "Imported", FolderPath = "(root)", ActionsSummary = "" }
      };

      var tempFile = Path.GetTempFileName();
      try
      {
        NagUtil.WriteImportReportHtml(results, tempFile);
        Assert.IsTrue(File.Exists(tempFile));

        var html = File.ReadAllText(tempFile);
        // Skipped should appear before Partial which appears before Success
        var skippedIdx = html.IndexOf("SkippedOne");
        var partialIdx = html.IndexOf("PartialOne");
        var importedIdx = html.IndexOf("ImportedTwo");

        Assert.IsTrue(skippedIdx >= 0 && partialIdx >= 0 && importedIdx >= 0);
        Assert.IsLessThan(partialIdx, skippedIdx, "Skipped should come before Partial");
        Assert.IsLessThan(importedIdx, partialIdx, "Partial should come before Success/Imported");
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void ConvertTriggers_EmptyTriggersArray_ReturnsEmpty()
    {
      var json = "{\"triggers\": []}";
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.IsEmpty(results);
    }

    [TestMethod]
    public void ConvertTriggers_InvalidJson_ReturnsEmpty()
    {
      var json = "not valid json";
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.IsEmpty(results);
    }

    [TestMethod]
    public void ConvertTriggers_MalformedActionType_TriggerSkippedOthersImported()
    {
      // "Bad" has a non-numeric actionType, which makes ParseTrigger throw mid-conversion.
      // The import must report only that trigger as Skipped and still convert the rest.
      var json = "{\"triggers\":["
        + "{\"name\":\"Bad\",\"triggerId\":\"t-bad\",\"capturePhrases\":[{\"phrase\":\"bad pattern\",\"useRegEx\":false}],"
        + "\"actions\":[{\"actionType\":\"not-a-number\"}]}"
        + ","
        + "{\"name\":\"Good\",\"triggerId\":\"t-good\",\"capturePhrases\":[{\"phrase\":\"good pattern\",\"useRegEx\":false}],"
        + "\"actions\":[{\"actionType\":0,\"displayText\":\"hello\"}]}"
        + "]}";

      var (nodes, results, metadata) = ConvertTriggersUnwrapped(json);

      // The healthy trigger still converts and gets metadata.
      Assert.HasCount(1, nodes);
      Assert.AreEqual("Good", nodes[0].Name);
      Assert.IsTrue(metadata.ContainsKey("t-good"));

      // Both triggers appear in the results, in input order; the malformed one is Skipped with a reason.
      Assert.HasCount(2, results);
      Assert.AreEqual("Bad", results[0].TriggerName);
      Assert.AreEqual("Skipped", results[0].Status);
      StringAssert.Contains(results[0].Reason, "Error parsing trigger");
      Assert.AreEqual("Good", results[1].TriggerName);
      Assert.AreEqual("Imported", results[1].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ScoreToPriority_Mapping()
    {
      var json = CreateTriggerJson("Scored Trigger", "pattern", score: 1.0, actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Score 1.0 should map to Priority 1
      Assert.AreEqual(1, nodes[0].TriggerData.Priority);
    }

    [TestMethod]
    public void ConvertTriggers_SequentialCapture_Skipped()
    {
      var json = "{\"triggers\":[{\"name\":\"Seq Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"captureMethod\":\"Sequential\",\"capturePhrases\":[{\"phrase\":\"You begin casting\",\"useRegEx\":false},{\"phrase\":\"Spell lands\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.HasCount(1, results);
      Assert.AreEqual("Skipped", results[0].Status);
      Assert.Contains("Sequential", results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_ClassLevels_MarkedPartial()
    {
      var json = "{\"triggers\":[{\"name\":\"Class Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"classLevels\":[{\"class\":\"Cleric\",\"level\":50}],\"capturePhrases\":[{\"phrase\":\"pattern\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("Class level filtering", results[0].Reason, results[0].Reason);
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_TextAction_Collected()
    {
      // NAG text overlay actions (type 0) have overlayId in 1860/1863 real cases
      var actionJson = "{\"actionType\":0,\"displayText\":\"Overlaid text\",\"overlayId\":\"ov-123\"}";
      var json = CreateTriggerJson("Overlay Trigger", "pattern", actions:
      [
        JsonDocument.Parse(actionJson).RootElement
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("ov-123", nodes[0].TriggerData.SelectedOverlays);
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_TtsAction_Collected()
    {
      // TTS actions (type 2) also support overlayId in NAG data
      var actionJson = "{\"actionType\":2,\"displayText\":\"TTS text\",\"overlayId\":\"ov-456\"}";
      var json = CreateTriggerJson("TTS Overlay Trigger", "pattern", actions:
      [
        JsonDocument.Parse(actionJson).RootElement
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("ov-456", nodes[0].TriggerData.SelectedOverlays);
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_AudioAction_Collected()
    {
      // Audio actions (type 1) also support overlayId in NAG data
      var actionJson = "{\"actionType\":1,\"audioFileId\":\"sound-123\",\"overlayId\":\"ov-789\"}";
      var json = CreateTriggerJson("Audio Overlay Trigger", "pattern", actions:
      [
        JsonDocument.Parse(actionJson).RootElement
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("ov-789", nodes[0].TriggerData.SelectedOverlays);
      // Missing audio file → Partial status
      Assert.AreEqual("Partial", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_ClipboardAction_Collected()
    {
      // Clipboard actions (type 9) also support overlayId in NAG data
      var actionJson = "{\"actionType\":9,\"displayText\":\"Clipboard text\",\"overlayId\":\"ov-clip\"}";
      var json = CreateTriggerJson("Clipboard Overlay Trigger", "pattern", actions:
      [
        JsonDocument.Parse(actionJson).RootElement
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.Contains("ov-clip", nodes[0].TriggerData.SelectedOverlays);
    }

    #endregion

    #region Metadata Dictionary

    [TestMethod]
    public void ConvertTriggers_ReturnsMetadataDictionary()
    {
      var json = CreateTriggerJson("Meta Trigger", "pattern", score: 0.8, actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, metadata) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.HasCount(1, metadata);
      Assert.IsTrue(metadata.ContainsKey("test-123"));
      var meta = metadata["test-123"];
      Assert.AreEqual("Meta Trigger", meta.TriggerName);
      Assert.AreEqual("(root)", meta.FolderPath);
      Assert.AreEqual(0.8, meta.Score);
      Assert.IsNotNull(meta.ActionsSummary);
    }

    [TestMethod]
    public void ConvertTriggers_Metadata_ExcludesSkipped()
    {
      var json = "{\"triggers\":["
        + "{\"name\":\"Good\",\"triggerId\":\"t-good\",\"onlyExecuteInDev\":false,\"capturePhrases\":[{\"phrase\":\"p\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]},"
        + "{\"name\":\"Skip\",\"triggerId\":\"t-skip\",\"onlyExecuteInDev\":true,\"capturePhrases\":[{\"phrase\":\"p\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}"
        + "]}";

      var (nodes, results, metadata) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsTrue(metadata.ContainsKey("t-good"));
      Assert.IsFalse(metadata.ContainsKey("t-skip"));
    }

    #endregion

    #region Overlay TextOverlayWrap Parsing

    [TestMethod]
    public void ConvertOverlays_TextOverlayWrap_ParsedFromNagData()
    {
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"Test Alert\",\"overlayType\":\"Alert\",\"textOverflow\":{\"whiteSpace\":\"nowrap\",\"overflow\":\"hidden\",\"textOverflow\":\"clip\"}}]}";

      var overlays = NagUtil.ConvertOverlays(json, out _, out _);

      Assert.HasCount(1, overlays);
      // NAG whiteSpace=nowrap means wrap is disabled
      Assert.IsFalse(overlays[0].OverlayData.TextOverlayWrap);
    }

    [TestMethod]
    public void ConvertOverlays_TextOverlayWrap_DefaultsToTrue()
    {
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"Test Alert\",\"overlayType\":\"Alert\"}]}";

      var overlays = NagUtil.ConvertOverlays(json, out _, out _);
      // Default is true (text wraps) when no textOverflow specified
      Assert.IsTrue(overlays[0].OverlayData.TextOverlayWrap);
    }

    [TestMethod]
    public void ConvertOverlays_FctOverlays_SkippedAndCounted()
    {
      var json = "{\"overlays\":[" +
        "{\"overlayId\":\"ov-1\",\"name\":\"Timer 1\",\"overlayType\":\"Timer\"}," +
        "{\"overlayId\":\"ov-2\",\"name\":\"FCT 1\",\"overlayType\":\"FCT\"}," +
        "{\"overlayId\":\"ov-3\",\"name\":\"FCT 2\",\"overlayType\":\"fct\"}" +
        "]}";

      var overlays = NagUtil.ConvertOverlays(json, out var skipped, out _);

      // Only the Timer overlay is imported; both FCT overlays are skipped and counted (case-insensitive).
      Assert.HasCount(1, overlays);
      Assert.AreEqual("Timer 1", overlays[0].Name);
      Assert.AreEqual(2, skipped);
    }

    [TestMethod]
    public void ConvertOverlays_TimerSortType_DescendingMappedToRemainingTime()
    {
      // NAG 2 (Descending = ending soonest first) matches EQLP Remaining Time exactly.
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"Desc Sort\",\"overlayType\":\"Timer\",\"timerSortType\":2}]}";

      var overlays = NagUtil.ConvertOverlays(json, out _, out var notes);

      Assert.HasCount(1, overlays);
      Assert.AreEqual(1, overlays[0].OverlayData.SortBy);
      Assert.IsEmpty(notes);
    }

    [TestMethod]
    public void ConvertOverlays_TimerSortType_AscendingKeptWithReversedOrderNote()
    {
      // NAG 1 (Ascending = most time remaining first) has no EQLP equivalent; it stays mapped to
      // Remaining Time and is reported via overlayNotes because the order is reversed vs NAG.
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"Asc Sort\",\"overlayType\":\"Timer\",\"timerSortType\":1}]}";

      var overlays = NagUtil.ConvertOverlays(json, out _, out var notes);

      Assert.HasCount(1, overlays);
      Assert.AreEqual(1, overlays[0].OverlayData.SortBy);
      Assert.HasCount(1, notes);
      StringAssert.Contains(notes[0], "Asc Sort");
    }

    [TestMethod]
    public void ConvertOverlays_TimerSortType_DefaultsToNoneWithoutNote()
    {
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"No Sort\",\"overlayType\":\"Timer\"}]}";

      var overlays = NagUtil.ConvertOverlays(json, out _, out var notes);

      Assert.HasCount(1, overlays);
      Assert.AreEqual(0, overlays[0].OverlayData.SortBy);
      Assert.IsEmpty(notes);
    }

    #endregion

    #region Missing Audio Files Tracking

    [TestMethod]
    public void ConvertTriggers_AudioFileNotInSoundsDir_TrackedInMissingAudioFiles()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions:
      [
        CreateAction(1, audioFileId: "audio-file-123")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Without files-database.json, the raw audioFileId is used as SoundToPlay.
      // Since "audio-file-123" doesn't have a .wav/.mp3 extension and won't exist in data/sounds/,
      // it should be tracked as missing.
      Assert.HasCount(1, results[0].MissingAudioFiles);
      Assert.AreEqual("audio-file-123", results[0].MissingAudioFiles[0]);
    }

    [TestMethod]
    public void ConvertTriggers_AudioFileWithExtensionNotInSoundsDir_TrackedInMissingAudioFiles()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions:
      [
        CreateAction(1, audioFileId: "test-sound.wav")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // "test-sound.wav" has a valid extension but doesn't exist in data/sounds/
      Assert.HasCount(1, results[0].MissingAudioFiles);
      Assert.AreEqual("test-sound.wav", results[0].MissingAudioFiles[0]);
    }

    [TestMethod]
    public void ConvertTriggers_NoAudioActions_MissingAudioFilesEmpty()
    {
      var json = CreateTriggerJson("Text Trigger", "pattern", actions:
      [
        CreateAction(0, displayText: "Hello")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.IsEmpty(results[0].MissingAudioFiles);
    }

    [TestMethod]
    public void ConvertTriggers_MultipleAudioActions_AllTrackedInMissingAudioFiles()
    {
      var json = CreateTriggerJson("Multi Audio Trigger", "pattern", actions:
      [
        CreateAction(1, audioFileId: "sound-a.wav"),
        CreateAction(1, audioFileId: "sound-b.mp3")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      Assert.HasCount(2, results[0].MissingAudioFiles);
      Assert.Contains("sound-a.wav", results[0].MissingAudioFiles);
      Assert.Contains("sound-b.mp3", results[0].MissingAudioFiles);
    }

    [TestMethod]
    public void ConvertTriggers_SkippedTrigger_MissingAudioFilesStillTracked()
    {
      var json = CreateTriggerJson("Skipped Trigger", "pattern", onlyExecuteInDev: true, actions:
      [
        CreateAction(1, audioFileId: "dev-only-sound.wav")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.IsEmpty(nodes);
      Assert.AreEqual("Skipped", results[0].Status);
      // Even skipped triggers should track their missing audio files for reporting
      Assert.HasCount(1, results[0].MissingAudioFiles);
    }

    [TestMethod]
    public void ConvertTriggers_AudioFileIdWithoutExtension_TrackedAsMissing()
    {
      // When there's no files-database.json, the raw audioFileId is used as SoundToPlay.
      // Even without a .wav/.mp3 extension, we still check for file existence —
      // if the file doesn't exist in data/sounds/, it's tracked as missing.
      var json = CreateTriggerJson("Ding Trigger", "pattern", actions:
      [
        CreateAction(1, audioFileId: "ShortWarningPing")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, nodes);
      // Without files-database.json, the raw ID is used directly
      Assert.AreEqual("ShortWarningPing", nodes[0].TriggerData.SoundToPlay);
      // Tracked as missing since the file doesn't exist in data/sounds/
      Assert.HasCount(1, results[0].MissingAudioFiles);
      Assert.AreEqual("ShortWarningPing", results[0].MissingAudioFiles[0]);
    }

    [TestMethod]
    public void ConvertTriggers_MissingAudioFilesInMetadata()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions:
      [
        CreateAction(1, audioFileId: "missing-sound.wav")
      ]);

      var (nodes, results, metadata) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(1, metadata);
      // Find the trigger ID from results to look up metadata
      var triggerId = results[0].TriggerId;
      Assert.IsTrue(metadata.ContainsKey(triggerId));
      Assert.IsNotNull(metadata[triggerId].MissingAudioFiles);
      Assert.HasCount(1, metadata[triggerId].MissingAudioFiles);
    }

    #endregion

    #region Helper Methods

    private string CreateTriggerJson(string? name, string pattern, bool onlyExecuteInDev = false, double score = 0.5,
        string? comments = null, JsonElement[]? capturePhrases = null, JsonElement[]? conditions = null,
        JsonElement[]? endEarlyPhrases = null, JsonElement[]? actions = null)
    {
      var phrases = capturePhrases ?? [CreateCapturePhrase(pattern)];
      var sb = new StringBuilder();
      sb.Append("{\"triggers\":[{\"name\":\"");
      sb.Append(name ?? "");
      sb.Append("\",\"triggerId\":\"test-123\",\"onlyExecuteInDev\":");
      sb.Append(onlyExecuteInDev.ToString().ToLower());
      if (score != 0.5)
      {
        sb.Append($",\"score\":{score}");
      }

      // NAG trigger-level comment
      if (!string.IsNullOrEmpty(comments))
      {
        var escapedComments = comments.Replace("\"", "\\\"");
        sb.Append($",\"comments\":\"{escapedComments}\"");
      }

      // Capture phrases
      sb.Append(",\"capturePhrases\":[");
      for (var i = 0; i < phrases.Length; i++)
      {
        if (i > 0) sb.Append(",");
        sb.Append(phrases[i].GetRawText());
      }
      sb.Append("]");

      // Conditions
      if (conditions != null && conditions.Length > 0)
      {
        sb.Append(",\"conditions\":[");
        for (var i = 0; i < conditions.Length; i++)
        {
          if (i > 0) sb.Append(",");
          sb.Append(conditions[i].GetRawText());
        }
        sb.Append("]");
      }

      // End early phrases
      if (endEarlyPhrases != null && endEarlyPhrases.Length > 0)
      {
        sb.Append(",\"endEarlyPhrases\":[");
        for (var i = 0; i < endEarlyPhrases.Length; i++)
        {
          if (i > 0) sb.Append(",");
          sb.Append(endEarlyPhrases[i].GetRawText());
        }
        sb.Append("]");
      }

      // Actions
      sb.Append(",\"actions\":[");
      var acts = actions ?? Array.Empty<JsonElement>();
      for (var i = 0; i < acts.Length; i++)
      {
        if (i > 0) sb.Append(",");
        sb.Append(acts[i].GetRawText());
      }
      sb.Append("]}]}");

      return sb.ToString();
    }

    private string CreateActionString(int actionType, string? displayText = null, double? duration = null, bool durationNull = false, string? audioFileId = null, string? variableName = null, string? phraseId = null, int? restartBehavior = null, bool? repeatTimer = null, int? repeatCount = null, bool? interruptSpeech = null, string[]? phrases = null, string[]? secondaryPhrases = null, string? extraJson = null)
    {
      var sb = new StringBuilder();
      sb.Append("{\"actionType\":");
      sb.Append(actionType);

      if (displayText != null)
      {
        sb.Append($",\"displayText\":\"{displayText.Replace("\"", "\\\"")}\"");
      }

      if (durationNull)
      {
        sb.Append(",\"duration\":null");
      }
      else if (duration.HasValue)
      {
        sb.Append($",\"duration\":{duration.Value}");
      }

      if (audioFileId != null)
      {
        sb.Append($",\"audioFileId\":\"{audioFileId}\"");
      }

      if (variableName != null)
      {
        sb.Append($",\"variableName\":\"{variableName}\"");
      }

      if (phraseId != null)
      {
        sb.Append($",\"phraseId\":\"{phraseId}\"");
      }

      if (restartBehavior.HasValue)
      {
        sb.Append($",\"restartBehavior\":{restartBehavior.Value}");
      }

      if (repeatTimer.HasValue)
      {
        sb.Append($",\"repeatTimer\":{(repeatTimer.Value ? "true" : "false")}");
      }

      if (repeatCount.HasValue)
      {
        sb.Append($",\"repeatCount\":{repeatCount.Value}");
      }

      if (interruptSpeech.HasValue)
      {
        sb.Append($",\"interruptSpeech\":{(interruptSpeech.Value ? "true" : "false")}");
      }

      if (phrases != null)
      {
        sb.Append($",\"phrases\":[{string.Join(",", phrases.Select(p => $"\"{p}\""))}]");
      }

      if (secondaryPhrases != null)
      {
        sb.Append($",\"secondaryPhrases\":[{string.Join(",", secondaryPhrases.Select(p => $"\"{p}\""))}]");
      }

      // Raw extra properties for fields without a dedicated parameter (e.g. "endingDuration":30).
      if (extraJson != null)
      {
        sb.Append(',' + extraJson);
      }

      sb.Append("}");
      return sb.ToString();
    }

    private JsonElement CreateAction(int actionType, string? displayText = null, double? duration = null, bool durationNull = false, string? audioFileId = null, string? variableName = null, string? phraseId = null, int? restartBehavior = null, bool? repeatTimer = null, int? repeatCount = null, bool? interruptSpeech = null, string[]? phrases = null, string[]? secondaryPhrases = null, string? extraJson = null)
    {
      var json = CreateActionString(actionType, displayText, duration, durationNull, audioFileId, variableName, phraseId, restartBehavior, repeatTimer, repeatCount, interruptSpeech, phrases, secondaryPhrases, extraJson);
      return JsonDocument.Parse(json).RootElement;
    }

    private JsonElement CreateCapturePhrase(string phrase, bool useRegEx = false, string? phraseId = null, bool? ignoreCase = null)
    {
      var escapedPhrase = phrase.Replace("\\", "\\\\").Replace("\"", "\\\"");
      var json = $"{{\"phrase\":\"{escapedPhrase}\",\"useRegEx\":{useRegEx.ToString().ToLower()}" + (phraseId != null ? $",\"phraseId\":\"{phraseId}\"" : "") + (ignoreCase.HasValue ? $",\"ignoreCase\":{ignoreCase.Value.ToString().ToLower()}" : "") + "}";
      return JsonDocument.Parse(json).RootElement;
    }

    private JsonElement CreateCondition(string variableName, int operatorType, string? variableValue)
    {
      var sb = new StringBuilder();
      sb.Append("{\"conditionType\":1,\"variableName\":\"");
      sb.Append(variableName);
      sb.Append("\",\"operatorType\":");
      sb.Append(operatorType);
      if (variableValue != null)
      {
        sb.Append($",\"variableValue\":\"{variableValue.Replace("\"", "\\\"")}\"");
      }
      sb.Append("}");
      return JsonDocument.Parse(sb.ToString()).RootElement;
    }

    private JsonElement CreateEndEarlyPhrase(string phrase)
    {
      var json = $"{{\"phrase\":\"{phrase.Replace("\"", "\\\"")}\"}}";
      return JsonDocument.Parse(json).RootElement;
    }

    #endregion

    #region Real Data Integration Tests

    /// <summary>
    /// Loads a real NAG trigger extracted from nag/trigger-database.json and verifies
    /// that set-variable (actionType 5) with phraseId correctly converts the capture
    /// group to a named group and creates a VariableAction.
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_SetVariableWithPhraseId()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // The trigger has 8 capture phrases → 8 EQLP triggers (but only 1 import result)
      Assert.HasCount(8, nodes);
      Assert.HasCount(1, results);

      // Phrase 0 (^You begin casting (.*)\.) should have set-variable mapping
      var spellCastNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("s1"));
      Assert.IsNotNull(spellCastNode, "Expected at least one trigger with a converted named group s1");

      // The pattern should use (?<s1>) instead of unnamed (.*)
      Assert.Contains("?<s1>", spellCastNode.TriggerData.Pattern);

      // Should have a VariableAction storing SpellBeingCast from {s1}
      var setVarAction = spellCastNode.TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "SpellBeingCast");
      Assert.IsNotNull(setVarAction);
      Assert.AreEqual("{s1}", setVarAction.Value);
      Assert.IsTrue(setVarAction.IsSetAction);

      // All triggers should import successfully (no dropped features)
      Assert.IsTrue(results.All(r => r.Status == "Imported" || r.Status == "Partial"), $"Unexpected status: {string.Join(", ", results.Select(r => r.Status))}");
    }

    /// <summary>
    /// Loads a real NAG trigger with set-variable (actionType 5) but NO phraseId, plus two
    /// countdown actions. Verifies that the variable mapping applies to ALL regex phrases with
    /// capture groups and that each NAG timer action produces its own nodes (2 phrases × 2
    /// timers = 4) instead of collapsing into one trigger where the last timer overwrote the
    /// first's duration, label, and restart behavior.
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_BardEpic_NoPhraseId_AppliesToAllRegexPhrases()
    {
      var json = LoadFixture("bard-epic-2-caster.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // 2 capture phrases × 2 countdown actions → 4 EQLP triggers (but only 1 import result for the trigger)
      Assert.HasCount(4, nodes);
      Assert.HasCount(1, results);

      // Phrase 0 (^([A-Za-z]{3,15}) begins? casting...) is regex with a capture group — both of
      // its timer nodes keep the converted named group and the set-variable mapping.
      var casterNodes = nodes.Where(n => n.TriggerData.Pattern.Contains("?<s1>")).ToList();
      Assert.AreEqual(2, casterNodes.Count, "Both timer variants of the regex phrase should keep the named group");

      // The set-variable mapping rides with the shared non-timer actions on the first timer
      // variant only — the sibling node matches the same line and must not set it again.
      var firstCaster = casterNodes.First(n => n.Name.EndsWith("(Timer 1)"));
      var setVarAction = firstCaster.TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "BrdEpic2Caster");
      Assert.IsNotNull(setVarAction);
      Assert.AreEqual("{s1}", setVarAction.Value);

      var secondCaster = casterNodes.First(n => n.Name.EndsWith("(Timer 2)"));
      Assert.AreEqual(0, secondCaster.TriggerData.VariableActions.Count(va => va.VariableName == "BrdEpic2Caster"),
        "Sibling timer node must not re-set the shared variable");

      // Phrase 1 (You are filled with the spirit of Vesagran.) is non-regex — no group conversion
      var spiritNodes = nodes.Where(n => !n.TriggerData.Pattern.Contains("?<s1>")).ToList();
      Assert.AreEqual(2, spiritNodes.Count);

      // Both countdown labels must survive on their own node pairs (the fixture trims the NAG
      // duration fields, so no visible timer is enabled — the point is per-action routing).
      Assert.AreEqual(2, nodes.Count(n => n.TriggerData.AltTimerName == "BRD Epic ({BrdEpic2Caster})"));
      Assert.AreEqual(2, nodes.Count(n => n.TriggerData.AltTimerName == "BRD Epic refresh ({BrdEpic2Caster})"));
    }

    /// <summary>
    /// Verifies the two displayText routes in the Bard Epic trigger (set-variable without
    /// phraseId, text overlay + two countdowns):
    /// - actionType 0's displayText "🎵Bard Epic🎶" is the TextToDisplay on each phrase's first
    ///   timer variant only (sibling variants must not repeat the shared text);
    /// - each countdown's label goes to AltTimerName with ${BrdEpic2Caster} converted to
    ///   {BrdEpic2Caster} (valid EQLP token syntax), and no node's TextToDisplay carries a
    ///   timer label.
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_BardEpic_DisplayTextVariableConverted()
    {
      var json = LoadFixture("bard-epic-2-caster.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(4, nodes);

      // The text overlay action's displayText is the node text on each phrase's first timer
      // variant only — sibling variants match the same line and would show it twice.
      var firstVariants = nodes.Where(n => n.Name.EndsWith("(Timer 1)")).ToList();
      var secondVariants = nodes.Where(n => n.Name.EndsWith("(Timer 2)")).ToList();
      Assert.AreEqual(2, firstVariants.Count);
      Assert.AreEqual(2, secondVariants.Count);
      Assert.IsTrue(firstVariants.All(n => n.TriggerData.TextToDisplay == "🎵Bard Epic🎶"),
        $"First variants should carry the text overlay: {string.Join(" | ", firstVariants.Select(n => n.TriggerData.TextToDisplay))}");
      Assert.IsTrue(secondVariants.All(n => string.IsNullOrEmpty(n.TriggerData.TextToDisplay)),
        "Sibling timer variants must not repeat the shared text overlay");

      // No timer label may leak into display text on any node.
      Assert.IsTrue(nodes.All(n => !(n.TriggerData.TextToDisplay ?? "").Contains("BRD Epic")),
        "Timer labels must not be imported as display text");

      // Timer labels live in AltTimerName with the NAG variable converted to EQLP syntax.
      foreach (var node in nodes)
      {
        Assert.IsNotNull(node.TriggerData.AltTimerName);
        Assert.IsFalse(node.TriggerData.AltTimerName.Contains("${BrdEpic2Caster}"),
          $"Unconverted NAG variable in timer label: {node.TriggerData.AltTimerName}");
        Assert.IsFalse(node.TriggerData.AltTimerName.Contains("{$"),
          $"Invalid token syntax in timer label: {node.TriggerData.AltTimerName}");
      }

      // TTS action's displayText is preserved as speech on the first variants only — not
      // clobbered by the timers and not repeated (spoken twice) on the siblings.
      Assert.IsTrue(firstVariants.All(n => n.TriggerData.TextToSpeak == "Bard epic"));
      Assert.IsTrue(secondVariants.All(n => string.IsNullOrEmpty(n.TriggerData.TextToSpeak)),
        "Sibling timer variants must not repeat the shared TTS");
    }

    /// <summary>
    /// Verifies that ${SpellBeingCast} in display text is converted to {SpellBeingCast}
    /// (valid EQLP syntax), not {$SpellBeingCast} (invalid).
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_DisplayTextVariableConverted()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // The actionType 7 (clear variable) has displayText "Spell ${SpellBeingCast} was interrupted."
      var nodeWithDisplay = nodes.FirstOrDefault(n => n.TriggerData.TextToDisplay.Contains("interrupted"));
      Assert.IsNotNull(nodeWithDisplay, "Expected a trigger with 'interrupted' in display text");

      // Should contain {SpellBeingCast} (valid EQLP), not {$SpellBeingCast} or ${SpellBeingCast}
      Assert.Contains("{SpellBeingCast}", nodeWithDisplay.TriggerData.TextToDisplay);
      Assert.DoesNotContain("{$SpellBeingCast}", nodeWithDisplay.TriggerData.TextToDisplay);
      Assert.DoesNotContain("${SpellBeingCast}", nodeWithDisplay.TriggerData.TextToDisplay);
    }

    /// <summary>
    /// Verifies that NAG counter actions (actionType 8) are properly converted:
    /// - Timer portion (duration, displayText, colors) becomes a regular timer trigger
    /// - A Counter-type VariableAction is added to increment the variable on each match
    /// - Reset phrases become separate triggers with Clear VariableAction
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_Counter_ConvertedToVariableActionWithResetTrigger()
    {
      var json = LoadFixture("counter-physical.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // Should have 2 nodes: 1 main counter trigger + 1 reset phrase trigger (but only 1 import result)
      Assert.HasCount(2, nodes);
      Assert.HasCount(1, results);

      // Main counter trigger (from capture phrase "Your bones are brittle.")
      var counterNode = nodes[0];
      // NAG counters are invisible tallies — the import must NOT create a visible timer.
      Assert.IsFalse(counterNode.TriggerData.EnableTimer, "NAG counters have no visible timer component");
      // The NAG duration is an idle-reset window, mapped to RepeatedResetTime (not DurationSeconds)
      Assert.AreEqual(300.0, counterNode.TriggerData.RepeatedResetTime);
      Assert.AreEqual("Physical", counterNode.TriggerData.TextToDisplay);
      // ConvertColor converts #RRGGBB → #AARRGGBB (FF = full opacity) for EQLP format
      Assert.AreEqual("#FFb71c1c", counterNode.TriggerData.ActiveColor);
      // TimerBackgroundColor rgba(48,7,7,0.75) → #BF300707 (alpha=192, r=48, g=7, b=7)
      Assert.AreEqual("#BF300707", counterNode.TriggerData.IdleColor);

      // Should have a Counter-type VariableAction named "Physical"
      var counterVarAction = counterNode.TriggerData.VariableActions.FirstOrDefault();
      Assert.IsNotNull(counterVarAction);
      Assert.IsTrue(counterVarAction.IsSetAction);
      Assert.IsTrue(counterVarAction.IsCounterType);
      Assert.AreEqual("Physical", counterVarAction.VariableName);
      Assert.AreEqual(1, counterVarAction.Step);

      // NAG counters are invisible tallies in NAG itself, and this import keeps them
      // invisible (variable action + RepeatedResetTime), so the conversion is faithful.
      Assert.AreEqual("Imported", results[0].Status);

      // Reset phrase trigger (from "^Your bones are no longer brittle.")
      var resetNode = nodes[1];
      Assert.Contains("Counter Reset", resetNode.Name, $"Reset trigger name should contain 'Counter Reset': {resetNode.Name}");
      Assert.AreEqual("^Your bones are no longer brittle\\.", resetNode.TriggerData.Pattern);
      Assert.IsTrue(resetNode.TriggerData.UseRegex);

      // Should have a Clear VariableAction for the counter variable
      var clearVarAction = resetNode.TriggerData.VariableActions.FirstOrDefault();
      Assert.IsNotNull(clearVarAction);
      Assert.IsTrue(clearVarAction.IsClearAction);
      Assert.AreEqual("Physical", clearVarAction.VariableName);

      // Comments should explain this is auto-generated from counter reset phrase
      Assert.Contains("counter", resetNode.TriggerData.Comments, "Reset trigger comments should mention counter");
    }

    /// <summary>
    /// Verifies that ${Character} in regex capture phrases is converted to {c}
    /// (EQLP native character name replacement) by DollarVarRegex.
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_CharacterRefConvertedInRegexPhrase()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // Phrase "^${Character}'s ${SpellBeingCast} spell has been reflected" should have {c}
      var reflectNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("reflected"));
      Assert.IsNotNull(reflectNode, "Expected a trigger with 'reflected' in pattern");
      Assert.Contains("{c}", reflectNode.TriggerData.Pattern);
      Assert.DoesNotContain("${Character}", reflectNode.TriggerData.Pattern);
    }

    /// <summary>
    /// Verifies that NAG actionType 7 (clear variable) with a specific phraseId
    /// creates a VariableAction { ActionType=Clear } on the matching phrase trigger only,
    /// rather than using EndTimerClearVariables which would never fire without a timer.
    /// In "Capture spell casting", phrase [3] ("Your X spell is interrupted") has a clear-variable
    /// action for SpellBeingCast — this should create a Clear VariableAction on that phrase's trigger.
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_PhraseSpecificClearVariable()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      // Should have 8 phrase triggers (but only 1 import result)
      Assert.HasCount(8, nodes);
      Assert.HasCount(1, results);
      // The trigger is Partial: the ${var} restriction note (phrases use ${SpellBeingCast})
      // and the clear-variable alert overlay note have no EQLP equivalents.
      Assert.AreEqual("Partial", results[0].Status);
      // Assert.Contains on a collection is an exact match — use the full note string.
      Assert.Contains("phrase ${var} restriction (NAG only matches stored variable values; import matches any text)", results[0].DroppedFeatures);

      // All 5 failure phrases [3-7] ("interrupted", "resisted", "blocked", "fizzles", "reflected")
      // should have a Clear VariableAction for SpellBeingCast, since the actionType 7 has
      // a "phrases" array listing all 5 phraseIds.
      var failurePatterns = new[] { "interrupted", "resisted", "did not take hold", "fizzles", "reflected" };
      foreach (var pattern in failurePatterns)
      {
        var node = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains(pattern));
        Assert.IsNotNull(node, $"Expected a trigger with '{pattern}' in pattern");
        var clearVarAction = node.TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "SpellBeingCast" && va.IsClearAction);
        Assert.IsNotNull(clearVarAction, $"Phrase matching '{pattern}' should have SpellBeingCast Clear VariableAction (in action's phrases array)");
      }

      // Phrase [0] ("You begin casting") and phrases [1-2] ("You activate", "You begin singing")
      // should NOT have the clear VariableAction — they are not in the action's phrases array.
      var nonFailureNodes = nodes.Where(n => failurePatterns.All(p => !n.TriggerData.Pattern.Contains(p))).ToList();
      Assert.AreEqual(3, nonFailureNodes.Count, "Expected 3 non-failure phrase triggers (begin casting, activate, singing)");
      foreach (var node in nonFailureNodes)
      {
        var hasClear = node.TriggerData.VariableActions.Any(va => va.VariableName == "SpellBeingCast" && va.IsClearAction);
        Assert.IsFalse(hasClear, $"Non-failure phrase should not have SpellBeingCast clear VariableAction: {node.Name}");
      }

      // The set-variable action (phrase [0]) should still work — that trigger gets a Set VariableAction
      var castingNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("s1"));
      Assert.IsNotNull(castingNode, "Expected phrase [0] to have converted capture group to s1");
      var setVarAction = castingNode.TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "SpellBeingCast" && va.IsSetAction);
      Assert.IsNotNull(setVarAction, "Phrase [0] should have Set VariableAction for SpellBeingCast");

      // The clear-variable action's NAG alert overlay (overlayId + 15s duration) has no EQLP
      // equivalent for non-timer actions — it must be reported as a dropped feature.
      Assert.Contains("clear variable action alert overlay", results[0].DroppedFeatures);
    }

    #region Dropped Feature Reporting

    [TestMethod]
    public void ConvertTriggers_InterruptSpeech_ReportedAsDroppedFeature()
    {
      // NAG interruptSpeech preempts currently-speaking text; the import approximates this by
      // assigning priority 1 (top urgency). The note stays visible for transparency, but it is
      // an implemented approximation — it must NOT downgrade status to Partial on its own.
      var json = CreateTriggerJson("Interrupt", "pattern", actions:
      [
        CreateAction(2, displayText: "interrupting text", interruptSpeech: true)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.AreEqual("Imported", results[0].Status);
      Assert.Contains(NagUtil.InterruptSpeechNote, results[0].DroppedFeatures);
      Assert.AreEqual(1, nodes[0].TriggerData.Priority);
    }

    [TestMethod]
    public void ConvertTriggers_NoInterruptSpeech_NoNote()
    {
      var json = CreateTriggerJson("No Interrupt", "pattern", actions:
      [
        CreateAction(0, displayText: "text")
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.AreEqual("Imported", results[0].Status);
      Assert.IsFalse(results[0].DroppedFeatures.Contains(NagUtil.InterruptSpeechNote));
      // No interruptSpeech — priority stays the score-derived default.
      Assert.AreEqual(3, nodes[0].TriggerData.Priority);
    }

    [TestMethod]
    public void ConvertTriggers_SecondaryPhrases_ReportedAsDroppedFeature()
    {
      var json = CreateTriggerJson("Secondary", "pattern", actions:
      [
        CreateAction(0, displayText: "text", secondaryPhrases: ["sp1"])
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("secondary phrases", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_MoreThanThreeEndEarlyPhrases_KeepsFirstThreeAndReportsNote()
    {
      var json = CreateTriggerJson("End Early 4", "pattern", endEarlyPhrases:
      [
        CreateEndEarlyPhrase("faded"),
        CreateEndEarlyPhrase("interrupted"),
        CreateEndEarlyPhrase("resisted"),
        CreateEndEarlyPhrase("blocked")
      ], actions:
      [
        CreateAction(3, displayText: "Channeling", duration: 30.0)
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("extra end-early phrases dropped (max 3)", results[0].DroppedFeatures);
      // First three are kept in order; the fourth is dropped.
      Assert.AreEqual("faded", nodes[0].TriggerData.EndEarlyPattern);
      Assert.AreEqual("interrupted", nodes[0].TriggerData.EndEarlyPattern2);
      Assert.AreEqual("resisted", nodes[0].TriggerData.EndEarlyPattern3);
    }

    [TestMethod]
    public void ConvertTriggers_ActionScopedToPhraseSubset_ReportedAsDroppedFeature()
    {
      // The timer action only lists phrase p1, but the import applies the merged action set to
      // both phrase triggers — the divergence must be reported.
      var json = CreateTriggerJson("Scoped", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^You cast \w+", useRegEx: true, phraseId: "p1"),
        CreateCapturePhrase(@"^You cast 2 \w+", useRegEx: true, phraseId: "p2")
      ], actions:
      [
        CreateAction(3, displayText: "Timer", duration: 10.0, phrases: ["p1"])
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.Contains("per-phrase action scoping", results[0].DroppedFeatures);
    }

    [TestMethod]
    public void ConvertTriggers_ActionCoversAllPhrases_NoScopingNote()
    {
      var json = CreateTriggerJson("Unscoped", "pattern", capturePhrases:
      [
        CreateCapturePhrase(@"^You cast \w+", useRegEx: true, phraseId: "p1"),
        CreateCapturePhrase(@"^You cast 2 \w+", useRegEx: true, phraseId: "p2")
      ], actions:
      [
        CreateAction(3, displayText: "Timer", duration: 10.0, phrases: ["p1", "p2"])
      ]);

      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(2, nodes);
      Assert.AreEqual("Imported", results[0].Status);
      Assert.IsFalse(results[0].DroppedFeatures.Contains("per-phrase action scoping"));
    }

    #endregion

    /// <summary>
    /// Verifies that capture phrases [1] and [2] in "Capture spell casting" ("You activate X",
    /// "You begin singing X") get set-variable VariableActions via fallback logic.
    /// These phrases have un-named capture groups and no ${var} references, but are not
    /// explicitly listed in the actionType 5's phrases array (which only includes phrase [0]).
    /// The fallback logic should inherit the SET action from phrase [0].
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_FallbackSetVariableForPhrases1And2()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(8, nodes);
      Assert.HasCount(1, results);

      // Phrase [1]: "^You activate (.*)\." — should get set-variable fallback
      var activateNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("activate") && n.TriggerData.Pattern.Contains("?<s"));
      Assert.IsNotNull(activateNode, "Expected phrase [1] 'You activate X' to have a converted named group");
      var activateSetVar = activateNode.TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "SpellBeingCast" && va.IsSetAction);
      Assert.IsNotNull(activateSetVar, "Phrase [1] should get SET VariableAction via fallback");
      Assert.AreEqual("{s2}", activateSetVar.Value, "Should use s2 group name (phrase index 2 = 0-based)");

      // Phrase [2]: "^You begin singing (.*)\." — should get set-variable fallback
      var singNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("singing") && n.TriggerData.Pattern.Contains("?<s"));
      Assert.IsNotNull(singNode, "Expected phrase [2] 'You begin singing X' to have a converted named group");
      var singSetVar = singNode.TriggerData.VariableActions.FirstOrDefault(va => va.VariableName == "SpellBeingCast" && va.IsSetAction);
      Assert.IsNotNull(singSetVar, "Phrase [2] should get SET VariableAction via fallback");
      Assert.AreEqual("{s3}", singSetVar.Value, "Should use s3 group name (phrase index 3 = 0-based)");
    }

    private static string LoadFixture(string fixtureName)
    {
      // Resolve by suffix: the manifest name depends on RootNamespace, which differs per project.
      var assembly = typeof(NagUtilTriggerImportTest).Assembly;
      var resourceName = assembly.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith($".{fixtureName}", StringComparison.OrdinalIgnoreCase));
      using var stream = assembly.GetManifestResourceStream(resourceName);
      if (stream is null) throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
      using var reader = new StreamReader(stream);
      var content = reader.ReadToEnd();
      // Wrap single trigger object in {"triggers":[...]} format expected by ConvertTriggers
      if (!content.TrimStart().StartsWith("{\"triggers\""))
      {
        return $"{{\"triggers\":[{content}]}}";
      }
      return content;
    }

    /// <summary>
    /// Verifies that phrase [0] ("You begin casting") does NOT get the interrupt display text
    /// from actionType 7's clear-variable action. The interrupt message should only appear
    /// on phrases [3-7] (the ones listed in the action's "phrases" array).
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_BeginCastPhraseNoInterruptDisplay()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(8, nodes);

      // Phrase [0]: "^You begin casting (.*)\." — should NOT have interrupt display text
      var beginCastNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("begin casting"));
      Assert.IsNotNull(beginCastNode, "Expected a trigger with 'begin casting' in pattern");
      Assert.IsFalse(beginCastNode.TriggerData.TextToDisplay.Contains("interrupted"),
        $"Begin-casting phrase should NOT show interrupt message. TextToDisplay: {beginCastNode.TriggerData.TextToDisplay}");

      // Phrase [3]: "^Your ${{SpellBeingCast}} spell is interrupted\." — SHOULD have interrupt display text
      var interruptNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("interrupted"));
      Assert.IsNotNull(interruptNode, "Expected a trigger with 'interrupted' in pattern (phrase [3])");
      Assert.IsTrue(interruptNode.TriggerData.TextToDisplay.Contains("Spell {SpellBeingCast} was interrupted."),
        $"Interrupt phrase should show interrupt message. TextToDisplay: {interruptNode.TriggerData.TextToDisplay}");

      // Phrases [4-7] (resisted, blocked, fizzles, reflected) — SHOULD also have interrupt display text
      var failurePatterns = new[] { "resisted", "did not take hold", "fizzles", "reflected" };
      foreach (var pattern in failurePatterns)
      {
        var node = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains(pattern));
        Assert.IsNotNull(node, $"Expected a trigger with '{pattern}' in pattern");
        Assert.IsTrue(node.TriggerData.TextToDisplay.Contains("Spell {SpellBeingCast} was interrupted."),
          $"Phrase matching '{pattern}' should show interrupt message. TextToDisplay: {node.TriggerData.TextToDisplay}");
      }
    }

    /// <summary>
    /// Verifies that phrase [5] ("X resisted your ${SpellBeingCast}") does NOT get a fallback
    /// set-variable VariableAction. This phrase already has a named capture group from
    /// ${SpellBeingCast} conversion, so the fallback should skip it — the spell name is
    /// already captured by (?<SpellBeingCast>.+?) and doesn't need to be stored again.
    /// The (.*) at the start of the pattern captures the NPC name, not the spell.
    /// </summary>
    [TestMethod]
    public void ConvertTriggers_RealData_CaptureSpellCasting_Phrase5NoFallbackSetVariable()
    {
      var json = LoadFixture("capture-spell-casting.json");
      var (nodes, results, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(8, nodes);

      // Phrase [5]: "^(.*) resisted your (?<SpellBeingCast>.+?)" — should NOT get a set-variable VariableAction
      var resistNode = nodes.FirstOrDefault(n => n.TriggerData.Pattern.Contains("resisted"));
      Assert.IsNotNull(resistNode, "Expected a trigger with 'resisted' in pattern (phrase [5])");

      // Should have the named capture group from ${SpellBeingCast} conversion
      Assert.IsTrue(resistNode.TriggerData.Pattern.Contains("?<SpellBeingCast>"),
        $"Pattern should contain named group SpellBeingCast: {resistNode.TriggerData.Pattern}");

      // Should NOT have a set-variable VariableAction that maps s-group to SpellBeingCast
      // (the (.*) captures NPC name, not spell name — fallback should skip this phrase)
      var wrongSetVar = resistNode.TriggerData.VariableActions.FirstOrDefault(va =>
        va.VariableName == "SpellBeingCast" &&
        va.IsSetAction &&
        va.Value != null &&
        va.Value.Contains("s") &&
        !va.Value.Contains("SpellBeingCast"));
      Assert.IsNull(wrongSetVar, "Phrase [5] should NOT get a fallback set-variable mapping s-group → SpellBeingCast (would capture NPC name instead of spell)");
    }

    #endregion

    #region Sibling Name Uniquification

    [TestMethod]
    public void ConvertTriggers_SameNameSiblings_UniquifySkipsTakenSuffixes()
    {
      // A literal sibling already named "A (2)" must not be clobbered by the generated suffix —
      // the duplicate moves to the next free number instead. (The shared triggerId is fine here:
      // only node generation is under test, and metadata is ignored.)
      var json = "{\"triggers\":[" + JoinTriggerBodies(
          CreateTriggerJson("A", "p1", actions: [CreateAction(0, displayText: "d1", duration: 5.0)]),
          CreateTriggerJson("A", "p2", actions: [CreateAction(0, displayText: "d2", duration: 5.0)]),
          CreateTriggerJson("A (2)", "p3", actions: [CreateAction(0, displayText: "d3", duration: 5.0)])) + "]}";

      var (nodes, _, _) = ConvertTriggersUnwrapped(json);

      Assert.HasCount(3, nodes);
      CollectionAssert.AreEquivalent(new[] { "A", "A (2)", "A (3)" }, nodes.Select(n => n.Name).ToList());

      // Each CreateTriggerJson output is {"triggers":[<one object>]} — strip the wrappers and join.
      static string JoinTriggerBodies(params string[] singleTriggerJsons) =>
        string.Join(",", singleTriggerJsons.Select(j => j["{\"triggers\":[".Length..^2]));
    }

    #endregion
  }
}

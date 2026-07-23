namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// Verify that TriggerUtil.Copy copies every property from the source Trigger.
  /// Regression tests for missing properties (e.g. EndTimerClearVariables).
  /// </summary>
  [TestClass]
  public class TriggerUtilCopyTest
  {
    private Trigger CreateSource()
    {
      return new Trigger
      {
        Private = true,
        AltTimerName = "alt timer",
        Comments = "test comment",
        DurationSeconds = 15.5,
        EnableTimer = true,
        TimerType = 2,
        EndEarlyPattern = "end early pattern",
        EndEarlyPattern2 = "end early pattern 2",
        EndEarlyPattern3 = "end early pattern 3",
        EndUseRegex = true,
        EndUseRegex2 = false,
        EndUseRegex3 = true,
        EndEarlyRepeatedCount = 42,
        WorstEvalTime = 123,
        Pattern = "test pattern",
        PreviousPattern = "prev pattern",
        MatchVariableCondition = "{hp} > 50",
        Priority = 7,
        TriggerAgainOption = 3,
        UseRegex = true,
        PreviousUseRegex = false,
        ActiveColor = "#FFFF0000",
        IdleColor = "#FF00FF00",
        ResetColor = "#FF0000FF",
        FontColor = "#FFFFFFFF",
        IconSource = "icon.png",
        SelectedOverlays = ["overlay1", "overlay2"],
        ResetDurationSeconds = 5.25,
        WarningSeconds = 10,
        EndEarlyTextToDisplay = "end early display",
        EndTextToDisplay = "end display",
        TextToDisplay = "main display",
        WarningTextToDisplay = "warning display",
        EndEarlyTextToSpeak = "end early speak",
        EndTextToSpeak = "end speak",
        TextToSpeak = "main speak",
        WarningTextToSpeak = "warning speak",
        SoundToPlay = "sound.wav",
        EndEarlySoundToPlay = "end-early-sound.wav",
        EndSoundToPlay = "end-sound.wav",
        WarningSoundToPlay = "warning-sound.wav",
        EndTimerClearVariables = "var1,var2,var3",
        ChatWebhook = "https://example.com/webhook",
        TextToSendToChat = "chat text",
        TextToShare = "share text",
        TimesToLoop = 5,
        LockoutTime = 2.5,
        VoiceRate = -10,
        Volume = 7,
        RepeatedResetTime = 1.25,
        VariableActions = [
          new VariableAction { ActionType = 0, DataType = 0, VariableName = "myVar", Value = "hello" },
          new VariableAction { ActionType = 1, DataType = 0, VariableName = "clearMe" }
        ]
      };
    }

    [TestMethod]
    public async Task Copy_CopiesAllStringProperties()
    {
      var source = CreateSource();
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.AreEqual(source.AltTimerName, dest.AltTimerName);
      Assert.AreEqual(source.Comments, dest.Comments);
      Assert.AreEqual(source.Pattern, dest.Pattern);
      Assert.AreEqual(source.PreviousPattern, dest.PreviousPattern);
      Assert.AreEqual(source.MatchVariableCondition, dest.MatchVariableCondition);
      Assert.AreEqual(source.EndEarlyPattern, dest.EndEarlyPattern);
      Assert.AreEqual(source.EndEarlyPattern2, dest.EndEarlyPattern2);
      Assert.AreEqual(source.EndEarlyPattern3, dest.EndEarlyPattern3);
      Assert.AreEqual(source.TextToDisplay, dest.TextToDisplay);
      Assert.AreEqual(source.TextToShare, dest.TextToShare);
      Assert.AreEqual(source.ChatWebhook, dest.ChatWebhook);
      Assert.AreEqual(source.TextToSendToChat, dest.TextToSendToChat);
      Assert.AreEqual(source.WarningTextToDisplay, dest.WarningTextToDisplay);
      Assert.AreEqual(source.EndTextToDisplay, dest.EndTextToDisplay);
      Assert.AreEqual(source.EndEarlyTextToDisplay, dest.EndEarlyTextToDisplay);
      Assert.AreEqual(source.TextToSpeak, dest.TextToSpeak);
      Assert.AreEqual(source.WarningTextToSpeak, dest.WarningTextToSpeak);
      Assert.AreEqual(source.EndTextToSpeak, dest.EndTextToSpeak);
      Assert.AreEqual(source.EndEarlyTextToSpeak, dest.EndEarlyTextToSpeak);
      Assert.AreEqual(source.SoundToPlay, dest.SoundToPlay);
      Assert.AreEqual(source.WarningSoundToPlay, dest.WarningSoundToPlay);
      Assert.AreEqual(source.EndSoundToPlay, dest.EndSoundToPlay);
      Assert.AreEqual(source.EndEarlySoundToPlay, dest.EndEarlySoundToPlay);
      Assert.AreEqual(source.EndTimerClearVariables, dest.EndTimerClearVariables);
      Assert.AreEqual(source.IconSource, dest.IconSource);
      Assert.AreEqual(source.ActiveColor, dest.ActiveColor);
      Assert.AreEqual(source.IdleColor, dest.IdleColor);
      Assert.AreEqual(source.ResetColor, dest.ResetColor);
      Assert.AreEqual(source.FontColor, dest.FontColor);
    }

    [TestMethod]
    public async Task Copy_CopiesAllNumericProperties()
    {
      var source = CreateSource();
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.AreEqual(source.DurationSeconds, dest.DurationSeconds);
      Assert.AreEqual(source.ResetDurationSeconds, dest.ResetDurationSeconds);
      Assert.AreEqual(source.WarningSeconds, dest.WarningSeconds);
      Assert.AreEqual(source.Priority, dest.Priority);
      Assert.AreEqual(source.TriggerAgainOption, dest.TriggerAgainOption);
      Assert.AreEqual(source.TimerType, dest.TimerType);
      Assert.AreEqual(source.TimesToLoop, dest.TimesToLoop);
      Assert.AreEqual(source.LockoutTime, dest.LockoutTime);
      Assert.AreEqual(source.VoiceRate, dest.VoiceRate);
      Assert.AreEqual(source.Volume, dest.Volume);
      Assert.AreEqual(source.RepeatedResetTime, dest.RepeatedResetTime);
      Assert.AreEqual(source.EndEarlyRepeatedCount, dest.EndEarlyRepeatedCount);
      Assert.AreEqual(source.WorstEvalTime, dest.WorstEvalTime);
    }

    [TestMethod]
    public async Task Copy_CopiesAllBooleanProperties()
    {
      var source = CreateSource();
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.AreEqual(source.Private, dest.Private);
      Assert.AreEqual(source.UseRegex, dest.UseRegex);
      Assert.AreEqual(source.PreviousUseRegex, dest.PreviousUseRegex);
      Assert.AreEqual(source.EndUseRegex, dest.EndUseRegex);
      Assert.AreEqual(source.EndUseRegex2, dest.EndUseRegex2);
      Assert.AreEqual(source.EndUseRegex3, dest.EndUseRegex3);
    }

    [TestMethod]
    public async Task Copy_CopiesSelectedOverlaysAsNewList()
    {
      var source = CreateSource();
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.AreEqual(source.SelectedOverlays.Count, dest.SelectedOverlays.Count);
      CollectionAssert.AreEquivalent(source.SelectedOverlays, dest.SelectedOverlays);
      // Verify it's a new list, not the same reference
      Assert.AreNotSame(source.SelectedOverlays, dest.SelectedOverlays);
    }

    [TestMethod]
    public async Task Copy_CopiesVariableActionsAsNewList()
    {
      var source = CreateSource();
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.AreEqual(source.VariableActions.Count, dest.VariableActions.Count);
      Assert.AreNotSame(source.VariableActions, dest.VariableActions);

      for (var i = 0; i < source.VariableActions.Count; i++)
      {
        var src = source.VariableActions[i];
        var dst = dest.VariableActions[i];
        Assert.AreEqual(src.ActionType, dst.ActionType);
        Assert.AreEqual(src.DataType, dst.DataType);
        Assert.AreEqual(src.VariableName, dst.VariableName);
        Assert.AreEqual(src.Value, dst.Value);
        Assert.AreEqual(src.Step, dst.Step);
        Assert.AreEqual(src.InitialValue, dst.InitialValue);
        Assert.AreEqual(src.TimeToLiveSeconds, dst.TimeToLiveSeconds);
      }
    }

    [TestMethod]
    public async Task Copy_EmptyVariableActionsProducesEmptyList()
    {
      var source = new Trigger { VariableActions = [] };
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.IsNotNull(dest.VariableActions);
      Assert.AreEqual(0, dest.VariableActions.Count);
    }

    [TestMethod]
    public async Task Copy_NullVariableActionsProducesEmptyList()
    {
      var source = new Trigger { VariableActions = null };
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.IsNotNull(dest.VariableActions);
      Assert.AreEqual(0, dest.VariableActions.Count);
    }

    [TestMethod]
    public async Task Copy_TrimsStringProperties()
    {
      var source = new Trigger
      {
        Pattern = "  trimmed  ",
        EndTimerClearVariables = "  var1,var2  ",
        TextToDisplay = "  display  "
      };
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.AreEqual("trimmed", dest.Pattern);
      Assert.AreEqual("var1,var2", dest.EndTimerClearVariables);
      Assert.AreEqual("display", dest.TextToDisplay);
    }

    [TestMethod]
    public async Task Copy_EnableTimerDerivedFromModel()
    {
      // When copying from TriggerPropertyModel, EnableTimer is derived from TimerType > 0.
      // This test verifies the direct Trigger-to-Trigger path copies it as-is.
      var source = new Trigger { EnableTimer = true };
      var dest = new Trigger();
      await TriggerUtil.Copy(dest, source);

      Assert.IsTrue(dest.EnableTimer);

      source.EnableTimer = false;
      dest = new Trigger();
      await TriggerUtil.Copy(dest, source);
      Assert.IsFalse(dest.EnableTimer);
    }
  }
}

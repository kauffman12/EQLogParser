using System.Text;
using System.Text.Json;
using EQLogParser;

namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// Tests for NagUtil.ConvertTriggers — NAG trigger import functionality.
  /// Covers: basic conversion, conditions, multi-phrase capture, null duration,
  /// action type handling, deduplication, and import result tracking.
  /// </summary>
  [TestClass]
  public class NagUtilTriggerImportTest
  {
    #region Basic Trigger Conversion

    [TestMethod]
    public void ConvertTriggers_SingleTextOverlay_ReturnsNode()
    {
      var json = CreateTriggerJson("Test Trigger", "spellcast:Fireball", actions: new[]
      {
        CreateAction(0, displayText: "Fireball cast!", duration: 5.0)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Test Trigger", nodes[0].Name);
      Assert.IsTrue(nodes[0].TriggerData.UseRegex);
      Assert.AreEqual("Fireball cast!", nodes[0].TriggerData.TextToDisplay);
      Assert.AreEqual(5.0, nodes[0].TriggerData.DurationSeconds);
      // Pattern must be set — this is the critical bug that was previously missed
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("spellcast:Fireball", nodes[0].TriggerData.Pattern);
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_MissingName_Skipped()
    {
      var json = CreateTriggerJson(null, "pattern", actions: new[]
      {
        CreateAction(0, displayText: "text")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual(1, results.Count);
      Assert.AreEqual("Skipped", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_DevOnly_Skipped()
    {
      var json = CreateTriggerJson("Dev Trigger", "pattern", onlyExecuteInDev: true, actions: new[]
      {
        CreateAction(0, displayText: "text")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual("Skipped", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("Dev-only"));
    }

    [TestMethod]
    public void ConvertTriggers_NoCapturePhrases_Skipped()
    {
      var json = CreateTriggerJson("No Phrases", "pattern", capturePhrases: Array.Empty<JsonElement>(), actions: new[]
      {
        CreateAction(0, displayText: "text")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual("Skipped", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_NoSupportedActions_Skipped()
    {
      var json = CreateTriggerJson("No Actions", "pattern", actions: new[]
      {
        CreateAction(5, displayText: "set variable") // Action type 5 is unsupported
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual("Skipped", results[0].Status);
    }

    #endregion

    #region Action Type Handling

    [TestMethod]
    public void ConvertTriggers_ActionType0_TextOverlay_Imported()
    {
      var json = CreateTriggerJson("Text Trigger", "pattern", actions: new[]
      {
        CreateAction(0, displayText: "Hello World", duration: 10.0)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Hello World", nodes[0].TriggerData.TextToDisplay);
      Assert.AreEqual(10.0, nodes[0].TriggerData.DurationSeconds);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType1_Audio_Imported()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions: new[]
      {
        CreateAction(1, audioFileId: "audio-file-123")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // Without files-database.json, audioFileId is used as-is
      Assert.AreEqual("audio-file-123", nodes[0].TriggerData.SoundToPlay);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType2_TTS_Imported()
    {
      var json = CreateTriggerJson("TTS Trigger", "pattern", actions: new[]
      {
        CreateAction(2, displayText: "Spell ready")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Spell ready", nodes[0].TriggerData.TextToSpeak);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType3_Timer_Imported()
    {
      var json = CreateTriggerJson("Timer Trigger", "pattern", actions: new[]
      {
        CreateAction(3, displayText: "Cooldown", duration: 30.0)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(30.0, nodes[0].TriggerData.DurationSeconds);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType4_LoopingTimer_Imported()
    {
      var json = CreateTriggerJson("Loop Timer", "pattern", actions: new[]
      {
        CreateAction(4, displayText: "Channeling", duration: 15.0)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(15.0, nodes[0].TriggerData.DurationSeconds);
      Assert.IsFalse(string.IsNullOrEmpty(nodes[0].TriggerData.Pattern));
      Assert.AreEqual("Imported", results[0].Status);
    }

    [TestMethod]
    public void ConvertTriggers_ActionType5_SetVariable_Dropped()
    {
      var json = CreateTriggerJson("Var Trigger", "pattern", actions: new[]
      {
        CreateAction(5, displayText: "set some var"),
        CreateAction(0, displayText: "text overlay")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("set variable"));
    }

    [TestMethod]
    public void ConvertTriggers_ActionType12_ScreenFlash_Dropped()
    {
      var json = CreateTriggerJson("Flash Trigger", "pattern", actions: new[]
      {
        CreateAction(12),
        CreateAction(0, displayText: "text overlay")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("screen flash"));
    }

    [TestMethod]
    public void ConvertTriggers_ActionType11_Hotkey_Dropped()
    {
      var json = CreateTriggerJson("Hotkey Trigger", "pattern", actions: new[]
      {
        CreateAction(11),
        CreateAction(0, displayText: "text overlay")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("hotkey"));
    }

    #endregion

    #region Null Duration Handling

    [TestMethod]
    public void ConvertTriggers_NullDuration_UsesDefault60()
    {
      var json = CreateTriggerJson("Null Dur Trigger", "pattern", actions: new[]
      {
        CreateAction(3, displayText: "Dynamic Timer", durationNull: true)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.EnableTimer);
      Assert.AreEqual(60.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("indefinite timer duration"));
    }

    [TestMethod]
    public void ConvertTriggers_NullDuration_WithEndEarlyPhrases_Merged()
    {
      // Null-duration timers rely on endEarlyPhrases to terminate early.
      // Trigger-level and action-level phrases should be merged (max 3 slots).
      var actionJson = CreateActionString(3, displayText: "Timer", durationNull: true);
      actionJson = actionJson.Replace("}", ",\"endEarlyPhrases\":[{\"phrase\":\"Spell faded\"}]}");

      var json = CreateTriggerJson("Null Dur EEP", "pattern", endEarlyPhrases: new[]
      {
        CreateEndEarlyPhrase("Channel broken")
      }, actions: new[]
      {
        JsonDocument.Parse(actionJson).RootElement
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual(60.0, nodes[0].TriggerData.DurationSeconds);
      Assert.AreEqual("Channel broken", nodes[0].TriggerData.EndEarlyPattern);
      Assert.AreEqual("Spell faded", nodes[0].TriggerData.EndEarlyPattern2);
    }

    #endregion

    #region Conditions Parsing

    [TestMethod]
    public void ConvertTriggers_ConditionOperator16_EqualityCheck()
    {
      var json = CreateTriggerJson("Zone Trigger", "pattern", conditions: new[]
      {
        CreateCondition("CurrentZone", 16, "Norg")
      }, actions: new[]
      {
        CreateAction(0, displayText: "In Norg")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("{CurrentZone} = \"Norg\"", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator1_Contains()
    {
      var json = CreateTriggerJson("Contains Trigger", "pattern", conditions: new[]
      {
        CreateCondition("SpellBeingCast", 1, "Fireball|Flame Strike")
      }, actions: new[]
      {
        CreateAction(0, displayText: "Fire spell")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("{SpellBeingCast} contains \"Fireball|Flame Strike\"", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_ConditionOperator2_Existence()
    {
      var json = CreateTriggerJson("Exists Trigger", "pattern", conditions: new[]
      {
        CreateCondition("EbItemZone", 2, null)
      }, actions: new[]
      {
        CreateAction(0, displayText: "Item check")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("{EbItemZone}", nodes[0].TriggerData.MatchVariableCondition);
    }

    [TestMethod]
    public void ConvertTriggers_MultipleConditions_JoinedWithAnd()
    {
      var json = CreateTriggerJson("Multi Cond Trigger", "pattern", conditions: new[]
      {
        CreateCondition("CurrentZone", 16, "Norg"),
        CreateCondition("SpellBeingCast", 16, "Fireball")
      }, actions: new[]
      {
        CreateAction(0, displayText: "Specific check")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      var cond = nodes[0].TriggerData.MatchVariableCondition;
      Assert.IsTrue(cond.Contains("{CurrentZone}"));
      Assert.IsTrue(cond.Contains("{SpellBeingCast}"));
      Assert.IsTrue(cond.Contains("&&"));
    }

    [TestMethod]
    public void ConvertTriggers_NullOperatorType_HandledGracefully()
    {
      // NAG data has 4 conditions with null operatorType — must not crash
      var json = "{\"triggers\":[{\"name\":\"Null Op Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"capturePhrases\":[{\"phrase\":\"pattern\",\"useRegEx\":false}],\"conditions\":[{\"conditionType\":1,\"variableName\":\"SomeVar\",\"operatorType\":null,\"variableValue\":\"val\"}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Imported", results[0].Status);
    }

    #endregion

    #region Multi-Phrase Capture

    [TestMethod]
    public void ConvertTriggers_MultiplePhrases_CombinedWithAlternation()
    {
      var json = CreateTriggerJson("Multi Phrase", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("You cast Fireball"),
        CreateCapturePhrase("You cast Flame Strike")
      }, actions: new[]
      {
        CreateAction(0, displayText: "Fire spell")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // Should be combined with alternation: (?:phrase1|phrase2)
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("(?:"));
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("|"));
      Assert.IsTrue(nodes[0].TriggerData.UseRegex);
    }

    [TestMethod]
    public void ConvertTriggers_MultiplePhrasesWithRegex_Preserved()
    {
      var json = CreateTriggerJson("Regex Phrase", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase(@"You cast (?<spellName>\w+)", useRegEx: true)
      }, actions: new[]
      {
        CreateAction(0, displayText: "{spellName} ready!")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.UseRegex);
    }

    #endregion

    #region End Early Phrases

    [TestMethod]
    public void ConvertTriggers_EndEarlyPhrases_AppliedToTrigger()
    {
      var json = CreateTriggerJson("End Early Trigger", "pattern", endEarlyPhrases: new[]
      {
        CreateEndEarlyPhrase("Spell ended"),
        CreateEndEarlyPhrase("Channel broken")
      }, actions: new[]
      {
        CreateAction(3, displayText: "Channeling", duration: 30.0)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
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

      var json = CreateTriggerJson("Merged EEP", "pattern", endEarlyPhrases: new[]
      {
        CreateEndEarlyPhrase("Channel broken")
      }, actions: new[]
      {
        JsonDocument.Parse(actionJson).RootElement
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
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

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // NAG comments are preserved as-is (no "Original:" prefix).
      // The NAG trigger ID is tracked via OriginalId and the metadata dictionary.
      Assert.IsFalse(nodes[0].TriggerData.Comments.Contains("Original:"));
      Assert.IsTrue(nodes[0].TriggerData.Comments.Contains("User's note here"));
    }

    [TestMethod]
    public void ConvertTriggers_DroppedFeatures_ListedInComment()
    {
      var json = CreateTriggerJson("Partial Trigger", "pattern", actions: new[]
      {
        CreateAction(5, displayText: "set var"), // Unsupported
        CreateAction(0, displayText: "text")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.Comments.Contains("EQLP Import Notes:"));
      Assert.IsTrue(nodes[0].TriggerData.Comments.Contains("set variable"));
    }

    #endregion

    #region Template Conversion

    [TestMethod]
    public void ConvertTriggers_NagTemplates_ConvertedToEqlp()
    {
      // NAG uses ${var} for variables and (?<name>...) for regex groups.
      // EQLP display text uses {$name} for variable references (the $ prefix is optional).
      var json = CreateTriggerJson("Template Trigger", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase(@"You cast (?<spellName>\w+)", useRegEx: true)
      }, actions: new[]
      {
        CreateAction(0, displayText: "{spellName} ready!")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // NAG's {groupName} should become EQLP's {$groupName}
      Assert.IsTrue(nodes[0].TriggerData.TextToDisplay.Contains("{$spellName}"));
    }

    [TestMethod]
    public void ConvertTriggers_NagDollarVar_ConvertedToEqlp()
    {
      // NAG ${var} → EQLP {$var} (in display text, not regex phrases)
      var json = CreateTriggerJson("Dollar Var Trigger", "pattern", actions: new[]
      {
        CreateAction(0, displayText: "${caster} casts!")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.TextToDisplay.Contains("{$caster}"));
      Assert.IsFalse(nodes[0].TriggerData.TextToDisplay.Contains("${caster}"));
    }

    #endregion

    #region NAG Variable References in Regex Phrases

    [TestMethod]
    public void ConvertTriggers_TsPlaceholder_PreservedInRegexPhrase()
    {
      // NAG uses {TS} as a duration placeholder in regex phrases.
      // EQLP's CheckOptions() at runtime converts {TS} → (?<TS>(?:\d+[dhms]?:?){1,4}).
      // Must NOT be converted to {$TS} by ConvertTemplates.
      var json = CreateTriggerJson("TS Trigger", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("^${Character} starts a {TS} timer\\.$", useRegEx: true)
      }, actions: new[]
      {
        CreateAction(3, displayText: "Timer started", durationNull: true)
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // {TS} must be preserved as-is (not {$TS})
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("{TS}"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("{$TS}"));
      // ${Character} must be replaced with (.+?)
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("(.+?)"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("${Character}"));
    }

    [TestMethod]
    public void ConvertTriggers_DollarVar_ReplacedWithCaptureGroup()
    {
      // NAG ${varName} in regex phrases → (.+?) since EQLP doesn't support {$var} in patterns
      var json = CreateTriggerJson("DollarVar Trigger", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("^${Character} casts a spell\\.$", useRegEx: true)
      }, actions: new[]
      {
        CreateAction(0, displayText: "Spell cast")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("(.+?)"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("${Character}"));
    }

    [TestMethod]
    public void ConvertTriggers_EqlpHandledVars_PreservedInRegexPhrase()
    {
      // EQLP handles {S}, {N} at runtime via CheckOptions(). Must not be converted.
      var json = CreateTriggerJson("EqlpVars Trigger", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("^You cast {S} for {N} damage\\.$", useRegEx: true)
      }, actions: new[]
      {
        CreateAction(0, displayText: "Cast")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // {S} and {N} should be preserved as-is for EQLP runtime conversion
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("{S}"));
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("{N}"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("{$S}"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("{$N}"));
    }

    [TestMethod]
    public void ConvertTriggers_UnhandledVar_ReplacedWithCaptureGroup()
    {
      // NAG {C} is not handled by EQLP — must be replaced with (.+?) to prevent regex errors
      var json = CreateTriggerJson("UnhandledVar Trigger", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("^A yellow cloud forms above {C}'s head\\.$", useRegEx: true)
      }, actions: new[]
      {
        CreateAction(0, displayText: "Cloud formed")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("(.+?)"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("{C}"));
    }

    [TestMethod]
    public void ConvertTriggers_NonRegexPhraseWithVar_EnablesRegexMode()
    {
      // 49 single-phrase non-regex triggers use {C} — they need regex mode enabled
      var json = CreateTriggerJson("NonRegex Var Trigger", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("A yellow cloud forms above {C}'s head.", useRegEx: false)
      }, actions: new[]
      {
        CreateAction(0, displayText: "Cloud")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // {C} should be replaced with (.+?) even though useRegEx was false
      Assert.IsTrue(nodes[0].TriggerData.Pattern.Contains("(.+?)"));
      Assert.IsFalse(nodes[0].TriggerData.Pattern.Contains("{C}"));
      Assert.IsTrue(nodes[0].TriggerData.UseRegex);
    }

    [TestMethod]
    public void ConvertTriggers_NonRegexPhraseWithoutVars_StayNonRegex()
    {
      // Non-regex phrases without NAG variables should stay non-regex (literal match)
      var json = CreateTriggerJson("Plain NonRegex", "pattern", capturePhrases: new[]
      {
        CreateCapturePhrase("You cast Fireball.", useRegEx: false)
      }, actions: new[]
      {
        CreateAction(0, displayText: "Cast")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
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

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(2, nodes.Count); // Good + Partial
      Assert.AreEqual(3, results.Count);

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
        Assert.IsTrue(html.Contains("/root/sub"));
        Assert.IsTrue(html.Contains("/root"));
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
        Assert.IsTrue(html.Contains("<!DOCTYPE html>"));
        Assert.IsTrue(html.Contains("</html>"));
        // Verify summary stats (display text uses 'Success' instead of 'Imported')
        Assert.IsTrue(html.Contains("Success"));
        Assert.IsTrue(html.Contains("Partial"));
        Assert.IsTrue(html.Contains("Skipped"));
        // Verify trigger names appear
        Assert.IsTrue(html.Contains("Test1"));
        Assert.IsTrue(html.Contains("Has,Comma"));
        // Verify folder paths appear
        Assert.IsTrue(html.Contains("Orphaned Triggers"));
        Assert.IsTrue(html.Contains("Kunark"));
        // Verify missing audio file is listed
        Assert.IsTrue(html.Contains("missing.wav"));
        // Verify badge classes and dark theme CSS
        Assert.IsTrue(html.Contains("badge-imported"));
        Assert.IsTrue(html.Contains("badge-partial"));
        Assert.IsTrue(html.Contains("badge-skipped"));
        Assert.IsTrue(html.Contains("background: #1e1e1e"));
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
        Assert.IsTrue(html.Contains("<!DOCTYPE html>"));
        Assert.IsTrue(html.Contains("</html>"));
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
        Assert.IsFalse(html.Contains("<with>"));
        Assert.IsTrue(html.Contains("&lt;with&gt;"));
        Assert.IsTrue(html.Contains("&amp;"));
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
        Assert.IsTrue(html.Contains("<em>(root)</em>"));
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
        Assert.IsTrue(skippedIdx < partialIdx, "Skipped should come before Partial");
        Assert.IsTrue(partialIdx < importedIdx, "Partial should come before Success/Imported");
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
      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void ConvertTriggers_InvalidJson_ReturnsEmpty()
    {
      var json = "not valid json";
      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void ConvertTriggers_ScoreToPriority_Mapping()
    {
      var json = CreateTriggerJson("Scored Trigger", "pattern", score: 1.0, actions: new[]
      {
        CreateAction(0, displayText: "text")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // Score 1.0 should map to Priority 1
      Assert.AreEqual(1, nodes[0].TriggerData.Priority);
    }

    [TestMethod]
    public void ConvertTriggers_SequentialCapture_Skipped()
    {
      var json = "{\"triggers\":[{\"name\":\"Seq Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"captureMethod\":\"Sequential\",\"capturePhrases\":[{\"phrase\":\"You begin casting\",\"useRegEx\":false},{\"phrase\":\"Spell lands\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual(1, results.Count);
      Assert.AreEqual("Skipped", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("Sequential"));
    }

    [TestMethod]
    public void ConvertTriggers_ClassLevels_MarkedPartial()
    {
      var json = "{\"triggers\":[{\"name\":\"Class Trigger\",\"triggerId\":\"t1\",\"onlyExecuteInDev\":false,\"classLevels\":[{\"class\":\"Cleric\",\"level\":50}],\"capturePhrases\":[{\"phrase\":\"pattern\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}]}";

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual("Partial", results[0].Status);
      Assert.IsTrue(results[0].Reason.Contains("class level filtering"));
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_TextAction_Collected()
    {
      // NAG text overlay actions (type 0) have overlayId in 1860/1863 real cases
      var actionJson = "{\"actionType\":0,\"displayText\":\"Overlaid text\",\"overlayId\":\"ov-123\"}";
      var json = CreateTriggerJson("Overlay Trigger", "pattern", actions: new[]
      {
        JsonDocument.Parse(actionJson).RootElement
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.SelectedOverlays.Contains("ov-123"));
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_TtsAction_Collected()
    {
      // TTS actions (type 2) also support overlayId in NAG data
      var actionJson = "{\"actionType\":2,\"displayText\":\"TTS text\",\"overlayId\":\"ov-456\"}";
      var json = CreateTriggerJson("TTS Overlay Trigger", "pattern", actions: new[]
      {
        JsonDocument.Parse(actionJson).RootElement
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.SelectedOverlays.Contains("ov-456"));
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_AudioAction_Collected()
    {
      // Audio actions (type 1) also support overlayId in NAG data
      var actionJson = "{\"actionType\":1,\"audioFileId\":\"sound-123\",\"overlayId\":\"ov-789\"}";
      var json = CreateTriggerJson("Audio Overlay Trigger", "pattern", actions: new[]
      {
        JsonDocument.Parse(actionJson).RootElement
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.SelectedOverlays.Contains("ov-789"));
    }

    [TestMethod]
    public void ConvertTriggers_OverlayId_ClipboardAction_Collected()
    {
      // Clipboard actions (type 9) also support overlayId in NAG data
      var actionJson = "{\"actionType\":9,\"displayText\":\"Clipboard text\",\"overlayId\":\"ov-clip\"}";
      var json = CreateTriggerJson("Clipboard Overlay Trigger", "pattern", actions: new[]
      {
        JsonDocument.Parse(actionJson).RootElement
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(nodes[0].TriggerData.SelectedOverlays.Contains("ov-clip"));
    }

    #endregion

    #region Metadata Dictionary

    [TestMethod]
    public void ConvertTriggers_ReturnsMetadataDictionary()
    {
      var json = CreateTriggerJson("Meta Trigger", "pattern", score: 0.8, actions: new[]
      {
        CreateAction(0, displayText: "text")
      });

      var (nodes, results, metadata) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual(1, metadata.Count);
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
        + "{\"name\":\"Good\",\"triggerId\":\"t-good\",\"onlyExecuteInDev\":false,\"capturePhrases\":[{\"phrase\":\"p\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}"
        + "{\"name\":\"Skip\",\"triggerId\":\"t-skip\",\"onlyExecuteInDev\":true,\"capturePhrases\":[{\"phrase\":\"p\",\"useRegEx\":false}],\"actions\":[{\"actionType\":0,\"displayText\":\"text\"}]}"
        + "]}";

      var (nodes, results, metadata) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.IsTrue(metadata.ContainsKey("t-good"));
      Assert.IsFalse(metadata.ContainsKey("t-skip"));
    }

    #endregion

    #region Overlay NoTextWrap Parsing

    [TestMethod]
    public void ConvertOverlays_NoTextWrap_ParsedFromNagData()
    {
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"Test Alert\",\"overlayType\":\"Alert\",\"textOverflow\":{\"whiteSpace\":\"nowrap\",\"overflow\":\"hidden\",\"textOverflow\":\"clip\"}}]}";

      var overlays = NagUtil.ConvertOverlays(json);

      Assert.AreEqual(1, overlays.Count);
      Assert.IsTrue(overlays[0].OverlayData.NoTextWrap);
    }

    [TestMethod]
    public void ConvertOverlays_NoTextWrap_DefaultsToFalse()
    {
      var json = "{\"overlays\":[{\"overlayId\":\"ov-1\",\"name\":\"Test Alert\",\"overlayType\":\"Alert\"}]}";

      var overlays = NagUtil.ConvertOverlays(json);

      Assert.AreEqual(1, overlays.Count);
      Assert.IsFalse(overlays[0].OverlayData.NoTextWrap);
    }

    #endregion

    #region Missing Audio Files Tracking

    [TestMethod]
    public void ConvertTriggers_AudioFileNotInSoundsDir_TrackedInMissingAudioFiles()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions: new[]
      {
        CreateAction(1, audioFileId: "audio-file-123")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // Without files-database.json, the raw audioFileId is used as SoundToPlay.
      // Since "audio-file-123" doesn't have a .wav/.mp3 extension and won't exist in data/sounds/,
      // it should be tracked as missing.
      Assert.AreEqual(1, results[0].MissingAudioFiles.Count);
      Assert.AreEqual("audio-file-123", results[0].MissingAudioFiles[0]);
    }

    [TestMethod]
    public void ConvertTriggers_AudioFileWithExtensionNotInSoundsDir_TrackedInMissingAudioFiles()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions: new[]
      {
        CreateAction(1, audioFileId: "test-sound.wav")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // "test-sound.wav" has a valid extension but doesn't exist in data/sounds/
      Assert.AreEqual(1, results[0].MissingAudioFiles.Count);
      Assert.AreEqual("test-sound.wav", results[0].MissingAudioFiles[0]);
    }

    [TestMethod]
    public void ConvertTriggers_NoAudioActions_MissingAudioFilesEmpty()
    {
      var json = CreateTriggerJson("Text Trigger", "pattern", actions: new[]
      {
        CreateAction(0, displayText: "Hello")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual(0, results[0].MissingAudioFiles.Count);
    }

    [TestMethod]
    public void ConvertTriggers_MultipleAudioActions_AllTrackedInMissingAudioFiles()
    {
      var json = CreateTriggerJson("Multi Audio Trigger", "pattern", actions: new[]
      {
        CreateAction(1, audioFileId: "sound-a.wav"),
        CreateAction(1, audioFileId: "sound-b.mp3")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      Assert.AreEqual(2, results[0].MissingAudioFiles.Count);
      Assert.IsTrue(results[0].MissingAudioFiles.Contains("sound-a.wav"));
      Assert.IsTrue(results[0].MissingAudioFiles.Contains("sound-b.mp3"));
    }

    [TestMethod]
    public void ConvertTriggers_SkippedTrigger_MissingAudioFilesStillTracked()
    {
      var json = CreateTriggerJson("Skipped Trigger", "pattern", onlyExecuteInDev: true, actions: new[]
      {
        CreateAction(1, audioFileId: "dev-only-sound.wav")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(0, nodes.Count);
      Assert.AreEqual("Skipped", results[0].Status);
      // Even skipped triggers should track their missing audio files for reporting
      Assert.AreEqual(1, results[0].MissingAudioFiles.Count);
    }

    [TestMethod]
    public void ConvertTriggers_AudioFileIdWithoutExtension_NotTrackedAsMissing()
    {
      // When there's no files-database.json, the raw audioFileId is used as SoundToPlay.
      // If it doesn't have a .wav/.mp3 extension (like "ShortWarningPing"), it won't be
      // recognized by SoundFileRegex at runtime, but we don't track it as missing since
      // we can't verify its existence without the file map.
      var json = CreateTriggerJson("Ding Trigger", "pattern", actions: new[]
      {
        CreateAction(1, audioFileId: "ShortWarningPing")
      });

      var (nodes, results, _) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, nodes.Count);
      // Without files-database.json, the raw ID is used directly
      Assert.AreEqual("ShortWarningPing", nodes[0].TriggerData.SoundToPlay);
      // Not tracked as missing since we can't verify without the file map
      Assert.AreEqual(0, results[0].MissingAudioFiles.Count);
    }

    [TestMethod]
    public void ConvertTriggers_MissingAudioFilesInMetadata()
    {
      var json = CreateTriggerJson("Audio Trigger", "pattern", actions: new[]
      {
        CreateAction(1, audioFileId: "missing-sound.wav")
      });

      var (nodes, results, metadata) = NagUtil.ConvertTriggers(json);

      Assert.AreEqual(1, metadata.Count);
      // Find the trigger ID from results to look up metadata
      var triggerId = results[0].TriggerId;
      Assert.IsTrue(metadata.ContainsKey(triggerId));
      Assert.IsNotNull(metadata[triggerId].MissingAudioFiles);
      Assert.AreEqual(1, metadata[triggerId].MissingAudioFiles.Count);
    }

    #endregion

    #region Helper Methods

    private string CreateTriggerJson(string? name, string pattern, bool onlyExecuteInDev = false, double score = 0.5,
        JsonElement[]? capturePhrases = null, JsonElement[]? conditions = null, JsonElement[]? endEarlyPhrases = null,
        JsonElement[]? actions = null)
    {
      var phrases = capturePhrases ?? new[] { CreateCapturePhrase(pattern) };
      var sb = new StringBuilder();
      sb.Append("{\"triggers\":[{\"name\":\"");
      sb.Append(name ?? "");
      sb.Append("\",\"triggerId\":\"test-123\",\"onlyExecuteInDev\":");
      sb.Append(onlyExecuteInDev.ToString().ToLower());
      if (score != 0.5)
      {
        sb.Append($",\"score\":{score}");
      }

      // Capture phrases
      sb.Append(",\"capturePhrases\":[");
      for (int i = 0; i < phrases.Length; i++)
      {
        if (i > 0) sb.Append(",");
        sb.Append(phrases[i].GetRawText());
      }
      sb.Append("]");

      // Conditions
      if (conditions != null && conditions.Length > 0)
      {
        sb.Append(",\"conditions\":[");
        for (int i = 0; i < conditions.Length; i++)
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
        for (int i = 0; i < endEarlyPhrases.Length; i++)
        {
          if (i > 0) sb.Append(",");
          sb.Append(endEarlyPhrases[i].GetRawText());
        }
        sb.Append("]");
      }

      // Actions
      sb.Append(",\"actions\":[");
      var acts = actions ?? Array.Empty<JsonElement>();
      for (int i = 0; i < acts.Length; i++)
      {
        if (i > 0) sb.Append(",");
        sb.Append(acts[i].GetRawText());
      }
      sb.Append("]}]}");

      return sb.ToString();
    }

    private string CreateActionString(int actionType, string? displayText = null, double? duration = null, bool durationNull = false, string? audioFileId = null)
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

      sb.Append("}");
      return sb.ToString();
    }

    private JsonElement CreateAction(int actionType, string? displayText = null, double? duration = null, bool durationNull = false, string? audioFileId = null)
    {
      var json = CreateActionString(actionType, displayText, duration, durationNull, audioFileId);
      return JsonDocument.Parse(json).RootElement;
    }

    private JsonElement CreateCapturePhrase(string phrase, bool useRegEx = false)
    {
      var json = $"{{\"phrase\":\"{phrase.Replace("\"", "\\\"")}\",\"useRegEx\":{useRegEx.ToString().ToLower()}}}";
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
  }
}

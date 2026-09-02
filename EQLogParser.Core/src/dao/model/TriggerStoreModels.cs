using LiteDB;
using System.Text.Json.Serialization;

namespace EQLogParser
{
  /* The type of variable action to perform. */
  internal enum VariableActionType
  {
    Set,
    Clear
  }

  /* The data type stored by a variable. */
  internal enum VariableDataType
  {
    Value,
    Counter
  }

  /* Represents a single variable action (set or clear) configured on a trigger. */
  internal class VariableAction
  {
    // Stored as int (not enum) to match TimerType/TriggerAgainOption pattern — immune to renames.
    // Left at the default (0): 0=Set / 0=Value, see the Is* helpers below.
    public int ActionType { get; set; } // 0=Set, 1=Clear
    public int DataType { get; set; }   // 0=Value, 1=Counter

    // Convenience helpers so callers don't use magic numbers
    public bool IsSetAction => ActionType == 0;
    public bool IsClearAction => ActionType == 1;
    public bool IsValueType => DataType == 0;
    public bool IsCounterType => DataType == 1;

    public string VariableName { get; set; } = "";

    // For Value: capture group ref like "{s1}", variable ref "{varName}" or "${varName}", or literal text
    public string Value { get; set; } = "";

    // Counter-only fields
    public double Step { get; set; } = 1;
    public double InitialValue { get; set; }

    // TTL field (applies to both Value and Counter); the default 0 means "no expiration"
    public double TimeToLiveSeconds { get; set; }
  }

  internal class Overlay
  {
    // Provenance marker. NAG import stores "nag:{overlayId}" here so a re-migration of the same
    // database updates this overlay in place (TriggerStateDB.Import matches by Source) instead of
    // adding a second copy. User-created and GINA/Quick Share overlays leave it null.
    public string Source { get; set; }
    public string OverlayComments { get; set; }
    public string FontSize { get; set; } = "12pt";
    public string FontWeight { get; set; } = "Normal";
    public int SortBy { get; set; }
    public int HorizontalAlignment { get; set; } = 1;
    public int VerticalAlignment { get; set; } = -1;
    public string FontColor { get; set; } = "#FFFFFFFF";
    public string FontFamily { get; set; } = "Segoe UI";
    public string ActiveColor { get; set; } = "#FF1D397E";
    public string BackgroundColor { get; set; } = "#5F000000";
    public string IdleColor { get; set; } = "#FF8f1515";
    public string ResetColor { get; set; } = "#FF8f1515";
    public string OverlayColor { get; set; } = "#00000000";
    public double IdleTimeoutSeconds { get; set; }
    public long FadeDelay { get; set; } = 10;
    public bool UseStandardTime { get; set; }
    public bool ShowMillis { get; set; }
    public bool IsTimerOverlay { get; set; }
    public bool IsTextOverlay { get; set; }
    public bool IsDefault { get; set; }
    public bool ShowActive { get; set; } = true;
    public bool ShowIdle { get; set; } = true;
    public bool ShowReset { get; set; } = true;
    public bool StreamerMode { get; set; }
    public bool HideDuplicates { get; set; }
    public bool UseTextDropShadow { get; set; } = true;
    public bool TextOverlayWrap { get; set; } = true;
    public int TimerMode { get; set; }
    public long Height { get; set; } = 400;
    public long Width { get; set; } = 300;
    public long Top { get; set; } = 200;
    public long Left { get; set; } = 100;
    public string ClosePattern { get; set; }
    public bool UseCloseRegex { get; set; }
  }

  internal class Trigger
  {
    public bool Private { get; set; }
    public double LastTriggered { get; set; }
    public string AltTimerName { get; set; }
    // When true the timer keeps the name captured when it started. Otherwise a name containing
    // trigger variables is re-resolved on each overlay render so it follows variable changes.
    public bool TimerNameStatic { get; set; }
    public string Comments { get; set; }
    public double RepeatedResetTime { get; set; } = 0.75;
    public double DurationSeconds { get; set; } = 0.2;
    public bool EnableTimer { get; set; }
    public int TimerType { get; set; }
    public string EndEarlyPattern { get; set; }
    public string EndEarlyPattern2 { get; set; }
    public string EndEarlyPattern3 { get; set; }
    public bool EndUseRegex { get; set; }
    public bool EndUseRegex2 { get; set; }
    public bool EndUseRegex3 { get; set; }
    public long EndEarlyRepeatedCount { get; set; }
    public long WorstEvalTime { get; set; } = -1;
    public string Pattern { get; set; }
    public string PreviousPattern { get; set; }
    public string MatchVariableCondition { get; set; }
    public long Priority { get; set; } = 3;
    public int TriggerAgainOption { get; set; }
    public bool UseRegex { get; set; }
    public bool PreviousUseRegex { get; set; }
    public string ActiveColor { get; set; }
    public string IdleColor { get; set; }
    public string ResetColor { get; set; }
    public string FontColor { get; set; }
    public string IconSource { get; set; }
    public List<string> SelectedOverlays { get; set; } = [];
    public double ResetDurationSeconds { get; set; }
    public long WarningSeconds { get; set; }
    public string EndEarlyTextToDisplay { get; set; }
    public string EndTextToDisplay { get; set; }
    public string TextToDisplay { get; set; }
    public string WarningTextToDisplay { get; set; }
    public string EndEarlyTextToSpeak { get; set; }
    public string EndTextToSpeak { get; set; }
    public string TextToSpeak { get; set; }
    public string WarningTextToSpeak { get; set; }
    public string SoundToPlay { get; set; }
    public string EndEarlySoundToPlay { get; set; }
    public string EndSoundToPlay { get; set; }
    public string WarningSoundToPlay { get; set; }
    public string EndTimerClearVariables { get; set; }
    public string ChatWebhook { get; set; }
    public string TextToSendToChat { get; set; }
    public string TextToShare { get; set; }
    public long TimesToLoop { get; set; }
    public double LockoutTime { get; set; }
    public int VoiceRate { get; set; }  // 0 for system setting
    public int Volume { get; set; } = 4; // no increase
    public List<VariableAction> VariableActions { get; set; } = [];
  }

  internal class TriggerNode
  {
    [BsonId]
    public string Id { get; set; }
    public bool IsExpanded { get; set; }
    public string Name { get; set; }
    // Original source ID (e.g. NAG triggerId). Persisted so re-imports can match nodes by
    // source identity — NAG allows duplicate names for distinct triggers, so matching by
    // name alone would collapse them. Null for hand-created and GINA-imported nodes.
    public string OriginalId { get; set; }
    public Trigger TriggerData { get; set; }
    public Overlay OverlayData { get; set; }
    public int Index { get; set; }
    public string Parent { get; set; }
  }
  internal class ExportTriggerNode : TriggerNode
  {
    public List<ExportTriggerNode> Nodes { get; set; } = [];
    [JsonIgnore][BsonIgnore] public bool HasMissingMedia { get; set; }
    // OriginalId is inherited from TriggerNode (persisted) — import flows set it from the
    // NAG triggerId and Import() uses it to match same-named distinct triggers on re-import.
  }
}

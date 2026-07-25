using LiteDB;
using Syncfusion.UI.Xaml.TreeView.Engine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  internal readonly record struct TriggerLogItem(LineData LineData, TriggerWrapper Wrapper, string Type, double Eval);

  internal class TriggerListOptionLabels
  {
    internal static List<string> TimerTypes = new(["No Timer", "Countdown", "Fast Countdown", "Progress", "Looping"]);
  }

  internal class Speak
  {
    public bool IsPrimary { get; init; }
    public TriggerWrapper Wrapper { get; init; }
    public string TtsOrSound { get; init; }
    public bool IsSound { get; init; }
    public string Action { get; init; }
    public Dictionary<string, string> Matches { get; init; }
    public Dictionary<string, string> Previous { get; init; }
    public Dictionary<string, string> Original { get; init; }
    /* Snapshot of _variables at enqueue time so TTL expiration doesn't affect TTS resolution. */
    public Dictionary<string, string> Variables { get; init; } = new();
    public long CounterCount { get; init; }
    public double BeginTime { get; init; }
    public long BeginTicks { get; init; }
  }

  internal class RepeatedData
  {
    public long Count { get; set; }
    public long CountTicks { get; set; }
  }

  internal class TimerData
  {
    public string CharacterId { get; init; }
    public CancellationTokenSource CancelSource { get; set; }
    public CancellationTokenSource WarningSource { get; set; }
    public bool Canceled { get; set; }
    public bool Warned { get; set; }
    public string DisplayName { get; set; }
    public string DisplayNameTemplate { get; set; }
    public ConcurrentDictionary<string, string> Variables { get; set; }
    public long LastVariableResolveTicks { get; set; }
    public double DurationSeconds { get; set; }
    public long BeginTicks { get; set; }
    public long EndTicks { get; set; }
    public long ResetTicks { get; set; }
    public long ResetDurationTicks { get; set; }
    public long DurationTicks { get; set; }
    public ReadOnlyCollection<string> TimerOverlayIds { get; set; }
    public int TriggerAgainOption { get; set; }
    public int TimerType { get; set; }
    public string Key { get; set; }
    public string TriggerId { get; set; }
    public string EndEarlyPattern { get; set; }
    public string EndEarlyPattern2 { get; set; }
    public string EndEarlyPattern3 { get; set; }
    public Regex EndEarlyRegex { get; set; }
    public Regex EndEarlyRegex2 { get; set; }
    public Regex EndEarlyRegex3 { get; set; }
    public List<NumberOptions> EndEarlyRegexNOptions { get; set; }
    public List<NumberOptions> EndEarlyRegex2NOptions { get; set; }
    public List<NumberOptions> EndEarlyRegex3NOptions { get; set; }
    public Dictionary<string, string> OriginalMatches { get; set; }
    public Dictionary<string, string> PreviousMatches { get; set; }
    public long CounterCount { get; set; } = -1;
    public long RepeatedCount { get; set; } = -1;
    public string LogTime { get; set; }
    public string ActiveColor { get; set; }
    public string IdleColor { get; set; }
    public string ResetColor { get; set; }
    public string FontColor { get; set; }
    public LineData RepeatingTimerLineData { get; set; }
    public int TimesToLoopCount { get; set; }
    public BitmapImage TimerIcon { get; set; }
  }

  internal class NumberOptions
  {
    public uint Value { get; set; }
    public string Key { get; set; }
    public string Op { get; set; }
  }

  internal class Overlay
  {
    // TODO: Source is used by local NagUtil.cs — remove if NagUtil is discarded
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
    public int TimerMode { get; set; }
    public long Height { get; set; } = 400;
    public long Width { get; set; } = 300;
    public long Top { get; set; } = 200;
    public long Left { get; set; } = 100;
    public string ClosePattern { get; set; }
    public bool UseCloseRegex { get; set; }
  }

  internal class LegacyOverlay : Overlay
  {
    public string Id { get; set; }
    public string Name { get; set; }
  }

  internal class OverlayWindowData
  {
    public Window TheWindow { get; set; }
    public long RemoveTicks { get; set; } = -1;
    public bool IsCooldown { get; set; }
  }

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
    // Stored as int (not enum) to match TimerType/TriggerAgainOption pattern — immune to renames
    public int ActionType { get; set; } = 0; // 0=Set, 1=Clear
    public int DataType { get; set; } = 0;   // 0=Value, 1=Counter

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

    // TTL field (applies to both Value and Counter)
    public double TimeToLiveSeconds { get; set; } = 0; // 0 = no expiration
  }

  /* ViewModel for VariableAction to support WPF data binding with INotifyPropertyChanged.
   * Used exclusively in the Variables tab UI; syncs to/from VariableAction model for persistence. */
  internal class VariableActionViewModel : INotifyPropertyChanged
  {
    // Pre-computed static arrays for ComboBox binding (no per-access allocation)
    private static readonly string[] s_actionTypeDisplays = ["Set Value", "Clear Value"];
    private static readonly string[] s_dataTypeDisplays = ["Value", "Counter"];

    // Instance properties for binding - return cached display strings
    public string[] ActionTypes => s_actionTypeDisplays;
    public string[] DataTypes => s_dataTypeDisplays;

    // Helper to get enum from selected display string
    public VariableActionType GetActionTypeFromDisplay(string display)
      => display == "Clear Value" ? VariableActionType.Clear : VariableActionType.Set;
    public VariableDataType GetDataTypeFromDisplay(string display)
      => display == "Counter" ? VariableDataType.Counter : VariableDataType.Value;
    public string GetDisplayFromActionType(VariableActionType type)
      => type == VariableActionType.Clear ? "Clear Value" : "Set Value";
    public string GetDisplayFromDataType(VariableDataType type)
      => type == VariableDataType.Counter ? "Counter" : "Value";

    private VariableActionType _actionType = VariableActionType.Set;
    private VariableDataType _dataType = VariableDataType.Value;
    private string _variableName = "";
    private string _value = "";
    private double _initialValue;
    private double _step = 1;
    private double _timeToLiveSeconds;
    private bool _isDirty;

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
      if (name != nameof(IsDirty))
      {
        IsDirty = true;
      }
    }

    public bool IsDirty
    {
      get => _isDirty;
      set
      {
        if (_isDirty != value)
        {
          _isDirty = value;
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        }
      }
    }

    // Computed properties for UI visibility bindings
    public bool IsSetAction => _actionType == VariableActionType.Set;
    public bool IsClearAction => _actionType == VariableActionType.Clear;
    public bool IsValueType => _dataType == VariableDataType.Value;
    public bool IsCounterType => _dataType == VariableDataType.Counter;

    // Cached display strings — updated only when the underlying enum changes
    private string _actionTypeDisplay = "Set Value";
    private string _dataTypeDisplay = "Value";

    public VariableActionType ActionType
    {
      get => _actionType;
      set
      {
        if (_actionType != value)
        {
          _actionType = value;
          _actionTypeDisplay = GetDisplayFromActionType(value);
          OnPropertyChanged();
          OnPropertyChanged(nameof(IsSetAction));
          OnPropertyChanged(nameof(IsClearAction));
          // Also notify the display property for ComboBox binding
          OnPropertyChanged(nameof(ActionTypeDisplay));
        }
      }
    }

    // Property for ComboBox SelectedItem binding (string-based)
    public string ActionTypeDisplay
    {
      get => _actionTypeDisplay;
      set
      {
        var newType = GetActionTypeFromDisplay(value);
        if (_actionType != newType)
        {
          _actionType = newType;
          _actionTypeDisplay = value;
          OnPropertyChanged(nameof(ActionType));
          OnPropertyChanged(nameof(IsSetAction));
          OnPropertyChanged(nameof(IsClearAction));
          OnPropertyChanged();
        }
      }
    }

    public VariableDataType DataType
    {
      get => _dataType;
      set
      {
        if (_dataType != value)
        {
          _dataType = value;
          _dataTypeDisplay = GetDisplayFromDataType(value);
          OnPropertyChanged();
          OnPropertyChanged(nameof(IsValueType));
          OnPropertyChanged(nameof(IsCounterType));
          // Also notify the display property for ComboBox binding
          OnPropertyChanged(nameof(DataTypeDisplay));
        }
      }
    }

    // Property for ComboBox SelectedItem binding (string-based)
    public string DataTypeDisplay
    {
      get => _dataTypeDisplay;
      set
      {
        var newType = GetDataTypeFromDisplay(value);
        if (_dataType != newType)
        {
          _dataType = newType;
          _dataTypeDisplay = value;
          OnPropertyChanged(nameof(DataType));
          OnPropertyChanged(nameof(IsValueType));
          OnPropertyChanged(nameof(IsCounterType));
          OnPropertyChanged();
        }
      }
    }

    public string VariableName
    {
      get => _variableName;
      set
      {
        var cleaned = value == null ? "" : new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (_variableName != cleaned)
        {
          _variableName = cleaned;
          OnPropertyChanged();
        }
      }
    }

    public string Value
    {
      get => _value;
      set
      {
        if (_value != value)
        {
          _value = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(IsValueEmpty));
        }
      }
    }

    /* Returns true if Value is null, empty, or whitespace. Used for placeholder visibility. */
    public bool IsValueEmpty => string.IsNullOrWhiteSpace(_value);

    public double InitialValue
    {
      get => _initialValue;
      set
      {
        if (_initialValue != value)
        {
          _initialValue = value;
          OnPropertyChanged();
        }
      }
    }

    public double Step
    {
      get => _step;
      set
      {
        if (_step != value)
        {
          _step = value;
          OnPropertyChanged();
        }
      }
    }

    public double TimeToLiveSeconds
    {
      get => _timeToLiveSeconds;
      set
      {
        if (_timeToLiveSeconds != value)
        {
          _timeToLiveSeconds = value;
          OnPropertyChanged();
        }
      }
    }

    /* Syncs this ViewModel's current values to the target model object.
     * Trims VariableName and Value once at save time to avoid interfering with editing. */
    public void SyncToModel(VariableAction model)
    {
      model.ActionType = (int)ActionType;
      model.DataType = (int)DataType;
      model.VariableName = VariableName?.Trim();
      model.Value = Value?.Trim();
      model.InitialValue = InitialValue;
      model.Step = Step;
      model.TimeToLiveSeconds = TimeToLiveSeconds;
      // Reset dirty flag after successful sync
      IsDirty = false;
    }

    /* Creates a ViewModel instance from a model object, firing PropertyChanged events. */
    public static VariableActionViewModel FromModel(VariableAction model)
      => new(model, suppressNotifications: false);

    /* Initializes a ViewModel from a model object. When suppressNotifications is true,
     * no PropertyChanged events are fired — use this during initialization. */
    private VariableActionViewModel(VariableAction model, bool suppressNotifications)
    {
      _actionType = (VariableActionType)model.ActionType;
      _dataType = (VariableDataType)model.DataType;
      _variableName = model.VariableName ?? "";
      _value = model.Value ?? "";
      _initialValue = model.InitialValue;
      _step = model.Step;
      _timeToLiveSeconds = model.TimeToLiveSeconds;
      _isDirty = false;
      _actionTypeDisplay = GetDisplayFromActionType(_actionType);
      _dataTypeDisplay = GetDisplayFromDataType(_dataType);

      if (!suppressNotifications)
      {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionType)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DataType)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VariableName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Step)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeToLiveSeconds)));
      }
    }

    /* Initializes a ViewModel with default values. When suppressNotifications is true,
     * no PropertyChanged events are fired — use this during initialization. */
    private VariableActionViewModel(string variableName, bool suppressNotifications)
    {
      _actionType = VariableActionType.Set;
      _dataType = VariableDataType.Value;
      _variableName = variableName ?? "";
      _value = "";
      _initialValue = 0;
      _step = 1;
      _timeToLiveSeconds = 0;
      _isDirty = false;
      _actionTypeDisplay = "Set Value";
      _dataTypeDisplay = "Value";

      if (!suppressNotifications)
      {
        OnPropertyChanged();
      }
    }

    /* Creates a ViewModel instance from a model object without firing PropertyChanged events.
     * Use this when loading data to avoid triggering UI updates during initialization. */
    internal static VariableActionViewModel FromModelSilent(VariableAction model)
      => new(model, suppressNotifications: true);

    /* Creates a fresh ViewModel with default values without firing PropertyChanged events.
     * Use this when adding new variable action cards during initialization. */
    internal static VariableActionViewModel CreateSilent(string variableName = "gVariable1")
      => new(variableName, suppressNotifications: true);
  }

  internal class Trigger
  {
    public bool Private { get; set; }
    public double LastTriggered { get; set; }
    public string AltTimerName { get; set; }
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

  internal class TimerOverlayPropertyModel : Overlay
  {
    public TimeSpan IdleTimeoutTimeSpan { get; set; }
    public SolidColorBrush FontBrush { get; set; }
    public SolidColorBrush ActiveBrush { get; set; }
    public SolidColorBrush IdleBrush { get; set; }
    public SolidColorBrush ResetBrush { get; set; }
    public SolidColorBrush BackgroundBrush { get; set; }
    public SolidColorBrush OverlayBrush { get; set; }
    // preview referenced dynamically
    public string TimerBarPreview { get; set; }
    public TriggerNode Node { get; set; }
    // expose node.Id for binding
    public string NodeId => Node?.Id;
  }

  internal class TextOverlayPropertyModel : Overlay
  {
    public SolidColorBrush FontBrush { get; set; }
    public SolidColorBrush OverlayBrush { get; set; }
    public TriggerNode Node { get; set; }
    // expose node.Id for binding
    public string NodeId => Node?.Id;
  }

  internal class TriggerPropertyModel : Trigger
  {
    public SolidColorBrush TriggerActiveBrush { get; set; }
    public SolidColorBrush TriggerIdleBrush { get; set; }
    public SolidColorBrush TriggerResetBrush { get; set; }
    public SolidColorBrush TriggerFontBrush { get; set; }
    public ObservableCollection<ComboBoxItemDetails> SelectedTextOverlays { get; set; }
    public ObservableCollection<ComboBoxItemDetails> SelectedTimerOverlays { get; set; }
    public TimeSpan DurationTimeSpan { get; set; }
    public TimeSpan ResetDurationTimeSpan { get; set; }
    public string SoundOrText { get; set; }
    public string EndEarlySoundOrText { get; set; }
    public string EndSoundOrText { get; set; }
    public string WarningSoundOrText { get; set; }
    public TriggerNode Node { get; set; }
    public DependencyObject DataContext { get; set; }
  }

  internal class TriggerState
  {
    [BsonId]
    public string Id { get; set; }
    public Dictionary<string, bool?> Enabled { get; set; } = [];
  }

  internal class TriggerNode
  {
    [BsonId]
    public string Id { get; set; }
    public bool IsExpanded { get; set; }
    public string Name { get; set; }
    public Trigger TriggerData { get; set; }
    public Overlay OverlayData { get; set; }
    public int Index { get; set; }
    public string Parent { get; set; }
  }

  internal class TriggerCharacter
  {
    public string Id { get; set; }
    public string Name { get; set; }
    public string FilePath { get; set; }
    public bool IsEnabled { get; set; }
    public string Voice { get; set; }
    public int VoiceRate { get; set; }
    public int CustomVolume { get; set; } = -1;
    public string ActiveColor { get; set; }
    public string IdleColor { get; set; }
    public string ResetColor { get; set; }
    public string FontColor { get; set; }
    [BsonIgnore] public bool? IsWaiting { get; set; } = true;
  }

  internal class TriggerConfig
  {
    [BsonId]
    public string Id { get; set; }
    public bool IsAdvanced { get; set; }
    public List<TriggerCharacter> Characters { get; set; } = [];
    public bool IsEnabled { get; set; }
    public string Voice { get; set; }
    public int VoiceRate { get; set; }
  }

  internal class ExportTriggerNode : TriggerNode
  {
    public List<ExportTriggerNode> Nodes { get; set; } = [];
    [JsonIgnore][BsonIgnore] public bool HasMissingMedia { get; set; }
  }

  internal class LegacyTriggerNode
  {
    public bool? IsEnabled { get; set; } = false;
    public bool IsExpanded { get; set; }
    public string Name { get; set; }
    public List<LegacyTriggerNode> Nodes { get; set; } = [];
    public Trigger TriggerData { get; set; }
    public LegacyOverlay OverlayData { get; set; }
  }

  internal class TriggerTreeViewNode : TreeViewNode
  {
    public TriggerNode SerializedData { get; set; }
    public bool IsTrigger() => SerializedData?.TriggerData != null;
    public bool IsOverlay() => SerializedData?.OverlayData != null;
    public bool IsDir() => !IsOverlay() && !IsTrigger();
    public bool IsRecentlyMerged { get; set; }
    public bool HasMissingMedia { get; set; }
  }

  internal class TriggerLogEntry
  {
    public double BeginTime { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public long Priority { get; set; }
    public double Eval { get; set; }
    public double LogTime { get; set; }
    public string Line { get; set; }
    public string NodeId { get; set; }
    public string CharacterId { get; set; }
  }

  internal class TriggerWrapper
  {
    public string Id { get; init; }
    public string Name { get; init; }
    public string ModifiedEndEarlyPattern { get; init; }
    public string ModifiedEndEarlyPattern2 { get; init; }
    public string ModifiedEndEarlyPattern3 { get; init; }
    public string ModifiedPattern { get; init; }
    public string ModifiedSpeak { get; init; }
    public string ModifiedEndSpeak { get; init; }
    public string ModifiedEndEarlySpeak { get; init; }
    public string ModifiedWarningSpeak { get; init; }
    public string ModifiedDisplay { get; init; }
    public string ModifiedShare { get; init; }
    public string ModifiedSendToChat { get; init; }
    public string ModifiedEndDisplay { get; init; }
    public string ModifiedEndEarlyDisplay { get; init; }
    public string ModifiedWarningDisplay { get; init; }
    public string ModifiedTimerName { get; init; }
    public bool HasCounterTimer { get; init; }
    public bool HasCounterText { get; init; }
    public bool HasCounterSpeak { get; init; }
    public bool HasRepeatedTimer { get; init; }
    public bool HasRepeatedText { get; init; }
    public bool HasRepeatedSpeak { get; init; }
    public bool HasLogTimeTimer { get; init; }
    public bool HasLogTimeText { get; init; }
    public bool HasLogTimeSpeak { get; init; }
    public bool HasLogTimeSendToChat { get; init; }
    public BitmapImage TimerIcon { get; init; }
    public Trigger TriggerData { get; init; }
    // only the main thread modifies these values
    public string ModifiedPreviousPattern { get; set; }
    public Regex Regex { get; set; }
    public Regex PreviousRegex { get; set; }
    public List<NumberOptions> RegexNOptions { get; set; }
    public List<NumberOptions> PreviousRegexNOptions { get; set; }
    public bool IsDisabled { get; set; }
    public string ContainsText { get; set; }
    public string PreviousContainsText { get; set; }
    public string StartText { get; set; }
    public string PreviousStartText { get; set; }
    public long LockedOutTicks { get; set; }
    public ConditionNode ConditionAst { get; set; }
  }
}

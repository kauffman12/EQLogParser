using LiteDB;
using Syncfusion.UI.Xaml.TreeView.Engine;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public string ChatWebhook { get; set; }
    public string TextToSendToChat { get; set; }
    public string TextToShare { get; set; }
    public long TimesToLoop { get; set; }
    public double LockoutTime { get; set; }
    public int VoiceRate { get; set; }  // 0 for system setting
    public int Volume { get; set; } = 4; // no increase
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
  }
}

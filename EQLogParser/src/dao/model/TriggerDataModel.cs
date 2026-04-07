using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  internal readonly record struct TriggerLogItem(LineData LineData, TriggerWrapper Wrapper, string Type, double Eval);

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

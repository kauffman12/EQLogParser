using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerProcessor : IDisposable
  {
    public readonly ObservableCollection<AlertEntry> AlertLog = new ObservableCollection<AlertEntry>();
    public readonly string CurrentCharacterId;
    public readonly string CurrentCharacterName;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private readonly object CollectionLock = new object();
    private readonly object LockObject = new object();
    private readonly object VoiceLock = new object();
    private readonly List<TimerData> ActiveTimers = new List<TimerData>();
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> RepeatedTextTimes = new Dictionary<string, Dictionary<string, RepeatedData>>();
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> RepeatedTimerTimes = new Dictionary<string, Dictionary<string, RepeatedData>>();
    private readonly ActionBlock<Tuple<string, double, bool>> Process;
    private readonly ActionBlock<Tuple<LinkedListNode<TriggerWrapper>, LineData>> ProcessLowPri;
    private readonly ActionBlock<Speak> ProcessSpeech;
    private readonly Action<string, Trigger> AddTextEvent;
    private readonly Action<Trigger, List<TimerData>> AddTimerEvent;
    private LinkedList<TriggerWrapper> ActiveTriggers;
    private SpeechSynthesizer Synth = null;
    private SoundPlayer SoundPlayer = null;
    private TriggerWrapper PreviousSpoken = null;

    internal TriggerProcessor(string id, string name, Action<string, Trigger> addTextEvent,
      Action<Trigger, List<TimerData>> addTimerEvent)
    {
      CurrentCharacterId = id;
      CurrentCharacterName = name;
      AddTextEvent = addTextEvent;
      AddTimerEvent = addTimerEvent;
      BindingOperations.EnableCollectionSynchronization(AlertLog, CollectionLock);

      Process = new ActionBlock<Tuple<string, double, bool>>(data => DoProcess(data.Item1, data.Item2));
      ProcessLowPri = new ActionBlock<Tuple<LinkedListNode<TriggerWrapper>, LineData>>(data => HandleTrigger(data.Item1, data.Item2));
      ProcessSpeech = new ActionBlock<Speak>(date => HandleSpeech(date));

      ActiveTriggers = GetActiveTriggers();
      Synth = TriggerUtil.GetSpeechSynthesizer();
      SetVoice(TriggerUtil.GetSelectedVoice());
      SetVoiceRate(TriggerUtil.GetVoiceRate());
      SoundPlayer = new SoundPlayer();
    }

    internal void LinkTo(ISourceBlock<Tuple<string, double, bool>> source)
    {
      source.LinkTo(Process, new DataflowLinkOptions { PropagateCompletion = false });
    }

    internal List<TimerData> GetActiveTimers()
    {
      lock (ActiveTimers) return ActiveTimers.ToList();
    }

    internal void SetVoice(string voice)
    {
      lock (VoiceLock)
      {
        if (Synth != null && !string.IsNullOrEmpty(voice) && voice.Length > 0 && Synth.Voice.Name != voice)
        {
          Synth.SelectVoice(voice);
        }
      }
    }

    internal void SetVoiceRate(int rate)
    {
      lock (VoiceLock)
      {
        if (Synth != null)
        {
          Synth.Rate = rate;
        }
      }
    }

    internal void UpdateActiveTriggers()
    {
      lock (LockObject)
      {
        ActiveTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
        ActiveTriggers = GetActiveTriggers();
      }
    }

    private string ModLine(string text, string line) => string.IsNullOrEmpty(text) ? null : text.Replace("{l}", line, StringComparison.OrdinalIgnoreCase);

    private string ModPlayer(string text)
    {
      return string.IsNullOrEmpty(text) ? null : text.Replace("{c}", ConfigUtil.PlayerName, StringComparison.OrdinalIgnoreCase);
    }

    private void DoProcess(string line, double dateTime)
    {
      var lineData = new LineData { Action = line.Substring(27), BeginTime = dateTime };
      LinkedListNode<TriggerWrapper> node = null;

      lock (LockObject)
      {
        node = ActiveTriggers.First;
      }

      while (node != null)
      {
        // save since the nodes may get reordered
        var nextNode = node.Next;

        // if within a month assume handle it right away (2592000000 millis is 30 days)
        if ((new TimeSpan(DateTime.Now.Ticks).TotalMilliseconds - node.Value.TriggerData.LastTriggered) <= 2592000000)
        {
          HandleTrigger(node, lineData);
        }
        else
        {
          ProcessLowPri.Post(Tuple.Create(node, lineData));
        }

        node = nextNode;
      }
    }

    private void HandleTrigger(LinkedListNode<TriggerWrapper> node, LineData lineData)
    {
      var time = long.MinValue;
      var start = DateTime.Now.Ticks;
      var action = lineData.Action;
      MatchCollection matches = null;
      var found = false;

      lock (node.Value)
      {
        var wrapper = node.Value;
        double dynamicDuration = -1;
        if (wrapper.Regex != null)
        {
          matches = wrapper.Regex.Matches(action);
          found = matches != null && matches.Count > 0 && TriggerUtil.CheckOptions(wrapper.RegexNOptions, matches, out dynamicDuration);
          if (dynamicDuration != -1 && wrapper.TriggerData.TimerType == 1)
          {
            wrapper.ModifiedDurationSeconds = dynamicDuration;
          }
        }
        else if (!string.IsNullOrEmpty(wrapper.ModifiedPattern))
        {
          found = action.Contains(wrapper.ModifiedPattern, StringComparison.OrdinalIgnoreCase);
        }

        if (found)
        {
          var beginTicks = DateTime.Now.Ticks;
          node.Value.TriggerData.LastTriggered = new TimeSpan(beginTicks).TotalMilliseconds;

          time = (beginTicks - start) / 10;
          wrapper.TriggerData.WorstEvalTime = Math.Max(time, wrapper.TriggerData.WorstEvalTime);

          if (ProcessMatchesText(wrapper.ModifiedTimerName, matches) is string displayName)
          {
            displayName = ModLine(displayName, lineData.Action);
            if (wrapper.HasRepeatedTimer)
            {
              UpdateRepeatedTimes(RepeatedTimerTimes, wrapper, displayName, beginTicks);
            }

            if (wrapper.TriggerData.TimerType > 0 && wrapper.TriggerData.DurationSeconds > 0)
            {
              StartTimer(wrapper, displayName, beginTicks, lineData, matches);
            }
          }

          var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out var isSound);
          if (!string.IsNullOrEmpty(speak))
          {
            ProcessSpeech.Post(new Speak
            {
              Wrapper = wrapper,
              TTSOrSound = speak,
              IsSound = isSound,
              Matches = matches,
              Action = lineData.Action
            });
          }

          if (ProcessDisplayText(wrapper.ModifiedDisplay, lineData.Action, matches, null) is string updatedDisplayText)
          {
            if (wrapper.HasRepeatedText)
            {
              var currentCount = UpdateRepeatedTimes(RepeatedTextTimes, wrapper, updatedDisplayText, beginTicks);
              updatedDisplayText = updatedDisplayText.Replace("{repeated}", currentCount.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            AddTextEvent(updatedDisplayText, wrapper.TriggerData);
          }

          AddEntry(lineData, wrapper, "Trigger", time);
        }
        else
        {
          wrapper.TimerList.ToList().ForEach(timerData =>
          {
            MatchCollection earlyMatches;
            var endEarly = CheckEndEarly(timerData.EndEarlyRegex, timerData.EndEarlyRegexNOptions, timerData.EndEarlyPattern, action, out earlyMatches);

            // try 2nd
            if (!endEarly)
            {
              endEarly = CheckEndEarly(timerData.EndEarlyRegex2, timerData.EndEarlyRegex2NOptions, timerData.EndEarlyPattern2, action, out earlyMatches);
            }

            if (endEarly)
            {
              var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out var isSound);
              speak = string.IsNullOrEmpty(speak) ? TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound) : speak;
              var displayText = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

              ProcessSpeech.Post(new Speak
              {
                Wrapper = wrapper,
                TTSOrSound = speak,
                IsSound = isSound,
                Matches = earlyMatches,
                OriginalMatches = timerData.OriginalMatches,
                Action = lineData.Action
              });

              if (ProcessDisplayText(displayText, lineData.Action, earlyMatches, timerData.OriginalMatches) is string updatedDisplayText)
              {
                AddTextEvent(updatedDisplayText, wrapper.TriggerData);
              }

              AddEntry(lineData, wrapper, "Timer End Early");
              CleanupTimer(wrapper, timerData);
            }
          });
        }
      }
    }

    private void StartTimer(TriggerWrapper wrapper, string displayName, long beginTicks, LineData lineData, MatchCollection matches)
    {
      var trigger = wrapper.TriggerData;

      // Restart Timer Option so clear out everything
      if (trigger.TriggerAgainOption == 1)
      {
        CleanupWrapper(wrapper);
      }
      else if (trigger.TriggerAgainOption == 2)
      {
        if (wrapper.TimerList.ToList().FirstOrDefault(timerData => displayName.Equals(timerData?.DisplayName, StringComparison.OrdinalIgnoreCase))
          is TimerData timerData)
        {
          CleanupTimer(wrapper, timerData);
        }
      }

      // Start a New independent Timer as long as one is not already running when Option 3 is selected
      // Option 3 is to Do Nothing when a 2nd timer is triggered so you onlu have the original timer running
      if (!(trigger.TriggerAgainOption == 3 && wrapper.TimerList.Count > 0))
      {
        TimerData newTimerData = null;
        if (trigger.WarningSeconds > 0 && wrapper.ModifiedDurationSeconds - trigger.WarningSeconds is double diff && diff > 0)
        {
          newTimerData = new TimerData { DisplayName = displayName, WarningSource = new CancellationTokenSource() };

          Task.Delay((int)diff * 1000).ContinueWith(task =>
          {
            var proceed = false;
            lock (wrapper)
            {
              if (newTimerData.WarningSource != null)
              {
                proceed = !newTimerData.WarningSource.Token.IsCancellationRequested;
                newTimerData.WarningSource.Dispose();
                newTimerData.WarningSource = null;
              }

              if (proceed)
              {
                var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out var isSound);
                ProcessSpeech.Post(new Speak
                {
                  Wrapper = wrapper,
                  TTSOrSound = speak,
                  IsSound = isSound,
                  Matches = matches,
                  Action = lineData.Action
                });

                if (ProcessDisplayText(wrapper.ModifiedWarningDisplay, lineData.Action, matches, null) is string updatedDisplayText)
                {
                  AddTextEvent(updatedDisplayText, trigger);
                }

                AddEntry(lineData, wrapper, "Timer Warning");
              }
            }
          }, newTimerData.WarningSource.Token);
        }

        if (newTimerData == null)
        {
          newTimerData = new TimerData { DisplayName = displayName };
        }

        if (wrapper.HasRepeatedTimer)
        {
          newTimerData.RepeatedCount = GetRepeatedCount(RepeatedTimerTimes, wrapper, displayName);
        }

        newTimerData.EndTicks = beginTicks + (long)(TimeSpan.TicksPerMillisecond * wrapper.ModifiedDurationSeconds * 1000);
        newTimerData.DurationTicks = newTimerData.EndTicks - beginTicks;
        newTimerData.ResetTicks = trigger.ResetDurationSeconds > 0 ?
          beginTicks + (long)(TimeSpan.TicksPerSecond * trigger.ResetDurationSeconds) : 0;
        newTimerData.ResetDurationTicks = newTimerData.ResetTicks - beginTicks;
        newTimerData.SelectedOverlays = trigger.SelectedOverlays.ToList();
        newTimerData.TriggerAgainOption = trigger.TriggerAgainOption;
        newTimerData.TimerType = trigger.TimerType;
        newTimerData.OriginalMatches = matches;
        newTimerData.ActiveColor = trigger.ActiveColor;
        newTimerData.FontColor = trigger.FontColor;
        newTimerData.Key = wrapper.Name + "-" + trigger.Pattern;
        newTimerData.CancelSource = new CancellationTokenSource();

        if (!string.IsNullOrEmpty(trigger.EndEarlyPattern))
        {
          var endEarlyPattern = ProcessMatchesText(trigger.EndEarlyPattern, matches);
          endEarlyPattern = UpdatePattern(trigger.EndUseRegex, ConfigUtil.PlayerName, endEarlyPattern, out var numberOptions2);

          if (trigger.EndUseRegex)
          {
            newTimerData.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase);
            newTimerData.EndEarlyRegexNOptions = numberOptions2;
          }
          else
          {
            newTimerData.EndEarlyPattern = endEarlyPattern;
          }
        }

        if (!string.IsNullOrEmpty(trigger.EndEarlyPattern2))
        {
          var endEarlyPattern2 = ProcessMatchesText(trigger.EndEarlyPattern2, matches);
          endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, ConfigUtil.PlayerName, endEarlyPattern2, out var numberOptions3);

          if (trigger.EndUseRegex2)
          {
            newTimerData.EndEarlyRegex2 = new Regex(endEarlyPattern2, RegexOptions.IgnoreCase);
            newTimerData.EndEarlyRegex2NOptions = numberOptions3;
          }
          else
          {
            newTimerData.EndEarlyPattern2 = endEarlyPattern2;
          }
        }

        wrapper.TimerList.Add(newTimerData);
        var needEvent = wrapper.TimerList.Count == 1;

        Task.Delay((int)(wrapper.ModifiedDurationSeconds * 1000)).ContinueWith(task =>
        {
          var proceed = false;
          lock (wrapper)
          {
            if (newTimerData.CancelSource != null)
            {
              proceed = !newTimerData.CancelSource.Token.IsCancellationRequested;
            }

            if (proceed)
            {
              var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.EndSoundToPlay, wrapper.ModifiedEndSpeak, out var isSound);

              ProcessSpeech.Post(new Speak
              {
                Wrapper = wrapper,
                TTSOrSound = speak,
                IsSound = isSound,
                Matches = matches,
                OriginalMatches = newTimerData.OriginalMatches,
                Action = lineData.Action
              });

              if (ProcessDisplayText(wrapper.ModifiedEndDisplay, lineData.Action, matches, newTimerData.OriginalMatches) is string updatedDisplayText)
              {
                AddTextEvent(updatedDisplayText, trigger);
              }

              AddEntry(lineData, wrapper, "Timer End");
              CleanupTimer(wrapper, newTimerData);
            }
          }
        }, newTimerData.CancelSource.Token);

        lock (ActiveTimers)
        {
          ActiveTimers.Add(newTimerData);
        }

        if (needEvent)
        {
          AddTimerEvent(trigger, GetActiveTimers());
        }
      }
    }

    private void HandleSpeech(Speak speak)
    {
      if (!string.IsNullOrEmpty(speak.TTSOrSound))
      {
        var cancel = speak.Wrapper.TriggerData.Priority < PreviousSpoken?.TriggerData?.Priority;

        lock (VoiceLock)
        {
          if (cancel && Synth.State == SynthesizerState.Speaking)
          {
            Synth.SpeakAsyncCancelAll();
          }
        }

        if (speak.IsSound)
        {
          if (SoundPlayer != null)
          {
            try
            {
              if (cancel)
              {
                SoundPlayer.Stop();
              }

              var theFile = @"data\sounds\" + speak.TTSOrSound;
              if (SoundPlayer.SoundLocation != theFile && File.Exists(theFile))
              {
                SoundPlayer.SoundLocation = theFile;
              }

              if (!string.IsNullOrEmpty(SoundPlayer.SoundLocation))
              {
                SoundPlayer.Play();
              }
            }
            catch (Exception e)
            {
              LOG.Debug(e);
            }
          }
        }
        else
        {
          var tts = ProcessMatchesText(speak.TTSOrSound, speak.OriginalMatches);
          tts = ProcessMatchesText(tts, speak.Matches);
          tts = ModLine(tts, speak.Action);

          lock (VoiceLock)
          {
            Synth?.SpeakAsync(tts);
          }
        }
      }

      PreviousSpoken = speak.Wrapper;
    }

    private LinkedList<TriggerWrapper> GetActiveTriggers()
    {
      var activeTriggers = new LinkedList<TriggerWrapper>();
      var enabledTriggers = TriggerStateManager.Instance.GetEnabledTriggers(CurrentCharacterId);

      var playerName = ConfigUtil.PlayerName;
      foreach (var enabled in enabledTriggers.OrderByDescending(enabled => enabled.Trigger.LastTriggered))
      {
        var trigger = enabled.Trigger;
        if (trigger.Pattern is string pattern && !string.IsNullOrEmpty(pattern))
        {
          try
          {
            var wrapper = new TriggerWrapper
            {
              Id = enabled.Id,
              Name = enabled.Name,
              TriggerData = trigger,
              ModifiedSpeak = ModPlayer(trigger.TextToSpeak),
              ModifiedWarningSpeak = ModPlayer(trigger.WarningTextToSpeak),
              ModifiedEndSpeak = ModPlayer(trigger.EndTextToSpeak),
              ModifiedEndEarlySpeak = ModPlayer(trigger.EndEarlyTextToSpeak),
              ModifiedDisplay = ModPlayer(trigger.TextToDisplay),
              ModifiedWarningDisplay = ModPlayer(trigger.WarningTextToDisplay),
              ModifiedEndDisplay = ModPlayer(trigger.EndTextToDisplay),
              ModifiedEndEarlyDisplay = ModPlayer(trigger.EndEarlyTextToDisplay),
              ModifiedTimerName = ModPlayer(string.IsNullOrEmpty(trigger.AltTimerName) ? enabled.Name : trigger.AltTimerName),
              ModifiedDurationSeconds = trigger.DurationSeconds
            };

            wrapper.ModifiedTimerName = string.IsNullOrEmpty(wrapper.ModifiedTimerName) ? "" : wrapper.ModifiedTimerName;
            wrapper.HasRepeatedText = !string.IsNullOrEmpty(wrapper.ModifiedDisplay) &&
              wrapper.ModifiedDisplay.Contains("{repeated}", StringComparison.OrdinalIgnoreCase);
            wrapper.HasRepeatedTimer = wrapper.ModifiedTimerName.Contains("{repeated}", StringComparison.OrdinalIgnoreCase);
            pattern = UpdatePattern(trigger.UseRegex, playerName, pattern, out var numberOptions);
            pattern = UpdateTimePattern(trigger.UseRegex, pattern);

            // temp
            if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.TimerType == 0)
            {
              wrapper.TriggerData.TimerType = 1;
            }

            if (trigger.UseRegex)
            {
              wrapper.Regex = new Regex(pattern, RegexOptions.IgnoreCase);
              wrapper.RegexNOptions = numberOptions;
            }
            else
            {
              wrapper.ModifiedPattern = pattern;
            }

            activeTriggers.AddLast(new LinkedListNode<TriggerWrapper>(wrapper));
          }
          catch (Exception ex)
          {
            LOG.Debug("Bad Trigger?", ex);
          }
        }
      }

      return activeTriggers;
    }

    private bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out MatchCollection earlyMatches)
    {
      earlyMatches = null;
      var endEarly = false;

      if (endEarlyRegex != null)
      {
        earlyMatches = endEarlyRegex.Matches(action);
        if (earlyMatches != null && earlyMatches.Count > 0 && TriggerUtil.CheckOptions(options, earlyMatches, out _))
        {
          endEarly = true;
        }
      }
      else if (!string.IsNullOrEmpty(endEarlyPattern))
      {
        if (action.Contains(endEarlyPattern, StringComparison.OrdinalIgnoreCase))
        {
          endEarly = true;
        }
      }

      return endEarly;
    }

    private string ProcessDisplayText(string text, string line, MatchCollection matches, MatchCollection originalMatches)
    {
      if (!string.IsNullOrEmpty(text))
      {
        text = ProcessMatchesText(text, originalMatches);
        text = ProcessMatchesText(text, matches);
        text = ModLine(text, line);
        return text;
      }
      return null;
    }

    private string ProcessMatchesText(string text, MatchCollection matches)
    {
      if (matches != null && !string.IsNullOrEmpty(text))
      {
        foreach (Match match in matches)
        {
          for (var i = 1; i < match.Groups.Count; i++)
          {
            if (!string.IsNullOrEmpty(match.Groups[i].Name))
            {
              text = text.Replace("${" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
              text = text.Replace("{" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
            }
          }
        }
      }

      return text;
    }

    private long GetRepeatedCount(Dictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper, string displayValue)
    {
      if (!string.IsNullOrEmpty(wrapper.Id) && times.TryGetValue(wrapper.Id, out var displayTimes))
      {
        if (displayTimes.TryGetValue(displayValue, out var repeatedData))
        {
          return repeatedData.Count;
        }
      }
      return -1;
    }

    private long UpdateRepeatedTimes(Dictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper,
      string displayValue, long beginTicks)
    {
      long currentCount = -1;

      if (!string.IsNullOrEmpty(wrapper.Id) && wrapper.TriggerData?.RepeatedResetTime >= 0)
      {
        if (times.TryGetValue(wrapper.Id, out var displayTimes))
        {
          if (displayTimes.TryGetValue(displayValue, out var repeatedData))
          {
            var diff = (beginTicks - repeatedData.CountTicks) / TimeSpan.TicksPerSecond;
            if (diff > wrapper.TriggerData.RepeatedResetTime)
            {
              repeatedData.Count = 1;
              repeatedData.CountTicks = beginTicks;
            }
            else
            {
              repeatedData.Count++;
            }

            currentCount = repeatedData.Count;
          }
          else
          {
            displayTimes[displayValue] = new RepeatedData { Count = 1, CountTicks = beginTicks };
            currentCount = 1;
          }
        }
        else
        {
          displayTimes = new Dictionary<string, RepeatedData> { { displayValue, new RepeatedData { Count = 1, CountTicks = beginTicks } } };
          times[wrapper.Id] = displayTimes;
          currentCount = 1;
        }
      }

      return currentCount;
    }

    private string UpdatePattern(bool useRegex, string playerName, string pattern, out List<NumberOptions> numberOptions)
    {
      numberOptions = new List<NumberOptions>();
      pattern = pattern.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

      if (useRegex)
      {
        if (Regex.Matches(pattern, @"{(s\d?)}", RegexOptions.IgnoreCase) is MatchCollection matches && matches.Count > 0)
        {
          foreach (Match match in matches)
          {
            if (match.Groups.Count == 2)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
            }
          }
        }

        if (Regex.Matches(pattern, @"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase) is MatchCollection matches2 && matches2.Count > 0)
        {
          foreach (Match match in matches2)
          {
            if (match.Groups.Count == 4)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + @">\d+)");

              if (!string.IsNullOrEmpty(match.Groups[2].Value) && !string.IsNullOrEmpty(match.Groups[3].Value) &&
                uint.TryParse(match.Groups[3].Value, out var value))
              {
                numberOptions.Add(new NumberOptions { Key = match.Groups[1].Value, Op = match.Groups[2].Value, Value = value });
              }
            }
          }
        }
      }

      return pattern;
    }

    private string UpdateTimePattern(bool useRegex, string pattern)
    {
      if (useRegex)
      {
        if (Regex.Matches(pattern, @"{(ts)}", RegexOptions.IgnoreCase) is MatchCollection matches2 && matches2.Count > 0)
        {
          foreach (Match match in matches2)
          {
            if (match.Groups.Count == 2)
            {
              // This regex pattern matches time in the formats hh:mm:ss, mm:ss, or ss
              var timePattern = @"(?<" + match.Groups[1].Value + @">(?:\d+[:]?){1,3})";
              pattern = pattern.Replace(match.Value, timePattern);
            }
          }
        }
      }

      return pattern;
    }

    private void AddEntry(LineData lineData, TriggerWrapper wrapper, string type, long eval = 0)
    {
      // update log
      var log = new AlertEntry
      {
        EventTime = DateUtil.ToDouble(DateTime.Now),
        LogTime = lineData?.BeginTime ?? double.NaN,
        Line = lineData?.Action ?? "",
        Name = wrapper.Name,
        Type = type,
        Eval = eval,
        Priority = wrapper.TriggerData.Priority,
        Trigger = wrapper.TriggerData
      };

      lock (CollectionLock)
      {
        AlertLog.Insert(0, log);

        if (AlertLog.Count > 1000)
        {
          AlertLog.RemoveAt(AlertLog.Count - 1);
        }
      }
    }

    private void CleanupTimer(TriggerWrapper wrapper, TimerData timerData)
    {
      timerData.CancelSource?.Cancel();
      timerData.CancelSource?.Dispose();
      timerData.WarningSource?.Cancel();
      timerData.WarningSource?.Dispose();
      timerData.CancelSource = null;
      timerData.WarningSource = null;
      wrapper.TimerList.Remove(timerData);

      lock (ActiveTimers)
      {
        ActiveTimers.Remove(timerData);
      }
    }

    private void CleanupWrapper(TriggerWrapper wrapper)
    {
      lock (wrapper)
      {
        wrapper.TimerList.ToList().ForEach(timerData => CleanupTimer(wrapper, timerData));
      }
    }

    public void Dispose()
    {
      Process?.Complete();
      ProcessLowPri?.Complete();
      ProcessSpeech?.Complete();
      Synth?.Dispose();
      SoundPlayer?.Dispose();
      ActiveTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
    }

    private class Speak
    {
      public TriggerWrapper Wrapper { get; set; }
      public string TTSOrSound { get; set; }
      public bool IsSound { get; set; }
      public MatchCollection Matches { get; set; }
      public MatchCollection OriginalMatches { get; set; }
      public string Action { get; set; }
    }

    private class RepeatedData
    {
      public long Count { get; set; }
      public long CountTicks { get; set; }
    }

    private class TriggerWrapper
    {
      public string Id { get; set; }
      public string Name { get; set; }
      public List<TimerData> TimerList { get; set; } = new List<TimerData>();
      public string ModifiedPattern { get; set; }
      public string ModifiedSpeak { get; set; }
      public string ModifiedEndSpeak { get; set; }
      public string ModifiedEndEarlySpeak { get; set; }
      public string ModifiedWarningSpeak { get; set; }
      public string ModifiedDisplay { get; set; }
      public string ModifiedEndDisplay { get; set; }
      public string ModifiedEndEarlyDisplay { get; set; }
      public string ModifiedWarningDisplay { get; set; }
      public string ModifiedTimerName { get; set; }
      public double ModifiedDurationSeconds { get; set; }
      public Regex Regex { get; set; }
      public List<NumberOptions> RegexNOptions { get; set; }
      public Trigger TriggerData { get; set; }
      public bool HasRepeatedTimer { get; set; }
      public bool HasRepeatedText { get; set; }
    }
  }
}

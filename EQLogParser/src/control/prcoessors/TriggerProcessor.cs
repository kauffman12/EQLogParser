using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerProcessor : ILogProcessor
  {
    public readonly ObservableCollection<AlertEntry> AlertLog = new();
    public readonly string CurrentCharacterId;
    public readonly string CurrentProcessorName;
    private const long SixHours = 6 * 60 * 60 * 1000;
    private readonly string CurrentPlayer;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly object CollectionLock = new();
    private readonly object ActiveTriggerLock = new();
    private readonly object VoiceLock = new();
    private readonly List<TimerData> ActiveTimers = new();
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> RepeatedTextTimes = new();
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> RepeatedTimerTimes = new();
    private readonly Action<string, Trigger> AddTextEvent;
    private readonly Action<Trigger, List<TimerData>> AddTimerEvent;
    private readonly SpeechSynthesizer Synth;
    private readonly SoundPlayer SoundPlayer;
    private readonly Channel<Tuple<TriggerWrapper, LineData>> LowPriChannel = Channel.CreateUnbounded<Tuple<TriggerWrapper, LineData>>();
    private readonly Channel<Speak> SpeechChannel = Channel.CreateBounded<Speak>(new BoundedChannelOptions(10)
    {
      FullMode = BoundedChannelFullMode.DropOldest
    });
    private TriggerWrapper PreviousSpoken;
    private LinkedList<TriggerWrapper> ActiveTriggers;

    internal TriggerProcessor(string id, string name, string playerName, Action<string, Trigger> addTextEvent,
      Action<Trigger, List<TimerData>> addTimerEvent)
    {
      CurrentCharacterId = id;
      CurrentProcessorName = name;
      CurrentPlayer = playerName;

      AddTextEvent = addTextEvent;
      AddTimerEvent = addTimerEvent;
      BindingOperations.EnableCollectionSynchronization(AlertLog, CollectionLock);

      ActiveTriggers = GetActiveTriggers();
      Synth = TriggerUtil.GetSpeechSynthesizer();
      SetVoice(TriggerUtil.GetSelectedVoice());
      SetVoiceRate(TriggerUtil.GetVoiceRate());
      SoundPlayer = new SoundPlayer();
    }

    public void LinkTo(BlockingCollection<Tuple<string, double, bool>> collection)
    {
      // setup processors before reading from the log
      Task.Run(async () =>
      {
        await foreach (var data in LowPriChannel.Reader.ReadAllAsync())
        {
          HandleTrigger(data.Item1, data.Item2);
        }
      });

      Task.Run(async () =>
      {
        await foreach (var data in SpeechChannel.Reader.ReadAllAsync())
        {
          HandleSpeech(data);
        }
      });

      Task.Run(() =>
      {
        foreach (var data in collection.GetConsumingEnumerable())
        {
          DoProcess(data.Item1, data.Item2);
        }
      });
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
      lock (ActiveTriggerLock)
      {
        ActiveTriggers.ToList().ForEach(CleanupWrapper);
        ActiveTriggers = GetActiveTriggers();
      }
    }

    private static string ModLine(string text, string line) => !string.IsNullOrEmpty(text) ?
      text.Replace("{l}", line, StringComparison.OrdinalIgnoreCase) : text;
    private static string ModCounter(string text) => !string.IsNullOrEmpty(text) ?
      text.Replace("{counter}", "{repeated}", StringComparison.OrdinalIgnoreCase) : text;
    private string ModPlayer(string text) => !string.IsNullOrEmpty(text) ?
      text.Replace("{c}", CurrentPlayer ?? string.Empty, StringComparison.OrdinalIgnoreCase) : text;

    private void DoProcess(string line, double dateTime)
    {
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime };
      LinkedListNode<TriggerWrapper> node;

      lock (ActiveTriggerLock)
      {
        node = ActiveTriggers.First;
      }

      while (node != null)
      {
        // save since the nodes may get reordered
        var wrapper = node.Value;
        node = node.Next;

        lock (wrapper)
        {
          // if within a month assume handle it right away (2592000000 millis is 30 days)
          if ((new TimeSpan(DateTime.Now.Ticks).TotalMilliseconds - wrapper.TriggerData.LastTriggered) <= 2592000000)
          {
            HandleTrigger(wrapper, lineData);
          }
          else
          {
            LowPriChannel?.Writer.WriteAsync(Tuple.Create(wrapper, lineData));
          }
        }
      }

      // look for quick shares after triggers have been processed
      var chatType = ChatLineParser.ParseChatType(lineData.Action);
      if (chatType != null && TriggerStateManager.Instance.IsActive())
      {
        // Look for Quick Share entries
        TriggerUtil.CheckQuickShare(chatType, lineData.Action, dateTime, CurrentCharacterId, CurrentProcessorName);
        GinaUtil.CheckGina(chatType, lineData.Action, dateTime, CurrentCharacterId, CurrentProcessorName);
      }

    }

    private void HandleTrigger(TriggerWrapper wrapper, LineData lineData)
    {
      Speak speak = null;

      // lock here because lowPri queue also calls this
      lock (wrapper)
      {
        var start = DateTime.Now.Ticks;
        var action = lineData.Action;
        MatchCollection matches = null;
        var found = false;

        var dynamicDuration = double.NaN;
        if (wrapper.Regex != null)
        {
          matches = wrapper.Regex.Matches(action);
          found = matches.Count > 0 && TriggerUtil.CheckOptions(wrapper.RegexNOptions, matches, out dynamicDuration);
          if (!double.IsNaN(dynamicDuration) && wrapper.TriggerData.TimerType == 1)
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
          var updatedTime = new TimeSpan(beginTicks).TotalMilliseconds;

          // no need to constantly updated the DB. 6 hour check
          if (updatedTime - wrapper.TriggerData.LastTriggered > SixHours)
          {
            TriggerStateManager.Instance.UpdateLastTriggered(wrapper.Id, updatedTime);
          }

          wrapper.TriggerData.LastTriggered = updatedTime;
          var time = (beginTicks - start) / 10;
          wrapper.TriggerData.WorstEvalTime = Math.Max(time, wrapper.TriggerData.WorstEvalTime);

          if (ProcessMatchesText(wrapper.ModifiedTimerName, matches) is { } displayName)
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

          var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out var isSound);
          if (!string.IsNullOrEmpty(tts))
          {
            speak = new Speak
            {
              Wrapper = wrapper,
              TtsOrSound = tts,
              IsSound = isSound,
              Matches = matches,
              Action = lineData.Action
            };
          }

          if (ProcessDisplayText(wrapper.ModifiedDisplay, lineData.Action, matches, null) is { } updatedDisplayText)
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
          foreach (ref var timerData in wrapper.TimerList.ToArray().AsSpan())
          {
            var endEarly = CheckEndEarly(timerData.EndEarlyRegex, timerData.EndEarlyRegexNOptions, timerData.EndEarlyPattern,
              action, out var earlyMatches);

            // try 2nd
            if (!endEarly)
            {
              endEarly = CheckEndEarly(timerData.EndEarlyRegex2, timerData.EndEarlyRegex2NOptions, timerData.EndEarlyPattern2, action, out earlyMatches);
            }

            if (endEarly)
            {
              var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out var isSound);
              tts = string.IsNullOrEmpty(tts) ? TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound) : tts;
              var displayText = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

              speak = new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = tts,
                IsSound = isSound,
                Matches = earlyMatches,
                OriginalMatches = timerData.OriginalMatches,
                Action = lineData.Action
              };

              if (ProcessDisplayText(displayText, lineData.Action, earlyMatches, timerData.OriginalMatches) is { } updatedDisplayText)
              {
                AddTextEvent(updatedDisplayText, wrapper.TriggerData);
              }

              AddEntry(lineData, wrapper, "Timer End Early");
              CleanupTimer(wrapper, timerData);
            }
          }
        }
      }

      if (speak != null)
      {
        SpeechChannel.Writer.WriteAsync(speak);
      }
    }

    private void StartTimer(TriggerWrapper wrapper, string displayName, long beginTicks, LineData lineData, MatchCollection matches)
    {
      var trigger = wrapper.TriggerData;
      switch (trigger.TriggerAgainOption)
      {
        // Restart Timer Option so clear out everything
        case 1:
          CleanupWrapper(wrapper);
          break;
        // Restart Timer only if it is already running
        case 2:
          {
            if (wrapper.TimerList.FirstOrDefault(data => displayName.Equals(data?.DisplayName, StringComparison.OrdinalIgnoreCase))
                is { } timerData)
            {
              CleanupTimer(wrapper, timerData);
            }

            break;
          }
        // Do nothing if any exist
        case 3 when wrapper.TimerList.Any():
        // Do nothing only if a timer with this name is already running
        case 4 when wrapper.TimerList.FirstOrDefault(data => displayName.Equals(data?.DisplayName, StringComparison.OrdinalIgnoreCase))
          is not null:
          {
            return;
          }
      }

      TimerData newTimerData = null;
      if (trigger.WarningSeconds > 0 && wrapper.ModifiedDurationSeconds - trigger.WarningSeconds is var diff and > 0)
      {
        newTimerData = new TimerData { DisplayName = displayName, WarningSource = new CancellationTokenSource() };

        var data = newTimerData;
        Task.Delay((int)diff * 1000).ContinueWith(_ =>
        {
          var proceed = false;
          lock (wrapper)
          {
            if (data.WarningSource != null)
            {
              proceed = !data.WarningSource.Token.IsCancellationRequested;
              data.WarningSource.Dispose();
              data.WarningSource = null;
            }

            if (proceed)
            {
              var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out var isSound);
              SpeechChannel?.Writer.WriteAsync(new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = speak,
                IsSound = isSound,
                Matches = matches,
                Action = lineData.Action
              });

              if (ProcessDisplayText(wrapper.ModifiedWarningDisplay, lineData.Action, matches, null) is { } updatedDisplayText)
              {
                AddTextEvent(updatedDisplayText, trigger);
              }

              AddEntry(lineData, wrapper, "Timer Warning");
            }
          }
        }, newTimerData.WarningSource.Token);
      }

      newTimerData ??= new TimerData { DisplayName = displayName };

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
        endEarlyPattern = UpdatePattern(trigger.EndUseRegex, endEarlyPattern, out var numberOptions2);

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
        endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, endEarlyPattern2, out var numberOptions3);

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

      Task.Delay((int)(wrapper.ModifiedDurationSeconds * 1000)).ContinueWith(_ =>
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

            SpeechChannel?.Writer.WriteAsync(new Speak
            {
              Wrapper = wrapper,
              TtsOrSound = speak,
              IsSound = isSound,
              Matches = matches,
              OriginalMatches = newTimerData.OriginalMatches,
              Action = lineData.Action
            });

            if (ProcessDisplayText(wrapper.ModifiedEndDisplay, lineData.Action, matches, newTimerData.OriginalMatches) is { } updatedDisplayText)
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

    private void HandleSpeech(Speak speak)
    {
      if (!string.IsNullOrEmpty(speak.TtsOrSound))
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

              var theFile = @"data\sounds\" + speak.TtsOrSound;
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
              Log.Debug(e);
            }
          }
        }
        else
        {
          var tts = ProcessMatchesText(speak.TtsOrSound, speak.OriginalMatches);
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
      foreach (var enabled in enabledTriggers.OrderByDescending(enabled => enabled.Trigger.LastTriggered))
      {
        var trigger = enabled.Trigger;
        if (trigger.Pattern is { } pattern && !string.IsNullOrEmpty(pattern))
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

            // replace GINA counted with repeated
            wrapper.ModifiedDisplay = ModCounter(wrapper.ModifiedDisplay);
            wrapper.ModifiedTimerName = ModCounter(wrapper.ModifiedTimerName);

            wrapper.ModifiedTimerName = string.IsNullOrEmpty(wrapper.ModifiedTimerName) ? "" : wrapper.ModifiedTimerName;
            wrapper.HasRepeatedText = wrapper.ModifiedDisplay?.Contains("{repeated}", StringComparison.OrdinalIgnoreCase) == true;
            wrapper.HasRepeatedTimer = wrapper.ModifiedTimerName?.Contains("{repeated}", StringComparison.OrdinalIgnoreCase) == true;
            pattern = UpdatePattern(trigger.UseRegex, pattern, out var numberOptions);
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
            Log.Debug("Bad Trigger?", ex);
          }
        }
      }

      return activeTriggers;
    }

    private static bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out MatchCollection earlyMatches)
    {
      earlyMatches = null;
      var endEarly = false;

      if (endEarlyRegex != null)
      {
        earlyMatches = endEarlyRegex.Matches(action);
        if (earlyMatches is { Count: > 0 } && TriggerUtil.CheckOptions(options, earlyMatches, out _))
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

    private static long GetRepeatedCount(Dictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper, string displayValue)
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

    private static long UpdateRepeatedTimes(Dictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper,
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
          displayTimes = new Dictionary<string, RepeatedData>
          {
            { displayValue, new RepeatedData { Count = 1, CountTicks = beginTicks } }
          };

          times[wrapper.Id] = displayTimes;
          currentCount = 1;
        }
      }

      return currentCount;
    }

    private string UpdatePattern(bool useRegex, string pattern, out List<NumberOptions> numberOptions)
    {
      numberOptions = new List<NumberOptions>();
      pattern = ModPlayer(pattern);

      if (useRegex)
      {
        if (Regex.Matches(pattern, @"{(s\d?)}", RegexOptions.IgnoreCase) is { Count: > 0 } matches)
        {
          foreach (Match match in matches)
          {
            if (match.Groups.Count == 2)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
            }
          }
        }

        if (Regex.Matches(pattern, @"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase) is { Count: > 0 } matches2)
        {
          foreach (var match in matches2.Cast<Match>())
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

    private static string UpdateTimePattern(bool useRegex, string pattern)
    {
      if (useRegex)
      {
        if (Regex.Matches(pattern, @"{(ts)}", RegexOptions.IgnoreCase) is { Count: > 0 } matches2)
        {
          foreach (var match in matches2.Cast<Match>())
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
        NodeId = wrapper.Id,
        Priority = wrapper.TriggerData.Priority
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
        // need an ew list because CleanupTimer will update TimerList
        wrapper.TimerList.ToList().ForEach(timerData => CleanupTimer(wrapper, timerData));
      }
    }

    public void Dispose()
    {
      LowPriChannel?.Writer.Complete();
      SpeechChannel?.Writer.Complete();
      SoundPlayer?.Dispose();

      lock (VoiceLock)
      {
        Synth?.Dispose();
      }

      lock (ActiveTriggerLock)
      {
        ActiveTriggers.ToList().ForEach(CleanupWrapper);
      }
    }

    private class Speak
    {
      public TriggerWrapper Wrapper { get; init; }
      public string TtsOrSound { get; init; }
      public bool IsSound { get; init; }
      public MatchCollection Matches { get; init; }
      public MatchCollection OriginalMatches { get; init; }
      public string Action { get; init; }
    }

    private class RepeatedData
    {
      public long Count { get; set; }
      public long CountTicks { get; set; }
    }

    private class TriggerWrapper
    {
      public string Id { get; init; }
      public string Name { get; init; }
      public List<TimerData> TimerList { get; } = new();
      public string ModifiedPattern { get; set; }
      public string ModifiedSpeak { get; init; }
      public string ModifiedEndSpeak { get; init; }
      public string ModifiedEndEarlySpeak { get; init; }
      public string ModifiedWarningSpeak { get; init; }
      public string ModifiedDisplay { get; set; }
      public string ModifiedEndDisplay { get; init; }
      public string ModifiedEndEarlyDisplay { get; init; }
      public string ModifiedWarningDisplay { get; init; }
      public string ModifiedTimerName { get; set; }
      public double ModifiedDurationSeconds { get; set; }
      public Regex Regex { get; set; }
      public List<NumberOptions> RegexNOptions { get; set; }
      public Trigger TriggerData { get; init; }
      public bool HasRepeatedTimer { get; set; }
      public bool HasRepeatedText { get; set; }
    }
  }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<Trigger> EventsSelectTrigger;
    private const long COUNT_TIME = TimeSpan.TicksPerMillisecond * 750;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<TriggerManager> _lazy = new Lazy<TriggerManager>(() => new TriggerManager());
    internal static TriggerManager Instance => _lazy.Value; // instance
    private readonly object CollectionLock = new object();
    private readonly object LockObject = new object();
    private readonly ObservableCollection<AlertEntry> AlertLog = new ObservableCollection<AlertEntry>();
    private readonly List<TimerData> ActiveTimers = new List<TimerData>();
    private readonly DispatcherTimer TriggerUpdateTimer;
    private readonly DispatcherTimer TextOverlayTimer;
    private readonly DispatcherTimer TimerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> TextWindows = new Dictionary<string, OverlayWindowData>();
    private readonly Dictionary<string, OverlayWindowData> TimerWindows = new Dictionary<string, OverlayWindowData>();
    private Channel<dynamic> LogChannel = null;
    private string CurrentVoice;
    private int CurrentVoiceRate;
    private Task RefreshTask = null;
    private int TimerIncrement = 0;

    public TriggerManager()
    {
      BindingOperations.EnableCollectionSynchronization(AlertLog, CollectionLock);
      CurrentVoice = TriggerUtil.GetSelectedVoice();
      CurrentVoiceRate = TriggerUtil.GetVoiceRate();
      TriggerUtil.LoadOverlayStyles();

      TriggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      TriggerUpdateTimer.Tick += TriggerDataUpdated;
      TextOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 450) };
      TimerOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 50) };
    }

    internal void Init() => (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
    internal ObservableCollection<AlertEntry> GetAlertLog() => AlertLog;
    internal void SetVoice(string voice) => CurrentVoice = voice;
    internal void SetVoiceRate(int rate) => CurrentVoiceRate = rate;
    internal void Select(Trigger trigger) => EventsSelectTrigger?.Invoke(trigger);
    private string ModLine(string text, string line) => string.IsNullOrEmpty(text) ? null : text.Replace("{l}", line, StringComparison.OrdinalIgnoreCase);
    private string ModPlayer(string text) => string.IsNullOrEmpty(text) ? null : text.Replace("{c}", ConfigUtil.PlayerName, StringComparison.OrdinalIgnoreCase);

    internal bool IsActive()
    {
      lock (LockObject)
      {
        return LogChannel != null;
      }
    }

    internal void AddAction(LineData lineData)
    {
      lock (LockObject)
      {
        LogChannel?.Writer.WriteAsync(lineData);
      }

      if (!double.IsNaN(lineData.BeginTime) && ConfigUtil.IfSetOrElse("TriggersWatchForGINA", false))
      {
        GinaUtil.CheckGina(lineData);
      }
    }

    internal void CloseOverlay(string id)
    {
      TriggerUtil.CloseOverlay(TextWindows, id);
      TriggerUtil.CloseOverlay(TimerWindows, id);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Start()
    {
      LOG.Info("Starting Trigger Manager");
      if (TriggerUtil.GetSpeechSynthesizer() == null)
      {
        return;
      }

      TextOverlayTimer.Tick += TextTick;
      TimerOverlayTimer.Tick += TimerTick;

      var lowPriChannel = Channel.CreateUnbounded<LowPriData>();
      var speechChannel = Channel.CreateUnbounded<Speak>();
      StartSpeechReader(speechChannel);
      Channel<dynamic> logChannel;

      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        logChannel = LogChannel = Channel.CreateUnbounded<dynamic>();
      }

      _ = Task.Run(async () =>
      {
        LinkedList<TriggerWrapper> activeTriggers = null;

        try
        {
          activeTriggers = GetActiveTriggers();
          while (await logChannel.Reader.WaitToReadAsync())
          {
            var result = await logChannel.Reader.ReadAsync();
            if (result is LinkedList<TriggerWrapper> updatedTriggers)
            {
              lock (activeTriggers)
              {
                activeTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
              }

              activeTriggers = updatedTriggers;
            }
            else if (result is LineData lineData)
            {
              LinkedListNode<TriggerWrapper> node = null;

              lock (activeTriggers)
              {
                node = activeTriggers.First;
              }

              while (node != null)
              {
                // save since the nodes may get reordered
                var nextNode = node.Next;

                // if within a month assume handle it right away
                // millis in 30 days
                if ((new TimeSpan(DateTime.Now.Ticks).TotalMilliseconds - node.Value.TriggerData.LastTriggered) <= 2592000000)
                {
                  HandleTrigger(activeTriggers, node, lineData, speechChannel);
                }
                else
                {
                  _ = lowPriChannel.Writer.WriteAsync(new LowPriData
                  {
                    ActiveTriggers = activeTriggers,
                    LineData = lineData,
                    Node = node,
                    SpeechChannel = speechChannel
                  });
                }

                node = nextNode;
              }
            }
          }
        }
        catch (Exception ex)
        {
          // channel closed
          LOG.Debug(ex);
        }

        lowPriChannel?.Writer.Complete();
        speechChannel?.Writer.Complete();

        lock (activeTriggers)
        {
          activeTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
        }
      });

      _ = Task.Run(async () =>
      {
        try
        {
          while (await lowPriChannel.Reader.WaitToReadAsync())
          {
            var result = await lowPriChannel.Reader.ReadAsync();
            HandleTrigger(result.ActiveTriggers, result.Node, result.LineData, result.SpeechChannel);
          }
        }
        catch (Exception e)
        {
          // end channel
          LOG.Debug(e);
        }
      });

      (Application.Current.MainWindow as MainWindow).ShowTriggersEnabled(true);
      ConfigUtil.SetSetting("TriggersEnabled", true.ToString(CultureInfo.CurrentCulture));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Stop(bool save = true)
    {
      LOG.Info("Shutting Down Trigger Manager");
      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        LogChannel = null;
      }

      TextOverlayTimer.Stop();
      TimerOverlayTimer.Stop();
      TextOverlayTimer.Tick -= TextTick;
      TimerOverlayTimer.Tick -= TimerTick;
      CloseOverlays();

      // SaveTriggers();
      (Application.Current.MainWindow as MainWindow)?.ShowTriggersEnabled(false);

      if (save)
      {
        ConfigUtil.SetSetting("TriggersEnabled", false.ToString(CultureInfo.CurrentCulture));
      }
    }

    private void HandleTrigger(LinkedList<TriggerWrapper> activeTriggers, LinkedListNode<TriggerWrapper> node,
      LineData lineData, Channel<Speak> speechChannel)
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

          if (ProcessText(wrapper.ModifiedTimerName, matches) is string displayName && !string.IsNullOrEmpty(displayName))
          {
            if (wrapper.TimerCounts.TryGetValue(displayName, out var timerCount))
            {
              if ((beginTicks - timerCount.CountTicks) > COUNT_TIME)
              {
                timerCount.Count = 1;
                timerCount.CountTicks = beginTicks;
              }
              else
              {
                timerCount.Count++;
              }
            }
            else
            {
              wrapper.TimerCounts[displayName] = new TimerCount { Count = 1, CountTicks = beginTicks };
            }

            if (wrapper.TriggerData.TimerType > 0 && wrapper.TriggerData.DurationSeconds > 0)
            {
              StartTimer(wrapper, displayName, beginTicks, speechChannel, lineData, matches);
            }
          }

          var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out var isSound);
          if (!string.IsNullOrEmpty(speak))
          {
            speechChannel.Writer.WriteAsync(new Speak
            {
              Wrapper = wrapper,
              TTSOrSound = speak,
              IsSound = isSound,
              Matches = matches
            });
          }

          AddTextEvent(lineData.Action, wrapper.ModifiedDisplay, wrapper.TriggerData, matches);
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
              bool isSound;
              var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out isSound);
              speak = string.IsNullOrEmpty(speak) ? TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound) : speak;
              var displayText = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

              speechChannel.Writer.WriteAsync(new Speak
              {
                Wrapper = wrapper,
                TTSOrSound = speak,
                IsSound = isSound,
                Matches = earlyMatches,
                OriginalMatches = timerData.OriginalMatches
              });

              AddTextEvent(lineData.Action, displayText, wrapper.TriggerData, earlyMatches, timerData.OriginalMatches);
              AddEntry(lineData, wrapper, "Timer End Early");
              CleanupTimer(wrapper, timerData);
            }
          });
        }
      }
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

    private void StartSpeechReader(Channel<Speak> speechChannel)
    {
      var task = Task.Run(async () =>
      {
        SpeechSynthesizer synth = null;
        SoundPlayer player = null;

        try
        {
          synth = TriggerUtil.GetSpeechSynthesizer();
          player = new SoundPlayer();
          TriggerWrapper previous = null;

          while (await speechChannel.Reader.WaitToReadAsync())
          {
            var result = await speechChannel.Reader.ReadAsync();
            if (!string.IsNullOrEmpty(result.TTSOrSound))
            {
              var cancel = result.Wrapper.TriggerData.Priority < previous?.TriggerData?.Priority;
              if (cancel && synth.State == SynthesizerState.Speaking)
              {
                synth.SpeakAsyncCancelAll();
                AddEntry(null, previous, "Speech Canceled");
              }

              if (result.IsSound)
              {
                try
                {
                  if (cancel)
                  {
                    player?.Stop();
                    AddEntry(null, previous, "Wav Canceled");
                  }

                  var theFile = @"data\sounds\" + result.TTSOrSound;
                  if (player.SoundLocation != theFile && File.Exists(theFile))
                  {
                    player.SoundLocation = theFile;
                  }

                  if (!string.IsNullOrEmpty(player.SoundLocation))
                  {
                    player.Play();
                  }
                }
                catch (Exception e)
                {
                  LOG.Debug(e);
                }
              }
              else
              {
                var speak = ProcessText(result.TTSOrSound, result.OriginalMatches);
                speak = ProcessText(speak, result.Matches);

                if (!string.IsNullOrEmpty(CurrentVoice) && synth.Voice.Name != CurrentVoice)
                {
                  synth.SelectVoice(CurrentVoice);
                }

                if (CurrentVoiceRate != synth.Rate)
                {
                  synth.Rate = CurrentVoiceRate;
                }

                synth.SpeakAsync(speak);
              }
            }

            previous = result.Wrapper;
          }
        }
        catch (Exception ex)
        {
          // channel closed
          LOG.Debug(ex);
        }
        finally
        {
          synth?.Dispose();
          player?.Dispose();
        }
      });
    }

    private void StartTimer(TriggerWrapper wrapper, string displayName, long beginTicks, Channel<Speak> speechChannel, LineData lineData, MatchCollection matches)
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

                speechChannel.Writer.WriteAsync(new Speak
                {
                  Wrapper = wrapper,
                  TTSOrSound = speak,
                  IsSound = isSound,
                  Matches = matches
                });

                AddTextEvent(lineData.Action, wrapper.ModifiedWarningDisplay, trigger, matches);
                AddEntry(lineData, wrapper, "Timer Warning");
              }
            }
          }, newTimerData.WarningSource.Token);
        }

        if (newTimerData == null)
        {
          newTimerData = new TimerData { DisplayName = displayName };
        }

        if (wrapper.HasRepeated)
        {
          newTimerData.Repeated = wrapper.TimerCounts[displayName].Count;
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
          var endEarlyPattern = ProcessText(trigger.EndEarlyPattern, matches);
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
          var endEarlyPattern2 = ProcessText(trigger.EndEarlyPattern2, matches);
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
              speechChannel.Writer.WriteAsync(new Speak
              {
                Wrapper = wrapper,
                TTSOrSound = speak,
                IsSound = isSound,
                Matches = matches,
                OriginalMatches = newTimerData.OriginalMatches
              });

              AddTextEvent(lineData.Action, wrapper.ModifiedEndDisplay, trigger, matches, newTimerData.OriginalMatches);
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
          AddTimerEvent(trigger);
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

    private string ProcessText(string text, MatchCollection matches)
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

    private LinkedList<TriggerWrapper> GetActiveTriggers()
    {
      var activeTriggers = new LinkedList<TriggerWrapper>();
      var enabledTriggers = TriggerStateManager.Instance.GetEnabledTriggers(TriggerStateManager.DEFAULT_USER);

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
            wrapper.HasRepeated = wrapper.ModifiedTimerName.Contains("{repeated}", StringComparison.OrdinalIgnoreCase);
            pattern = UpdatePattern(trigger.UseRegex, playerName, pattern, out var numberOptions);
            pattern = UpdateTimePattern(trigger.UseRegex, playerName, pattern);

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

    private string UpdateTimePattern(bool useRegex, string playerName, string pattern)
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

    internal void UpdateTriggers()
    {
      TriggerUpdateTimer.Stop();
      TriggerUpdateTimer.Start();
    }

    private void TriggerDataUpdated(object sender, EventArgs e)
    {
      TriggerUpdateTimer.Stop();
      RequestRefresh();
    }

    private void AddEntry(LineData lineData, TriggerWrapper wrapper, string type, long eval = 0)
    {
      var eventTime = DateUtil.ToDouble(DateTime.Now);
      _ = Application.Current.Dispatcher.InvokeAsync(() =>
      {
        // update log
        var log = new AlertEntry
        {
          EventTime = eventTime,
          LogTime = lineData?.BeginTime ?? double.NaN,
          Line = lineData?.Action ?? "",
          Name = wrapper.Name,
          Type = type,
          Eval = eval,
          Priority = wrapper.TriggerData.Priority,
          Trigger = wrapper.TriggerData
        };

        AlertLog.Insert(0, log);

        if (AlertLog.Count > 1000)
        {
          AlertLog.RemoveAt(AlertLog.Count - 1);
        }
      });
    }

    private void CloseOverlays()
    {
      TriggerUtil.CloseOverlays(TextWindows);
      TriggerUtil.CloseOverlays(TimerWindows);
    }

    private void RequestRefresh()
    {
      if (RefreshTask?.IsCompleted == true)
      {
        RefreshTask?.Dispose();
        RefreshTask = null;
      }

      if (RefreshTask == null)
      {
        RefreshTask = Task.Run(() =>
        {
          Application.Current.Dispatcher.InvokeAsync(() => CloseOverlays());
          var updatedTriggers = GetActiveTriggers();
          lock (LockObject)
          {
            LogChannel?.Writer.WriteAsync(updatedTriggers);
          }
        });
      }
    }

    private void EventsLogLoadingComplete(object sender, bool e)
    {
      lock (LockObject)
      {
        if (LogChannel != null)
        {
          RequestRefresh();
        }
      }
    }

    private void TextTick(object sender, EventArgs e) => WindowTick(TextWindows, TextOverlayTimer);

    private void TimerTick(object sender, EventArgs e)
    {
      TimerIncrement++;
      WindowTick(TimerWindows, TimerOverlayTimer, TimerIncrement);

      if (TimerIncrement == 10)
      {
        TimerIncrement = 0;
      }
    }

    private void WindowTick(Dictionary<string, OverlayWindowData> windows, DispatcherTimer dispatchTimer, int increment = 10)
    {
      var removeList = new List<string>();
      List<TimerData> data = null;

      lock (ActiveTimers)
      {
        data = ActiveTimers.ToList();
      }

      lock (windows)
      {
        foreach (var keypair in windows)
        {
          var done = false;
          var shortTick = false;
          if (keypair.Value is OverlayWindowData windowData)
          {
            if (windowData.TheWindow is TextOverlayWindow textWindow)
            {
              done = textWindow.Tick();
            }
            else if (windowData.TheWindow is TimerOverlayWindow timerWindow)
            {
              // full tick every 500ms
              if (increment == 10)
              {
                done = timerWindow.Tick(data);
              }
              else
              {
                timerWindow.ShortTick(data);
                shortTick = true;
              }
            }

            if (!shortTick)
            {
              if (done)
              {
                var nowTicks = DateTime.Now.Ticks;
                if (windowData.RemoveTicks == -1)
                {
                  windowData.RemoveTicks = nowTicks + (TimeSpan.TicksPerMinute * 2);
                }
                else if (nowTicks > windowData.RemoveTicks)
                {
                  removeList.Add(keypair.Key);
                }
              }
              else
              {
                windowData.RemoveTicks = -1;
              }
            }
          }
        }

        foreach (var id in removeList)
        {
          if (windows.Remove(id, out var windowData))
          {
            windowData.TheWindow?.Close();
          }
        }

        if (windows.Count == 0)
        {
          dispatchTimer.Stop();
        }
      }
    }

    private void AddTextEvent(string action, string text, Trigger trigger, MatchCollection matches, MatchCollection originalMatches = null)
    {
      if (!string.IsNullOrEmpty(text))
      {
        var beginTicks = DateTime.Now.Ticks;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
          text = ProcessText(text, originalMatches);
          text = ProcessText(text, matches);
          text = ModLine(text, action);
          var textOverlayFound = false;

          trigger.SelectedOverlays?.ForEach(overlayId =>
          {
            OverlayWindowData windowData = null;
            lock (TextWindows)
            {
              if (!TextWindows.TryGetValue(overlayId, out windowData))
              {
                if (TriggerStateManager.Instance.GetOverlayById(overlayId) is TriggerNode node
                  && node?.OverlayData?.IsTextOverlay == true)
                {
                  windowData = new OverlayWindowData { TheWindow = new TextOverlayWindow(node) };
                  TextWindows[overlayId] = windowData;
                  windowData.TheWindow.Show();
                }
              }

              if (windowData != null)
              {
                var brush = TriggerUtil.GetBrush(trigger.FontColor);
                (windowData?.TheWindow as TextOverlayWindow).AddTriggerText(text, beginTicks, brush);
                textOverlayFound = true;
              }
            }
          });

          if (textOverlayFound && !TextOverlayTimer.IsEnabled)
          {
            TextOverlayTimer.Start();
          }
        }, DispatcherPriority.Render);
      }
    }

    private void AddTimerEvent(Trigger trigger)
    {
      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        var timerOverlayFound = false;
        trigger.SelectedOverlays?.ForEach(overlayId =>
        {
          OverlayWindowData windowData = null;
          lock (TimerWindows)
          {
            if (!TimerWindows.TryGetValue(overlayId, out windowData))
            {
              if (TriggerStateManager.Instance.GetOverlayById(overlayId) is TriggerNode node
                && node?.OverlayData?.IsTimerOverlay == true)
              {
                windowData = new OverlayWindowData { TheWindow = new TimerOverlayWindow(node) };
                TimerWindows[overlayId] = windowData;
                windowData.TheWindow.Show();

                // tick right away
                List<TimerData> data = null;

                lock (ActiveTimers)
                {
                  data = ActiveTimers.ToList();
                }

                ((TimerOverlayWindow)windowData?.TheWindow).Tick(data);
              }
            }

            // may not have found a timer overlay
            if (windowData != null)
            {
              timerOverlayFound = true;
            }
          }
        });

        if (timerOverlayFound && !TimerOverlayTimer.IsEnabled)
        {
          TimerOverlayTimer.Start();
        }
      }, DispatcherPriority.Render);
    }

    private class Speak
    {
      public TriggerWrapper Wrapper { get; set; }
      public string TTSOrSound { get; set; }
      public bool IsSound { get; set; }
      public MatchCollection Matches { get; set; }
      public MatchCollection OriginalMatches { get; set; }
    }

    private class LowPriData
    {
      public LinkedList<TriggerWrapper> ActiveTriggers { get; set; }
      public LinkedListNode<TriggerWrapper> Node { get; set; }
      public LineData LineData { get; set; }
      public Channel<Speak> SpeechChannel { get; set; }
    }

    private class TimerCount
    {
      public int Count { get; set; }
      public long CountTicks { get; set; }
    }

    private class TriggerWrapper
    {
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
      public Dictionary<string, TimerCount> TimerCounts = new Dictionary<string, TimerCount>();
      public bool HasRepeated { get; set; }
    }
  }
}

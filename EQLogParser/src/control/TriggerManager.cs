using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.Json;
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
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal event EventHandler<bool> EventsUpdateTree;
    internal event EventHandler<Trigger> EventsSelectTrigger;
    internal event EventHandler<dynamic> EventsAddText;
    internal event EventHandler<dynamic> EventsNewTimer;
    internal event EventHandler<dynamic> EventsUpdateTimer;
    internal event EventHandler<Trigger> EventsEndTimer;

    internal static TriggerManager Instance = new TriggerManager();
    private readonly string TRIGGERS_FILE = "triggers.json";
    private readonly DispatcherTimer TriggerUpdateTimer;
    private readonly TriggerNode TriggerNodes;
    private Channel<dynamic> LogChannel = null;
    private string CurrentVoice;
    private int CurrentVoiceRate;
    private Task RefreshTask = null;
    private static object LockObject = new object();
    private ObservableCollection<dynamic> AlertLog = new ObservableCollection<dynamic>();
    private object CollectionLock = new object();

    public TriggerManager()
    {
      BindingOperations.EnableCollectionSynchronization(AlertLog, CollectionLock);

      var json = ConfigUtil.ReadConfigFile(TRIGGERS_FILE);
      TriggerNodes = (json != null) ? JsonSerializer.Deserialize<TriggerNode>(json, new JsonSerializerOptions { IncludeFields = true }) : new TriggerNode();

      CurrentVoice = TriggerUtil.GetSelectedVoice();
      CurrentVoiceRate = TriggerUtil.GetVoiceRate();

      TriggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
      TriggerUpdateTimer.Tick += TriggerDataUpdated;
    }

    internal void Init() => (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
    internal ObservableCollection<dynamic> GetAlertLog() => AlertLog;
    internal TriggerTreeViewNode GetTriggerTreeView() => TriggerUtil.GetTreeView(TriggerNodes, "Triggers");
    internal void SetVoice(string voice) => CurrentVoice = voice;
    internal void SetVoiceRate(int rate) => CurrentVoiceRate = rate;

    internal void AddAction(LineData lineData)
    {
      lock (LockObject)
      {
        LogChannel?.Writer.WriteAsync(lineData);
      }

      if (!double.IsNaN(lineData.BeginTime) && ConfigUtil.IfSetOrElse("TriggersWatchForGINA", false))
      {
        TriggerUtil.CheckGina(lineData);
      }
    }

    internal bool IsActive()
    {
      bool active = false;

      lock (LockObject)
      {
        active = (LogChannel != null);
      }

      return active;
    }

    internal void Select(Trigger trigger) => EventsSelectTrigger?.Invoke(this, trigger);

    internal void MergeTriggers(List<TriggerNode> list, TriggerNode parent)
    {
      lock (TriggerNodes)
      {
        foreach (var node in list)
        {
          TriggerUtil.DisableNodes(node);
          TriggerUtil.MergeNodes(node.Nodes, parent);
        }

        SaveTriggers();
      }

      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void MergeTriggers(TriggerNode newTriggers, string newFolder)
    {
      newFolder += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
      newTriggers.Name = newFolder;

      lock (TriggerNodes)
      {
        TriggerNodes.Nodes.Add(newTriggers);
        SaveTriggers();
      }

      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void MergeTriggers(TriggerNode newTriggers, TriggerNode parent = null)
    {
      lock (TriggerNodes)
      {
        TriggerUtil.MergeNodes(newTriggers.Nodes, (parent == null) ? TriggerNodes : parent);
        SaveTriggers();
      }

      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void UpdateTriggers(bool needRefresh = true)
    {
      TriggerUpdateTimer.Stop();
      TriggerUpdateTimer.Start();

      if (needRefresh)
      {
        TriggerUpdateTimer.Tag = needRefresh;
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Start()
    {
      LOG.Info("Starting Trigger Manager");
      try
      {
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
        return;
      }

      TriggerOverlayManager.Instance.Start();
      Channel<LowPriData> lowPriChannel = Channel.CreateUnbounded<LowPriData>();
      Channel<Speak> speechChannel = Channel.CreateUnbounded<Speak>();
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
              activeTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
              activeTriggers = updatedTriggers;
            }
            else if (result is LineData lineData)
            {
              var node = activeTriggers.First;

              while (node != null)
              {
                // save since the nodes may get reordered
                var nextNode = node.Next;

                // if within a month assume handle it right away
                if ((DateUtil.ToDouble(DateTime.Now) - node.Value.TriggerData.LastTriggered) <= 2628000)
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
        activeTriggers?.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
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
        catch (Exception)
        {
          // end channel
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

      TriggerOverlayManager.Instance.Stop();

      SaveTriggers();
      (Application.Current.MainWindow as MainWindow)?.ShowTriggersEnabled(false);

      if (save)
      {
        ConfigUtil.SetSetting("TriggersEnabled", false.ToString(CultureInfo.CurrentCulture));
      }
    }

    private void HandleTrigger(LinkedList<TriggerWrapper> activeTriggers, LinkedListNode<TriggerWrapper> node,
      LineData lineData, Channel<Speak> speechChannel)
    {
      long time = long.MinValue;
      long start = DateTime.Now.Ticks;
      var action = lineData.Action;
      var wrapper = node.Value;
      MatchCollection matches = null;
      bool found = false;

      if (wrapper.Regex != null)
      {
        matches = wrapper.Regex.Matches(action);
        if (matches != null && matches.Count > 0 && TriggerUtil.CheckNumberOptions(wrapper.RegexNOptions, matches))
        {
          found = true;
          wrapper.RegexMatches = matches;
          UpdateTriggerTime(activeTriggers, node, lineData.BeginTime);
        }
      }
      else if (!string.IsNullOrEmpty(wrapper.ModifiedPattern))
      {
        if (action.Contains(wrapper.ModifiedPattern, StringComparison.OrdinalIgnoreCase))
        {
          found = true;
          UpdateTriggerTime(activeTriggers, node, lineData.BeginTime);
        }
      }

      if (wrapper.TimerCancellations.Count > 0)
      {
        bool endEarly = CheckEndEarly(wrapper.EndEarlyRegex, wrapper.EndEarlyRegexNOptions, wrapper.ModifiedEndEarlyPattern,
          action, out MatchCollection earlyMatches);

        // try 2nd
        if (!endEarly)
        {
          endEarly = CheckEndEarly(wrapper.EndEarlyRegex2, wrapper.EndEarlyRegex2NOptions, wrapper.ModifiedEndEarlyPattern2,
            action, out earlyMatches);
        }

        if (endEarly)
        {
          time = (long)((DateTime.Now.Ticks - start) / 10);
          wrapper.TriggerData.LongestEvalTime = Math.Max(time, wrapper.TriggerData.LongestEvalTime);
          EventsEndTimer?.Invoke(this, wrapper.TriggerData);
          CleanupFirstTimer(wrapper);
        
          string speak;
          bool isSound;
          if (!string.IsNullOrEmpty(wrapper.ModifiedEndEarlySpeak))
          {
            speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out isSound);
          }
          else
          {
            speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound);
          }
          
          speechChannel.Writer.WriteAsync(new Speak
          {
            Trigger = wrapper.TriggerData,
            TextOrSound = speak,
            IsSound = isSound,
            Matches = earlyMatches,
            OriginalMatches = wrapper.RegexMatches
          });

          AddEntry(lineData.Line, wrapper.TriggerData, "Timer End Early", time);
        }
      }

      // wasnt set by end early
      if (time == long.MinValue)
      {
        time = (long)((DateTime.Now.Ticks - start) / 10);
        wrapper.TriggerData.LongestEvalTime = Math.Max(time, wrapper.TriggerData.LongestEvalTime);
      }

      if (found)
      {
        var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out bool isSound);
        if (!string.IsNullOrEmpty(speak))
        {
          speechChannel.Writer.WriteAsync(new Speak
          {
            Trigger = wrapper.TriggerData,
            TextOrSound = speak,
            IsSound = isSound,
            Matches = matches,
          });
        }

        AddEntry(lineData.Line, wrapper.TriggerData, "Trigger", time);

        if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.DurationSeconds > 0)
        {
          StartTimer(wrapper, speechChannel, lineData.Line, matches);
        }
      }
    }

    private bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out MatchCollection earlyMatches)
    {
      earlyMatches = null;
      bool endEarly = false;

      if (endEarlyRegex != null)
      {
        earlyMatches = endEarlyRegex.Matches(action);
        if (earlyMatches != null && earlyMatches.Count > 0 && TriggerUtil.CheckNumberOptions(options, earlyMatches))
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
          synth = new SpeechSynthesizer();
          synth.SetOutputToDefaultAudioDevice();
          player = new SoundPlayer();
          Trigger previous = null;

          while (await speechChannel.Reader.WaitToReadAsync())
          {
            var result = await speechChannel.Reader.ReadAsync();

            if (!string.IsNullOrEmpty(result.TextOrSound))
            {
              if (result.Trigger.Priority < previous?.Priority)
              {
                synth.SpeakAsyncCancelAll();
              }

              if (result.IsSound)
              {
                try
                {
                  if (File.Exists(@"data\sounds\" + result.TextOrSound))
                  {
                    player.SoundLocation = @"data\sounds\" + result.TextOrSound;
                    player.Play();
                  }
                }
                catch (Exception)
                {
                  // ignore
                }
              }
              else
              {
                var speak = ProcessSpeakDisplayText(result.TextOrSound, result.Matches);

                if (result.OriginalMatches != null)
                {
                  speak = ProcessSpeakDisplayText(speak, result.OriginalMatches);
                }

                if (!string.IsNullOrEmpty(CurrentVoice) && synth.Voice.Name != CurrentVoice)
                {
                  synth.SelectVoice(CurrentVoice);
                }

                if (CurrentVoiceRate != synth.Rate)
                {
                  synth.Rate = CurrentVoiceRate;
                }

                synth.SpeakAsync(speak);
                EventsAddText?.Invoke(this, new { Text = speak, result.Trigger });
              }

              previous = result.Trigger;
            }
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

    private void AddEntry(string line, Trigger trigger, string type, long eval = 0)
    {
      _ = Application.Current.Dispatcher.InvokeAsync(() =>
      {
        // update log
        var log = new ExpandoObject() as dynamic;
        log.Time = DateUtil.ToDouble(DateTime.Now);
        log.Line = line;
        log.Name = trigger.Name;
        log.Type = type;
        log.Eval = eval;
        log.Trigger = trigger;
        AlertLog.Insert(0, log);

        if (AlertLog.Count > 1000)
        {
          AlertLog.RemoveAt(AlertLog.Count - 1);
        }
      });
    }

    private string ProcessSpeakDisplayText(string text, MatchCollection matches)
    {
      if (matches != null && !string.IsNullOrEmpty(text))
      {
        foreach (Match match in matches)
        {
          for (int i = 1; i < match.Groups.Count; i++)
          {
            if (!string.IsNullOrEmpty(match.Groups[i].Name))
            {
              // try with and then without $ before {}
              text = text.Replace("${" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
              text = text.Replace("{" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
            }
          }
        }
      }

      return text;
    }

    private void StartTimer(TriggerWrapper wrapper, Channel<Speak> speechChannel, string line, MatchCollection matches)
    {
      Task.Run(() =>
      {
        // Restart Timer Option
        bool timerUpdated = false;
        if (wrapper.TriggerData.TriggerAgainOption == 1)
        {
          CleanupWrapper(wrapper);
          timerUpdated = true;
          EventsUpdateTimer?.Invoke(this, new { Trigger = wrapper.TriggerData, DisplayName = ProcessSpeakDisplayText(wrapper.ModifiedTimerName, matches) });
        }

        // Start a New independent Timer as long as one is not already running when Option 2 is selected
        // Option 2 is to Do Nothing when a 2nd timer is triggered so you onlu have the original timer running
        if (!(wrapper.TriggerData.TriggerAgainOption == 2 && wrapper.TimerCancellations.Count > 0))
        {
          CancellationTokenSource warningSource = null;
          if (wrapper.TriggerData.WarningSeconds > 0)
          {
            var diff = wrapper.TriggerData.DurationSeconds - wrapper.TriggerData.WarningSeconds;
            if (diff > 0)
            {
              warningSource = new CancellationTokenSource();

              lock (wrapper)
              {
                wrapper.WarningCancellations.Add(warningSource);
              }

              Task.Delay((int)diff * 1000).ContinueWith(task =>
              {
                var proceed = !warningSource.Token.IsCancellationRequested;

                lock (wrapper)
                {
                  if (wrapper.WarningCancellations.Contains(warningSource))
                  {
                    wrapper.WarningCancellations.Remove(warningSource);
                    warningSource?.Dispose();
                  }
                }

                if (proceed)
                {
                  var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out bool isSound);
                  speechChannel.Writer.WriteAsync(new Speak
                  {
                    Trigger = wrapper.TriggerData,
                    TextOrSound = speak,
                    IsSound = isSound
                  });

                  AddEntry(line, wrapper.TriggerData, "Timer Warning");
                }
              }, warningSource.Token);
            }
          }

          if (!timerUpdated)
          {
            EventsNewTimer?.Invoke(this, new { Trigger = wrapper.TriggerData, DisplayName = ProcessSpeakDisplayText(wrapper.ModifiedTimerName, matches) });
          }

          var timerSource = new CancellationTokenSource();

          lock (wrapper)
          {
            wrapper.TimerCancellations.Add(timerSource);
          }

          Task.Delay((int)wrapper.TriggerData.DurationSeconds * 1000).ContinueWith(task =>
          {
            var proceed = !timerSource.Token.IsCancellationRequested;

            lock (wrapper)
            {
              if (wrapper.WarningCancellations.Contains(warningSource))
              {
                wrapper.WarningCancellations.Remove(warningSource);
                warningSource?.Cancel();
                warningSource?.Dispose();
              }

              if (wrapper.TimerCancellations.Contains(timerSource))
              {
                wrapper.TimerCancellations.Remove(timerSource);
                timerSource?.Dispose();
              }
            }

            if (proceed)
            {
              var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out bool isSound);
              speechChannel.Writer.WriteAsync(new Speak
              {
                Trigger = wrapper.TriggerData,
                TextOrSound = speak,
                IsSound = isSound,
                OriginalMatches = wrapper.RegexMatches
              });

              AddEntry(line, wrapper.TriggerData, "Timer End");
            }
          }, timerSource.Token);
        }
      });
    }

    private void EventsLogLoadingComplete(object sender, bool e)
    {
      lock (LockObject)
      {
        if (LogChannel != null)
        {
          RequestRefresh();
        }
        else if (ConfigUtil.IfSetOrElse("TriggersEnabled", false))
        {
          Start();
        }
      }
    }

    private void UpdateTriggerTime(LinkedList<TriggerWrapper> activeTriggers, LinkedListNode<TriggerWrapper> node, double beginTime)
    {
      var previous = node.Value.TriggerData.LastTriggered;
      node.Value.TriggerData.LastTriggered = beginTime;

      // if no data yet then just move to front
      // next client restart will re-order everything
      if (previous == 0)
      {
        activeTriggers.Remove(node);
        activeTriggers.AddFirst(node);
      }
    }

    private void TriggerDataUpdated(object sender, EventArgs e)
    {
      TriggerUpdateTimer.Stop();

      if (TriggerUpdateTimer.Tag != null)
      {
        RequestRefresh();
        TriggerUpdateTimer.Tag = null;
      }

      SaveTriggers();
    }

    private void RequestRefresh()
    {
      if (RefreshTask == null || RefreshTask.IsCompleted)
      {
        RefreshTask = Task.Run(() =>
        {
          var updatedTriggers = GetActiveTriggers();
          lock (LockObject)
          {
            LogChannel?.Writer.WriteAsync(updatedTriggers);
          }
        });
      }
    }

    private LinkedList<TriggerWrapper> GetActiveTriggers()
    {
      var activeTriggers = new LinkedList<TriggerWrapper>();
      var enabledTriggers = new List<Trigger>();

      lock (TriggerNodes)
      {
        LoadActiveTriggers(TriggerNodes, enabledTriggers);
      }

      var playerName = ConfigUtil.PlayerName;
      foreach (var trigger in enabledTriggers.OrderByDescending(trigger => trigger.LastTriggered))
      {
        if (trigger.Pattern is string pattern && !string.IsNullOrEmpty(pattern))
        {
          try
          {
            var modifiedSpeak = string.IsNullOrEmpty(trigger.TextToSpeak) ? null :
              trigger.TextToSpeak.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);
            var modifiedEndSpeak = string.IsNullOrEmpty(trigger.EndTextToSpeak) ? null :
              trigger.EndTextToSpeak.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);
            var modifiedEndEarlySpeak = string.IsNullOrEmpty(trigger.EndEarlyTextToSpeak) ? null :
              trigger.EndEarlyTextToSpeak.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);
            var modifiedWarningSpeak = string.IsNullOrEmpty(trigger.WarningTextToSpeak) ? null :
              trigger.WarningTextToSpeak.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);
            var modifiedTimerName = string.IsNullOrEmpty(trigger.AltTimerName) ? trigger.Name :
              trigger.AltTimerName.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

            var wrapper = new TriggerWrapper
            {
              TriggerData = trigger,
              ModifiedSpeak = modifiedSpeak,
              ModifiedWarningSpeak = modifiedWarningSpeak,
              ModifiedEndSpeak = modifiedEndSpeak,
              ModifiedEndEarlySpeak = modifiedEndEarlySpeak,
              ModifiedTimerName = modifiedTimerName
            };

            pattern = UpdatePattern(trigger.UseRegex, playerName, pattern, out List<NumberOptions> numberOptions);

            if (trigger.UseRegex)
            {
              wrapper.Regex = new Regex(pattern, RegexOptions.IgnoreCase);
              wrapper.RegexNOptions = numberOptions;
            }
            else
            {
              wrapper.ModifiedPattern = pattern;
            }

            if (trigger.EnableTimer)
            {
              if (trigger.EndEarlyPattern is string endEarlyPattern && !string.IsNullOrEmpty(endEarlyPattern))
              {
                endEarlyPattern = UpdatePattern(trigger.EndUseRegex, playerName, endEarlyPattern, out List<NumberOptions> numberOptions2);

                if (trigger.EndUseRegex)
                {
                  wrapper.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase);
                  wrapper.EndEarlyRegexNOptions = numberOptions2;
                }
                else
                {
                  wrapper.ModifiedEndEarlyPattern = endEarlyPattern;
                }
              }

              if (trigger.EndEarlyPattern2 is string endEarlyPattern2 && !string.IsNullOrEmpty(endEarlyPattern2))
              {
                endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, playerName, endEarlyPattern2, out List<NumberOptions> numberOptions3);

                if (trigger.EndUseRegex2)
                {
                  wrapper.EndEarlyRegex2 = new Regex(endEarlyPattern2, RegexOptions.IgnoreCase);
                  wrapper.EndEarlyRegex2NOptions = numberOptions3;
                }
                else
                {
                  wrapper.ModifiedEndEarlyPattern2 = endEarlyPattern2;
                }
              }
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

    private void LoadActiveTriggers(TriggerNode data, List<Trigger> triggers)
    {
      if (data != null && data.Nodes != null && data.IsEnabled != false)
      {
        foreach (var node in data.Nodes)
        {
          if (node.TriggerData != null)
          {
            triggers.Add(node.TriggerData);
          }
          else if (node.OverlayData == null)
          {
            LoadActiveTriggers(node, triggers);
          }
        }
      }
    }

    private void SaveTriggers()
    {
      lock (TriggerNodes)
      {
        var json = JsonSerializer.Serialize(TriggerNodes, new JsonSerializerOptions { IncludeFields = true });
        ConfigUtil.WriteConfigFile(TRIGGERS_FILE, json);
      }
    }

    private void CleanupFirstTimer(TriggerWrapper wrapper)
    {
      lock (wrapper)
      {
        if (wrapper.TimerCancellations.Count > 0)
        {
          var firstTimerCancel = wrapper.TimerCancellations.First();
          firstTimerCancel.Cancel();
          firstTimerCancel.Dispose();
          wrapper.TimerCancellations.RemoveAt(0);
        }

        if (wrapper.WarningCancellations.Count > 0)
        {
          var firstWarningCancel = wrapper.WarningCancellations.First();
          firstWarningCancel.Cancel();
          firstWarningCancel.Dispose();
          wrapper.WarningCancellations.RemoveAt(0);
        }
      }
    }

    private void CleanupWrapper(TriggerWrapper wrapper)
    {
      lock (wrapper)
      {
        wrapper.TimerCancellations.ForEach(source => source.Cancel());
        wrapper.WarningCancellations.ForEach(source => source.Cancel());
        wrapper.TimerCancellations.ForEach(source => source.Dispose());
        wrapper.WarningCancellations.ForEach(source => source.Dispose());
        wrapper.TimerCancellations.Clear();
        wrapper.WarningCancellations.Clear();
      }
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
        else if (Regex.Matches(pattern, @"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase) is MatchCollection matches2 && matches2.Count > 0)
        {
          foreach (Match match in matches2)
          {
            if (match.Groups.Count == 4)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + @">\d+)");

              if (!string.IsNullOrEmpty(match.Groups[2].Value) && !string.IsNullOrEmpty(match.Groups[3].Value) &&
                uint.TryParse(match.Groups[3].Value, out uint value))
              {
                numberOptions.Add(new NumberOptions { Key = match.Groups[1].Value, Op = match.Groups[2].Value, Value = value });
              }
            }
          }
        }
      }

      return pattern;
    }

    private class Speak
    {
      public Trigger Trigger { get; set; }
      public string TextOrSound { get; set; }
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

    private class TriggerWrapper
    {
      public List<CancellationTokenSource> TimerCancellations { get; } = new List<CancellationTokenSource>();
      public List<CancellationTokenSource> WarningCancellations { get; } = new List<CancellationTokenSource>();
      public string ModifiedSpeak { get; set; }
      public string ModifiedPattern { get; set; }
      public string ModifiedEndSpeak { get; set; }
      public string ModifiedEndEarlySpeak { get; set; }
      public string ModifiedWarningSpeak { get; set; }
      public string ModifiedEndEarlyPattern { get; set; }
      public string ModifiedEndEarlyPattern2 { get; set; }
      public string ModifiedTimerName { get; set; }
      public Regex Regex { get; set; }
      public Regex EndEarlyRegex { get; set; }
      public Regex EndEarlyRegex2 { get; set; }
      public List<NumberOptions> RegexNOptions { get; set; }
      public List<NumberOptions> EndEarlyRegexNOptions { get; set; }
      public List<NumberOptions> EndEarlyRegex2NOptions { get; set; }
      public MatchCollection RegexMatches { get; set; }
      public Trigger TriggerData { get; set; }
    }
  }
}

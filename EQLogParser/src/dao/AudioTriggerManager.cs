using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class AudioTriggerManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    internal event EventHandler<bool> EventsUpdateTree;
    internal static AudioTriggerManager Instance = new AudioTriggerManager();
    private readonly string TRIGGERS_FILE = "audioTriggers.json";
    private readonly DispatcherTimer UpdateTimer;
    private readonly AudioTriggerData Data;
    private Channel<dynamic> LogChannel = null;
    private Task RefreshTask = null;
    private static object LockObject = new object();

    public AudioTriggerManager()
    {
      var jsonString = ConfigUtil.ReadConfigFile(TRIGGERS_FILE);
      if (jsonString != null)
      {
        Data = JsonSerializer.Deserialize<AudioTriggerData>(jsonString, new JsonSerializerOptions { IncludeFields = true });
      }
      else
      {
        Data = new AudioTriggerData();
      }

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
      UpdateTimer.Tick += DataUpdated;
    }

    internal void Init() => (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;

    internal void AddAction(LineData lineData)
    {
      lock (LockObject)
      {
        LogChannel?.Writer.WriteAsync(lineData);
      }

      if (!double.IsNaN(lineData.BeginTime) && ConfigUtil.IfSetOrElse("AudioTriggersWatchForGINA", false))
      {
        GINAXmlParser.CheckGina(lineData);
      }
    }

    internal AudioTriggerTreeViewNode GetTreeView()
    {
      var result = new AudioTriggerTreeViewNode { Content = "All Audio Triggers", IsChecked = Data.IsEnabled, IsTrigger = false, IsExpanded = Data.IsExpanded };
      result.SerializedData = Data;

      lock (Data)
      {
        AudioTriggerUtil.AddTreeNodes(Data.Nodes, result);
      }

      return result;
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

    internal void MergeTriggers(AudioTriggerData newTriggers, AudioTriggerData parent = null)
    {
      if (newTriggers != null)
      {
        lock (Data)
        {
          AudioTriggerUtil.MergeNodes(newTriggers.Nodes, (parent == null) ? Data : parent);
          SaveTriggers();
        }

        RequestRefresh();
        EventsUpdateTree?.Invoke(this, true);
      }
    }

    internal void Update(bool needRefresh = true)
    {
      UpdateTimer.Stop();
      UpdateTimer.Start();

      if (needRefresh)
      {
        UpdateTimer.Tag = needRefresh;
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Start()
    {
      Channel<dynamic> channel;

      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        channel = LogChannel = Channel.CreateUnbounded<dynamic>();
      }

      _ = Task.Run(async () =>
      {
        LinkedList<TriggerWrapper> activeTriggers = null;
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();

        try
        {
          activeTriggers = GetActiveTriggers();
          AudioTrigger previous = null;

          while (await channel.Reader.WaitToReadAsync())
          {
            var result = await channel.Reader.ReadAsync();

            if (result is LinkedList<TriggerWrapper> updatedTriggers)
            {
              activeTriggers.ForEach(wrapper =>
              {
                lock (wrapper)
                {
                  CleanupWrapper(wrapper);
                }
              });

              activeTriggers = updatedTriggers;
            }
            else if (result is LineData lineData)
            {
              var action = lineData.Action;
              var node = activeTriggers.First;
              long totalTime = 0;
              while (node != null)
              {
                // save since the nodes may get reordered
                var nextNode = node.Next;
                bool found = false;
                long start = DateTime.Now.Ticks;
                MatchCollection matches = null;
                var wrapper = node.Value;

                if (wrapper.Regex != null)
                {
                  matches = wrapper.Regex.Matches(action);
                  if (matches != null && matches.Count > 0)
                  {
                    found = true;
                    wrapper.TriggerData.LastTriggered = DateUtil.ToDouble(DateTime.Now);
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

                lock (wrapper)
                {
                  if (wrapper.TimerCancellation != null)
                  {
                    bool endEarly = false;
                    if (wrapper.EndEarlyRegex != null)
                    {
                      var earlyMatches = wrapper.EndEarlyRegex.Matches(action);
                      if (earlyMatches != null && earlyMatches.Count > 0)
                      {
                        endEarly = true;
                      }
                    }
                    else if (!string.IsNullOrEmpty(wrapper.ModifiedEndEarlyPattern))
                    {
                      if (action.Contains(wrapper.ModifiedEndEarlyPattern, StringComparison.OrdinalIgnoreCase))
                      {
                        endEarly = true;
                      }
                    }

                    if (endEarly)
                    {
                      CleanupWrapper(wrapper);

                      if (wrapper.TriggerData.Priority < previous?.Priority)
                      {
                        synth?.SpeakAsyncCancelAll();
                      }

                      synth?.SpeakAsync(wrapper.ModifiedEndSpeak);
                    }
                  }
                }

                if (found && !string.IsNullOrEmpty(wrapper.ModifiedSpeak))
                {
                  if (wrapper.TriggerData.Priority < previous?.Priority)
                  {
                    synth.SpeakAsyncCancelAll();
                  }

                  var speak = wrapper.ModifiedSpeak;
                  if (matches != null)
                  {
                    matches.ForEach(match =>
                    {
                      for (int i = 1; i < match.Groups.Count; i++)
                      {
                        if (!string.IsNullOrEmpty(match.Groups[i].Name) && Regex.IsMatch(match.Groups[i].Name, @"s\d?", RegexOptions.IgnoreCase))
                        {
                          speak = speak.Replace("{" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
                        }
                      }
                    });
                  }

                  previous = wrapper.TriggerData;
                  synth.SpeakAsync(speak);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                  if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.DurationSeconds > 0 &&
                    !string.IsNullOrEmpty(wrapper.ModifiedEndSpeak))
                  {
                    lock (wrapper)
                    {
                      wrapper.TimerCancellation = new CancellationTokenSource();
                      Task.Delay((int)wrapper.TriggerData.DurationSeconds * 1000).ContinueWith(task =>
                      {
                        var proceed = true;

                        lock (wrapper)
                        {
                          proceed = !wrapper.TimerCancellation.Token.IsCancellationRequested;
                          CleanupWrapper(wrapper, false);
                        }

                        if (proceed)
                        {
                          if (wrapper.TriggerData.Priority < previous?.Priority)
                          {
                            synth?.SpeakAsyncCancelAll();
                          }

                          synth?.SpeakAsync(wrapper.ModifiedEndSpeak);
                        }
                      }, wrapper.TimerCancellation.Token);
                    }

                    if (wrapper.TriggerData.WarningSeconds > 0 && !string.IsNullOrEmpty(wrapper.ModifiedWarningSpeak))
                    {
                      var diff = wrapper.TriggerData.DurationSeconds - wrapper.TriggerData.WarningSeconds;
                      if (diff > 0)
                      {
                        wrapper.WarningCancellation = new CancellationTokenSource();
                        Task.Delay((int)diff * 1000).ContinueWith(task =>
                        {
                          var proceed = true;

                          lock (wrapper)
                          {
                            proceed = !wrapper.WarningCancellation.Token.IsCancellationRequested;
                            wrapper.WarningCancellation?.Dispose();
                            wrapper.WarningCancellation = null;
                          }

                          if (proceed)
                          {
                            if (wrapper.TriggerData.Priority < previous?.Priority)
                            {
                              synth?.SpeakAsyncCancelAll();
                            }

                            synth?.SpeakAsync(wrapper.ModifiedWarningSpeak);
                          }
                        }, wrapper.WarningCancellation.Token);
                      }
                    }
                  }
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }

                var time = (long)((DateTime.Now.Ticks - start) / 10);
                totalTime += time;
                wrapper.TriggerData.LongestEvalTime = Math.Max(time, wrapper.TriggerData.LongestEvalTime);
                node = nextNode;
              }

              if (totalTime > 50000)
              {
                LOG.Warn("Warning. Slow Audio Trigger execution time of " + (totalTime / 1000) + " milliseconds.");
              }
            }
          }
        }
        catch (Exception)
        {
          // channel closed
        }

        activeTriggers?.ForEach(wrapper =>
        {
          lock (wrapper)
          {
            CleanupWrapper(wrapper);
          }
        });

        synth.Dispose();
        synth = null;
      });

      (Application.Current.MainWindow as MainWindow).ShowAudioTriggers(true);
      ConfigUtil.SetSetting("AudioTriggersEnabled", true.ToString(CultureInfo.CurrentCulture));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Stop(bool save = true)
    {
      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        LogChannel = null;
      }

      SaveTriggers();
      (Application.Current.MainWindow as MainWindow)?.ShowAudioTriggers(false);

      if (save)
      {
        ConfigUtil.SetSetting("AudioTriggersEnabled", false.ToString(CultureInfo.CurrentCulture));
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
        else if (ConfigUtil.IfSetOrElse("AudioTriggersEnabled", false))
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

    private void DataUpdated(object sender, EventArgs e)
    {
      UpdateTimer.Stop();

      if (UpdateTimer.Tag != null)
      {
        RequestRefresh();
        UpdateTimer.Tag = null;
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
      var enabledTriggers = new List<AudioTrigger>();

      lock (Data)
      {
        LoadActive(Data, enabledTriggers);
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
            var modifiedWarningSpeak = string.IsNullOrEmpty(trigger.WarningTextToSpeak) ? null :
              trigger.WarningTextToSpeak.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

            var wrapper = new TriggerWrapper
            {
              TriggerData = trigger,
              ModifiedSpeak = modifiedSpeak,
              ModifiedWarningSpeak = modifiedWarningSpeak,
              ModifiedEndSpeak = modifiedEndSpeak
            };

            pattern = AudioTriggerUtil.UpdatePattern(trigger.UseRegex, playerName, pattern);

            if (trigger.UseRegex)
            {
              wrapper.Regex = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            else
            {
              wrapper.ModifiedPattern = pattern;
            }

            if (trigger.EnableTimer)
            {
              if (trigger.EndEarlyPattern is string endEarlyPattern && !string.IsNullOrEmpty(endEarlyPattern))
              {
                endEarlyPattern = AudioTriggerUtil.UpdatePattern(trigger.EndUseRegex, playerName, endEarlyPattern);

                if (trigger.EndUseRegex)
                {
                  wrapper.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase);
                }
                else
                {
                  wrapper.ModifiedEndEarlyPattern = endEarlyPattern;
                }
              }
            }

            activeTriggers.AddLast(new LinkedListNode<TriggerWrapper>(wrapper));
          }
          catch (Exception ex)
          {
            LOG.Debug("Bad Audio Trigger?", ex);
          }
        }
      }

      return activeTriggers;
    }

    private void LoadActive(AudioTriggerData data, List<AudioTrigger> triggers)
    {
      if (data != null && data.Nodes != null && data.IsEnabled != false)
      {
        foreach (var node in data.Nodes)
        {
          if (node.TriggerData != null)
          {
            triggers.Add(node.TriggerData);
          }
          else
          {
            LoadActive(node, triggers);
          }
        }
      }
    }

    private void SaveTriggers()
    {
      lock (Data)
      {
        var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { IncludeFields = true });
        ConfigUtil.WriteConfigFile(TRIGGERS_FILE, json);
      }
    }

    private void CleanupWrapper(TriggerWrapper wrapper, bool cancel = true)
    {
      if (cancel)
      {
        wrapper.TimerCancellation?.Cancel();
        wrapper.WarningCancellation?.Cancel();
      }

      wrapper.TimerCancellation?.Dispose();
      wrapper.WarningCancellation?.Dispose();
      wrapper.TimerCancellation = null;
      wrapper.WarningCancellation = null;
    }

    private class TriggerWrapper
    {
      public CancellationTokenSource TimerCancellation { get; set; }
      public CancellationTokenSource WarningCancellation { get; set; }
      public string ModifiedSpeak { get; set; }
      public string ModifiedPattern { get; set; }
      public string ModifiedEndSpeak { get; set; }
      public string ModifiedWarningSpeak { get; set; }
      public string ModifiedEndEarlyPattern { get; set; }
      public Regex Regex { get; set; }
      public Regex EndEarlyRegex { get; set; }
      public AudioTrigger TriggerData { get; set; }
    }
  }
}

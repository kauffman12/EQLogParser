using Syncfusion.Data.Extensions;
using System;
using System.Collections.Concurrent;
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

    internal void MergeTriggers(AudioTriggerData newTriggers, string newFolder)
    {
      newFolder += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
      newTriggers.Name = newFolder;

      lock (Data)
      {
        Data.Nodes.Add(newTriggers);
        SaveTriggers();
      }

      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void MergeTriggers(AudioTriggerData newTriggers, AudioTriggerData parent = null)
    {
      lock (Data)
      {
        AudioTriggerUtil.MergeNodes(newTriggers.Nodes, (parent == null) ? Data : parent);
        SaveTriggers();
      }

      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
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
              activeTriggers.ForEach(wrapper => CleanupWrapper(wrapper));
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
                  lowPriChannel.Writer.WriteAsync(new LowPriData { ActiveTriggers= activeTriggers, LineData = lineData,
                    Node = node, SpeechChannel = speechChannel});
                }

                node = nextNode;
              }
            }
          }
        }
        catch (Exception)
        {
          // channel closed
        }

        lowPriChannel?.Writer.Complete();
        speechChannel?.Writer.Complete();
        activeTriggers?.ForEach(wrapper => CleanupWrapper(wrapper));
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

    private void HandleTrigger(LinkedList<TriggerWrapper> activeTriggers, LinkedListNode<TriggerWrapper> node,
      LineData lineData, Channel<Speak> speechChannel)
    {
      long start = DateTime.Now.Ticks;
      var action = lineData.Action;
      var wrapper = node.Value;
      MatchCollection matches = null;
      bool found = false;

      if (wrapper.Regex != null)
      {
        matches = wrapper.Regex.Matches(action);
        if (matches != null && matches.Count > 0)
        {
          found = true;
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
        MatchCollection earlyMatches = null;
        bool endEarly = false;
        if (wrapper.EndEarlyRegex != null)
        {
          earlyMatches = wrapper.EndEarlyRegex.Matches(action);
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
          speechChannel.Writer.WriteAsync(new Speak { TriggerData = wrapper.TriggerData, Text = wrapper.ModifiedEndSpeak, Matches = earlyMatches });
        }
      }

      if (found)
      {
        var speak = wrapper.ModifiedSpeak;
        if (!string.IsNullOrEmpty(speak))
        {
          speechChannel.Writer.WriteAsync(new Speak { TriggerData = wrapper.TriggerData, Text = speak, Matches = matches });
        }

        if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.DurationSeconds > 0)
        {
          StartTimer(wrapper, speechChannel);
        }

        var time = (long)((DateTime.Now.Ticks - start) / 10);
        wrapper.TriggerData.LongestEvalTime = Math.Max(time, wrapper.TriggerData.LongestEvalTime);
      }
    }

    private void StartSpeechReader(Channel<Speak> speechChannel)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          var synth = new SpeechSynthesizer();
          synth.Rate = 1;
          synth.SetOutputToDefaultAudioDevice();
          synth.SelectVoiceByHints(VoiceGender.Female);
          AudioTrigger previous = null;

          while (await speechChannel.Reader.WaitToReadAsync())
          {
            var result = await speechChannel.Reader.ReadAsync();

            if (!string.IsNullOrEmpty(result.Text))
            {
              if (result.TriggerData.Priority < previous?.Priority)
              {
                synth.SpeakAsyncCancelAll();
              }

              var speak = result.Text;
              if (result.Matches != null)
              {
                result.Matches.ForEach(match =>
                {
                  for (int i = 1; i < match.Groups.Count; i++)
                  {
                    if (!string.IsNullOrEmpty(match.Groups[i].Name))
                    {
                      // try with and then without $ before {}
                      speak = speak.Replace("${" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
                      speak = speak.Replace("{" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
                    }
                  }
                });
              }

              synth.SpeakAsync(speak);
              previous = result.TriggerData;
            }
          }
        }
        catch (Exception)
        {
          // channel closed
        }
      });
    }

    private void StartTimer(TriggerWrapper wrapper, Channel<Speak> speechChannel)
    {
      Task.Run(() =>
      {
        // Restart Timer Option
        if (wrapper.TriggerData.TriggerAgainOption == 1)
        {
          CleanupWrapper(wrapper);
        }

        // Start a New independent Timer as long as one is not already running when Option 2 is selected
        // Option 2 is to Do Nothing when a 2nd timer is triggered so you onlu have the original timer running
        if (!(wrapper.TriggerData.TriggerAgainOption == 2 && wrapper.TimerCancellations.Count > 0))
        {
          CancellationTokenSource warningSource = null;
          if (wrapper.TriggerData.WarningSeconds > 0 && !string.IsNullOrEmpty(wrapper.ModifiedWarningSpeak))
          {
            var diff = wrapper.TriggerData.DurationSeconds - wrapper.TriggerData.WarningSeconds;
            if (diff > 0)
            {
              warningSource = new CancellationTokenSource();
              wrapper.WarningCancellations[warningSource] = true;
              Task.Delay((int)diff * 1000).ContinueWith(task =>
              {
                var proceed = !warningSource.Token.IsCancellationRequested;
                if (wrapper.WarningCancellations.TryRemove(warningSource, out bool _))
                {
                  warningSource?.Dispose();
                }

                if (proceed)
                {
                  speechChannel.Writer.WriteAsync(new Speak { TriggerData = wrapper.TriggerData, Text = wrapper.ModifiedWarningSpeak });
                }
              }, warningSource.Token);
            }
          }

          var timerSource = new CancellationTokenSource();
          wrapper.TimerCancellations[timerSource] = true;
          Task.Delay((int)wrapper.TriggerData.DurationSeconds * 1000).ContinueWith(task =>
          {
            if (wrapper.WarningCancellations.TryRemove(warningSource, out bool _))
            {
              warningSource?.Cancel();
              warningSource?.Dispose();
            }

            var proceed = !timerSource.Token.IsCancellationRequested;
            if (wrapper.TimerCancellations.TryRemove(timerSource, out bool _))
            {
              timerSource?.Dispose();
            }

            if (proceed)
            {
              speechChannel.Writer.WriteAsync(new Speak { TriggerData = wrapper.TriggerData, Text = wrapper.ModifiedEndSpeak });
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

    private void CleanupWrapper(TriggerWrapper wrapper)
    {
      wrapper.TimerCancellations.Keys.ForEach(source => source.Cancel());
      wrapper.WarningCancellations.Keys.ForEach(source => source.Cancel());
      wrapper.TimerCancellations.Keys.ForEach(source => source.Dispose());
      wrapper.WarningCancellations.Keys.ForEach(source => source.Dispose());
      wrapper.TimerCancellations.Clear();
      wrapper.WarningCancellations.Clear();
    }

    private class Speak
    {
      public AudioTrigger TriggerData { get; set; }
      public string Text { get; set; }
      public MatchCollection Matches { get; set; }
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
      public ConcurrentDictionary<CancellationTokenSource, bool> TimerCancellations { get; } = new ConcurrentDictionary<CancellationTokenSource, bool>();
      public ConcurrentDictionary<CancellationTokenSource, bool> WarningCancellations { get; } = new ConcurrentDictionary<CancellationTokenSource, bool>();
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

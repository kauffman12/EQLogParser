using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private Channel<LineData> LogChannel = null;
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

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 2) };
      UpdateTimer.Tick += DataUpdated;
    }

    internal void Init()
    {
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Start()
    {
      Channel<LineData> channel;

      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        channel = LogChannel = Channel.CreateUnbounded<LineData>();
      }

      Task.Run(async () =>
      {
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();

        try
        {
          var activeTriggers = GetActiveTriggers();
          AudioTrigger previous = null;

          while (await channel.Reader.WaitToReadAsync())
          {
            var lineData = await channel.Reader.ReadAsync();
            var action = lineData.Action;

            // reload triggers
            if (lineData.BeginTime == 0 && action == "Reload-Triggers")
            {
              activeTriggers = GetActiveTriggers();
            }
            else
            {
              var node = activeTriggers.First;
              while (node != null)
              {
                bool found = false;
                MatchCollection matches = null;
                if (node.Value.Regex != null)
                {
                  matches = node.Value.Regex.Matches(action);
                  if (matches != null && matches.Count > 0)
                  {
                    found = true;
                    node.Value.TriggerData.LastTriggered = DateUtil.ToDouble(DateTime.Now);
                    UpdateTriggerTime(activeTriggers, node, lineData.BeginTime);
                  }
                }
                else if (!string.IsNullOrEmpty(node.Value.ModifiedPattern))
                {
                  if (action.Contains(node.Value.ModifiedPattern, StringComparison.OrdinalIgnoreCase))
                  {
                    found = true;
                    UpdateTriggerTime(activeTriggers, node, lineData.BeginTime);
                  }
                }

                if (found && !string.IsNullOrEmpty(node.Value.ModifiedSpeak))
                {
                  if (previous != null && node.Value.TriggerData.Priority < previous.Priority)
                  {
                    synth.SpeakAsyncCancelAll();
                  }

                  var speak = node.Value.ModifiedSpeak;
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

                  previous = node.Value.TriggerData;
                  synth.SpeakAsync(speak);
                }

                node = node.Next;
              }
            }
          }
        }
        catch (Exception)
        {
          // channel closed
        }

        synth.Dispose();
      });

      (Application.Current.MainWindow as MainWindow).ShowAudioTriggers(true);
      ConfigUtil.SetSetting("AudioTriggersEnabled", true.ToString(CultureInfo.CurrentCulture));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Stop(bool save = false)
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

    internal void Update(bool needRefresh = true)
    {
      UpdateTimer.Stop();
      UpdateTimer.Start();

      if (needRefresh)
      {
        UpdateTimer.Tag = needRefresh;
      }
    }

    internal AudioTriggerTreeViewNode GetTreeView()
    {
      var result = new AudioTriggerTreeViewNode { Content = "All Audio Triggers", IsChecked = Data.IsEnabled, IsTrigger = false, IsExpanded = Data.IsExpanded };
      result.SerializedData = Data;

      lock (Data)
      {
        AddTreeNodes(Data.Nodes, result);
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

    internal void MergeTriggers(AudioTriggerData newTriggers)
    {
      if (newTriggers != null)
      {
        lock (Data)
        {
          MergeNodes(newTriggers.Nodes, Data);
          SaveTriggers();
        }

        RequestRefresh();
        EventsUpdateTree?.Invoke(this, true);
      }
    }

    private void AddTreeNodes(List<AudioTriggerData> nodes, AudioTriggerTreeViewNode treeNode)
    {
      if (nodes != null)
      {
        foreach (var node in nodes)
        {
          var child = new AudioTriggerTreeViewNode { Content = node.Name, SerializedData = node };
          if (node.TriggerData != null)
          {
            child.IsTrigger = true;
            treeNode.ChildNodes.Add(child);
          }
          else
          {
            child.IsChecked = node.IsEnabled;
            child.IsExpanded = node.IsExpanded;
            child.IsTrigger = false;
            treeNode.ChildNodes.Add(child);
            AddTreeNodes(node.Nodes, child);
          }
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
      lock (LockObject)
      {
        LogChannel?.Writer.WriteAsync(new LineData { Action = "Reload-Triggers" });
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
        if (!string.IsNullOrEmpty(trigger.Pattern))
        {
          try
          {
            var pattern = trigger.Pattern;
            pattern = pattern.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

            if (trigger.UseRegex && Regex.Matches(pattern, @"{(s\d?)}", RegexOptions.IgnoreCase) is MatchCollection matches && matches.Count > 0)
            {
              matches.ForEach(match =>
              {
                if (match.Groups.Count > 1)
                {
                  pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
                }
              });
            }

            var modifiedSpeak = string.IsNullOrEmpty(trigger.Speak) ? null : trigger.Speak.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);
            var wrapper = new TriggerWrapper { TriggerData = trigger, ModifiedSpeak = modifiedSpeak };
            if (trigger.UseRegex)
            {
              if (trigger.Warnings != "Invalid Regex")
              {
                wrapper.Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                activeTriggers.AddLast(new LinkedListNode<TriggerWrapper>(wrapper));
              }
            }
            else
            {
              wrapper.ModifiedPattern = pattern;
              activeTriggers.AddLast(new LinkedListNode<TriggerWrapper>(wrapper));
            }
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

    private void MergeNodes(List<AudioTriggerData> newNodes, AudioTriggerData parent)
    {
      if (newNodes != null)
      {
        if (parent.Nodes == null)
        {
          parent.Nodes = newNodes;
        }
        else
        {
          var needsSort = new List<AudioTriggerData>();
          foreach (var newNode in newNodes)
          {
            var found = parent.Nodes.Find(node => node.Name == newNode.Name);

            if (found != null)
            {
              if (newNode.TriggerData != null && found.TriggerData != null)
              {
                found.TriggerData.UseRegex = newNode.TriggerData.UseRegex;
                found.TriggerData.Pattern = newNode.TriggerData.Pattern;
                found.TriggerData.Speak = newNode.TriggerData.Speak;
                found.TriggerData.Priority = newNode.TriggerData.Priority;
              }
              else
              {
                MergeNodes(newNode.Nodes, found);
              }
            }
            else
            {
              parent.Nodes.Add(newNode);
              needsSort.Add(parent);
            }
          }

          needsSort.ForEach(parent => parent.Nodes = parent.Nodes.OrderBy(node => node.Name).ToList());
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

    private class TriggerWrapper
    {
      public string ModifiedSpeak { get; set; }
      public string ModifiedPattern { get; set; }
      public Regex Regex { get; set; }
      public AudioTrigger TriggerData { get; set; }
    }
  }
}

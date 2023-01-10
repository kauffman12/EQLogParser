using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class AudioTriggerManager
  {
    internal event EventHandler<bool> EventsUpdateTree;
    internal static AudioTriggerManager Instance = new AudioTriggerManager();
    private readonly string TRIGGERS_FILE = "audioTriggers.json";
    private readonly DispatcherTimer UpdateTimer;
    private readonly AudioTriggerData Data;
    private Channel<string> LogChannel;
    private bool Running = false;

    public AudioTriggerManager()
    {
      var jsonString = ConfigUtil.ReadConfigFile(TRIGGERS_FILE);
      if (jsonString != null)
      {
        Data = JsonSerializer.Deserialize<AudioTriggerData>(jsonString, new JsonSerializerOptions { IncludeFields = true });
      }

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 2000) };
      UpdateTimer.Tick += (sender, e) => SaveTriggers();
    }

    internal void AddAction(string action, double dateTime)
    {
      if (Running && LogChannel != null)
      {
        LogChannel.Writer.WriteAsync(action);
      }

      GINAXmlParser.CheckGina(action, dateTime);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Start()
    {
      if (LogChannel != null)
      {
        LogChannel.Writer.Complete();
      }

      var activeTriggers = new LinkedList<TriggerWrapper>();
      var enabledTriggers = new List<AudioTrigger>();

      lock (Data)
      {
        LoadActive(Data, enabledTriggers);
      }

      foreach (var trigger in enabledTriggers.OrderByDescending(trigger => trigger.LastTriggered))
      {
        if (!string.IsNullOrEmpty(trigger.Pattern))
        {
          var pattern = trigger.Pattern;
          pattern = pattern.Replace("{c}", ConfigUtil.PlayerName);
          pattern = pattern.Replace("{C}", ConfigUtil.PlayerName);

          var wrapper = new TriggerWrapper { TriggerData = trigger };
          if (trigger.UseRegex)
          {
            wrapper.TriggerRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
          }
          else
          {
            wrapper.TriggerPattern = pattern;
          }

          activeTriggers.AddLast(new LinkedListNode<TriggerWrapper>(wrapper));
        }
      }

      LogChannel = Channel.CreateUnbounded<string>();

      Task.Run(async () =>
      {
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();

        try
        {
          while (await LogChannel.Reader.WaitToReadAsync())
          {
            var action = await LogChannel.Reader.ReadAsync();
            var node = activeTriggers.First;
            while (node != null)
            {
              bool found = false;
              if (node.Value.TriggerRegex != null)
              {
                if (node.Value.TriggerRegex.IsMatch(action))
                {
                  found = true;
                  node.Value.TriggerData.LastTriggered = DateUtil.ToDouble(DateTime.Now);
                }
              }
              else if (!string.IsNullOrEmpty(node.Value.TriggerPattern))
              {
                if (action.Contains(node.Value.TriggerPattern, StringComparison.OrdinalIgnoreCase))
                {
                  found = true;
                  node.Value.TriggerData.LastTriggered = DateUtil.ToDouble(DateTime.Now);
                }
              }

              if (found && !string.IsNullOrEmpty(node.Value.TriggerData.Speak))
              {
                synth.Speak(node.Value.TriggerData.Speak);
              }

              node = node.Next;
            }
          }
        }
        catch (Exception)
        {
          // channel closed
        }

        synth.Dispose();
      });

      Running = true;

      //var pat = "^Stones form from shadows in the {s} corners of the room\\.$";
      //var pat = "^Stones form from shadows in the (?<s>.*) corners of the room\\.$";
      //var regex = new Regex(pat, RegexOptions.Compiled);
      //var matches = regex.Matches("Stones form from shadows in the north western corners of the room.");

      // Speak a string.  
      //Synth.Speak("Starting the TTS Service!");
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Stop()
    {
      Running = false;

      if (LogChannel != null)
      {
        LogChannel.Writer.Complete();
        LogChannel = null;
      }

      SaveTriggers();
    }

    internal void Update()
    {
      UpdateTimer.Stop();
      UpdateTimer.Start();
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

    internal void MergeTriggers(AudioTriggerData newTriggers)
    {
      if (newTriggers != null)
      {
        lock (Data)
        {
          MergeNodes(newTriggers.Nodes, Data);
          SaveTriggers();
        }

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
          foreach (var newNode in newNodes)
          {
            var found = parent.Nodes.Find(node => node.Name == newNode.Name);

            if (found != null)
            {
              if (newNode.TriggerData != null && found.TriggerData != null)
              {
                found.TriggerData = newNode.TriggerData;
              }
              else
              {
                MergeNodes(newNode.Nodes, found);
              }
            }
            else
            {
              parent.Nodes.Add(newNode);
            }
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

    private class TriggerWrapper
    {
      public string TriggerPattern { get; set; }
      public Regex TriggerRegex { get; set; }
      public AudioTrigger TriggerData { get; set; }
    }
  }
}

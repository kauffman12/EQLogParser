using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class AudioTriggerManager
  {
    internal event EventHandler<bool> EventsUpdateTree;
    internal static AudioTriggerManager Instance = new AudioTriggerManager();
    private readonly string TRIGGERS_FILE = "audioTriggers.json";
    private readonly DispatcherTimer UpdateTimer;
    private AudioTriggerData Data = new AudioTriggerData();
    private SpeechSynthesizer Synth;
    private List<AudioTrigger> ActiveTriggers;
   
    public AudioTriggerManager()
    {
      LoadTriggers();
      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 2000) };
      UpdateTimer.Tick += (sender, e) => DoUpdate();
    }

    internal void Start()
    {
      Synth = new SpeechSynthesizer();
      Synth.SetOutputToDefaultAudioDevice();
      var list = new List<AudioTrigger>();

      lock (Data)
      {
        LoadActive(Data, list);
      }

      ActiveTriggers = list.OrderByDescending(trigger => trigger.LastTriggered).ToList();

      // Speak a string.  
      //Synth.Speak("Starting the TTS Service!");
    }

    internal void Stop()
    {
      Synth.Dispose();
    }

    internal void Update()
    {
      UpdateTimer.Stop();
      UpdateTimer.Start();
    }

    internal AudioTriggerTreeViewNode GetTreeView()
    {
      var result = new AudioTriggerTreeViewNode { Content = "All Audio Triggers", IsChecked = Data.IsEnabled, IsTrigger = false };
      result.SerializedData = Data;

      lock (Data)
      {
        AddTreeNodes(Data.Nodes, result);
        AddAudioTriggers(Data.Triggers, result);
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
          MergeTriggers(newTriggers.Triggers, Data);
        }

        EventsUpdateTree?.Invoke(this, true);
      }
    }

    private void AddAudioTriggers(List<AudioTrigger> triggers, AudioTriggerTreeViewNode treeNode)
    {
      if (triggers != null)
      {
        foreach (var trigger in triggers)
        {
          var child = new AudioTriggerTreeViewNode { Content = trigger.Name, IsTrigger = true, TriggerData = trigger };
          treeNode.ChildNodes.Add(child);
        }
      }
    }

    private void AddTreeNodes(List<AudioTriggerData> nodes, AudioTriggerTreeViewNode treeNode)
    {
      if (nodes != null)
      {
        foreach (var node in nodes)
        {
          var child = new AudioTriggerTreeViewNode { Content = node.Name, IsChecked = node.IsEnabled, IsTrigger = false, SerializedData = node };
          treeNode.ChildNodes.Add(child);
          AddTreeNodes(node.Nodes, child);
          AddAudioTriggers(node.Triggers, child);
        }
      }
    }

    private void LoadActive(AudioTriggerData data, List<AudioTrigger> triggers)
    {
      if (data.Triggers != null && data.IsEnabled != false)
      {
        data.Triggers.ForEach(trigger => triggers.Add(trigger));
      }

      if (data.Nodes != null)
      {
        foreach (var node in data.Nodes)
        {
          LoadActive(node, triggers);
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
              MergeNodes(newNode.Nodes, found);
              MergeTriggers(newNode.Triggers, found);
            }
            else
            {
              parent.Nodes.Add(newNode);
            }
          }
        }
      }
    }

    private void MergeTriggers(List<AudioTrigger> newTriggers, AudioTriggerData parent)
    {
      if (newTriggers != null)
      {
        if (parent.Triggers == null)
        {
          parent.Triggers = newTriggers;
        }
        else
        {
          foreach (var newTrigger in newTriggers)
          {
            var found = parent.Triggers.Find(trigger => trigger.Name == newTrigger.Name);

            if (found != null)
            {
              found.Pattern = newTrigger.Pattern;
              found.Speak = newTrigger.Speak;
              found.UseRegex = newTrigger.UseRegex;
            }
            else
            {
              parent.Triggers.Add(newTrigger);
            }
          }
        }
      }
    }

    private void DoUpdate()
    {
      lock (Data)
      {
        var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { IncludeFields = true });
        ConfigUtil.WriteConfigFile(TRIGGERS_FILE, json);
      }
    }

    private void LoadTriggers()
    {
      var jsonString = ConfigUtil.ReadConfigFile(TRIGGERS_FILE);
      if (jsonString != null)
      {
        Data = JsonSerializer.Deserialize<AudioTriggerData>(jsonString);
      }
    }
  }
}

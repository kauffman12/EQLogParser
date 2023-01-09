using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
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
      UpdateTimer.Tick += (sender, e) => SaveTriggers();
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

      //var pat = "^Stones form from shadows in the {s} corners of the room\\.$";
      //var pat = "^Stones form from shadows in the (?<s>.*) corners of the room\\.$";
      //var regex = new Regex(pat, RegexOptions.Compiled);
      //var matches = regex.Matches("Stones form from shadows in the north western corners of the room.");

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

    private void LoadTriggers()
    {
      var jsonString = ConfigUtil.ReadConfigFile(TRIGGERS_FILE);
      if (jsonString != null)
      {
        Data = JsonSerializer.Deserialize<AudioTriggerData>(jsonString, new JsonSerializerOptions { IncludeFields = true });
      }
    }
  }
}

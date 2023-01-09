using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace EQLogParser
{
  internal class GINAXmlParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static AudioTriggerData ConvertToJson(string xml)
    {
      AudioTriggerData result = new AudioTriggerData();

      try
      {
        XmlDocument doc = new XmlDocument();
        doc.LoadXml(xml);

        result.Nodes = new List<AudioTriggerData>();
        var nodeList = doc.DocumentElement.SelectSingleNode("/SharedData");
        var added = new List<AudioTrigger>();
        HandleTriggerGroups(nodeList.ChildNodes, result.Nodes, added);

        if (added.Count == 0)
        {
          result = null;
        }
      }
      catch (Exception ex)
      {
        LOG.Error("Error Parsing GINA Data", ex);
      }

      return result;
    }

    internal static void HandleTriggerGroups(XmlNodeList nodeList, List<AudioTriggerData> audioTriggerNodes, List<AudioTrigger> added)
    {
      foreach (XmlNode node in nodeList)
      {
        if (node.Name == "TriggerGroup")
        {
          var data = new AudioTriggerData();
          data.Nodes = new List<AudioTriggerData>();
          data.Name = node.SelectSingleNode("Name").InnerText;
          audioTriggerNodes.Add(data);

          var triggers = new List<AudioTriggerData>();
          var triggersList = node.SelectSingleNode("Triggers");
          if (triggersList != null)
          {
            foreach (XmlNode triggerNode in triggersList.SelectNodes("Trigger"))
            {
              // ignore anything that's not using text to voice
              if (triggerNode.SelectSingleNode("UseTextToVoice").InnerText is string value && bool.TryParse(value, out bool result) && result)
              {
                var trigger = new AudioTrigger();
                trigger.Name = triggerNode.SelectSingleNode("Name").InnerText;
                trigger.UseRegex = bool.Parse(triggerNode.SelectSingleNode("EnableRegex").InnerText);
                trigger.Pattern = triggerNode.SelectSingleNode("TriggerText").InnerText;
                trigger.Speak = triggerNode.SelectSingleNode("TextToVoiceText").InnerText;
                triggers.Add(new AudioTriggerData { Name = trigger.Name, TriggerData = trigger });
                added.Add(trigger);
              }
            }
          }

          var moreGroups = node.SelectNodes("TriggerGroups");
          HandleTriggerGroups(moreGroups, data.Nodes, added);

          // GINA UI sorts by default
          data.Nodes = data.Nodes.OrderBy(n => n.Name).ToList();

          if (triggers.Count > 0)
          {
            // GINA UI sorts by default
            data.Nodes.AddRange(triggers.OrderBy(trigger => trigger.Name).ToList());
          }
        }
        else if (node.Name == "TriggerGroups")
        {
          HandleTriggerGroups(node.ChildNodes, audioTriggerNodes, added);
        }
      }
    }
  }
}

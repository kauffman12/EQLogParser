using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace EQLogParser
{
  static class TextFormatUtils
  {
    private const string BB_CELL_HEADER = "  [td]{0}    [/td]";
    private const string BB_CELL_BODY = "[td][right]{0}   [/right][/td]";
    private const string BB_CELL_FIRST = "[td]{0}[/td]";
    private const string BB_ROW_START = "[tr]";
    private const string BB_ROW_END = "[/tr]\r\n";
    private const string BB_TABLE_START = "[table]\r\n";
    private const string BB_TABLE_END = "[/table]\r\n";
    private const string BB_TITLE = "[b]{0}[/b]\r\n";
    private const string CSV_STRING_CELL = "\"{0}\"\t";
    private const string CSV_NUMBER_CELL = "{0}\t";

    private const string BB_GAMPARSE_SPELL_COUNT = "   --- {0} - {1}";

    private static readonly Dictionary<int, string> ROMAN = new Dictionary<int, string>()
    {
      { 400, "CD" }, { 100, "C" }, { 90, "XC" }, { 50, "L" }, { 40, "XL" }, { 10, "X" }, { 9, "IX" }, { 5, "V" }, { 4, "IV" }, { 1, "I" }
    };

    internal static string ParseSpellOrNpc(string[] split, int index) => string.Join(" ", split, index, split.Length - index).Trim('.');
    internal static string ToUpper(string name) => string.IsNullOrEmpty(name) ? "" : (char.ToUpper(name[0]) + (name.Length > 1 ? name.Substring(1) : ""));

    internal static string BuildCsv(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(title).Append('"').AppendLine();
      }

      // header
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, CSV_STRING_CELL, item));
      sb.AppendLine();

      // data
      data.ForEach(row =>
      {
        row.ToList().ForEach(item =>
        {
          sb.AppendFormat(CultureInfo.CurrentCulture, item.GetType() == typeof(string) ? CSV_STRING_CELL : CSV_NUMBER_CELL, item);
        });

        sb.AppendLine();
      });

      return sb.ToString();
    }

    internal static string BuildBBCodeTable(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.AppendFormat(CultureInfo.CurrentCulture, BB_TITLE, title);
      }

      // start table
      sb.Append(BB_TABLE_START);

      // header
      sb.Append(BB_ROW_START);
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, BB_CELL_HEADER, item));
      sb.Append(BB_ROW_END);

      // data
      data.ForEach(row =>
      {
        sb.Append(BB_ROW_START);
        var first = row.FirstOrDefault();
        if (first != null)
        {
          sb.AppendFormat(CultureInfo.CurrentCulture, BB_CELL_FIRST, first);
        }

        row.Skip(1).ToList().ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, BB_CELL_BODY, item));
        sb.Append(BB_ROW_END);
      });

      // end table
      sb.Append(BB_TABLE_END);
      return sb.ToString();
    }

    internal static string BuildGamparseList(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.AppendFormat(CultureInfo.CurrentCulture, BB_TITLE, title);
        sb.AppendLine();
      }

      // for each header minus the Totals
      var sorted = new SortedList<string, int>();
      for (var i = 1; i < header.Count - 1; i++)
      {
        sorted.Add(header[i], i);
      }

      foreach (var pair in sorted)
      {
        var updatedHeader = header[pair.Value].Replace("=", "-");
        sb.AppendFormat(CultureInfo.CurrentCulture, BB_TITLE, updatedHeader);

        data.OrderBy(row => row[0]).ToList().ForEach(row =>
        {
          if (row[pair.Value] != null && row[pair.Value].ToString().Length > 0 && row[pair.Value].ToString() != "0")
          {
            sb.AppendFormat(CultureInfo.CurrentCulture, BB_GAMPARSE_SPELL_COUNT, row[0], row[pair.Value]);
            sb.AppendLine();
          }
        });
      }

      return sb.ToString();
    }

    internal static void SaveHTML(string selectedFileName, Dictionary<string, SummaryTable> tables)
    {
      var headerTemplate = DotLiquid.Template.Parse(File.ReadAllText(@"data\html\header.html"));
      var tableKeys = tables.Keys.OrderBy(key => key);
      var tablechoices = new List<object>();

      foreach (var key in tableKeys)
      {
        tablechoices.Add(new { type = key, title = tables[key].GetTitle() });
      }

      var headerValue = headerTemplate.Render(DotLiquid.Hash.FromAnonymousObject(new { tablechoices }));
      File.WriteAllText(selectedFileName, headerValue);
      headerValue = null;
      headerTemplate = null;

      var contentTemplate = DotLiquid.Template.Parse(File.ReadAllText(@"data\html\content.html"));
      foreach (var key in tableKeys)
      {
        var headers = tables[key].GetHeaders();
        var playerStats = tables[key].GetPlayerStats();
        var isPetsCombined = tables[key].IsPetsCombined();

        var columns = headers.Select(header => header[1]).ToList();
        var rows = new List<object>();
        foreach (var stats in playerStats)
        {
          var data = new List<object>();
          foreach (var column in headers.Skip(1))
          {
            var value = stats.GetType().GetProperty(column[0]).GetValue(stats, null);
            if (column[1].Contains("%"))
            {
              if (value is double doubleValue && doubleValue == 0)
              {
                value = "-";
              }
            }
            else if (!(value is string))
            {
              value = $"{value:n0}";
            }

            data.Add(value);
          }

          var isChild = stats.IsTopLevel == false && isPetsCombined;
          var row = new
          {
            rank = isChild ? "" : stats.Rank.ToString(),
            ischild = isChild,
            haschild = stats.Name.Contains(" +Pets"),
            data
          };

          rows.Add(row);
        }

        var content = contentTemplate.Render(DotLiquid.Hash.FromAnonymousObject(new { columns, rows, tableid = key }));
        File.AppendAllText(selectedFileName, content);
      }

      var footer = File.ReadAllText(@"data\html\footer.html");
      File.AppendAllText(selectedFileName, footer);
    }

    internal static string Trim(string value)
    {
      if (value != null)
      {
        value = value.Trim();
        if (value.Length == 0)
        {
          value = null;
        }
      }

      return value;
    }

    internal static void ParseGinaTriggerGroups(XmlNodeList nodeList, List<TriggerNode> audioTriggerNodes, List<Trigger> added)
    {
      foreach (XmlNode node in nodeList)
      {
        if (node.Name == "TriggerGroup")
        {
          var data = new TriggerNode();
          data.Nodes = new List<TriggerNode>();
          data.Name = node.SelectSingleNode("Name").InnerText;
          audioTriggerNodes.Add(data);

          var triggers = new List<TriggerNode>();
          var triggersList = node.SelectSingleNode("Triggers");
          if (triggersList != null)
          {
            foreach (XmlNode triggerNode in triggersList.SelectNodes("Trigger"))
            {
              var goodTrigger = false;
              var trigger = new Trigger();
              trigger.Name = Helpers.GetText(triggerNode, "Name");
              trigger.Pattern = Helpers.GetText(triggerNode, "TriggerText");
              trigger.Comments = Helpers.GetText(triggerNode, "Comments");

              var timerName = Helpers.GetText(triggerNode, "TimerName");
              if (!string.IsNullOrEmpty(timerName) && timerName != trigger.Name)
              {
                trigger.AltTimerName = timerName;
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "UseText"), out var _))
              {
                goodTrigger = true;
                trigger.TextToDisplay = Helpers.GetText(triggerNode, "DisplayText");
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "UseTextToVoice"), out var _))
              {
                goodTrigger = true;
                trigger.TextToSpeak = Helpers.GetText(triggerNode, "TextToVoiceText");
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "EnableRegex"), out var regex))
              {
                trigger.UseRegex = regex;
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "InterruptSpeech"), out var interrupt))
              {
                trigger.Priority = interrupt ? 1 : 3;
              }

              if ("Timer".Equals(Helpers.GetText(triggerNode, "TimerType")))
              {
                goodTrigger = true;

                if (int.TryParse(Helpers.GetText(triggerNode, "TimerDuration"), out var duration) && duration > 0)
                {
                  trigger.DurationSeconds = duration;
                }

                if (int.TryParse(Helpers.GetText(triggerNode, "TimerMillisecondDuration"), out var millis) && millis > 0)
                {
                  trigger.DurationSeconds = millis / (double)1000;
                  if (trigger.DurationSeconds > 0 && trigger.DurationSeconds < 0.2)
                  {
                    trigger.DurationSeconds = 0.2;
                  }
                }

                // short duration timer <= 2s
                trigger.TimerType = (trigger.DurationSeconds < 2.0) ? 2 : 1;

                if (triggerNode.SelectSingleNode("TimerEndingTrigger") is XmlNode timerEndingNode)
                {
                  if (bool.TryParse(Helpers.GetText(timerEndingNode, "UseText"), out var _))
                  {
                    trigger.WarningTextToDisplay = Helpers.GetText(timerEndingNode, "DisplayText");
                  }

                  if (bool.TryParse(Helpers.GetText(timerEndingNode, "UseTextToVoice"), out var _))
                  {
                    trigger.WarningTextToSpeak = Helpers.GetText(timerEndingNode, "TextToVoiceText");
                  }
                }

                if (int.TryParse(Helpers.GetText(triggerNode, "TimerEndingTime"), out var endTime))
                {
                  // GINA defaults to 1 even if there's no text?
                  if (!string.IsNullOrEmpty(trigger.WarningTextToSpeak) || endTime > 1)
                  {
                    trigger.WarningSeconds = endTime;
                  }
                }

                var behavior = Helpers.GetText(triggerNode, "TimerStartBehavior");
                if ("StartNewTimer".Equals(behavior))
                {
                  trigger.TriggerAgainOption = 0;
                }
                else if ("RestartTimer".Equals(behavior))
                {
                  if (bool.TryParse(Helpers.GetText(triggerNode, "RestartBasedOnTimerName"), out var onTimerName))
                  {
                    trigger.TriggerAgainOption = onTimerName ? 2 : 1;
                  }
                }
                else
                {
                  trigger.TriggerAgainOption = 3;
                }

                if (triggerNode.SelectSingleNode("TimerEndedTrigger") is XmlNode timerEndedNode)
                {
                  if (bool.TryParse(Helpers.GetText(timerEndedNode, "UseText"), out var _))
                  {
                    trigger.EndTextToDisplay = Helpers.GetText(timerEndedNode, "DisplayText");
                  }

                  if (bool.TryParse(Helpers.GetText(timerEndedNode, "UseTextToVoice"), out var _))
                  {
                    trigger.EndTextToSpeak = Helpers.GetText(timerEndedNode, "TextToVoiceText");
                  }
                }

                if (triggerNode.SelectSingleNode("TimerEarlyEnders") is XmlNode endingEarlyNode)
                {
                  if (endingEarlyNode.SelectNodes("EarlyEnder") is XmlNodeList enderNodes)
                  {
                    // only take 2 cancel patterns
                    if (enderNodes.Count > 0)
                    {
                      trigger.EndEarlyPattern = Helpers.GetText(enderNodes[0], "EarlyEndText");
                      if (bool.TryParse(Helpers.GetText(enderNodes[0], "EnableRegex"), out var regex2))
                      {
                        trigger.EndUseRegex = regex2;
                      }
                    }

                    if (enderNodes.Count > 1)
                    {
                      trigger.EndEarlyPattern2 = Helpers.GetText(enderNodes[1], "EarlyEndText");
                      if (bool.TryParse(Helpers.GetText(enderNodes[1], "EnableRegex"), out var regex2))
                      {
                        trigger.EndUseRegex2 = regex2;
                      }
                    }
                  }
                }
              }

              if (goodTrigger)
              {
                triggers.Add(new TriggerNode { Name = trigger.Name, TriggerData = trigger });
                added.Add(trigger);
              }
            }
          }

          var moreGroups = node.SelectNodes("TriggerGroups");
          ParseGinaTriggerGroups(moreGroups, data.Nodes, added);

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
          ParseGinaTriggerGroups(node.ChildNodes, audioTriggerNodes, added);
        }
      }
    }

    internal static string IntToRoman(int value)
    {
      var roman = new StringBuilder();

      foreach (var item in ROMAN)
      {
        while (value >= item.Key)
        {
          roman.Append(item.Value);
          value -= item.Key;
        }
      }

      return roman.ToString();
    }

    [DebuggerHidden]
    internal static bool IsValidRegex(string pattern)
    {
      var pass = true;

      try
      {
        new Regex(pattern);
      }
      catch (Exception)
      {
        pass = false;
      }

      return pass;
    }
  }
}

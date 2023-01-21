using Syncfusion.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;

namespace EQLogParser
{
  internal class TriggerUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static ConcurrentDictionary<string, string> GinaCache = new ConcurrentDictionary<string, string>();

    internal static void AddTreeNodes(List<TriggerNode> nodes, TriggerTreeViewNode treeNode)
    {
      if (nodes != null)
      {
        foreach (var node in nodes)
        {
          var child = new TriggerTreeViewNode { Content = node.Name, SerializedData = node };
          if (node.TriggerData != null)
          {
            child.IsTrigger = true;
            child.IsChecked = node.IsEnabled;
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

    internal static void Copy(Trigger to, Trigger from)
    {
      to.Comments = from.Comments;
      to.DurationSeconds = from.DurationSeconds;
      to.EnableTimer = from.EnableTimer;
      to.CancelPattern = from.CancelPattern;
      to.EndTextToSpeak = from.EndTextToSpeak;
      to.EndUseRegex = from.EndUseRegex;
      to.Errors = "None";
      to.LongestEvalTime = -1;
      to.Pattern = from.Pattern;
      to.Priority = from.Priority;
      to.TextToSpeak = from.TextToSpeak;
      to.TriggerAgainOption = from.TriggerAgainOption;
      to.UseRegex = from.UseRegex;
      to.WarningSeconds = from.WarningSeconds;
      to.WarningTextToSpeak = from.WarningTextToSpeak;
    }

    internal static void MergeNodes(List<TriggerNode> newNodes, TriggerNode parent)
    {
      if (newNodes != null)
      {
        if (parent.Nodes == null)
        {
          parent.Nodes = newNodes;
        }
        else
        {
          var needsSort = new List<TriggerNode>();
          foreach (var newNode in newNodes)
          {
            var found = parent.Nodes.Find(node => node.Name == newNode.Name);

            if (found != null)
            {
              if (newNode.TriggerData != null && found.TriggerData != null)
              {
                Copy(found.TriggerData, newNode.TriggerData);
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

    internal static string UpdatePattern(bool useRegex, string playerName, string pattern)
    {
      pattern = pattern.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

      if (useRegex && Regex.Matches(pattern, @"{(s\d?)}", RegexOptions.IgnoreCase) is MatchCollection matches && matches.Count > 0)
      {
        matches.ForEach(match =>
        {
          if (match.Groups.Count > 1)
          {
            pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
          }
        });
      }

      return pattern;
    }

    internal static void CheckGina(LineData lineData)
    {
      var action = lineData.Action;

      // if GINA data is recent then try to handle it
      if (action.IndexOf("{GINA:", StringComparison.OrdinalIgnoreCase) is int index && index > -1 &&
        (DateTime.Now - DateUtil.FromDouble(lineData.BeginTime)).TotalSeconds <= 20 && action.IndexOf("}", index + 40) is int end && end > index)
      {
        string player = null;
        string[] split = action.Split(' ');
        if (split.Length > 0)
        {
          if (split[0] == ConfigUtil.PlayerName)
          {
            return;
          }

          if (PlayerManager.IsPossiblePlayerName(split[0]))
          {
            player = split[0];
          }
        }

        string ginaKey = null;
        var start = index + 6;
        var finish = end - index - 6;
        if (start < finish)
        {
          ginaKey = action.Substring(index + 6, end - index - 6);
        }

        if (string.IsNullOrEmpty(ginaKey))
        {
          return;
        }

        // ignore if we're still processing plus avoid spam
        if (GinaCache.ContainsKey(ginaKey) || GinaCache.Count > 5)
        {
          return;
        }

        GinaCache[ginaKey] = player;

        if (GinaCache.Count == 1)
        {
          RunGinaTask(ginaKey, player);
        }
      }
    }

    internal static void ImportFromGina(byte[] data, string player, string ginaKey)
    {
      var dispatcher = Application.Current.Dispatcher;

      using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
      {
        var entry = zip.Entries.First();
        using (StreamReader sr = new StreamReader(entry.Open()))
        {
          var triggerXml = sr.ReadToEnd();
          var audioTriggerData = ConvertGinaXmlToJson(triggerXml);

          dispatcher.InvokeAsync(() =>
          {
            if (audioTriggerData == null)
            {
              string badMessage = "GINA Triggers received";
              if (!string.IsNullOrEmpty(player))
              {
                badMessage += " from " + player;
              }

              badMessage += " but no supported Triggers found.";
              new MessageWindow(badMessage, EQLogParser.Resource.RECEIVE_GINA).ShowDialog();
            }
            else
            {
              var message = "Merge GINA Triggers or Import to New Folder?\r\n";
              if (!string.IsNullOrEmpty(player))
              {
                message = "Merge GINA Triggers from " + player + " or Import to New Folder?\r\n";
              }

              var msgDialog = new MessageWindow(message, EQLogParser.Resource.RECEIVE_GINA, MessageWindow.IconType.Question, "New Folder", "Merge");
              msgDialog.ShowDialog();

              if (msgDialog.IsYes2Clicked)
              {
                TriggerManager.Instance.MergeTriggers(audioTriggerData);
              }
              else if (msgDialog.IsYes1Clicked)
              {
                var folderName = (player == null) ? "New Folder" : "From " + player;
                TriggerManager.Instance.MergeTriggers(audioTriggerData, folderName);
              }
            }

            if (ginaKey != null)
            {
              NextGinaTask(ginaKey);
            }
          });
        }
      }
    }

    internal static void ImportFromGina(byte[] data, TriggerNode parent)
    {
      var dispatcher = Application.Current.Dispatcher;

      using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
      {
        var entry = zip.Entries.First();
        using (StreamReader sr = new StreamReader(entry.Open()))
        {
          var triggerXml = sr.ReadToEnd();
          var audioTriggerData = ConvertGinaXmlToJson(triggerXml);

          dispatcher.InvokeAsync(() =>
          {
            if (audioTriggerData != null)
            {
              TriggerManager.Instance.MergeTriggers(audioTriggerData, parent);
            }
          });
        }
      }
    }

    private static void NextGinaTask(string ginaKey)
    {
      GinaCache.TryRemove(ginaKey, out string _);

      if (GinaCache.Count > 0)
      {
        var nextKey = GinaCache.Keys.First();
        RunGinaTask(nextKey, GinaCache[nextKey]);
      }
    }

    private static void RunGinaTask(string ginaKey, string player)
    {
      var dispatcher = Application.Current.Dispatcher;

      Task.Delay(500).ContinueWith(task =>
      {
        var client = new HttpClient();

        try
        {
          var postData = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><DownloadPackageChunk xmlns=\"http://tempuri.org/\"><sessionId>" +
            ginaKey + "</sessionId><chunkNumber>0</chunkNumber></DownloadPackageChunk></s:Body></s:Envelope>";

          var content = new StringContent(postData, UnicodeEncoding.UTF8, "text/xml");
          content.Headers.Add("Content-Length", postData.Length.ToString());

          var message = new HttpRequestMessage(HttpMethod.Post, @"http://eq.gimasoft.com/GINAServices/Package.svc");
          message.Content = content;
          message.Headers.Add("SOAPAction", "http://tempuri.org/IPackageService/DownloadPackageChunk");
          message.Headers.Add("Accept-Encoding", "gzip, deflate");

          var response = client.Send(message);
          if (response.IsSuccessStatusCode)
          {
            using (var data = response.Content.ReadAsStreamAsync())
            {
              data.Wait();

              var buffer = new byte[data.Result.Length];
              var read = data.Result.ReadAsync(buffer, 0, buffer.Length);
              read.Wait();

              using (var bufferStream = new MemoryStream(buffer))
              {
                using (var gzip = new GZipStream(bufferStream, CompressionMode.Decompress))
                {
                  using (var memory = new MemoryStream())
                  {
                    gzip.CopyTo(memory);
                    var xml = Encoding.UTF8.GetString(memory.ToArray());

                    if (!string.IsNullOrEmpty(xml) && xml.IndexOf("<a:ChunkData>") is int start && start > -1 && xml.IndexOf("</a:ChunkData>") is int end &&
                      end > start)
                    {
                      var encoded = xml.Substring(start + 13, end - start - 13);
                      var decoded = Convert.FromBase64String(encoded);
                      ImportFromGina(decoded, player, ginaKey);
                    }
                    else
                    {
                      // no chunk data in response. too old?
                      NextGinaTask(ginaKey);
                    }
                  }
                }
              }
            }
          }
          else
          {
            LOG.Error("Error Downloading GINA Triggers. Received Status Code = " + response.StatusCode.ToString());
            NextGinaTask(ginaKey);
          }
        }
        catch (Exception ex)
        {
          if (ex.Message != null && ex.Message.Contains("An attempt was made to access a socket in a way forbidden by its access permissions"))
          {
            dispatcher.InvokeAsync(() =>
            {
              new MessageWindow("Error Downloading GINA Triggers. Blocked by Firewall?", EQLogParser.Resource.RECEIVE_GINA).ShowDialog();
              NextGinaTask(ginaKey);
            });
          }
          else
          {
            NextGinaTask(ginaKey);
          }

          LOG.Error("Error Downloading GINA Triggers", ex);
        }
        finally
        {
          client.Dispose();
        }
      });
    }

    private static TriggerNode ConvertGinaXmlToJson(string xml)
    {
      TriggerNode result = new TriggerNode();

      try
      {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        result.Nodes = new List<TriggerNode>();
        var nodeList = doc.DocumentElement.SelectSingleNode("/SharedData");
        var added = new List<Trigger>();
        HandleGinaTriggerGroups(nodeList.ChildNodes, result.Nodes, added);

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

    internal static void HandleGinaTriggerGroups(XmlNodeList nodeList, List<TriggerNode> audioTriggerNodes, List<Trigger> added)
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
              bool goodTrigger = false;
              var trigger = new Trigger();
              trigger.Name = GetText(triggerNode, "Name");
              trigger.Pattern = GetText(triggerNode, "TriggerText");
              trigger.Comments = GetText(triggerNode, "Comments");

              if (bool.TryParse(GetText(triggerNode, "UseTextToVoice"), out bool useText))
              {
                goodTrigger = true;
                trigger.TextToSpeak = GetText(triggerNode, "TextToVoiceText");
              }

              if (bool.TryParse(GetText(triggerNode, "EnableRegex"), out bool regex))
              {
                trigger.UseRegex = regex;
              }

              if (bool.TryParse(GetText(triggerNode, "InterruptSpeech"), out bool interrupt))
              {
                trigger.Priority = interrupt ? 1 : 5;
              }

              if ("Timer".Equals(GetText(triggerNode, "TimerType")))
              {
                goodTrigger = true;
                trigger.EnableTimer = true;

                if (int.TryParse(GetText(triggerNode, "TimerDuration"), out int duration))
                {
                  trigger.DurationSeconds = duration;
                }

                if (int.TryParse(GetText(triggerNode, "TimerEndingTime"), out int endTime))
                {
                  trigger.WarningSeconds = endTime;
                }

                var behavior = GetText(triggerNode, "TimerStartBehavior");
                if ("StartNewTimer".Equals(behavior))
                {
                  trigger.TriggerAgainOption = 0;
                }
                else if ("RestartTimer".Equals(behavior))
                {
                  trigger.TriggerAgainOption = 1;
                }
                else
                {
                  trigger.TriggerAgainOption = 2;
                }

                if (triggerNode.SelectSingleNode("TimerEndedTrigger") is XmlNode timerEndedNode)
                {
                  if (bool.TryParse(GetText(timerEndedNode, "UseTextToVoice"), out bool useText2))
                  {
                    trigger.EndTextToSpeak = GetText(timerEndedNode, "TextToVoiceText");
                  }
                }

                if (triggerNode.SelectSingleNode("TimerEndingTrigger") is XmlNode timerEndingNode)
                {
                  if (bool.TryParse(GetText(timerEndingNode, "UseTextToVoice"), out bool useText2))
                  {
                    trigger.WarningTextToSpeak = GetText(timerEndingNode, "TextToVoiceText");
                  }
                }

                if (triggerNode.SelectSingleNode("TimerEarlyEnders") is XmlNode endingEarlyNode)
                {
                  if (endingEarlyNode.SelectSingleNode("EarlyEnder") is XmlNode enderNode)
                  {
                    trigger.CancelPattern = GetText(enderNode, "EarlyEndText");

                    if (bool.TryParse(GetText(enderNode, "EnableRegex"), out bool regex2))
                    {
                      trigger.EndUseRegex = regex2;
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
          HandleGinaTriggerGroups(moreGroups, data.Nodes, added);

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
          HandleGinaTriggerGroups(node.ChildNodes, audioTriggerNodes, added);
        }
      }
    }

    private static string GetText(XmlNode node, string value)
    {
      if (node.SelectSingleNode(value) is XmlNode selected)
      {
        return selected.InnerText?.Trim();
      }

      return "";
    }
  }
}

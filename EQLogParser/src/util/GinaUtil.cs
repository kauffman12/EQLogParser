using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;

namespace EQLogParser
{
  internal static class GinaUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static ConcurrentDictionary<string, string> GinaCache = new ConcurrentDictionary<string, string>();

    internal static List<ExportTriggerNode> CovertToTriggerNodes(byte[] data) => Convert(ReadXml(data));

    internal static void CheckGina(LineData lineData)
    {
      var action = lineData.Action;

      // if GINA data is recent then try to handle it
      if (action.IndexOf("{GINA:", StringComparison.OrdinalIgnoreCase) is int index && index > -1 &&
        (DateTime.Now - DateUtil.FromDouble(lineData.BeginTime)).TotalSeconds <= 20 && action.IndexOf("}") is int end && end > (index + 40))
      {
        string player = null;
        var split = action.Split(' ');
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

        // ignore if we're still processing plus avoid spam
        if (string.IsNullOrEmpty(ginaKey) || GinaCache.ContainsKey(ginaKey) || GinaCache.Count > 5)
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
      var nodes = Convert(ReadXml(data));
      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (nodes[0].Nodes.Count == 0)
        {
          var badMessage = "GINA Triggers received";
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
            message = $"Merge GINA Triggers from {player} or Import to New Folder?\r\n";
          }

          var msgDialog = new MessageWindow(message, EQLogParser.Resource.RECEIVE_GINA, MessageWindow.IconType.Question, "New Folder", "Merge");
          msgDialog.ShowDialog();

          if (msgDialog.IsYes2Clicked)
          {
            TriggerStateManager.Instance.ImportTriggers("", nodes);
          }
          if (msgDialog.IsYes1Clicked)
          {
            var folderName = (player == null) ? "New Folder" : "From " + player;
            folderName += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
            TriggerStateManager.Instance.ImportTriggers(folderName, nodes);
          }
        }

        if (ginaKey != null)
        {
          NextGinaTask(ginaKey);
        }
      });
    }

    private static string ReadXml(byte[] data)
    {
      string result = null;
      using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
      {
        if (zip.Entries.FirstOrDefault() is ZipArchiveEntry entry)
        {
          using (var sr = new StreamReader(entry.Open()))
          {
            result = sr.ReadToEnd();
          }
        }
      }
      return result;
    }

    private static void NextGinaTask(string ginaKey)
    {
      GinaCache.TryRemove(ginaKey, out var _);

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

                    if (!string.IsNullOrEmpty(xml) && xml.IndexOf("<a:ChunkData>") is int start && start > -1
                      && xml.IndexOf("</a:ChunkData>") is int end && end > start)
                    {
                      var encoded = xml.Substring(start + 13, end - start - 13);
                      var decoded = System.Convert.FromBase64String(encoded);
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

    private static List<ExportTriggerNode> Convert(string xml)
    {
      var result = new List<ExportTriggerNode>() { new ExportTriggerNode() };

      try
      {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var nodeList = doc.DocumentElement.SelectSingleNode("/SharedData");
        ParseGinaTriggerGroups(nodeList.ChildNodes, result[0].Nodes);
      }
      catch (Exception ex)
      {
        LOG.Error("Error Parsing GINA Data", ex);
      }

      return result;
    }

    private static string GetText(XmlNode node, string value)
    {
      if (node.SelectSingleNode(value) is XmlNode selected)
      {
        return selected.InnerText?.Trim();
      }

      return "";
    }

    private static void ParseGinaTriggerGroups(XmlNodeList nodeList, List<ExportTriggerNode> newNodes)
    {
      foreach (XmlNode node in nodeList)
      {
        if (node.Name == "TriggerGroup")
        {
          var data = new ExportTriggerNode();
          data.Name = node.SelectSingleNode("Name").InnerText;
          newNodes.Add(data);

          var triggers = new List<ExportTriggerNode>();
          var triggersList = node.SelectSingleNode("Triggers");
          if (triggersList != null)
          {
            foreach (XmlNode triggerNode in triggersList.SelectNodes("Trigger"))
            {
              var goodTrigger = false;
              var trigger = new Trigger();
              var triggerName = GetText(triggerNode, "Name");
              trigger.Pattern = GetText(triggerNode, "TriggerText");
              trigger.Comments = GetText(triggerNode, "Comments");

              var timerName = GetText(triggerNode, "TimerName");
              if (!string.IsNullOrEmpty(timerName) && timerName != triggerName)
              {
                trigger.AltTimerName = timerName;
              }

              if (bool.TryParse(GetText(triggerNode, "UseText"), out var _))
              {
                goodTrigger = true;
                trigger.TextToDisplay = GetText(triggerNode, "DisplayText");
              }

              if (bool.TryParse(GetText(triggerNode, "UseTextToVoice"), out var _))
              {
                goodTrigger = true;
                trigger.TextToSpeak = GetText(triggerNode, "TextToVoiceText");
              }

              if (bool.TryParse(GetText(triggerNode, "EnableRegex"), out var regex))
              {
                trigger.UseRegex = regex;
              }

              if (bool.TryParse(GetText(triggerNode, "InterruptSpeech"), out var interrupt))
              {
                trigger.Priority = interrupt ? 1 : 3;
              }

              if ("Timer".Equals(GetText(triggerNode, "TimerType")))
              {
                goodTrigger = true;

                if (int.TryParse(GetText(triggerNode, "TimerDuration"), out var duration) && duration > 0)
                {
                  trigger.DurationSeconds = duration;
                }

                if (int.TryParse(GetText(triggerNode, "TimerMillisecondDuration"), out var millis) && millis > 0)
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
                  if (bool.TryParse(GetText(timerEndingNode, "UseText"), out var _))
                  {
                    trigger.WarningTextToDisplay = GetText(timerEndingNode, "DisplayText");
                  }

                  if (bool.TryParse(GetText(timerEndingNode, "UseTextToVoice"), out var _))
                  {
                    trigger.WarningTextToSpeak = GetText(timerEndingNode, "TextToVoiceText");
                  }
                }

                if (int.TryParse(GetText(triggerNode, "TimerEndingTime"), out var endTime))
                {
                  // GINA defaults to 1 even if there's no text?
                  if (!string.IsNullOrEmpty(trigger.WarningTextToSpeak) || endTime > 1)
                  {
                    trigger.WarningSeconds = endTime;
                  }
                }

                var behavior = GetText(triggerNode, "TimerStartBehavior");
                if ("StartNewTimer".Equals(behavior))
                {
                  trigger.TriggerAgainOption = 0;
                }
                else if ("RestartTimer".Equals(behavior))
                {
                  if (bool.TryParse(GetText(triggerNode, "RestartBasedOnTimerName"), out var onTimerName))
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
                  if (bool.TryParse(GetText(timerEndedNode, "UseText"), out var _))
                  {
                    trigger.EndTextToDisplay = GetText(timerEndedNode, "DisplayText");
                  }

                  if (bool.TryParse(GetText(timerEndedNode, "UseTextToVoice"), out var _))
                  {
                    trigger.EndTextToSpeak = GetText(timerEndedNode, "TextToVoiceText");
                  }
                }

                if (triggerNode.SelectSingleNode("TimerEarlyEnders") is XmlNode endingEarlyNode)
                {
                  if (endingEarlyNode.SelectNodes("EarlyEnder") is XmlNodeList enderNodes)
                  {
                    // only take 2 cancel patterns
                    if (enderNodes.Count > 0)
                    {
                      trigger.EndEarlyPattern = GetText(enderNodes[0], "EarlyEndText");
                      if (bool.TryParse(GetText(enderNodes[0], "EnableRegex"), out var regex2))
                      {
                        trigger.EndUseRegex = regex2;
                      }
                    }

                    if (enderNodes.Count > 1)
                    {
                      trigger.EndEarlyPattern2 = GetText(enderNodes[1], "EarlyEndText");
                      if (bool.TryParse(GetText(enderNodes[1], "EnableRegex"), out var regex2))
                      {
                        trigger.EndUseRegex2 = regex2;
                      }
                    }
                  }
                }
              }

              if (goodTrigger)
              {
                triggers.Add(new ExportTriggerNode { Name = triggerName, TriggerData = trigger });
              }
            }
          }

          var moreGroups = node.SelectNodes("TriggerGroups");
          ParseGinaTriggerGroups(moreGroups, data.Nodes);

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
          ParseGinaTriggerGroups(node.ChildNodes, newNodes);
        }
      }
    }
  }
}

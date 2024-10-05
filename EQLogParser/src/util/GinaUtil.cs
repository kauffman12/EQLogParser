using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace EQLogParser
{
  internal static class GinaUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly ConcurrentDictionary<string, CharacterData> GinaCache = new();

    internal static List<ExportTriggerNode> CovertToTriggerNodes(byte[] data) => Convert(ReadXml(data));

    internal static void CheckGina(ChatType chatType, string action, double dateTime, string characterId, string processorName)
    {
      // if GINA data is recent then try to handle it
      if (chatType.Sender != null && action.IndexOf("{GINA:", StringComparison.OrdinalIgnoreCase) is var index and > -1 &&
          action.IndexOf('}') is var end && end > (index + 10))
      {
        var start = index + 6;
        var finish = end - start;
        if (action.Length > (start + finish))
        {
          var ginaKey = action.Substring(start, finish);
          var fullKey = $"{{GINA:{ginaKey}}}";
          if (!string.IsNullOrEmpty(ginaKey))
          {
            var to = chatType.Channel == ChatChannels.Tell ? "You" : chatType.Channel;
            var record = new QuickShareRecord
            {
              BeginTime = dateTime,
              Key = fullKey,
              From = chatType.Sender,
              To = (to == "You" && processorName != null && characterId != TriggerStateManager.DefaultUser) ? processorName : TextUtils.ToUpper(to),
              IsMine = chatType.SenderIsYou,
              Type = "GINA"
            };

            RecordManager.Instance.Add(record);

            // don't handle immediately unless enabled
            if (characterId != null && !chatType.SenderIsYou && (chatType.Channel is ChatChannels.Group or ChatChannels.Guild
                  or ChatChannels.Raid or ChatChannels.Tell) && ConfigUtil.IfSet("TriggersWatchForQuickShare") &&
                !RecordManager.Instance.IsQuickShareMine(fullKey))
            {
              // ignore if we're still processing a bunch
              if (GinaCache.Count > 5)
              {
                return;
              }

              lock (GinaCache)
              {
                if (!GinaCache.TryGetValue(ginaKey, out var value))
                {
                  GinaCache[ginaKey] = new CharacterData { Sender = chatType.Sender };
                  GinaCache[ginaKey].CharacterIds.Add(characterId);
                  RunGinaTask(ginaKey);
                }
                else
                {
                  value.CharacterIds.Add(characterId);
                }
              }
            }
          }
        }
      }
    }

    internal static void ImportQuickShare(string shareKey, string from)
    {
      // if Quick Share data is recent then try to handle it
      if (shareKey.IndexOf("{GINA:", StringComparison.OrdinalIgnoreCase) is var index and > -1 &&
          shareKey.IndexOf('}') is var end && end > (index + 10))
      {
        var start = index + 6;
        var finish = end - start;
        if (shareKey.Length > (start + finish))
        {
          var quickShareKey = shareKey.Substring(start, finish);
          if (!string.IsNullOrEmpty(quickShareKey))
          {
            GinaCache.TryAdd(quickShareKey, new CharacterData { Sender = from });
            if (GinaCache.Count == 1)
            {
              RunGinaTask(quickShareKey);
            }
          }
        }
      }
    }

    private static void ImportFromGina(byte[] data, string ginaKey)
    {
      var nodes = Convert(ReadXml(data));
      if (GinaCache.TryGetValue(ginaKey, out var quickShareData))
      {
        var player = quickShareData.Sender;
        var characterIds = quickShareData.CharacterIds;

        UiUtil.InvokeAsync(async () =>
        {
          if (nodes.Count > 0 && nodes[0].Nodes.Count == 0)
          {
            var badMessage = "GINA Triggers Received";
            if (!string.IsNullOrEmpty(player))
            {
              badMessage += " from " + player;
            }

            badMessage += " but no supported Triggers found.";
            new MessageWindow(badMessage, Resource.RECEIVED_GINA).ShowDialog();
          }
          else
          {
            var message = "Merge GINA Triggers or Import to New Folder?\r\n";
            if (!string.IsNullOrEmpty(player))
            {
              message = $"Merge GINA Triggers from {player} or Import to New Folder?\r\n";
            }

            var msgDialog = new MessageWindow(message, Resource.RECEIVED_GINA, MessageWindow.IconType.Question,
              "New Folder", "Merge", characterIds.Count > 0);
            msgDialog.ShowDialog();

            if (msgDialog.IsYes2Clicked)
            {
              await TriggerStateManager.Instance.ImportTriggers("", nodes, characterIds);
            }
            if (msgDialog.IsYes1Clicked)
            {
              var folderName = (player == null) ? "New Folder" : "From " + player;
              folderName += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
              await TriggerStateManager.Instance.ImportTriggers(folderName, nodes, characterIds);
            }
          }

          NextGinaTask(ginaKey);
        });
      }
    }

    private static string ReadXml(byte[] data)
    {
      string result = null;
      using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
      if (zip.Entries.FirstOrDefault() is { } entry)
      {
        using var sr = new StreamReader(entry.Open());
        result = sr.ReadToEnd();
      }
      return result;
    }

    private static void NextGinaTask(string ginaKey)
    {
      GinaCache.TryRemove(ginaKey, out var _);

      if (!GinaCache.IsEmpty)
      {
        var nextKey = GinaCache.Keys.First();
        RunGinaTask(nextKey);
      }
    }

    private static void RunGinaTask(string ginaKey)
    {
      Task.Delay(1000).ContinueWith(_ =>
      {
        try
        {
          var totalRead = 0;
          var totalSize = 0;
          XNamespace ns = "http://schemas.datacontract.org/2004/07/GimaSoft.Service.GINA";
          using var decodedStream = new MemoryStream();

          for (var chunk = 0; chunk < 100; chunk++)
          {
            var postData = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><DownloadPackageChunk xmlns=\"http://tempuri.org/\"><sessionId>" +
                           ginaKey + "</sessionId><chunkNumber>" + chunk + "</chunkNumber></DownloadPackageChunk></s:Body></s:Envelope>";

            var content = new StringContent(postData, Encoding.UTF8, "text/xml");
            content.Headers.Add("Content-Length", postData.Length.ToString());

            var message = new HttpRequestMessage(HttpMethod.Post, @"http://eq.gimasoft.com/GINAServices/Package.svc");
            message.Content = content;
            message.Headers.Add("SOAPAction", "http://tempuri.org/IPackageService/DownloadPackageChunk");
            message.Headers.Add("Accept-Encoding", "gzip, deflate");
            var response = MainActions.TheHttpClient.Send(message);
            if (response.IsSuccessStatusCode)
            {
              string xml = null;
              using var data = response.Content.ReadAsStream();
              var buffer = new byte[data.Length];
              var read = data.Read(buffer, 0, buffer.Length);
              if (read > 0)
              {
                using var bufferStream = new MemoryStream(buffer);
                using var gzip = new GZipStream(bufferStream, CompressionMode.Decompress);
                using var memory = new MemoryStream();
                gzip.CopyTo(memory);
                xml = Encoding.UTF8.GetString(memory.ToArray());
              }

              var handled = false;
              if (!string.IsNullOrEmpty(xml))
              {
                try
                {
                  var doc = XDocument.Parse(xml);
                  var chunkData = doc.Descendants(ns + "ChunkData").FirstOrDefault()?.Value;
                  var totalSizeString = doc.Descendants(ns + "TotalSize").FirstOrDefault()?.Value;
                  var success = doc.Descendants(ns + "Success").FirstOrDefault()?.Value == "true";
                  if (success && chunkData != null && int.TryParse(totalSizeString, out totalSize))
                  {
                    var decoded = System.Convert.FromBase64String(chunkData);
                    decodedStream.Write(decoded, 0, decoded.Length);
                    totalRead += decoded.Length;
                    handled = true;
                  }
                }
                catch (Exception)
                {
                  // something is wrong so just break
                  break;
                }
              }

              if (!handled || totalRead >= totalSize)
              {
                break;
              }
            }
          }

          var allDecoded = decodedStream.ToArray();
          if (allDecoded.Length > 0)
          {
            ImportFromGina(allDecoded, ginaKey);
          }
          else
          {
            UiUtil.InvokeAsync(() =>
              new MessageWindow("Unable to Import. No data found, possibly expired?", Resource.RECEIVED_GINA).ShowDialog());

            // no chunk data in response. too old?
            NextGinaTask(ginaKey);
          }
        }
        catch (Exception ex)
        {
          if (ex.Message.Contains("An attempt was made to access a socket in a way forbidden by its access permissions"))
          {
            UiUtil.InvokeAsync(() =>
            {
              new MessageWindow("Error Downloading GINA Triggers. Blocked by Firewall?", Resource.RECEIVED_GINA).ShowDialog();
              Log.Error("Error Downloading GINA Triggers", ex);
              NextGinaTask(ginaKey);
            });
          }
          else
          {
            UiUtil.InvokeAsync(() =>
            {
              new MessageWindow("Unable to Import. May be Expired.\nCheck Error Log for Details.", Resource.SHARE_ERROR).ShowDialog();
            });

            Log.Error("Error Downloading GINA Triggers", ex);
            NextGinaTask(ginaKey);
          }
        }
      });
    }

    private static List<ExportTriggerNode> Convert(string xml)
    {
      var result = new List<ExportTriggerNode> { new() };

      try
      {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var nodeList = doc.DocumentElement?.SelectSingleNode("/SharedData");
        ParseGinaTriggerGroups(nodeList?.ChildNodes, result[0].Nodes);
      }
      catch (Exception ex)
      {
        Log.Error("Error Parsing GINA Data", ex);
      }

      return result;
    }

    private static string GetText(XmlNode node, string value)
    {
      if (node.SelectSingleNode(value) is { } selected && !string.IsNullOrEmpty(selected.InnerText))
      {
        return selected.InnerText.Trim();
      }

      return "";
    }

    private static void ParseGinaTriggerGroups(XmlNodeList nodeList, List<ExportTriggerNode> newNodes)
    {
      if (nodeList != null)
      {
        foreach (XmlNode node in nodeList)
        {
          if (node.Name == "TriggerGroup")
          {
            var data = new ExportTriggerNode
            {
              Name = node.SelectSingleNode("Name").InnerText
            };
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

                if (bool.TryParse(GetText(triggerNode, "UseText"), out _))
                {
                  goodTrigger = true;
                  trigger.TextToDisplay = GetText(triggerNode, "DisplayText");
                }

                if (bool.TryParse(GetText(triggerNode, "CopyToClipboard"), out _))
                {
                  goodTrigger = true;
                  trigger.TextToShare = GetText(triggerNode, "ClipboardText");
                }

                if (bool.TryParse(GetText(triggerNode, "UseTextToVoice"), out _))
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

                if (GetText(triggerNode, "TimerType") is { } timerType && timerType != "NoTimer")
                {
                  goodTrigger = true;

                  // default stopwatches to a minute since they don't use a duration in GINA
                  if (timerType == "Stopwatch")
                  {
                    trigger.DurationSeconds = 60;
                    trigger.TimerType = 3;
                  }
                  else
                  {
                    if (int.TryParse(GetText(triggerNode, "TimerDuration"), out var duration) && duration > 0)
                    {
                      trigger.DurationSeconds = duration;
                    }

                    if (int.TryParse(GetText(triggerNode, "TimerMillisecondDuration"), out var millis) && millis > 0)
                    {
                      trigger.DurationSeconds = millis / (double)1000;
                      if (trigger.DurationSeconds is > 0 and < 0.2)
                      {
                        trigger.DurationSeconds = 0.2;
                      }
                    }

                    if (timerType == "RepeatingTimer")
                    {
                      trigger.TimerType = 4;
                      // default to 5 so it doesn't run forever 
                      trigger.TimesToLoop = 5;
                    }
                    else
                    {
                      // short duration timer <= 2s
                      trigger.TimerType = (trigger.DurationSeconds < 2.0) ? 2 : 1;
                    }
                  }

                  if (triggerNode.SelectSingleNode("TimerEndingTrigger") is { } timerEndingNode)
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
                    // do nothing if same timer name
                    trigger.TriggerAgainOption = 4;
                  }

                  if (triggerNode.SelectSingleNode("TimerEndedTrigger") is { } timerEndedNode)
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

                  if (triggerNode.SelectSingleNode("TimerEarlyEnders") is { } endingEarlyNode)
                  {
                    if (endingEarlyNode.SelectNodes("EarlyEnder") is { } enderNodes)
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
            data.Nodes = [.. data.Nodes.OrderBy(n => n.Name)];

            if (triggers.Count > 0)
            {
              // GINA UI sorts by default
              data.Nodes.AddRange([.. triggers.OrderBy(trigger => trigger.Name)]);
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
}

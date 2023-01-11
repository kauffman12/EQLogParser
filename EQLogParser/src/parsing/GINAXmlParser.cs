using Syncfusion.Data.Extensions;
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
  internal class GINAXmlParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static ConcurrentDictionary<string, string> GinaCache = new ConcurrentDictionary<string, string>();

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

                      using (var zip = new ZipArchive(new MemoryStream(decoded), ZipArchiveMode.Read))
                      {
                        var entry = zip.Entries.First();
                        using (StreamReader sr = new StreamReader(entry.Open()))
                        {
                          var triggerXml = sr.ReadToEnd();
                          var audioTriggerData = ConvertToJson(triggerXml);

                          dispatcher.InvokeAsync(() =>
                          {
                            if (audioTriggerData == null)
                            {
                              string badMessage = "GINA Triggers received";
                              if (!string.IsNullOrEmpty(player))
                              {
                                badMessage += " from " + player;
                              }

                              badMessage += " but no Voice to Text Triggers found.";
                              new MessageWindow(badMessage, EQLogParser.Resource.RECEIVE_GINA, false).ShowDialog();
                            }
                            else
                            {
                              var message = "Accept GINA Triggers?\r\n";
                              if (!string.IsNullOrEmpty(player))
                              {
                                message = "Accept GINA Triggers from " + player + "?\r\n";
                              }

                              string includes = null;
                              if (audioTriggerData.Nodes.Count > 0)
                              {
                                includes = string.Join(",", audioTriggerData.Nodes.Select(node => node.Name).ToArray());
                              }

                              message = string.IsNullOrEmpty(includes) ? message : (message + "Includes: " + includes);

                              var msgDialog = new MessageWindow(message, EQLogParser.Resource.RECEIVE_GINA, true);
                              msgDialog.ShowDialog();

                              if (msgDialog.IsYesClicked)
                              {
                                AudioTriggerManager.Instance.MergeTriggers(audioTriggerData);
                              }
                            }

                            NextGinaTask(ginaKey);
                          });
                        }
                      }
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

    private static AudioTriggerData ConvertToJson(string xml)
    {
      AudioTriggerData result = new AudioTriggerData();

      try
      {
        var doc = new XmlDocument();
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
                trigger.Pattern = triggerNode.SelectSingleNode("TriggerText").InnerText;
                trigger.Speak = triggerNode.SelectSingleNode("TextToVoiceText").InnerText;

                if (bool.TryParse(triggerNode.SelectSingleNode("EnableRegex").InnerText, out bool regex))
                {
                  trigger.UseRegex = regex;
                }
                
                if (bool.TryParse(triggerNode.SelectSingleNode("InterruptSpeech").InnerText, out bool interrupt))
                {
                  trigger.Priority = interrupt ? 1 : 5;
                }

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

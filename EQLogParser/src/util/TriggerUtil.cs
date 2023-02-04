using Microsoft.Win32;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace EQLogParser
{
  internal class TriggerUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static ConcurrentDictionary<string, string> GinaCache = new ConcurrentDictionary<string, string>();

    internal static string GetSelectedVoice() => ConfigUtil.GetSetting("TriggersSelectedVoice", "");

    internal static int GetVoiceRate()
    {
      var rate = ConfigUtil.GetSettingAsInteger("TriggersVoiceRate");
      return rate == int.MaxValue ? 0 : rate;
    }

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
          else if (node.OverlayData != null)
          {
            child.IsOverlay = true;
            child.IsChecked = node.IsEnabled;
            treeNode.ChildNodes.Add(child);
          }
          else
          {
            child.IsChecked = node.IsEnabled;
            child.IsExpanded = node.IsExpanded;
            child.IsTrigger = false;
            child.IsOverlay = false;
            treeNode.ChildNodes.Add(child);
            AddTreeNodes(node.Nodes, child);
          }
        }
      }
    }

    internal static void Copy(object to, object from)
    {
      if (to is Trigger toTrigger && from is Trigger fromTrigger)
      {
        toTrigger.AltTimerName = fromTrigger.AltTimerName;
        toTrigger.Comments = fromTrigger.Comments;
        toTrigger.DurationSeconds = fromTrigger.DurationSeconds;
        toTrigger.EnableTimer = fromTrigger.EnableTimer;
        toTrigger.EndEarlyPattern = fromTrigger.EndEarlyPattern;
        toTrigger.EndEarlyPattern2 = fromTrigger.EndEarlyPattern2;
        toTrigger.EndTextToSpeak = fromTrigger.EndTextToSpeak;
        toTrigger.EndUseRegex = fromTrigger.EndUseRegex;
        toTrigger.EndUseRegex2 = fromTrigger.EndUseRegex2;
        toTrigger.Errors = fromTrigger.Errors;
        toTrigger.LongestEvalTime = fromTrigger.LongestEvalTime;
        toTrigger.ResetDurationSeconds = fromTrigger.ResetDurationSeconds;
        toTrigger.Pattern = fromTrigger.Pattern;
        toTrigger.Priority = fromTrigger.Priority;
        toTrigger.SelectedOverlays = fromTrigger.SelectedOverlays;
        toTrigger.TextToSpeak = fromTrigger.TextToSpeak;
        toTrigger.TriggerAgainOption = fromTrigger.TriggerAgainOption;
        toTrigger.UseRegex = fromTrigger.UseRegex;
        toTrigger.WarningSeconds = fromTrigger.WarningSeconds;
        toTrigger.WarningTextToSpeak = fromTrigger.WarningTextToSpeak;

        if (toTrigger is TriggerPropertyModel toModel)
        {
          toModel.SelectedTextOverlays = TriggerOverlayManager.Instance.GetTextOverlayItems(toModel.SelectedOverlays);
          toModel.SelectedTimerOverlays = TriggerOverlayManager.Instance.GetTimerOverlayItems(toModel.SelectedOverlays);
          toModel.DurationTimeSpan = new TimeSpan(0, 0, (int)toModel.DurationSeconds);
          toModel.ResetDurationTimeSpan = new TimeSpan(0, 0, (int)toModel.ResetDurationSeconds);
        }
        else if (fromTrigger is TriggerPropertyModel fromModel)
        {
          var selectedOverlays = fromModel.SelectedTextOverlays.Where(item => item.IsChecked).Select(item => item.Value).ToList();
          selectedOverlays.AddRange(fromModel.SelectedTimerOverlays.Where(item => item.IsChecked).Select(item => item.Value));
          toTrigger.SelectedOverlays = selectedOverlays;
          toTrigger.DurationSeconds = fromModel.DurationTimeSpan.TotalSeconds;
          toTrigger.ResetDurationSeconds = fromModel.ResetDurationTimeSpan.TotalSeconds;
        }
      }
      else if (to is Overlay toOverlay && from is Overlay fromOverlay)
      {
        toOverlay.OverlayComments = fromOverlay.OverlayComments;
        toOverlay.FontColor = fromOverlay.FontColor;
        toOverlay.PrimaryColor = fromOverlay.PrimaryColor;
        toOverlay.SecondaryColor = fromOverlay.SecondaryColor;
        toOverlay.BackgroundColor = fromOverlay.BackgroundColor;
        toOverlay.OverlayColor = fromOverlay.OverlayColor;
        toOverlay.FontSize = fromOverlay.FontSize;
        toOverlay.SortBy = fromOverlay.SortBy;
        toOverlay.TimerMode = fromOverlay.TimerMode;
        toOverlay.Id = fromOverlay.Id;
        toOverlay.UseStandardTime = fromOverlay.UseStandardTime;
        toOverlay.Name = fromOverlay.Name;
        toOverlay.FadeDelay = fromOverlay.FadeDelay;
        toOverlay.IsTimerOverlay = fromOverlay.IsTimerOverlay;
        toOverlay.IsTextOverlay = fromOverlay.IsTextOverlay;

        if (toOverlay is TimerOverlayPropertyModel toModel)
        {
          Application.Current.Resources["OverlayText-" + toModel.Id] = toModel.Name;

          if (fromOverlay.OverlayColor is string overlayColor && !string.IsNullOrEmpty(overlayColor))
          {
            toModel.OverlayBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(overlayColor) };
            Application.Current.Resources["OverlayBrushColor-" + toModel.Id] = toModel.OverlayBrush;
          }

          if (fromOverlay.FontColor is string fontColor && !string.IsNullOrEmpty(fontColor))
          {
            toModel.FontBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(fontColor) };
            Application.Current.Resources["TimerBarFontColor-" + toModel.Id] = toModel.FontBrush;
          }

          if (fromOverlay.PrimaryColor is string primary && !string.IsNullOrEmpty(primary))
          {
            toModel.PrimaryBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(primary) };
            Application.Current.Resources["TimerBarProgressColor-" + toModel.Id] = toModel.PrimaryBrush;
          }

          if (fromOverlay.SecondaryColor is string secondary && !string.IsNullOrEmpty(secondary))
          {
            toModel.SecondaryBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(secondary) };
            Application.Current.Resources["TimerBarResetColor-" + toModel.Id] = toModel.SecondaryBrush;
          }

          if (fromOverlay.BackgroundColor is string background && !string.IsNullOrEmpty(background))
          {
            toModel.BackgroundBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(background) };
            Application.Current.Resources["TimerBarTrackColor-" + toModel.Id] = toModel.BackgroundBrush;
          }

          if (fromOverlay.FontSize is string fontSize && !string.IsNullOrEmpty(fontSize) && fontSize.Split("pt") is string[] split && split.Length == 2
           && double.TryParse(split[0], out double newFontSize))
          {
            Application.Current.Resources["TimerBarFontSize-" + toModel.Id] = newFontSize;
            Application.Current.Resources["TimerBarHeight-" + toModel.Id] = GetTimerBarHeight(newFontSize);
          }
        }
        else if (fromOverlay is TimerOverlayPropertyModel fromModel)
        {
          toOverlay.OverlayColor = fromModel.OverlayBrush.Color.ToString();
          toOverlay.FontColor = fromModel.FontBrush.Color.ToString();
          toOverlay.PrimaryColor = fromModel.PrimaryBrush.Color.ToString();
          toOverlay.BackgroundColor = fromModel.BackgroundBrush.Color.ToString();
          toOverlay.SecondaryColor = fromModel.SecondaryBrush.Color.ToString();
        }
        else if (toOverlay is TextOverlayPropertyModel toTextModel)
        {
          Application.Current.Resources["OverlayText-" + toTextModel.Id] = toTextModel.Name;

          if (fromOverlay.OverlayColor is string overlayColor && !string.IsNullOrEmpty(overlayColor))
          {
            toTextModel.OverlayBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(overlayColor) };
            Application.Current.Resources["OverlayBrushColor-" + toTextModel.Id] = toTextModel.OverlayBrush;
          }

          if (fromOverlay.FontColor is string fontColor && !string.IsNullOrEmpty(fontColor))
          {
            toTextModel.FontBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(fontColor) };
            Application.Current.Resources["TextOverlayFontColor-" + toTextModel.Id] = toTextModel.FontBrush;
          }

          if (fromOverlay.FontSize is string fontSize && !string.IsNullOrEmpty(fontSize) && fontSize.Split("pt") is string[] split && split.Length == 2
           && double.TryParse(split[0], out double newFontSize))
          {
            Application.Current.Resources["TextOverlayFontSize-" + toTextModel.Id] = newFontSize;
          }
        }
        else if (fromOverlay is TextOverlayPropertyModel fromTextModel)
        {
          toOverlay.FontColor = fromTextModel.FontBrush.Color.ToString();
          toOverlay.OverlayColor = fromTextModel.OverlayBrush.Color.ToString();
        }
      }
    }

    internal static bool CheckNumberOptions(List<NumberOptions> options, MatchCollection matches)
    {
      bool passed = true;
      if (matches.Count > 0)
      {
        foreach (var option in options)
        {
          foreach (Match match in matches)
          {
            if (match.Success)
            {
              for (int i = 0; i < match.Groups.Count; i++)
              {
                if (match.Groups[i].Name == option.Key && !string.IsNullOrEmpty(option.Op))
                {
                  if (StatsUtil.ParseUInt(match.Groups[i].Value) is uint value && value != uint.MaxValue)
                  {
                    switch (option.Op)
                    {
                      case ">":
                        passed = (value > option.Value);
                        break;
                      case ">=":
                        passed = (value >= option.Value);
                        break;
                      case "<":
                        passed = (value < option.Value);
                        break;
                      case "<=":
                        passed = (value <= option.Value);
                        break;
                      case "=":
                      case "==":
                        passed = (value == option.Value);
                        break;
                    }

                    if (!passed)
                    {
                      return false;
                    }
                  }
                }
              }
            }
          }
        }
      }
      return passed;
    }

    internal static double GetTimerBarHeight(double fontSize) => fontSize + 2;

    internal static void DisableNodes(TriggerNode node)
    {
      if (node.TriggerData == null && node.OverlayData == null)
      {
        node.IsEnabled = false;
        node.IsExpanded = false;
        if (node.Nodes != null)
        {
          foreach (var child in node.Nodes)
          {
            DisableNodes(child);
          }
        }
      }
    }

    internal static TriggerTreeViewNode GetTreeView(TriggerNode nodes, string title)
    {
      var result = new TriggerTreeViewNode
      {
        Content = title,
        IsChecked = nodes.IsEnabled,
        IsTrigger = false,
        IsOverlay = false,
        IsExpanded = nodes.IsExpanded,
        SerializedData = nodes
      };

      lock (nodes)
      {
        AddTreeNodes(nodes.Nodes, result);
      }

      return result;
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
              else if (newNode.OverlayData != null && found.OverlayData != null)
              {
                Copy(found.OverlayData, newNode.OverlayData);
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

    internal static TriggerTreeViewNode FindAndExpandNode(SfTreeView treeView, TriggerTreeViewNode node, object file)
    {
      if (node.SerializedData?.TriggerData == file || node.SerializedData?.OverlayData == file)
      {
        return node;
      }

      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        if (FindAndExpandNode(treeView, child, file) is TriggerTreeViewNode found && found != null)
        {
          treeView.ExpandNode(node);
          return found;
        }
      }

      return null;
    }

    internal static void Import(TriggerTreeViewNode node)
    {
      if (node != null)
      {
        try
        {
          // WPF doesn't have its own file chooser so use Win32 Version
          OpenFileDialog dialog = new OpenFileDialog
          {
            // filter to txt files
            DefaultExt = ".scf.gz",
            Filter = "All Supported Files|*.tgf.gz;*.gtp"
          };

          // show dialog and read result
          if (dialog.ShowDialog().Value)
          {
            // limit to 100 megs just incase
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Exists && fileInfo.Length < 100000000)
            {
              if (dialog.FileName.EndsWith("tgf.gz"))
              {
                GZipStream decompressionStream = new GZipStream(fileInfo.OpenRead(), CompressionMode.Decompress);
                var reader = new StreamReader(decompressionStream);
                string json = reader?.ReadToEnd();
                reader?.Close();
                var data = JsonSerializer.Deserialize<List<TriggerNode>>(json, new JsonSerializerOptions { IncludeFields = true });
                TriggerManager.Instance.MergeTriggers(data, node.SerializedData);
              }
              else if (dialog.FileName.EndsWith(".gtp"))
              {
                var data = new byte[fileInfo.Length];
                fileInfo.OpenRead().Read(data);
                TriggerUtil.ImportFromGina(data, node.SerializedData);
              }
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Importing Triggers. Check Error Log for details.", EQLogParser.Resource.IMPORT_ERROR).ShowDialog();
          LOG.Error("Import Failure", ex);
        }
      }
    }

    internal static void Export(Syncfusion.UI.Xaml.TreeView.Engine.TreeViewNodeCollection collection, List<TriggerTreeViewNode> nodes)
    {
      if (nodes != null)
      {
        try
        {
          var exportList = new List<TriggerNode>();
          foreach (var selected in nodes)
          {
            // if the root is in there just use it
            if (selected == collection[0])
            {
              exportList = new List<TriggerNode>() { selected.SerializedData };
              break;
            }
            else if (selected.IsOverlay || selected == collection[1])
            {
              break;
            }

            var start = selected.ParentNode as TriggerTreeViewNode;
            var child = selected.SerializedData;
            TriggerNode newNode = null;
            while (start != null)
            {
              newNode = new TriggerNode
              {
                Name = start.SerializedData.Name,
                IsEnabled = start.SerializedData.IsEnabled,
                IsExpanded = start.SerializedData.IsExpanded,
                Nodes = new List<TriggerNode>() { child }
              };

              child = newNode;
              start = start.ParentNode as TriggerTreeViewNode;
            }

            if (newNode != null)
            {
              exportList.Add(newNode);
            }
          }

          if (exportList.Count > 0)
          {
            var result = System.Text.Json.JsonSerializer.Serialize(exportList, new JsonSerializerOptions { IncludeFields = true });
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            string filter = "Triggers File (*.tgf.gz)|*.tgf.gz";
            saveFileDialog.Filter = filter;
            if (saveFileDialog.ShowDialog().Value)
            {
              FileInfo gzipFileName = new FileInfo(saveFileDialog.FileName);
              FileStream gzipTargetAsStream = gzipFileName.Create();
              GZipStream gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
              var writer = new StreamWriter(gzipStream);
              writer?.Write(result);
              writer?.Close();
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Exporting Triggers. Check Error Log for details.", EQLogParser.Resource.EXPORT_ERROR).ShowDialog();
          LOG.Error(ex);
        }
      }
    }

    internal static void CheckGina(LineData lineData)
    {
      var action = lineData.Action;

      // if GINA data is recent then try to handle it
      if (action.IndexOf("{GINA:", StringComparison.OrdinalIgnoreCase) is int index && index > -1 &&
        (DateTime.Now - DateUtil.FromDouble(lineData.BeginTime)).TotalSeconds <= 20 && action.IndexOf("}") is int end && end > (index + 40))
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

                    if (!string.IsNullOrEmpty(xml) && xml.IndexOf("<a:ChunkData>") is int start && start > -1 
                      && xml.IndexOf("</a:ChunkData>") is int end && end > start)
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
              trigger.Name = Helpers.GetText(triggerNode, "Name");
              trigger.Pattern = Helpers.GetText(triggerNode, "TriggerText");
              trigger.Comments = Helpers.GetText(triggerNode, "Comments");

              var timerName = Helpers.GetText(triggerNode, "TimerName");
              if (!string.IsNullOrEmpty(timerName) && timerName != trigger.Name)
              {
                trigger.AltTimerName = timerName;
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "UseTextToVoice"), out bool useText))
              {
                goodTrigger = true;
                trigger.TextToSpeak = Helpers.GetText(triggerNode, "TextToVoiceText");
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "EnableRegex"), out bool regex))
              {
                trigger.UseRegex = regex;
              }

              if (bool.TryParse(Helpers.GetText(triggerNode, "InterruptSpeech"), out bool interrupt))
              {
                trigger.Priority = interrupt ? 1 : 5;
              }

              if ("Timer".Equals(Helpers.GetText(triggerNode, "TimerType")))
              {
                goodTrigger = true;
                trigger.EnableTimer = true;

                if (int.TryParse(Helpers.GetText(triggerNode, "TimerDuration"), out int duration))
                {
                  trigger.DurationSeconds = duration;
                }

                if (triggerNode.SelectSingleNode("TimerEndingTrigger") is XmlNode timerEndingNode)
                {
                  if (bool.TryParse(Helpers.GetText(timerEndingNode, "UseTextToVoice"), out bool useText2))
                  {
                    trigger.WarningTextToSpeak = Helpers.GetText(timerEndingNode, "TextToVoiceText");
                  }
                }

                if (int.TryParse(Helpers.GetText(triggerNode, "TimerEndingTime"), out int endTime))
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
                  trigger.TriggerAgainOption = 1;
                }
                else
                {
                  trigger.TriggerAgainOption = 2;
                }

                if (triggerNode.SelectSingleNode("TimerEndedTrigger") is XmlNode timerEndedNode)
                {
                  if (bool.TryParse(Helpers.GetText(timerEndedNode, "UseTextToVoice"), out bool useText2))
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
                      if (bool.TryParse(Helpers.GetText(enderNodes[0], "EnableRegex"), out bool regex2))
                      {
                        trigger.EndUseRegex = regex2;
                      }
                    }

                    if (enderNodes.Count > 1)
                    {
                      trigger.EndEarlyPattern2 = Helpers.GetText(enderNodes[1], "EarlyEndText");
                      if (bool.TryParse(Helpers.GetText(enderNodes[1], "EnableRegex"), out bool regex2))
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

    internal static void RunTest(string text, bool realTime)
    {
      (Application.Current.MainWindow as MainWindow).testButton.IsEnabled = false;
      if (!realTime)
      {
        var allLines = text.Split("\r\n").Where(line => !string.IsNullOrEmpty(line)).ToList();
        allLines.ForEach(line =>
        {
          if (line.Length > MainWindow.ACTION_INDEX)
          {
            var dateTime = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", line, 5);
            if (dateTime != DateTime.MinValue)
            {
              var beginTime = DateUtil.ToDouble(dateTime);
              TriggerManager.Instance.AddAction(new LineData { Line = line, Action = line.Substring(MainWindow.ACTION_INDEX), BeginTime = beginTime });
            }
          }
        });

        (Application.Current.MainWindow as MainWindow).testButton.IsEnabled = true;
      }
      else
      {    
        Task.Run(() =>
        {
          var allLines = text.Split("\r\n").Where(line => !string.IsNullOrEmpty(line) && line.Length > MainWindow.ACTION_INDEX).ToList();
          var firstDate = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", allLines.First(), 5);
          var lastDate = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", allLines.Last(), 5);
          if (firstDate != DateTime.MinValue && lastDate != DateTime.MinValue)
          {
            var startTime = DateUtil.ToDouble(firstDate);
            var endTime = DateUtil.ToDouble(lastDate);
            var range = (int)(endTime - startTime + 1);
            var data = new List<string>[range];

            int dataIndex = 0;
            data[dataIndex] = new List<string>();
            foreach (var line in allLines)
            {
              var current = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", line, 5);
              if (current != DateTime.MinValue)
              {
                var currentTime = DateUtil.ToDouble(current);
                if (currentTime == startTime)
                {
                  data[dataIndex].Add(line);
                }
                else
                {
                  var diff = (currentTime - startTime);
                  if (diff == 1)
                  {
                    dataIndex++;
                    data[dataIndex] = new List<string>();
                    data[dataIndex].Add(line);
                    startTime++;
                  }
                  else if (diff > 1)
                  {
                    for (int i = 1; i < diff; i++)
                    {
                      dataIndex++;
                      data[dataIndex] = new List<string>();
                    }

                    dataIndex++;
                    data[dataIndex] = new List<string>();
                    data[dataIndex].Add(line);
                    startTime += diff;
                  }
                }
              }
            }

            var nowTime = DateUtil.ToDouble(DateTime.Now);
            Application.Current.Dispatcher.Invoke(() =>
            {
              (Application.Current.MainWindow as MainWindow).testStatus.Text = "| Time Remaining: " + data.Length + " seconds";
              (Application.Current.MainWindow as MainWindow).testStatus.Visibility = Visibility.Visible;
            });

            int count = 0;
            foreach (var list in data)
            {
              if (list != null)
              {
                if (list.Count == 0)
                {
                  Thread.Sleep(1000);
                }
                else
                {
                  var start = DateTime.Now;
                  foreach (var line in list)
                  {
                    var action = line.Substring(MainWindow.ACTION_INDEX);
                    TriggerManager.Instance.AddAction(new LineData { Line = line, Action = action, BeginTime = nowTime });
                  }

                  var took = (DateTime.Now - start).Ticks;
                  long ticks = (long)10000000 - took;
                  Thread.Sleep(new TimeSpan(ticks));
                }
              }

              nowTime++;
              count++;
              var remaining = data.Length - count;
              Application.Current.Dispatcher.Invoke(() => (Application.Current.MainWindow as MainWindow).testStatus.Text = "| Time Remaining: " +  remaining + " seconds");
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
              (Application.Current.MainWindow as MainWindow).testStatus.Visibility = Visibility.Collapsed;
              (Application.Current.MainWindow as MainWindow).testButton.IsEnabled = true;
            });
          }
        });
      }
    }
  }
}

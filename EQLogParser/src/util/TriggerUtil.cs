using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace EQLogParser
{
  internal static class TriggerUtil
  {
    private static readonly ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    internal static double GetTimerBarHeight(double fontSize) => fontSize + 2;
    internal static void ImportTriggers(TriggerNode parent) => Import(parent);
    internal static void ImportOverlays(TriggerNode triggerNode) => Import(triggerNode, false);

    internal static string GetSelectedVoice()
    {
      string defaultVoice = null;

      try
      {
        var testSynth = new SpeechSynthesizer();
        defaultVoice = testSynth.GetInstalledVoices().Select(voice => voice.VoiceInfo.Name).ToList().FirstOrDefault();
      }
      catch (Exception e)
      {
        LOG.Debug(e);
      }

      defaultVoice = string.IsNullOrEmpty(defaultVoice) ? "" : defaultVoice;
      return ConfigUtil.GetSetting("TriggersSelectedVoice", defaultVoice);
    }

    internal static SpeechSynthesizer GetSpeechSynthesizer()
    {
      SpeechSynthesizer result = null;
      try
      {
        result = new SpeechSynthesizer();
        result.SetOutputToDefaultAudioDevice();
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
      return result;
    }

    internal static int GetVoiceRate()
    {
      var rate = ConfigUtil.GetSettingAsInteger("TriggersVoiceRate");
      return rate == int.MaxValue ? 0 : rate;
    }

    internal static bool TestRegexProperty(bool useRegex, string pattern, PatternEditor editor)
    {
      var isValid = useRegex ? TextUtils.IsValidRegex(pattern) : true;
      editor.SetForeground(isValid ? "ContentForeground" : "EQStopForegroundBrush");
      return isValid;
    }

    internal static SolidColorBrush GetBrush(string color)
    {
      SolidColorBrush brush = null;
      if (!string.IsNullOrEmpty(color))
      {
        if (!BrushCache.TryGetValue(color, out brush))
        {
          brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(color) };
          BrushCache[color] = brush;
        }
      }
      return brush;
    }

    internal static void Copy(object to, object from)
    {
      if (to is Trigger toTrigger && from is Trigger fromTrigger)
      {
        toTrigger.AltTimerName = TextUtils.Trim(fromTrigger.AltTimerName);
        toTrigger.Comments = TextUtils.Trim(fromTrigger.Comments);
        toTrigger.DurationSeconds = fromTrigger.DurationSeconds;
        toTrigger.Pattern = TextUtils.Trim(fromTrigger.Pattern);
        toTrigger.EndEarlyPattern = TextUtils.Trim(fromTrigger.EndEarlyPattern);
        toTrigger.EndEarlyPattern2 = TextUtils.Trim(fromTrigger.EndEarlyPattern2);
        toTrigger.EndUseRegex = fromTrigger.EndUseRegex;
        toTrigger.EndUseRegex2 = fromTrigger.EndUseRegex2;
        toTrigger.WorstEvalTime = fromTrigger.WorstEvalTime;
        toTrigger.ResetDurationSeconds = fromTrigger.ResetDurationSeconds;
        toTrigger.Priority = fromTrigger.Priority;
        toTrigger.RepeatedResetTime = fromTrigger.RepeatedResetTime;
        toTrigger.SelectedOverlays = fromTrigger.SelectedOverlays;
        toTrigger.TriggerAgainOption = fromTrigger.TriggerAgainOption;
        toTrigger.TimerType = fromTrigger.TimerType;
        toTrigger.UseRegex = fromTrigger.UseRegex;
        toTrigger.WarningSeconds = fromTrigger.WarningSeconds;
        toTrigger.EndTextToDisplay = TextUtils.Trim(fromTrigger.EndTextToDisplay);
        toTrigger.EndEarlyTextToDisplay = TextUtils.Trim(fromTrigger.EndEarlyTextToDisplay);
        toTrigger.TextToDisplay = TextUtils.Trim(fromTrigger.TextToDisplay);
        toTrigger.WarningTextToDisplay = TextUtils.Trim(fromTrigger.WarningTextToDisplay);
        toTrigger.EndTextToSpeak = TextUtils.Trim(fromTrigger.EndTextToSpeak);
        toTrigger.EndEarlyTextToSpeak = TextUtils.Trim(fromTrigger.EndEarlyTextToSpeak);
        toTrigger.TextToSpeak = TextUtils.Trim(fromTrigger.TextToSpeak);
        toTrigger.WarningTextToSpeak = TextUtils.Trim(fromTrigger.WarningTextToSpeak);
        toTrigger.SoundToPlay = TextUtils.Trim(fromTrigger.SoundToPlay);
        toTrigger.EndEarlySoundToPlay = TextUtils.Trim(fromTrigger.EndEarlySoundToPlay);
        toTrigger.EndSoundToPlay = TextUtils.Trim(fromTrigger.EndSoundToPlay);
        toTrigger.WarningSoundToPlay = TextUtils.Trim(fromTrigger.WarningSoundToPlay);

        if (toTrigger is TriggerPropertyModel toModel)
        {
          if (!string.IsNullOrEmpty(fromTrigger.ActiveColor))
          {
            toModel.TriggerActiveBrush = GetBrush(fromTrigger.ActiveColor);
          }

          if (!string.IsNullOrEmpty(fromTrigger.FontColor))
          {
            toModel.TriggerFontBrush = GetBrush(fromTrigger.FontColor);
          }

          var (textItems, timerItems) = GetOverlayItems(toModel.SelectedOverlays);
          toModel.SelectedTextOverlays = textItems;
          toModel.SelectedTimerOverlays = timerItems;
          toModel.ResetDurationTimeSpan = new TimeSpan(0, 0, (int)toModel.ResetDurationSeconds);
          toModel.SoundOrText = GetFromCodedSoundOrText(toModel.SoundToPlay, toModel.TextToSpeak, out _);
          toModel.EndEarlySoundOrText = GetFromCodedSoundOrText(toModel.EndEarlySoundToPlay, toModel.EndEarlyTextToSpeak, out _);
          toModel.EndSoundOrText = GetFromCodedSoundOrText(toModel.EndSoundToPlay, toModel.EndTextToSpeak, out _);
          toModel.WarningSoundOrText = GetFromCodedSoundOrText(toModel.WarningSoundToPlay, toModel.WarningTextToSpeak, out _);

          if (fromTrigger.EnableTimer && fromTrigger.TimerType == 0)
          {
            toModel.TimerType = 1;
            toModel.Node.TriggerData.TimerType = 1;
          }

          if (toModel.TimerType == 1)
          {
            toModel.DurationTimeSpan = new TimeSpan(0, 0, (int)toModel.DurationSeconds);
          }
        }
        else if (fromTrigger is TriggerPropertyModel fromModel)
        {
          toTrigger.ActiveColor = fromModel.TriggerActiveBrush?.Color.ToHexString();
          toTrigger.FontColor = fromModel.TriggerFontBrush?.Color.ToHexString();
          var selectedOverlays = fromModel.SelectedTextOverlays.Where(item => item.IsChecked).Select(item => item.Value).ToList();
          selectedOverlays.AddRange(fromModel.SelectedTimerOverlays.Where(item => item.IsChecked).Select(item => item.Value));
          toTrigger.SelectedOverlays = selectedOverlays;
          toTrigger.ResetDurationSeconds = fromModel.ResetDurationTimeSpan.TotalSeconds;

          MatchSoundFile(fromModel.SoundOrText, out var soundFile, out var text);
          toTrigger.SoundToPlay = soundFile;
          toTrigger.TextToSpeak = text;
          MatchSoundFile(fromModel.EndEarlySoundOrText, out soundFile, out text);
          toTrigger.EndEarlySoundToPlay = soundFile;
          toTrigger.EndEarlyTextToSpeak = text;
          MatchSoundFile(fromModel.EndSoundOrText, out soundFile, out text);
          toTrigger.EndSoundToPlay = soundFile;
          toTrigger.EndTextToSpeak = text;
          MatchSoundFile(fromModel.WarningSoundOrText, out soundFile, out text);
          toTrigger.WarningSoundToPlay = soundFile;
          toTrigger.WarningTextToSpeak = text;

          toTrigger.EnableTimer = fromModel.TimerType > 0;
          if (fromModel.TimerType == 1)
          {
            toTrigger.DurationSeconds = fromModel.DurationTimeSpan.TotalSeconds;
          }
        }
      }
      else if (to is Overlay toOverlay && from is Overlay fromOverlay)
      {
        toOverlay.ActiveColor = fromOverlay.ActiveColor;
        toOverlay.BackgroundColor = fromOverlay.BackgroundColor;
        toOverlay.FadeDelay = fromOverlay.FadeDelay;
        toOverlay.FontColor = fromOverlay.FontColor;
        toOverlay.FontFamily = fromOverlay.FontFamily;
        toOverlay.FontSize = fromOverlay.FontSize;
        toOverlay.Height = fromOverlay.Height;
        toOverlay.IdleColor = fromOverlay.IdleColor;
        toOverlay.IdleTimeoutSeconds = fromOverlay.IdleTimeoutSeconds;
        toOverlay.IsTextOverlay = fromOverlay.IsTextOverlay;
        toOverlay.IsTimerOverlay = fromOverlay.IsTimerOverlay;
        toOverlay.IsDefault = fromOverlay.IsDefault;
        toOverlay.Left = fromOverlay.Left;
        toOverlay.OverlayColor = fromOverlay.OverlayColor;
        toOverlay.OverlayComments = fromOverlay.OverlayComments;
        toOverlay.ResetColor = fromOverlay.ResetColor;
        toOverlay.SortBy = fromOverlay.SortBy;
        toOverlay.TimerMode = fromOverlay.TimerMode;
        toOverlay.Top = fromOverlay.Top;
        toOverlay.UseStandardTime = fromOverlay.UseStandardTime;
        toOverlay.Width = fromOverlay.Width;

        if (toOverlay is TimerOverlayPropertyModel toModel)
        {
          toModel.IdleTimeoutTimeSpan = new TimeSpan(0, 0, (int)toModel.IdleTimeoutSeconds);
          Application.Current.Resources["OverlayText-" + toModel.Node.Id] = toModel.Node.Name;

          AssignResource(toModel, fromOverlay, "OverlayColor", "OverlayBrush", "OverlayBrushColor");
          AssignResource(toModel, fromOverlay, "FontColor", "FontBrush", "TimerBarFontColor");
          AssignResource(toModel, fromOverlay, "ActiveColor", "ActiveBrush", "TimerBarActiveColor");
          AssignResource(toModel, fromOverlay, "IdleColor", "IdleBrush", "TimerBarIdleColor");
          AssignResource(toModel, fromOverlay, "ResetColor", "ResetBrush", "TimerBarResetColor");
          AssignResource(toModel, fromOverlay, "BackgroundColor", "BackgroundBrush", "TimerBarTrackColor");

          if (!string.IsNullOrEmpty(fromOverlay.FontSize) && fromOverlay.FontSize.Split("pt") is { Length: 2 } split
                                                          && double.TryParse(split[0], out var newFontSize))
          {
            Application.Current.Resources["TimerBarFontSize-" + toModel.Node.Id] = newFontSize;
            Application.Current.Resources["TimerBarHeight-" + toModel.Node.Id] = GetTimerBarHeight(newFontSize);
          }
        }
        else if (fromOverlay is TimerOverlayPropertyModel fromModel)
        {
          toOverlay.IdleTimeoutSeconds = fromModel.IdleTimeoutTimeSpan.TotalSeconds;
          toOverlay.OverlayColor = fromModel.OverlayBrush.Color.ToHexString();
          toOverlay.FontColor = fromModel.FontBrush.Color.ToHexString();
          toOverlay.ActiveColor = fromModel.ActiveBrush.Color.ToHexString();
          toOverlay.BackgroundColor = fromModel.BackgroundBrush.Color.ToHexString();
          toOverlay.IdleColor = fromModel.IdleBrush.Color.ToHexString();
          toOverlay.ResetColor = fromModel.ResetBrush.Color.ToHexString();
        }
        else if (toOverlay is TextOverlayPropertyModel toTextModel)
        {
          Application.Current.Resources["OverlayText-" + toTextModel.Node.Id] = toTextModel.Node.Name;

          AssignResource(toTextModel, fromOverlay, "OverlayColor", "OverlayBrush", "OverlayBrushColor");
          AssignResource(toTextModel, fromOverlay, "FontColor", "FontBrush", "TextOverlayFontColor");

          if (!string.IsNullOrEmpty(fromOverlay.FontFamily))
          {
            toTextModel.FontFamily = fromOverlay.FontFamily;
            Application.Current.Resources["TextOverlayFontFamily-" + toTextModel.Node.Id] = new FontFamily(toTextModel.FontFamily);
          }

          if (!string.IsNullOrEmpty(fromOverlay.FontSize) && fromOverlay.FontSize.Split("pt") is { Length: 2 } split
                                                          && double.TryParse(split[0], out var newFontSize))
          {
            Application.Current.Resources["TextOverlayFontSize-" + toTextModel.Node.Id] = newFontSize;
          }
        }
        else if (fromOverlay is TextOverlayPropertyModel fromTextModel)
        {
          toOverlay.FontColor = fromTextModel.FontBrush.Color.ToHexString();
          toOverlay.OverlayColor = fromTextModel.OverlayBrush.Color.ToHexString();
        }
      }
    }

    internal static void LoadOverlayStyles()
    {
      foreach (var od in TriggerStateManager.Instance.GetAllOverlays())
      {
        var node = new TriggerNode { Name = od.Name, Id = od.Id, OverlayData = od.OverlayData };
        Application.Current.Resources["OverlayText-" + od.Id] = od.Name;
        if (od.OverlayData?.IsTextOverlay == true)
        {
          // workaround to load styles
          Copy(new TextOverlayPropertyModel { Node = node }, od.OverlayData);
        }
        else if (od.OverlayData?.IsTextOverlay == false)
        {
          // workaround to load styles
          Copy(new TimerOverlayPropertyModel { Node = node }, od.OverlayData);
        }
      }
    }

    private static void AssignResource(dynamic toModel, object fromOverlay, string colorProperty, string brushProperty, string prefixx)
    {
      var colorValue = (string)fromOverlay.GetType().GetProperty(colorProperty)?.GetValue(fromOverlay);
      if (!string.IsNullOrEmpty(colorValue))
      {
        var brush = GetBrush(colorValue);
        toModel.GetType().GetProperty(brushProperty)?.SetValue(toModel, brush);
        Application.Current.Resources[$"{prefixx}-{toModel.Node.Id}"] = brush;
      }
    }

    private static (ObservableCollection<ComboBoxItemDetails>, ObservableCollection<ComboBoxItemDetails>)
      GetOverlayItems(List<string> overlayIds)
    {
      var text = new ObservableCollection<ComboBoxItemDetails>();
      var timer = new ObservableCollection<ComboBoxItemDetails>();

      foreach (var data in TriggerStateManager.Instance.GetAllOverlays())
      {
        var isChecked = overlayIds?.Contains(data.Id) ?? false;
        var details = new ComboBoxItemDetails { IsChecked = isChecked, Text = data.Name, Value = data.Id };
        if (data.OverlayData.IsTextOverlay)
        {
          text.Add(details);
        }
        else
        {
          timer.Add(details);
        }
      }

      return (text, timer);
    }

    internal static string GetFromCodedSoundOrText(string soundToPlay, string text, out bool isSound)
    {
      isSound = false;
      if (!string.IsNullOrEmpty(soundToPlay) && soundToPlay.EndsWith(".wav"))
      {
        isSound = true;
        return "<<" + soundToPlay + ">>";
      }
      else
      {
        return text;
      }
    }

    internal static string GetFromDecodedSoundOrText(string soundToPlay, string text, out bool isSound)
    {
      isSound = false;
      if (!string.IsNullOrEmpty(soundToPlay) && soundToPlay.EndsWith(".wav"))
      {
        isSound = true;
        return soundToPlay;
      }
      else
      {
        return text;
      }
    }

    internal static bool MatchSoundFile(string text, out string file, out string notFile)
    {
      file = null;
      notFile = text;
      var success = false;
      if (!string.IsNullOrEmpty(text))
      {
        var match = Regex.Match(text, @"<<(.*\.wav)>>$");
        if (match.Success)
        {
          file = match.Groups[1].Value;
          notFile = null;
          success = true;
        }
      }
      return success;
    }

    internal static bool CheckOptions(List<NumberOptions> options, MatchCollection matches, out double duration)
    {
      duration = double.NaN;

      foreach (Match match in matches)
      {
        if (!match.Success)
        {
          continue;
        }

        for (var i = 0; i < match.Groups.Count; i++)
        {
          var groupName = match.Groups[i].Name;
          var groupValue = match.Groups[i].Value;

          if ("TS".Equals(groupName, StringComparison.OrdinalIgnoreCase) && DateUtil.SimpleTimeToSeconds(groupValue) is uint sec)
          {
            if (sec > 0)
            {
              duration = sec;
            }
            else
            {
              return false;
            }
          }
          else
          {
            var passed = true;
            foreach (var option in options)
            {
              if (groupName == option.Key && !string.IsNullOrEmpty(option.Op))
              {
                if (StatsUtil.ParseUInt(groupValue) is var value && value != uint.MaxValue)
                {
                  switch (option.Op)
                  {
                    case ">":
                      passed = value > option.Value;
                      break;
                    case ">=":
                      passed = value >= option.Value;
                      break;
                    case "<":
                      passed = value < option.Value;
                      break;
                    case "<=":
                      passed = value <= option.Value;
                      break;
                    case "=":
                    case "==":
                      passed = value == option.Value;
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

      return true;
    }

    internal static List<TriggerCharacter> UpdateCharacterList(List<TriggerCharacter> list, TriggerConfig config)
    {
      var characters = config.Characters.ToList();

      if (list != null)
      {
        if (list.Count != characters.Count)
        {
          return characters;
        }
        else
        {
          for (var i = 0; i < list.Count; i++)
          {
            if (list[i].Id == characters[i].Id)
            {
              if (list[i].Name != characters[i].Name)
              {
                return characters;
              }

              list[i] = characters[i];
            }
            else
            {
              return characters;
            }
          }
        }
      }
      else
      {
        return characters;
      }

      return null;
    }

    internal static FileSystemWatcher CreateSoundsWatcher(ObservableCollection<string> fileList)
    {
      FileSystemWatcher watcher = null;
      try
      {
        LoadSounds(fileList);
        watcher = new FileSystemWatcher(@"data/sounds");
        watcher.Created += (sender, e) => OnWatcherUpdated(sender, e, fileList);
        watcher.Deleted += (sender, e) => OnWatcherUpdated(sender, e, fileList);
        watcher.Changed += (sender, e) => OnWatcherUpdated(sender, e, fileList);
        watcher.Filter = "*.wav";
        watcher.EnableRaisingEvents = true;
      }
      catch (Exception e)
      {
        LOG.Debug(e);
      }

      void OnWatcherUpdated(object sender, FileSystemEventArgs _, ObservableCollection<string> fileList)
      {
        LoadSounds(fileList);
      }
      return watcher;
    }

    internal static void LoadSounds(ObservableCollection<string> fileList)
    {
      var current = Directory.GetFiles(@"data/sounds", "*.wav").Select(Path.GetFileName).OrderBy(file => file).ToList();

      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        try
        {
          for (var i = 0; i < current.Count; i++)
          {
            if (i < fileList.Count)
            {
              if (fileList[i] != current[i])
              {
                fileList[i] = current[i];
              }
            }
            else
            {
              fileList.Add(current[i]);
            }
          }

          for (var j = fileList.Count - 1; j >= current.Count; j--)
          {
            fileList.RemoveAt(j);
          }
        }
        catch (Exception e)
        {
          LOG.Debug(e);
        }
      });
    }

    internal static void Export(IEnumerable<TriggerTreeViewNode> viewNodes)
    {
      if (viewNodes != null)
      {
        try
        {
          var exportList = new List<ExportTriggerNode>();
          foreach (var viewNode in viewNodes)
          {
            var node = Create(viewNode);
            var top = BuildUpTree(viewNode.ParentNode as TriggerTreeViewNode, node);
            BuildDownTree(viewNode, node);
            exportList.Add(top);
          }

          if (exportList.Count > 0)
          {
            var isTriggers = exportList[0].Name == TriggerStateManager.TRIGGERS;
            var result = JsonSerializer.Serialize(exportList);
            var saveFileDialog = new SaveFileDialog();
            var filter = isTriggers ? "Triggers File (*.tgf.gz)|*.tgf.gz" : "Overlays File (*.ogf.gz)|*.ogf.gz";
            saveFileDialog.Filter = filter;
            if (saveFileDialog.ShowDialog().Value)
            {
              var gzipFileName = new FileInfo(saveFileDialog.FileName);
              var gzipTargetAsStream = gzipFileName.Create();
              var gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
              var writer = new StreamWriter(gzipStream);
              writer?.Write(result);
              writer?.Close();
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Exporting Triggers/Overlays. Check Error Log for Details.", Resource.EXPORT_ERROR).ShowDialog();
          LOG.Error(ex);
        }
      }
    }

    private static void Import(TriggerNode parent, bool triggers = true)
    {
      try
      {
        var defExt = triggers ? ".tgf.gz" : ".ogf.gz";
        var filter = triggers ? "All Supported Files|*.tgf.gz;*.gtp" : "All Supported Files|*.ogf.gz";

        // WPF doesn't have its own file chooser so use Win32 Version
        var dialog = new OpenFileDialog
        {
          // filter to txt files
          DefaultExt = defExt,
          Filter = filter
        };

        if (dialog.ShowDialog().Value)
        {
          // limit to 100 megs just incase
          var fileInfo = new FileInfo(dialog.FileName);
          if (fileInfo.Exists && fileInfo.Length < 100000000)
          {
            if (dialog.FileName.EndsWith("tgf.gz") || dialog.FileName.EndsWith("ogf.gz"))
            {
              var decompressionStream = new GZipStream(fileInfo.OpenRead(), CompressionMode.Decompress);
              var reader = new StreamReader(decompressionStream);
              var json = reader?.ReadToEnd();
              reader?.Close();
              var data = JsonSerializer.Deserialize<List<ExportTriggerNode>>(json, new JsonSerializerOptions { IncludeFields = true });
              if (triggers)
              {
                TriggerStateManager.Instance.ImportTriggers(parent, data);
              }
              else
              {
                TriggerStateManager.Instance.ImportOverlays(parent, data);
              }
            }
            else if (dialog.FileName.EndsWith(".gtp"))
            {
              var data = new byte[fileInfo.Length];
              fileInfo.OpenRead().Read(data);
              var imported = GinaUtil.CovertToTriggerNodes(data);
              TriggerStateManager.Instance.ImportTriggers(parent, imported);
            }
          }
        }
      }
      catch (Exception ex)
      {
        new MessageWindow("Problem Importing Triggers. Check Error Log for details.", Resource.IMPORT_ERROR).ShowDialog();
        LOG.Error("Import Triggers Failure", ex);
      }
    }

    private static ExportTriggerNode Create(TriggerTreeViewNode viewNode)
    {
      return new ExportTriggerNode
      {
        Name = viewNode.SerializedData.Name,
        TriggerData = viewNode.SerializedData.TriggerData,
        OverlayData = viewNode.SerializedData.OverlayData,
      };
    }

    private static ExportTriggerNode BuildUpTree(TriggerTreeViewNode viewNode, ExportTriggerNode child = null)
    {
      if (viewNode != null)
      {
        var node = Create(viewNode);

        if (child != null)
        {
          node.Nodes.Add(child);
        }

        if (viewNode.ParentNode is TriggerTreeViewNode parent)
        {
          return BuildUpTree(parent, node);
        }

        return node;
      }

      return child;
    }

    private static void BuildDownTree(TriggerTreeViewNode viewNode, ExportTriggerNode node)
    {
      if (viewNode.HasChildNodes)
      {
        foreach (var childView in viewNode.ChildNodes.Cast<TriggerTreeViewNode>())
        {
          var child = Create(childView);
          node.Nodes.Add(child);
          BuildDownTree(childView, child);
        }
      }
    }
  }
}

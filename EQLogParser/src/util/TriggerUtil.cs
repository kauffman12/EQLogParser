using log4net;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Path = System.IO.Path;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EQLogParser
{
  internal static partial class TriggerUtil
  {
    public const string ShareTrigger = "EQLPT";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();
    private static readonly ConcurrentDictionary<string, CharacterData> QuickShareCache = new();
    private const string ExtTrigger = "tgf";
    private const string ExtOverlay = "ogf";
    internal static double GetTimerBarHeight(double fontSize) => fontSize + 2;
    internal static void ImportTriggers(TriggerNode parent) => Import(parent);
    internal static void ImportOverlays(TriggerNode triggerNode) => Import(triggerNode, false);

    private static readonly Size OriginalResolution = new(1920, 1080); // Hard-coded original screen resolution
    private const double OriginalTop = 550; // Hard-coded original top position
    private const double OriginalLeft = 650; // Hard-coded original left position

    internal static Point CalculateDefaultTextOverlayPosition()
    {
      // Fetch the current screen's resolution
      var newResolution = new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
      var relativeTop = OriginalTop / OriginalResolution.Height;
      var relativeLeft = OriginalLeft / OriginalResolution.Width;
      var newTop = relativeTop * newResolution.Height;
      var newLeft = relativeLeft * newResolution.Width;
      return new Point(newLeft, newTop);
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
        Log.Error(ex);
      }
      return result;
    }

    internal static bool TestRegexProperty(bool useRegex, string pattern, PatternEditor editor)
    {
      var isValid = !useRegex || TextUtils.IsValidRegex(pattern);
      editor.SetForeground(isValid ? "ContentForeground" : "EQStopForegroundBrush");
      return isValid;
    }

    internal static SolidColorBrush GetBrush(string color)
    {
      SolidColorBrush brush = null;
      if (!string.IsNullOrEmpty(color) && !BrushCache.TryGetValue(color, out brush))
      {
        brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(color)! };
        BrushCache[color] = brush;
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
        toTrigger.TimesToLoop = fromTrigger.TimesToLoop;
        toTrigger.EndTextToDisplay = TextUtils.Trim(fromTrigger.EndTextToDisplay);
        toTrigger.EndEarlyTextToDisplay = TextUtils.Trim(fromTrigger.EndEarlyTextToDisplay);
        toTrigger.TextToDisplay = TextUtils.Trim(fromTrigger.TextToDisplay);
        toTrigger.TextToShare = TextUtils.Trim(fromTrigger.TextToShare);
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
          else
          {
            toModel.TriggerActiveBrush = null;
          }

          if (!string.IsNullOrEmpty(fromTrigger.FontColor))
          {
            toModel.TriggerFontBrush = GetBrush(fromTrigger.FontColor);
          }
          else
          {
            toModel.TriggerFontBrush = null;
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

          // any timer type except short duration
          if (toModel.TimerType > 0 && toModel.TimerType != 2)
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

          if (fromModel.TimerType > 0 && fromModel.TimerType != 2)
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
        toOverlay.ShowActive = fromOverlay.ShowActive;
        toOverlay.ShowIdle = fromOverlay.ShowIdle;
        toOverlay.ShowReset = fromOverlay.ShowReset;
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

          if (!string.IsNullOrEmpty(fromOverlay.FontSize) && fromOverlay.FontSize.Split("pt") is { Length: 2 } split &&
            double.TryParse(split[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var newFontSize))
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

          if (!string.IsNullOrEmpty(fromOverlay.FontSize) && fromOverlay.FontSize.Split("pt") is { Length: 2 } split &&
            double.TryParse(split[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var newFontSize))
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

      return text;
    }

    internal static string GetFromDecodedSoundOrText(string soundToPlay, string text, out bool isSound)
    {
      isSound = false;
      if (!string.IsNullOrEmpty(soundToPlay) && soundToPlay.EndsWith(".wav"))
      {
        isSound = true;
        return soundToPlay;
      }

      return text;
    }

    internal static bool MatchSoundFile(string text, out string file, out string notFile)
    {
      file = null;
      notFile = text;
      var success = false;
      if (!string.IsNullOrEmpty(text))
      {
        var match = WavFileRegex().Match(text);
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

      foreach (var match in matches.Cast<Match>())
      {
        if (!match.Success)
        {
          continue;
        }

        for (var i = 0; i < match.Groups.Count; i++)
        {
          var groupName = match.Groups[i].Name;
          var groupValue = match.Groups[i].Value;

          if ("TS".Equals(groupName, StringComparison.OrdinalIgnoreCase) && DateUtil.SimpleTimeToSeconds(groupValue) is var sec)
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
        if (Directory.Exists("data/sounds"))
        {
          LoadSounds(fileList);
          watcher = new FileSystemWatcher(@"data/sounds");
          watcher.Created += (_, _) => OnWatcherUpdated(fileList);
          watcher.Deleted += (_, _) => OnWatcherUpdated(fileList);
          watcher.Changed += (_, _) => OnWatcherUpdated(fileList);
          watcher.Filter = "*.wav";
          watcher.EnableRaisingEvents = true;
        }
      }
      catch (Exception e)
      {
        Log.Debug(e);
      }

      return watcher;

      void OnWatcherUpdated(ObservableCollection<string> soundFiles)
      {
        LoadSounds(soundFiles);
      }
    }

    private static void LoadSounds(ObservableCollection<string> fileList)
    {
      var current = Directory.GetFiles("data/sounds", "*.wav").Select(Path.GetFileName).OrderBy(file => file).ToList();

      UiUtil.InvokeNow(() =>
      {
        try
        {
          for (var i = 0; i < current.Count; i++)
          {
            if (i < fileList.Count)
            {
              if (fileList[i] == null || fileList[i] != current[i])
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
          Log.Debug(e);
        }
      });
    }

    internal static void Export(IEnumerable<TriggerTreeViewNode> viewNodes)
    {
      if (BuildExportList(viewNodes) is { } exportList)
      {
        try
        {
          if (exportList.Count > 0)
          {
            var isTriggers = exportList[0].Name == TriggerStateManager.Triggers;
            var result = JsonSerializer.Serialize(exportList);
            var saveFileDialog = new SaveFileDialog();
            var filter = isTriggers ? $"Triggers File (*.{ExtTrigger}.gz)|*.{ExtTrigger}.gz" : $"Overlays File (*.{ExtOverlay}.gz)|*.{ExtOverlay}.gz";
            saveFileDialog.Filter = filter;

            if (saveFileDialog.ShowDialog() == true)
            {
              var gzipFileName = new FileInfo(saveFileDialog.FileName);
              var gzipTargetAsStream = gzipFileName.Create();
              var gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
              var writer = new StreamWriter(gzipStream);
              writer.Write(result);
              writer.Close();
            }
          }
          else
          {
            new MessageWindow("No Triggers found in Selection. Nothing to Export.", Resource.EXPORT_ERROR).ShowDialog();
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Exporting Triggers/Overlays. Check Error Log for Details.", Resource.EXPORT_ERROR).ShowDialog();
          Log.Error(ex);
        }
      }
    }

    internal static void CheckQuickShare(ChatType chatType, string action, double dateTime, string characterId, string processorName)
    {
      if (chatType.Sender == null || action == null)
      {
        return;
      }

      // handle stop command
      if (chatType.SenderIsYou && (chatType.TextStart - 27) is var s and > 0 && action.Length > s
          && action.AsSpan()[s..].StartsWith("{EQLP:STOP}"))
      {
        TriggerManager.Instance.TriggersUpdated();
        return;
      }

      // if Quick Share data is recent then try to handle it
      if (action.IndexOf($"{{{ShareTrigger}:", StringComparison.OrdinalIgnoreCase) is var index and > -1 &&
          action.IndexOf('}') is var end && end > (index + 10))
      {
        var start = index + 7;
        var finish = end - start;
        if (action.Length > (start + finish))
        {
          var quickShareKey = action.Substring(start, finish);
          var fullKey = $"{{{ShareTrigger}:{quickShareKey}}}";
          if (!string.IsNullOrEmpty(quickShareKey))
          {
            var to = chatType.Channel == ChatChannels.Tell ? "You" : chatType.Channel;
            var record = new QuickShareRecord
            {
              BeginTime = dateTime,
              Key = fullKey,
              From = chatType.Sender,
              To = (to == "You" && processorName != null && characterId != TriggerStateManager.DefaultUser) ? processorName : TextUtils.ToUpper(to),
              IsMine = chatType.SenderIsYou,
              Type = ShareTrigger
            };

            RecordManager.Instance.Add(record);

            // don't handle immediately unless enabled
            if (characterId != null && !chatType.SenderIsYou && (chatType.Channel is ChatChannels.Group or ChatChannels.Guild or
                  ChatChannels.Raid or ChatChannels.Tell) && ConfigUtil.IfSet("TriggersWatchForQuickShare") && !RecordManager.Instance.IsQuickShareMine(fullKey))
            {
              // ignore if we're still processing a bunch
              if (QuickShareCache.Count > 5)
              {
                return;
              }

              lock (QuickShareCache)
              {
                if (!QuickShareCache.TryGetValue(quickShareKey, out var value))
                {
                  QuickShareCache[quickShareKey] = new CharacterData { Sender = chatType.Sender };
                  QuickShareCache[quickShareKey].CharacterIds.Add(characterId);
                  RunQuickShareTask(quickShareKey);
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
      if (shareKey.IndexOf($"{{{ShareTrigger}:", StringComparison.OrdinalIgnoreCase) is var index and > -1 &&
          shareKey.IndexOf('}') is var end && end > (index + 10))
      {
        var start = index + 7;
        var finish = end - start;
        if (shareKey.Length > (start + finish))
        {
          var quickShareKey = shareKey.Substring(start, finish);
          if (!string.IsNullOrEmpty(quickShareKey))
          {
            QuickShareCache.TryAdd(quickShareKey, new CharacterData { Sender = from });
            if (QuickShareCache.Count == 1)
            {
              RunQuickShareTask(quickShareKey);
            }
          }
        }
      }
    }

    internal static async Task ShareAsync(List<TriggerTreeViewNode> viewNodes)
    {
      if (BuildExportList(viewNodes) is { Count: > 0 } exportList)
      {
        try
        {
          var result = JsonSerializer.Serialize(exportList);
          var inputBytes = Encoding.UTF8.GetBytes(result);
          using var stream = new MemoryStream();
          await using (var gzipStream = new GZipStream(stream, CompressionMode.Compress))
          {
            gzipStream.Write(inputBytes, 0, inputBytes.Length);
            await gzipStream.FlushAsync();
          }

          var content = new ByteArrayContent(stream.ToArray());
          content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
          content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
          {
            Name = "file",
            FileName = "test"
          };

          using var multiPart = new MultipartFormDataContent();
          multiPart.Add(content, "file");

          var request = new HttpRequestMessage(HttpMethod.Post, "http://share.kizant.net:8080/upload");
          request.Headers.Add("EQLogParser", "true");
          request.Content = multiPart;

          var response = await MainActions.TheHttpClient.SendAsync(request);
          if (response.IsSuccessStatusCode)
          {
            if (await response.Content.ReadAsStringAsync() is var shareLink && shareLink != "")
            {
              var withKey = $"{{{ShareTrigger}:{shareLink}}}";

              var record = new QuickShareRecord
              {
                BeginTime = DateUtil.ToDouble(DateTime.Now),
                Key = withKey,
                From = "You",
                IsMine = true,
                To = "Created Share Key",
                Type = ShareTrigger
              };

              RecordManager.Instance.Add(record);
              new MessageWindow($"Share Key: {withKey}", Resource.SHARE_MESSAGE, withKey).ShowDialog();
            }
          }
          else
          {
            var detailedErrorResponse = await response.Content.ReadAsStringAsync();
            if (detailedErrorResponse == "Content is too large")
            {
              new MessageWindow($"Problem Sharing: Maximum Share Size Exceeded", Resource.SHARE_ERROR).ShowDialog();
            }
            else
            {
              new MessageWindow($"Problem Sharing: {response.ReasonPhrase}", Resource.SHARE_ERROR).ShowDialog();
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Sharing. Check Error Log for Details.", Resource.SHARE_ERROR).ShowDialog();
          Log.Error(ex);
        }
      }
    }

    private static void RunQuickShareTask(string quickShareKey, int tries = 0)
    {
      Task.Delay(1200).ContinueWith(async _ =>
      {
        try
        {
          var url = $"http://share.kizant.net:8080/download/{quickShareKey}";
          var response = MainActions.TheHttpClient.GetAsync(url).Result;
          if (response.IsSuccessStatusCode)
          {
            await using var decompressionStream = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            await decompressionStream.CopyToAsync(ms);
            ms.Position = 0;
            ImportFromQuickShare(Encoding.UTF8.GetString(ms.ToArray()), quickShareKey);
          }
          else
          {
            if (tries == 0)
            {
              // try a 2nd time
              RunQuickShareTask(quickShareKey, 1);
              return;
            }

            UiUtil.InvokeAsync(() =>
            {
              new MessageWindow($"Unable to Import. Key Expired.", Resource.RECEIVED_SHARE).ShowDialog();
              NextQuickShareTask(quickShareKey);
            });
          }
        }
        catch (Exception ex)
        {
          if (ex.Message.Contains("An attempt was made to access a socket in a way forbidden by its access permissions"))
          {
            UiUtil.InvokeAsync(() =>
            {
              new MessageWindow("Unable to Import. Blocked by Firewall?", Resource.SHARE_ERROR).ShowDialog();
              Log.Error("Error Downloading Quick Share", ex);
              NextQuickShareTask(quickShareKey);
            });
          }
          else
          {
            if (tries == 0)
            {
              // try a 2nd time
              RunQuickShareTask(quickShareKey, 1);
              return;
            }

            UiUtil.InvokeAsync(() =>
            {
              new MessageWindow("Unable to Import. May be Expired.\nCheck Error Log for Details.", Resource.SHARE_ERROR).ShowDialog();
            });

            Log.Error("Error Downloading Quick Share", ex);
            NextQuickShareTask(quickShareKey);
          }
        }
      });
    }

    private static void ImportFromQuickShare(string data, string quickShareKey)
    {
      if (QuickShareCache.TryGetValue(quickShareKey, out var quickShareData))
      {
        var player = quickShareData.Sender;
        var characterIds = quickShareData.CharacterIds;

        UiUtil.InvokeAsync(() =>
        {
          var nodes = JsonSerializer.Deserialize<List<ExportTriggerNode>>(data, new JsonSerializerOptions { IncludeFields = true });
          if (nodes.Count > 0 && nodes[0].Nodes.Count == 0)
          {
            var badMessage = "Quick Share Received";
            if (!string.IsNullOrEmpty(player))
            {
              badMessage += " from " + player;
            }

            badMessage += " but no supported Triggers or Overlays found.";
            new MessageWindow(badMessage, Resource.RECEIVED_SHARE).ShowDialog();
          }
          else
          {
            var message = "Merge Quick Share or Import to New Folder?\r\n";
            if (!string.IsNullOrEmpty(player))
            {
              message = $"Merge Quick Share from {player} or Import to New Folder?\r\n";
            }

            var msgDialog = new MessageWindow(message, Resource.RECEIVED_SHARE, MessageWindow.IconType.Question,
              "New Folder", "Merge", characterIds.Count > 0);
            msgDialog.ShowDialog();

            if (msgDialog.IsYes2Clicked)
            {
              TriggerStateManager.Instance.ImportTriggers("", nodes, characterIds);
            }
            if (msgDialog.IsYes1Clicked)
            {
              var folderName = (player == null) ? "New Folder" : "From " + player;
              folderName += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
              TriggerStateManager.Instance.ImportTriggers(folderName, nodes, characterIds);
            }
          }

          NextQuickShareTask(quickShareKey);
        });
      }
    }

    private static void NextQuickShareTask(string quickShareKey)
    {
      QuickShareCache.TryRemove(quickShareKey, out var _);

      if (QuickShareCache.Count > 0)
      {
        var nextKey = QuickShareCache.Keys.First();
        RunQuickShareTask(nextKey);
      }
    }

    private static List<ExportTriggerNode> BuildExportList(IEnumerable<TriggerTreeViewNode> viewNodes)
    {
      var exportList = new List<ExportTriggerNode>();
      if (viewNodes != null)
      {
        foreach (var viewNode in viewNodes)
        {
          var node = Create(viewNode);
          var top = BuildUpTree(viewNode.ParentNode as TriggerTreeViewNode, node);
          BuildDownTree(viewNode, node);
          exportList.Add(top);
        }
      }
      return exportList;
    }

    private static void Import(TriggerNode parent, bool triggers = true)
    {
      try
      {
        var defExt = triggers ? $".{ExtTrigger}.gz" : $".{ExtOverlay}.gz";
        var filter = triggers ? $"All Supported Files|*.{ExtTrigger}.gz;*.gtp" : $"All Supported Files|*.{ExtOverlay}.gz";

        // WPF doesn't have its own file chooser so use Win32 Version
        var dialog = new OpenFileDialog
        {
          // filter to txt files
          DefaultExt = defExt,
          Filter = filter
        };

        if (dialog.ShowDialog() == true)
        {
          // limit to 100 megs just in case
          var fileInfo = new FileInfo(dialog.FileName);
          if (fileInfo.Exists && fileInfo.Length < 100000000)
          {
            if (dialog.FileName.EndsWith($"{ExtTrigger}.gz") || dialog.FileName.EndsWith($"{ExtOverlay}.gz"))
            {
              var decompressionStream = new GZipStream(fileInfo.OpenRead(), CompressionMode.Decompress);
              var reader = new StreamReader(decompressionStream);
              var json = reader.ReadToEnd();
              reader.Close();
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
        Log.Error("Import Triggers Failure", ex);
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

    [GeneratedRegex(@"<<(.*\.wav)>>$")]
    private static partial Regex WavFileRegex();
  }

  internal class CharacterData
  {
    public string Sender { get; set; }
    public HashSet<string> CharacterIds { get; set; } = [];
  }
}

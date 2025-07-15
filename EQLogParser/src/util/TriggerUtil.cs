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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace EQLogParser
{
  internal static partial class TriggerUtil
  {
    public const string ShareOverlay = "EQLPO";
    public const string ShareTrigger = "EQLPT";
    internal static async Task ImportTriggers(TriggerNode parent) => await Import(parent);
    internal static async Task ImportOverlays(TriggerNode triggerNode) => await Import(triggerNode, false);

    private const string ExtTrigger = "tgf";
    private const string ExtOverlay = "ogf";
    private const double OriginalTop = 550; // Hard-coded original top position
    private const double OriginalLeft = 650; // Hard-coded original left position
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly ConcurrentDictionary<string, CharacterData> QuickShareCache = new();
    private static readonly JsonSerializerOptions SerializationOptions = new JsonSerializerOptions { IncludeFields = true };
    private static readonly Size OriginalResolution = new(1920, 1080); // Hard-coded original screen resolution
    private static readonly Regex ShareRegex = new(@"\{(" + ShareTrigger + "|" + ShareOverlay + @"):([^\{\}]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    internal static double CalculateTimerBarHeight(double fontSize, FontFamily family)
    {
      if (family != null)
      {
        return UiElementUtil.CalculateTextBoxHeight(family, fontSize, new Thickness(), new Thickness());
      }

      return fontSize + 2;
    }

    internal static bool TestRegexProperty(bool useRegex, string pattern, PatternEditor editor)
    {
      var isValid = !useRegex || TextUtils.IsValidRegex(pattern);
      editor.SetForeground(isValid ? "ContentForeground" : "EQStopForegroundBrush");
      return isValid;
    }

    internal static async Task Copy(object to, object from)
    {
      if (to is Trigger toTrigger && from is Trigger fromTrigger)
      {
        toTrigger.AltTimerName = TextUtils.Trim(fromTrigger.AltTimerName);
        toTrigger.Comments = TextUtils.Trim(fromTrigger.Comments);
        toTrigger.DurationSeconds = fromTrigger.DurationSeconds;
        toTrigger.Pattern = TextUtils.Trim(fromTrigger.Pattern);
        toTrigger.PreviousPattern = TextUtils.Trim(fromTrigger.PreviousPattern);
        toTrigger.EndEarlyPattern = TextUtils.Trim(fromTrigger.EndEarlyPattern);
        toTrigger.EndEarlyPattern2 = TextUtils.Trim(fromTrigger.EndEarlyPattern2);
        toTrigger.EndUseRegex = fromTrigger.EndUseRegex;
        toTrigger.EndUseRegex2 = fromTrigger.EndUseRegex2;
        toTrigger.WorstEvalTime = fromTrigger.WorstEvalTime;
        toTrigger.ResetDurationSeconds = fromTrigger.ResetDurationSeconds;
        toTrigger.Priority = fromTrigger.Priority;
        toTrigger.RepeatedResetTime = fromTrigger.RepeatedResetTime;
        toTrigger.LockoutTime = fromTrigger.LockoutTime;
        toTrigger.SelectedOverlays = fromTrigger.SelectedOverlays;
        toTrigger.TriggerAgainOption = fromTrigger.TriggerAgainOption;
        toTrigger.TimerType = fromTrigger.TimerType;
        toTrigger.UseRegex = fromTrigger.UseRegex;
        toTrigger.PreviousUseRegex = fromTrigger.PreviousUseRegex;
        toTrigger.WarningSeconds = fromTrigger.WarningSeconds;
        toTrigger.TimesToLoop = fromTrigger.TimesToLoop;
        toTrigger.EndTextToDisplay = TextUtils.Trim(fromTrigger.EndTextToDisplay);
        toTrigger.EndEarlyTextToDisplay = TextUtils.Trim(fromTrigger.EndEarlyTextToDisplay);
        toTrigger.TextToDisplay = TextUtils.Trim(fromTrigger.TextToDisplay);
        toTrigger.TextToShare = TextUtils.Trim(fromTrigger.TextToShare);
        toTrigger.ChatWebhook = TextUtils.Trim(fromTrigger.ChatWebhook);
        toTrigger.TextToSendToChat = TextUtils.Trim(fromTrigger.TextToSendToChat);
        toTrigger.WarningTextToDisplay = TextUtils.Trim(fromTrigger.WarningTextToDisplay);
        toTrigger.EndTextToSpeak = TextUtils.Trim(fromTrigger.EndTextToSpeak);
        toTrigger.EndEarlyTextToSpeak = TextUtils.Trim(fromTrigger.EndEarlyTextToSpeak);
        toTrigger.TextToSpeak = TextUtils.Trim(fromTrigger.TextToSpeak);
        toTrigger.WarningTextToSpeak = TextUtils.Trim(fromTrigger.WarningTextToSpeak);
        toTrigger.SoundToPlay = TextUtils.Trim(fromTrigger.SoundToPlay);
        toTrigger.EndEarlySoundToPlay = TextUtils.Trim(fromTrigger.EndEarlySoundToPlay);
        toTrigger.EndSoundToPlay = TextUtils.Trim(fromTrigger.EndSoundToPlay);
        toTrigger.WarningSoundToPlay = TextUtils.Trim(fromTrigger.WarningSoundToPlay);
        toTrigger.Volume = fromTrigger.Volume;

        if (toTrigger is TriggerPropertyModel toModel)
        {
          toModel.TriggerActiveBrush = UiUtil.GetBrush(fromTrigger.ActiveColor);
          toModel.TriggerFontBrush = UiUtil.GetBrush(fromTrigger.FontColor);
          toModel.TriggerIconSource = UiElementUtil.CreateBitmap(fromTrigger.IconSource);

          var (textItems, timerItems) = await GetOverlayItems(toModel.SelectedOverlays);
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
          toTrigger.IconSource = fromModel.TriggerIconSource?.UriSource?.OriginalString;
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
        toOverlay.FontWeight = fromOverlay.FontWeight;
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
        toOverlay.HorizontalAlignment = fromOverlay.HorizontalAlignment;
        toOverlay.VerticalAlignment = fromOverlay.VerticalAlignment;
        toOverlay.ClosePattern = TextUtils.Trim(fromOverlay.ClosePattern);
        toOverlay.UseCloseRegex = fromOverlay.UseCloseRegex;

        if (toOverlay is TimerOverlayPropertyModel toModel)
        {
          toModel.IdleTimeoutTimeSpan = new TimeSpan(0, 0, (int)toModel.IdleTimeoutSeconds);
          Application.Current.Resources["OverlayText-" + toModel.Node.Id] = toModel.Node.Name;

          // NOTE: not currently implement for Timers
          Application.Current.Resources["OverlayHorizontalAlignment-" + toModel.Node.Id] = (HorizontalAlignment)toModel.HorizontalAlignment;
          // make sure old default data is no longer set (should be fixed during startup)
          Application.Current.Resources["OverlayVerticalAlignment-" + toModel.Node.Id] = (VerticalAlignment)toModel.VerticalAlignment;

          AssignResource(toModel, fromOverlay, "OverlayColor", "OverlayBrush", "OverlayBrushColor");
          AssignResource(toModel, fromOverlay, "FontColor", "FontBrush", "TimerBarFontColor");
          AssignResource(toModel, fromOverlay, "ActiveColor", "ActiveBrush", "TimerBarActiveColor");
          AssignResource(toModel, fromOverlay, "IdleColor", "IdleBrush", "TimerBarIdleColor");
          AssignResource(toModel, fromOverlay, "ResetColor", "ResetBrush", "TimerBarResetColor");
          AssignResource(toModel, fromOverlay, "BackgroundColor", "BackgroundBrush", "TimerBarTrackColor");

          FontFamily family = null;
          if (!string.IsNullOrEmpty(fromOverlay.FontFamily))
          {
            toModel.FontFamily = fromOverlay.FontFamily;
            family = new FontFamily(toModel.FontFamily);
            Application.Current.Resources["TimerBarFontFamily-" + toModel.Node.Id] = family;
          }

          var fontSize = UiElementUtil.ParseFontSize(fromOverlay.FontSize);
          Application.Current.Resources["TimerBarFontSize-" + toModel.Node.Id] = fontSize;
          var fontWeight = UiElementUtil.GetFontWeightByName(fromOverlay.FontWeight);
          Application.Current.Resources["TimerBarFontWeight-" + toModel.Node.Id] = fontWeight;
          Application.Current.Resources["TimerBarHeight-" + toModel.Node.Id] = CalculateTimerBarHeight(fontSize, family);

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

          Application.Current.Resources["OverlayHorizontalAlignment-" + toTextModel.Node.Id] = (HorizontalAlignment)toTextModel.HorizontalAlignment;
          // make sure old default data is no longer set (should be fixed during startup)
          Application.Current.Resources["OverlayVerticalAlignment-" + toTextModel.Node.Id] = (VerticalAlignment)toTextModel.VerticalAlignment;

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

          if (!string.IsNullOrEmpty(fromOverlay.FontWeight))
          {
            toTextModel.FontWeight = fromOverlay.FontWeight;
            Application.Current.Resources["TextOverlayFontWeight-" + toTextModel.Node.Id] = UiElementUtil.GetFontWeightByName(toTextModel.FontWeight);
          }
        }
        else if (fromOverlay is TextOverlayPropertyModel fromTextModel)
        {
          toOverlay.FontColor = fromTextModel.FontBrush.Color.ToHexString();
          toOverlay.OverlayColor = fromTextModel.OverlayBrush.Color.ToHexString();
        }
      }
    }

    internal static async Task LoadOverlayStyles()
    {
      foreach (var od in await TriggerStateManager.Instance.GetAllOverlays())
      {
        var node = new TriggerNode { Name = od.Name, Id = od.Id, OverlayData = od.OverlayData };
        await LoadOverlayStyle(node, od.OverlayData);
      }
    }

    internal static async Task LoadOverlayStyle(TriggerNode node, Overlay overlay)
    {
      Application.Current.Resources["OverlayText-" + node.Id] = node.Name;
      if (overlay?.IsTextOverlay == true)
      {
        // workaround to load styles
        await Copy(new TextOverlayPropertyModel { Node = node }, overlay);
      }
      else if (overlay?.IsTextOverlay == false)
      {
        // workaround to load styles
        await Copy(new TimerOverlayPropertyModel { Node = node }, overlay);
      }
    }

    private static void AssignResource(dynamic toModel, object fromOverlay, string colorProperty, string brushProperty, string prefix)
    {
      var colorValue = (string)fromOverlay.GetType().GetProperty(colorProperty)?.GetValue(fromOverlay);
      if (!string.IsNullOrEmpty(colorValue))
      {
        var brush = UiUtil.GetBrush(colorValue);
        toModel.GetType().GetProperty(brushProperty)?.SetValue(toModel, brush);
        Application.Current.Resources[$"{prefix}-{toModel.Node.Id}"] = brush;
      }
    }

    private static async Task<(ObservableCollection<ComboBoxItemDetails>, ObservableCollection<ComboBoxItemDetails>)> GetOverlayItems(List<string> overlayIds)
    {
      var text = new ObservableCollection<ComboBoxItemDetails>();
      var timer = new ObservableCollection<ComboBoxItemDetails>();

      foreach (var data in await TriggerStateManager.Instance.GetAllOverlays())
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
      if (!string.IsNullOrEmpty(soundToPlay) && SoundFileRegex().IsMatch(soundToPlay))
      {
        isSound = true;
        return "<<" + soundToPlay + ">>";
      }

      return text;
    }

    internal static string GetFromDecodedSoundOrText(string soundToPlay, string text, out bool isSound)
    {
      isSound = false;
      if (!string.IsNullOrEmpty(soundToPlay) && SoundFileRegex().IsMatch(soundToPlay))
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
        var match = SoundFileTextRegex().Match(text);
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

            var waiting = list[i].IsWaiting;
            list[i] = characters[i];
            list[i].IsWaiting = waiting;
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
          watcher = new FileSystemWatcher("data/sounds");
          watcher.Created += (_, _) => OnWatcherUpdated(fileList);
          watcher.Deleted += (_, _) => OnWatcherUpdated(fileList);
          watcher.Changed += (_, _) => OnWatcherUpdated(fileList);
          watcher.EnableRaisingEvents = true;
        }
      }
      catch (Exception e)
      {
        Log.Debug(e);
      }

      return watcher;

      static void OnWatcherUpdated(ObservableCollection<string> soundFiles)
      {
        LoadSounds(soundFiles);
      }
    }

    private static void LoadSounds(ObservableCollection<string> fileList)
    {
      var current = Directory.GetFiles("data/sounds", "*.*")
        .Where(file => SoundFileRegex().IsMatch(file))
        .Select(Path.GetFileName).OrderBy(file => file).ToList();

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

    internal static async void CheckQuickShare(List<TrustedPlayer> trust, ChatType chatType, string action, double dateTime, string characterId, string processorName)
    {
      if (chatType.Sender == null || action == null)
      {
        return;
      }

      // handle stop command
      if (chatType.SenderIsYou && (chatType.TextStart - 27) is var s and > 0 && action.Length > s
          && action.AsSpan()[s..].StartsWith("{EQLP:STOP}", StringComparison.OrdinalIgnoreCase))
      {
        await TriggerManager.Instance.StopTriggersAsync();
        return;
      }

      var match = ShareRegex.Match(action);
      if (!match.Success && match.Groups.Count != 3)
      {
        return;
      }

      var type = match.Groups[1].Value.Trim();
      var quickShareKey = match.Groups[2].Value.Trim();
      var fullKey = $"{{{type}:{quickShareKey}}}";
      var to = chatType.Channel == ChatChannels.Tell ? "You" : chatType.Channel;

      var record = new QuickShareRecord
      {
        BeginTime = dateTime,
        Key = fullKey,
        From = chatType.Sender,
        To = (to == "You" && processorName != null && characterId != TriggerStateManager.DefaultUser) ? processorName : TextUtils.ToUpper(to),
        IsMine = chatType.SenderIsYou,
        Type = type
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
            var autoMerge = chatType.Channel != ChatChannels.Tell && trust.Any(tp => tp.Name.Equals(chatType.Sender, StringComparison.OrdinalIgnoreCase));
            QuickShareCache[quickShareKey] = new CharacterData { Sender = chatType.Sender, AutoMerge = autoMerge, IsTrigger = type == ShareTrigger };
            QuickShareCache[quickShareKey].CharacterIds.Add(characterId);
            _ = RunQuickShareTaskAsync(quickShareKey, autoMerge);
          }
          else
          {
            value.CharacterIds.Add(characterId);
          }
        }
      }
    }

    internal static void ImportQuickShare(string shareKey, string from)
    {
      var match = ShareRegex.Match(shareKey);
      if (!match.Success && match.Groups.Count != 3)
      {
        return;
      }

      var type = match.Groups[1].Value.Trim();
      var quickShareKey = match.Groups[2].Value.Trim();
      QuickShareCache.TryAdd(quickShareKey, new CharacterData { Sender = from, IsTrigger = type == ShareTrigger });
      if (QuickShareCache.Count == 1)
      {
        _ = RunQuickShareTaskAsync(quickShareKey, false);
      }
    }

    internal static bool IsProbRegex(string value)
    {
      if (string.IsNullOrEmpty(value))
      {
        return false;
      }

      return TestRegex().Match(value).Success;
    }

    internal static async Task ShareAsync(List<TriggerTreeViewNode> viewNodes, bool isTrigger)
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
              var type = isTrigger ? ShareTrigger : ShareOverlay;
              var withKey = $"{{{type}:{shareLink}}}";

              var record = new QuickShareRecord
              {
                BeginTime = DateUtil.ToDouble(DateTime.Now),
                Key = withKey,
                From = "You",
                IsMine = true,
                To = "Created Share Key",
                Type = type
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

    private static void NextQuickShareTask(string quickShareKey)
    {
      QuickShareCache.TryRemove(quickShareKey, out _);

      if (!QuickShareCache.IsEmpty)
      {
        var nextKey = QuickShareCache.Keys.First();
        _ = RunQuickShareTaskAsync(nextKey, QuickShareCache[nextKey].AutoMerge);
      }
    }

    private static async Task RunQuickShareTaskAsync(string quickShareKey, bool autoMerge, int tries = 0)
    {
      await Task.Delay(1000);

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
          await ImportFromQuickShareAsync(Encoding.UTF8.GetString(ms.ToArray()), quickShareKey, autoMerge);
        }
        else
        {
          if (tries == 0)
          {
            // try a 2nd time
            _ = RunQuickShareTaskAsync(quickShareKey, autoMerge, 1);
            return;
          }

          await UiUtil.InvokeAsync(() =>
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
          await UiUtil.InvokeAsync(() =>
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
            _ = RunQuickShareTaskAsync(quickShareKey, autoMerge, 1);
            return;
          }

          await UiUtil.InvokeAsync(() =>
          {
            new MessageWindow("Unable to Import. May be Expired.\nCheck Error Log for Details.", Resource.SHARE_ERROR).ShowDialog();
          });

          Log.Error("Error Downloading Quick Share", ex);
          NextQuickShareTask(quickShareKey);
        }
      }
    }

    private static async Task ImportFromQuickShareAsync(string data, string quickShareKey, bool autoMerge)
    {
      if (QuickShareCache.TryGetValue(quickShareKey, out var quickShareData))
      {
        var player = quickShareData.Sender;
        var characterIds = quickShareData.CharacterIds;

        await UiUtil.InvokeAsync(async () =>
        {
          var nodes = JsonSerializer.Deserialize<List<ExportTriggerNode>>(data, SerializationOptions);
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
            if (autoMerge)
            {
              if (quickShareData.IsTrigger)
              {
                await TriggerStateManager.Instance.ImportTriggers("", nodes, characterIds);
              }
              else
              {
                await TriggerStateManager.Instance.ImportOverlays(nodes);
              }
            }
            else
            {
              if (quickShareData.IsTrigger)
              {
                var message = "Merge Triggers or Import to New Folder?\r\n";
                if (!string.IsNullOrEmpty(player))
                {
                  message = $"Merge Triggers from {player} or Import to New Folder?\r\n";
                }

                var msgDialog = new MessageWindow(message, Resource.RECEIVED_SHARE, MessageWindow.IconType.Question,
                  "New Folder", "Merge", characterIds.Count > 0);
                msgDialog.ShowDialog();

                var mergeIds = msgDialog.MergeOption ? characterIds : null;
                if (msgDialog.IsYes2Clicked)
                {
                  await TriggerStateManager.Instance.ImportTriggers("", nodes, mergeIds);
                }
                if (msgDialog.IsYes1Clicked)
                {
                  var folderName = (player == null) ? "New Folder" : "From " + player;
                  folderName += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
                  await TriggerStateManager.Instance.ImportTriggers(folderName, nodes, mergeIds);
                }
              }
              else
              {
                var message = "Import Overlays?\r\n";
                if (!string.IsNullOrEmpty(player))
                {
                  message = $"Import Overlays from {player}?\r\n";
                }

                var msgDialog = new MessageWindow(message, Resource.RECEIVED_SHARE, MessageWindow.IconType.Question, "Import");
                msgDialog.ShowDialog();

                if (msgDialog.IsYes1Clicked)
                {
                  await TriggerStateManager.Instance.ImportOverlays(nodes);
                }
              }
            }
          }

          NextQuickShareTask(quickShareKey);
        });
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

    private static async Task Import(TriggerNode parent, bool triggers = true)
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
            if (dialog.FileName.EndsWith($"{ExtTrigger}.gz", StringComparison.OrdinalIgnoreCase) ||
              dialog.FileName.EndsWith($"{ExtOverlay}.gz", StringComparison.OrdinalIgnoreCase))
            {
              var decompressionStream = new GZipStream(fileInfo.OpenRead(), CompressionMode.Decompress);
              var reader = new StreamReader(decompressionStream);
              var json = await reader.ReadToEndAsync();
              reader.Close();
              var data = JsonSerializer.Deserialize<List<ExportTriggerNode>>(json, SerializationOptions);
              if (triggers)
              {
                await TriggerStateManager.Instance.ImportTriggers(parent, data);
              }
              else
              {
                await TriggerStateManager.Instance.ImportOverlays(data);
              }
            }
            else if (dialog.FileName.EndsWith(".gtp", StringComparison.InvariantCulture))
            {
              var data = new byte[fileInfo.Length];
              var read = fileInfo.OpenRead().Read(data);
              if (read > 0)
              {
                var imported = GinaUtil.CovertToTriggerNodes(data);
                await TriggerStateManager.Instance.ImportTriggers(parent, imported);
              }
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
        Id = viewNode.SerializedData.OverlayData != null ? viewNode.SerializedData.Id : null,
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

    [GeneratedRegex(@"<<(.*\.(wav|mp3))>>$", RegexOptions.IgnoreCase)]
    private static partial Regex SoundFileTextRegex();

    [GeneratedRegex(@"\.(wav|mp3)$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex SoundFileRegex();

    [GeneratedRegex(@"\{(TS|[sn](?:\s*[0-9]+\s*|\s*[><]=?\s*[0-9]+\s*|=\s*[0-9]+\s*)?)\}", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TestRegex();
  }

  internal class CharacterData
  {
    public string Sender { get; set; }
    public HashSet<string> CharacterIds { get; set; } = [];
    public bool AutoMerge { get; set; }
    public bool IsTrigger { get; set; }
  }
}

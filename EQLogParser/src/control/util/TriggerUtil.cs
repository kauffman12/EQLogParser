using EQLogParser.Audio;
using log4net;
using log4net.Appender;
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

    // Legacy: shows file dialog and processes in one call
    internal static async Task ImportTriggers(TriggerNode parent) => await Import(parent);
    internal static async Task ImportOverlays(TriggerNode triggerNode) => await Import(triggerNode, false);

    // Pick a NAG database directory via folder dialog; returns the path or null if cancelled
    internal static string SelectNagDatabaseDirectory()
    {
      using var dialog = new System.Windows.Forms.FolderBrowserDialog
      {
        Description = "Select the directory containing your NAG database files. (*.json)",
        AutoUpgradeEnabled = true,
      };

      return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    internal static string SelectImportFile(TriggerNode parent, bool triggers = true)
    {
      var defExt = triggers ? $".{ExtTrigger}.gz" : $".{ExtOverlay}.gz";
      var filter = triggers ? $"All Supported Files|*.{ExtTrigger}.gz;*.gtp" : $"All Supported Files|*.{ExtOverlay}.gz";

      var dialog = new OpenFileDialog
      {
        DefaultExt = defExt,
        Filter = filter
      };

      return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // Import overlays from a NAG database directory (reads overlays-database.json and parses via NagUtil).
    // Returns the number of imported overlays, the number of skipped FCT overlays (unsupported),
    // and notes for overlays imported with reduced fidelity (e.g. reversed timer sort order).
    internal static async Task<(int Imported, int SkippedFct, List<string> Notes)> ImportNagOverlays(string databaseDirectory)
    {
      try
      {
        var filePath = Path.Combine(databaseDirectory, "overlays-database.json");
        if (!File.Exists(filePath))
        {
          return (0, 0, []);
        }

        var json = await File.ReadAllTextAsync(filePath);
        var imported = NagUtil.ConvertOverlays(json, out var skippedFct, out var notes);
        if (imported?.Count > 0)
        {
          await TriggerStateDB.Instance.ImportOverlays(imported);
        }

        return (imported?.Count ?? 0, skippedFct, notes);
      }
      catch (Exception ex)
      {
        Log.Error("Error importing NAG overlays", ex);
      }

      return (0, 0, []);
    }

    // Prefix for NAG import root folders ("NAG Ingest - <timestamp>"). Each import creates a new
    // root so re-imports never update earlier imports — the user deletes what they don't need.
    private const string NagImportRootPrefix = "NAG Ingest - ";

    // Import triggers from a NAG database directory (reads trigger-database.json and parses via NagUtil)
    internal static async Task ImportNagTriggers(string databaseDirectory)
    {
      try
      {
        var filePath = Path.Combine(databaseDirectory, "trigger-database.json");
        if (!File.Exists(filePath))
        {
          await UiUtil.InvokeAsync(() =>
          {
            new MessageWindow("Trigger-database.json not found in selected directory.", Resource.IMPORT_ERROR).ShowDialog();
          });
          return;
        }

        var json = await File.ReadAllTextAsync(filePath);
        // Conversion is CPU-bound (JSON + regex work over the whole database) — run it off the UI thread.
        var (nodes, results) = await Task.Run(() => NagUtil.ConvertTriggers(json, databaseDirectory));

        // Import overlays BEFORE triggers so that ValidateOverlays() can find them
        // when triggers reference overlay IDs. Triggers must be imported after overlays.
        var (overlayCount, fctSkipped, overlayNotes) = await ImportNagOverlays(databaseDirectory);

        // NAG actions reference overlays by their NAG id, but EQLP node ids are store-generated
        // UUIDs (matched back through OverlayData.Source) — rewrite the references now or
        // ValidateOverlays strips every one during the trigger import.
        var overlayRemap = NagUtil.BuildOverlayIdRemap(await TriggerStateDB.Instance.GetAllOverlays());
        NagUtil.RemapOverlayReferences(nodes, overlayRemap);

        // Count earlier NAG imports so the summary can warn this adds another copy.
        var priorImports = await TriggerStateDB.Instance.CountChildren(TriggerStateDB.Triggers, NagImportRootPrefix);

        if (nodes?.Count > 0)
        {
          var folderName = $"{NagImportRootPrefix}{DateTime.Now:yyyy-MM-dd HH:mm}";
          // Imported triggers intentionally start disabled — NAG per-character enable states are
          // not imported; the user enables what they want in the Triggers view.
          await TriggerStateDB.Instance.ImportTriggers(folderName, nodes);
        }

        // Generate HTML import report — save in the EQLP log directory
        // (where the error log lives). This keeps the NAG database
        // directory clean for users who may want to delete it entirely.
        var htmlReportPath = string.Empty;
        try
        {
          var logDir = GetLogDirectory();
          if (!string.IsNullOrEmpty(logDir))
          {
            htmlReportPath = Path.Combine(logDir, "nag-import.html");
            NagUtil.WriteImportReportHtml(results ?? [], htmlReportPath, fctSkipped, overlayNotes);
          }
        }
        catch (Exception ex)
        {
          Log.Warn("Could not write import report", ex);
          htmlReportPath = null;
        }

        // Show summary dialog
        await UiUtil.InvokeAsync(() =>
        {
          if (results is null || results.Count == 0)
          {
            new MessageWindow($"No triggers were processed.\nOverlays imported: {overlayCount}", "NAG Ingest Complete").ShowDialog();
            return;
          }

          var imported = results.Count(r => r.ImportStatus == NagImportStatus.Imported);
          var partial = results.Count(r => r.ImportStatus == NagImportStatus.Partial);
          var skipped = results.Count(r => r.ImportStatus == NagImportStatus.Skipped);

          var overlayLine = $"Overlays imported: {overlayCount}" + (fctSkipped > 0 ? $" ({fctSkipped} FCT skipped — unsupported)" : "");
          var message = $"NAG Ingest Complete\n\n" +
            $"{overlayLine}\n" +
            $"Triggers processed: {results.Count}\n" +
            $"Imported: {imported}\n" +
            $"Partial (some features dropped): {partial}\n" +
            $"Skipped: {skipped}\n\n";

          if (skipped > 0)
          {
            var skipReasons = results.Where(r => r.ImportStatus == NagImportStatus.Skipped)
              .GroupBy(r => r.Reason)
              .Select(g => $"{g.Key}: {g.Count()}")
              .ToList();
            message += "Skipped triggers:\n" + string.Join("\n", skipReasons.Take(10)) + "\n";
          }

          if (overlayNotes.Count > 0)
          {
            message += $"Note: {overlayNotes.Count} overlay(s) display timers in reversed order vs NAG (no equivalent sort option) — see the report.\n\n";
          }

          if (priorImports > 0)
          {
            message += $"\nNote: {priorImports} earlier NAG import(s) already exist — this import adds a separate copy. Delete any you no longer need.\n";
          }

          // Missing audio files are listed in the HTML report, not here,
          // to keep the dialog concise.

          if (!string.IsNullOrEmpty(htmlReportPath))
          {
            message += $"\nReport: {htmlReportPath}";

            var msg = new MessageWindow(message, "NAG Ingest Complete", MessageWindow.IconType.Info, "Open Report");
            msg.ShowDialog();
            if (msg.IsYes1Clicked)
            {
              MainActions.OpenFileWithDefault("\"" + htmlReportPath + "\"");
            }
          }
          else
          {
            new MessageWindow(message, "NAG Ingest Complete").ShowDialog();
          }
        });
      }
      catch (Exception ex)
      {
        Log.Error("Error importing NAG triggers", ex);
        await UiUtil.InvokeAsync(() =>
        {
          new MessageWindow("Problem importing NAG triggers. Check Error Log for details.", Resource.IMPORT_ERROR).ShowDialog();
        });
      }
    }

    // Process a previously-selected import file (caller shows progress UI around this)
    internal static async Task ProcessImportFile(string filePath, TriggerNode parent, bool triggers = true)
    {
      var fileInfo = new FileInfo(filePath);
      if (!fileInfo.Exists || fileInfo.Length >= 100000000) return;

      if (filePath.EndsWith($"{ExtTrigger}.gz", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith($"{ExtOverlay}.gz", StringComparison.OrdinalIgnoreCase))
      {
        await using var fs = fileInfo.OpenRead();
        using var decompressionStream = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressionStream);
        var json = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<List<ExportTriggerNode>>(json, SerializationOptions);
        if (triggers)
          await TriggerStateDB.Instance.ImportTriggers(parent, data);
        else
          await TriggerStateDB.Instance.ImportOverlays(data);
      }
      else if (filePath.EndsWith(".gtp", StringComparison.OrdinalIgnoreCase))
      {
        await using var fs = fileInfo.OpenRead();
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        var data = ms.ToArray();
        if (data.Length > 0)
        {
          var imported = GinaUtil.ConvertToTriggerNodes(data);
          await TriggerStateDB.Instance.ImportTriggers(parent, imported);
        }
      }
    }

    private const string ExtTrigger = "tgf";
    private const string ExtOverlay = "ogf";
    private const double OriginalTop = 550; // Hard-coded original top position
    private const double OriginalLeft = 650; // Hard-coded original left position
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly ConcurrentDictionary<string, CharacterData> QuickShareCache = new();
    private static readonly JsonSerializerOptions SerializationOptions = new JsonSerializerOptions { IncludeFields = true };
    private static readonly Size OriginalResolution = new(1920, 1080); // Hard-coded original screen resolution
    private static readonly Regex ShareRegex = new(@"\{(" + ShareTrigger + "|" + ShareOverlay + @"):([^\{\}]+)\}", RegexOptions.Compiled |
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        var dpi = VisualTreeHelper.GetDpi(MainActions.GetOwner()).PixelsPerDip;
        return UiElementUtil.CalculateTextHeight(dpi, family, fontSize);
      }

      return fontSize + 2;
    }

    internal static bool TestRegexProperty(bool useRegex, string pattern, PatternEditor editor)
    {
      var isValid = !useRegex || TextUtils.IsValidRegex(pattern);
      editor.SetForeground(isValid ? "ContentForeground" : "EQStopForegroundBrush");
      return isValid;
    }

    internal static bool TestConditionProperty(string condition, ConditionEditor editor)
    {
      var isValid = string.IsNullOrWhiteSpace(condition) || ConditionParser.Parse(condition) != null;
      editor.SetForeground(isValid ? "ContentForeground" : "EQStopForegroundBrush");
      return isValid;
    }

    internal static async Task Copy(object to, object from)
    {
      if (to is Trigger toTrigger && from is Trigger fromTrigger)
      {
        toTrigger.Private = fromTrigger.Private;
        toTrigger.AltTimerName = TextUtils.Trim(fromTrigger.AltTimerName);
        toTrigger.TimerNameStatic = fromTrigger.TimerNameStatic;
        toTrigger.Comments = TextUtils.Trim(fromTrigger.Comments);
        toTrigger.DurationSeconds = fromTrigger.DurationSeconds;
        toTrigger.Pattern = TextUtils.Trim(fromTrigger.Pattern);
        toTrigger.PreviousPattern = TextUtils.Trim(fromTrigger.PreviousPattern);
        toTrigger.MatchVariableCondition = TextUtils.Trim(fromTrigger.MatchVariableCondition);
        toTrigger.EndEarlyPattern = TextUtils.Trim(fromTrigger.EndEarlyPattern);
        toTrigger.EndEarlyPattern2 = TextUtils.Trim(fromTrigger.EndEarlyPattern2);
        toTrigger.EndEarlyPattern3 = TextUtils.Trim(fromTrigger.EndEarlyPattern3);
        toTrigger.EndUseRegex = fromTrigger.EndUseRegex;
        toTrigger.EndUseRegex2 = fromTrigger.EndUseRegex2;
        toTrigger.EndUseRegex3 = fromTrigger.EndUseRegex3;
        toTrigger.EndEarlyRepeatedCount = fromTrigger.EndEarlyRepeatedCount;
        toTrigger.WorstEvalTime = fromTrigger.WorstEvalTime;
        toTrigger.ResetDurationSeconds = fromTrigger.ResetDurationSeconds;
        toTrigger.Priority = fromTrigger.Priority;
        toTrigger.RepeatedResetTime = fromTrigger.RepeatedResetTime;
        toTrigger.LockoutTime = fromTrigger.LockoutTime;
        toTrigger.EnableTimer = fromTrigger.EnableTimer;
        toTrigger.SelectedOverlays = fromTrigger.SelectedOverlays is { Count: > 0 } srcOverlays
          ? [.. srcOverlays]
          : [];
        toTrigger.ActiveColor = fromTrigger.ActiveColor;
        toTrigger.IdleColor = fromTrigger.IdleColor;
        toTrigger.ResetColor = fromTrigger.ResetColor;
        toTrigger.FontColor = fromTrigger.FontColor;
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
        toTrigger.EndTimerClearVariables = TextUtils.Trim(fromTrigger.EndTimerClearVariables);
        toTrigger.IconSource = fromTrigger.IconSource;
        toTrigger.VoiceRate = fromTrigger.VoiceRate;
        toTrigger.Volume = fromTrigger.Volume;
        toTrigger.VariableActions = fromTrigger.VariableActions is { Count: > 0 } srcActions
          ? srcActions.Select(va => new VariableAction
          {
            ActionType = va.ActionType,
            DataType = va.DataType,
            VariableName = va.VariableName,
            Value = va.Value,
            Step = va.Step,
            InitialValue = va.InitialValue,
            TimeToLiveSeconds = va.TimeToLiveSeconds
          }).ToList()
          : [];

        if (toTrigger is TriggerPropertyModel toModel)
        {
          toModel.TriggerActiveBrush = UiUtil.GetBrush(fromTrigger.ActiveColor, false);
          toModel.TriggerIdleBrush = UiUtil.GetBrush(fromTrigger.IdleColor, false);
          toModel.TriggerResetBrush = UiUtil.GetBrush(fromTrigger.ResetColor, false);
          toModel.TriggerFontBrush = UiUtil.GetBrush(fromTrigger.FontColor, false);

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
          // The property model's brushes are authoritative for these: they were loaded from the
          // stored colors when the trigger was selected, so a null brush means the user reset the
          // color and the stored value must be cleared, not kept.
          toTrigger.ActiveColor = fromModel.TriggerActiveBrush?.Color.ToHexString();
          toTrigger.IdleColor = fromModel.TriggerIdleBrush?.Color.ToHexString();
          toTrigger.ResetColor = fromModel.TriggerResetBrush?.Color.ToHexString();
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
        toOverlay.ShowMillis = fromOverlay.ShowMillis;
        toOverlay.TimerMode = fromOverlay.TimerMode;
        toOverlay.Top = fromOverlay.Top;
        toOverlay.UseStandardTime = fromOverlay.UseStandardTime;
        toOverlay.HideDuplicates = fromOverlay.HideDuplicates;
        toOverlay.ShowActive = fromOverlay.ShowActive;
        toOverlay.ShowIdle = fromOverlay.ShowIdle;
        toOverlay.ShowReset = fromOverlay.ShowReset;
        toOverlay.StreamerMode = fromOverlay.StreamerMode;
        toOverlay.UseTextDropShadow = fromOverlay.UseTextDropShadow;
        toOverlay.Width = fromOverlay.Width;
        toOverlay.HorizontalAlignment = fromOverlay.HorizontalAlignment;
        toOverlay.VerticalAlignment = fromOverlay.VerticalAlignment;
        toOverlay.TextOverlayWrap = fromOverlay.TextOverlayWrap;
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
          Application.Current.Resources["OverlayTextEffect-" + toModel.Node.Id] = toModel.UseTextDropShadow ? ThemeConfig.OverlayTextEffect : null;

          AssignBrushResource(toModel, fromOverlay, "OverlayColor", "OverlayBrush", "OverlayBrushColor");
          AssignBrushResource(toModel, fromOverlay, "FontColor", "FontBrush", "TimerBarFontColor");
          AssignBrushResource(toModel, fromOverlay, "ActiveColor", "ActiveBrush", "TimerBarActiveColor");
          AssignBrushResource(toModel, fromOverlay, "IdleColor", "IdleBrush", "TimerBarIdleColor");
          AssignBrushResource(toModel, fromOverlay, "ResetColor", "ResetBrush", "TimerBarResetColor");
          AssignBrushResource(toModel, fromOverlay, "BackgroundColor", "BackgroundBrush", "TimerBarTrackColor");

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
          Application.Current.Resources["OverlayTextEffect-" + toTextModel.Node.Id] = toTextModel.UseTextDropShadow ? ThemeConfig.OverlayTextEffect : null;
          Application.Current.Resources["TextOverlayTextWrapping-" + toTextModel.Node.Id] = toTextModel.Node.OverlayData.TextOverlayWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

          AssignBrushResource(toTextModel, fromOverlay, "OverlayColor", "OverlayBrush", "OverlayBrushColor");
          AssignBrushResource(toTextModel, fromOverlay, "FontColor", "FontBrush", "TextOverlayFontColor");

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
      foreach (var od in await TriggerStateDB.Instance.GetAllOverlays())
      {
        var node = new TriggerNode { Name = od.Name, Id = od.Id, OverlayData = od.OverlayData };
        await LoadOverlayStyle(node, od.OverlayData);
      }
    }

    internal static async Task LoadOverlayStyle(TriggerNode node, Overlay overlay)
    {
      Application.Current.Resources["OverlayText-" + node.Id] = node.Name;
      if (overlay?.IsTextOverlay is true)
      {
        // workaround to load styles
        await Copy(new TextOverlayPropertyModel { Node = node }, overlay);
      }
      else if (overlay?.IsTextOverlay is false)
      {
        // workaround to load styles
        await Copy(new TimerOverlayPropertyModel { Node = node }, overlay);
      }
    }

    private static void AssignBrushResource(dynamic toModel, object fromOverlay, string colorProperty, string brushProperty, string prefix)
    {
      var colorValue = (string)fromOverlay.GetType().GetProperty(colorProperty)?.GetValue(fromOverlay);
      if (!string.IsNullOrEmpty(colorValue))
      {
        var brush = UiUtil.GetBrush(colorValue, false);
        toModel.GetType().GetProperty(brushProperty)?.SetValue(toModel, brush);
        Application.Current.Resources[$"{prefix}-{toModel.Node.Id}"] = brush;
      }
    }

    private static async Task<(ObservableCollection<ComboBoxItemDetails>, ObservableCollection<ComboBoxItemDetails>)> GetOverlayItems(List<string> overlayIds)
    {
      var text = new ObservableCollection<ComboBoxItemDetails>();
      var timer = new ObservableCollection<ComboBoxItemDetails>();

      foreach (var data in await TriggerStateDB.Instance.GetAllOverlays())
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

    // Resolves a sound file reference to a full path. If the filename contains
    // a path separator, it is treated as an explicit (absolute or relative) path.
    // Otherwise, it is resolved from the default data/sounds directory.
    internal static string ResolveSoundPath(string soundFile)
    {
      if (string.IsNullOrEmpty(soundFile))
      {
        return null;
      }

      // If it contains a path separator, treat as an explicit path
      if (soundFile.Contains('\\') || soundFile.Contains('/'))
      {
        return Path.IsPathRooted(soundFile)
          ? soundFile
          : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, soundFile));
      }

      // Otherwise, look in the default sounds directory
      return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sounds", soundFile);
    }

    internal static bool SoundFileExists(string text)
    {
      if (string.IsNullOrEmpty(text))
      {
        return false;
      }

      return File.Exists(ResolveSoundPath(text));
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

    internal static bool CheckOptions(List<NumberOptions> options, Dictionary<string, string> matches, out double duration)
    {
      duration = double.NaN;

      if (matches == null)
      {
        return true;
      }

      foreach (var kv in matches)
      {
        var groupName = kv.Key;
        var groupValue = kv.Value;

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
              if (TextUtils.ParseUInt(groupValue) is var value && value != uint.MaxValue)
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
          watcher.Created += (_, _) => OnWatcherUpdated(fileList, true);
          watcher.Deleted += (_, _) => OnWatcherUpdated(fileList);
          watcher.Changed += (_, _) => OnWatcherUpdated(fileList);
          watcher.Renamed += (_, _) => OnWatcherUpdated(fileList);
          watcher.EnableRaisingEvents = true;
        }
      }
      catch (Exception e)
      {
        Log.Debug(e);
      }

      return watcher;

      static void OnWatcherUpdated(ObservableCollection<string> soundFiles, bool create = false)
      {
        if (!create)
        {
          // clear cache for audio files
          foreach (var key in App.AppCache.Keys)
          {
            if (key is string { Length: > 0 } skey && skey.StartsWith(AudioManager.AudioCacheKey, StringComparison.OrdinalIgnoreCase))
            {
              App.AppCache.Remove(key);
            }
          }
        }

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
      if (BuildExportList(viewNodes, false) is { } exportList)
      {
        try
        {
          if (exportList.Count > 0)
          {
            var isTriggers = exportList[0].Name == TriggerStateDB.Triggers;
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

    /// <summary>Check for embedded EQLP commands ({EQLP:STOP}, {EQLP:CLEAR}) and execute them.</summary>
    /// <remarks>Called fire-and-forget from the chat processing loop (Task.Run context). Uses async void intentionally.</remarks>
    internal static async void CheckCommands(ChatType chatType, string action)
    {
      if (chatType?.Sender == null || action == null || !chatType.SenderIsYou)
        return;

      // TextStart is an absolute position in the full log line (includes 27-char timestamp prefix).
      // Since 'action' has the timestamp stripped, subtract 27 to get the correct offset.
      var offset = chatType.TextStart - 27;
      if (offset > 0 && action.Length > offset)
      {
        var tail = action[offset..];
        if (tail.StartsWith("{EQLP:STOP}", StringComparison.OrdinalIgnoreCase))
        {
          await TriggerManager.Instance.StopTriggersAsync();
        }
        else if (tail.StartsWith("{EQLP:CLEAR}", StringComparison.OrdinalIgnoreCase))
        {
          await TriggerManager.Instance.ClearVariablesAsync();
        }
      }
    }

    internal static void CheckQuickShare(ChatType chatType, string action, double dateTime, bool doImport, string characterId,
      string processorName = null, List<TrustedPlayer> trust = null)
    {
      if (chatType.Sender == null || action == null)
      {
        return;
      }

      if (MatchQuickShare(action) is not { } match)
      {
        return;
      }

      var type = match.Groups[1].Value.Trim();
      var quickShareKey = match.Groups[2].Value.Trim();
      var fullKey = $"{{{type}:{quickShareKey}}}";
      var record = QuickShareRecord.FromChat(chatType, type, fullKey, dateTime, characterId, processorName);

      QuickShareManager.Instance.Add(record);

      if (doImport)
      {
        // don't handle immediately unless enabled
        if (characterId != null && !chatType.SenderIsYou && (chatType.Channel is ChatChannels.Group or ChatChannels.Guild or
              ChatChannels.Raid or ChatChannels.Tell) && ConfigUtil.IfSet("TriggersWatchForQuickShare") && !QuickShareManager.Instance.IsMine(fullKey))
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
              var autoMerge = chatType.Channel != ChatChannels.Tell && trust?.Any(tp => tp.Name.Equals(chatType.Sender, StringComparison.OrdinalIgnoreCase)) is true;
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
    }

    internal static void ImportQuickShare(string shareKey, string from)
    {
      if (MatchQuickShare(shareKey) is not { } match)
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
      if (BuildExportList(viewNodes, true) is { Count: > 0 } exportList)
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
                BeginTime = DateUtil.ToDotNetSeconds(DateTime.Now),
                Key = withKey,
                From = "You",
                IsMine = true,
                To = "Created Share Key",
                Type = type
              };

              QuickShareManager.Instance.Add(record);

              Task action() => OpenQuickShareStatusAsync(shareLink);
              new MessageWindow($"Share Key: {withKey}", Resource.SHARE_MESSAGE, withKey, "View Quick Share Stats", action).ShowDialog();
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

    internal static async Task OpenQuickShareStatusAsync(string selected)
    {
      List<string> keys = [];
      // Snapshot of the shared state rather than the window's bound collection: this runs off the UI
      // thread and wants a point-in-time view, not a live WPF collection that may be updating.
      foreach (var share in QuickShareState.Instance.Snapshot())
      {
        if (MatchQuickShare(share.Key) is { } match)
        {
          keys.Add(match.Groups[2].Value.Trim());
        }
      }

      var kvps = keys.Select(k => new KeyValuePair<string, string>("k", k));
      var queryString = await new FormUrlEncodedContent(kvps).ReadAsStringAsync();

      if (!string.IsNullOrEmpty(selected))
      {
        queryString = $"select={selected}&{queryString}";
      }

      MainActions.OpenFileWithDefault($"{App.ParserHome}/status.html?{queryString}");
    }

    internal static IReadOnlyDictionary<string, string> ToLexiconDictionary(this List<LexiconItem> lexicon)
    {
      if (lexicon == null || lexicon.Count == 0)
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      return lexicon
          .Where(item =>
              !string.IsNullOrEmpty(item?.Replace) &&
              !string.IsNullOrEmpty(item?.With))
          .GroupBy(item => item.Replace, StringComparer.OrdinalIgnoreCase)
          .ToDictionary(
              g => g.Key,
              g => g.First().With,
              StringComparer.OrdinalIgnoreCase);
    }

    private static Match MatchQuickShare(string text)
    {
      var match = ShareRegex.Match(text);
      if (match.Success && match.Groups.Count == 3)
      {
        return match;
      }
      return null;
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
        var response = await MainActions.TheHttpClient.GetAsync(url);
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
                await TriggerStateDB.Instance.ImportTriggers("", nodes, characterIds);
              }
              else
              {
                await TriggerStateDB.Instance.ImportOverlays(nodes);
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
                  await TriggerStateDB.Instance.ImportTriggers("", nodes, mergeIds);
                }
                if (msgDialog.IsYes1Clicked)
                {
                  var folderName = (player == null) ? "New Folder" : "From " + player;
                  folderName += " (" + DateUtil.FormatDotNetDateSeconds(DateUtil.ToDotNetSeconds(DateTime.Now)) + ")";
                  await TriggerStateDB.Instance.ImportTriggers(folderName, nodes, mergeIds);
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
                  await TriggerStateDB.Instance.ImportOverlays(nodes);
                }
              }
            }
          }

          NextQuickShareTask(quickShareKey);
        });
      }
    }

    private static List<ExportTriggerNode> BuildExportList(IEnumerable<TriggerTreeViewNode> viewNodes, bool hidePrivateTriggers)
    {
      var exportList = new List<ExportTriggerNode>();
      if (viewNodes != null)
      {
        foreach (var viewNode in viewNodes)
        {
          if (hidePrivateTriggers && viewNode?.SerializedData?.TriggerData?.Private is true)
          {
            continue;
          }

          var node = Create(viewNode);
          var top = BuildUpTree(viewNode.ParentNode as TriggerTreeViewNode, node);
          BuildDownTree(viewNode, node, hidePrivateTriggers);
          exportList.Add(top);
        }
      }
      return exportList;
    }

    private static async Task Import(TriggerNode parent, bool triggers = true)
    {
      try
      {
        var filePath = SelectImportFile(parent, triggers);
        if (filePath is not null)
          await ProcessImportFile(filePath, parent, triggers);
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

    private static void BuildDownTree(TriggerTreeViewNode viewNode, ExportTriggerNode node, bool hidePrivateTriggers)
    {
      if (viewNode.HasChildNodes)
      {
        foreach (var childView in viewNode.ChildNodes.Cast<TriggerTreeViewNode>())
        {
          if (hidePrivateTriggers && childView?.SerializedData?.TriggerData?.Private is true)
          {
            continue;
          }

          var child = Create(childView);
          node.Nodes.Add(child);
          BuildDownTree(childView, child, hidePrivateTriggers);
        }
      }
    }

    [GeneratedRegex(@"<<(.*\.(wav|mp3))>>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SoundFileTextRegex();

    [GeneratedRegex(@"\.(wav|mp3)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SoundFileRegex();

    [GeneratedRegex(@"\{(TS|[sn](?:\s*[0-9]+\s*|\s*[><]=?\s*[0-9]+\s*|=\s*[0-9]+\s*)?)\}", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestRegex();

    /// <summary>
    /// Returns the directory of the EQLP log file (where the error log lives),
    /// or null if it cannot be determined.
    /// </summary>
    private static string GetLogDirectory()
    {
      try
      {
        var fileAppender = Log.Logger.Repository.GetAppenders()
          .OfType<FileAppender>()
          .FirstOrDefault(fa => !string.IsNullOrEmpty(fa.File));
        if (fileAppender is not null)
        {
          return Path.GetDirectoryName(fileAppender.File);
        }
      }
      catch (Exception ex)
      {
        Log.Warn("Could not determine log directory", ex);
      }

      return null;
    }
  }

}

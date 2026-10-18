using log4net;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class LogManagementWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly bool _ready;
    private readonly LogManagementSettings _savedSettings;

    internal LogManagementWindow()
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      var settings = LogManagementSettings.Load();
      enableCheckBox.IsChecked = settings.Enabled;
      // read archive folder
      var savedFolder = settings.ArchiveFolder;
      if (!string.IsNullOrWhiteSpace(savedFolder))
      {
        // The XAML starts this box italic-hinted ("Select Archive Folder"); a loaded folder is a real value,
        // so it switches out of the cue like ChooseFolderClicked's write does (and SpritePickerWindow's load).
        txtFolderPath.Text = savedFolder;
        txtFolderPath.FontStyle = System.Windows.FontStyles.Normal;
      }

      // read saved settings
      UpdateComboBox(fileSizes, settings.MinFileSize);
      UpdateComboBox(fileAges, settings.MinFileAge);
      UpdateComboBox(compress, settings.Compress);
      UpdateComboBox(organize, settings.Organize);
      UpdateComboBox(type, settings.Type);
      UpdateComboBox(day, settings.Day);
      UpdateComboBox(hour, settings.Hour);
      UpdateComboBox(min, settings.Minute);
      UpdateComboBox(ampm, settings.AmPm);

      _savedSettings = ReadSettings();
      _ready = true;
      EnableSave();
    }

    private void OptionsChanged(object sender, RoutedEventArgs e) => EnableSave();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void EnableSave()
    {
      if (_ready)
      {
        var enabled = enableCheckBox.IsChecked == true;
        type.IsEnabled = day.IsEnabled = hour.IsEnabled = min.IsEnabled = ampm.IsEnabled = fileSizes.IsEnabled =
          fileAges.IsEnabled = compress.IsEnabled = organize.IsEnabled = enabled;
        titleLabel.SetResourceReference(ForegroundProperty, enabled ? "EQGoodForegroundBrush" : "EQStopForegroundBrush");
        titleLabel.Content = enabled ? "Log Management Active" : "Enable Log Management";
        ActivityChanged();
        var settings = ReadSettings();
        var hasChanges = settings != _savedSettings;
        closeButton.Content = hasChanges ? "Cancel" : "Close";
        saveButton.IsEnabled = hasChanges && settings.CanSave;
      }
    }

    private void ActivityChanged()
    {
      dayPicker.Visibility = (type.SelectedIndex == 0) ? Visibility.Collapsed : Visibility.Visible;

      if (type.SelectedIndex == 0)
      {
        helpText.Text = "When Archiving based on Activity, the archived logs will be created when a file is first opened or when zoning.";
      }
      else
      {
        helpText.Text = "When Archiving based on a Selected Day/Time, the archived logs will be created if EQLP is running during that time.";
      }
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
      var settings = ReadSettings();
      if (!settings.CanSave)
      {
        return;
      }

      if (!settings.TrySave())
      {
        new MessageWindow("Problem saving log management settings. Check Error Log for details.",
          "Log Management", MessageWindow.IconType.Warn).ShowDialog();
        return;
      }

      LogArchiveManager.SetArchiveSchedule();
      Close();
    }

    private LogManagementSettings ReadSettings() => new()
    {
      Enabled = enableCheckBox.IsChecked == true,
      ArchiveFolder = txtFolderPath.FontStyle == FontStyles.Italic ? "" : txtFolderPath.Text,
      MinFileSize = SelectedText(fileSizes),
      MinFileAge = SelectedText(fileAges),
      Compress = SelectedText(compress),
      Organize = SelectedText(organize),
      Type = SelectedText(type),
      Day = SelectedText(day),
      Hour = SelectedText(hour),
      Minute = SelectedText(min),
      AmPm = SelectedText(ampm)
    };

    private static string SelectedText(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content.ToString();

    private static void UpdateComboBox(ComboBox combo, string saved)
    {
      var index = 0;
      for (var i = 0; i < combo.Items.Count; i++)
      {
        if (combo.Items[i] is ComboBoxItem item && saved.Equals(item.Content.ToString(), StringComparison.OrdinalIgnoreCase))
        {
          index = i;
          break;
        }
      }

      combo.SelectedIndex = index;
    }

    private void ChooseFolderClicked(object sender, RoutedEventArgs e)
    {
      // The box holds a folder, so it goes in as-is. It used to be run through GetDirectoryName, which turned
      // "D:\Logs" into "D:" and opened the chooser at the drive root instead.
      var folder = FileDialogUtil.PickFolder(this, txtFolderPath.Text, "archive");
      if (folder != null)
      {
        txtFolderPath.FontStyle = FontStyles.Normal;
        txtFolderPath.Text = folder;
        EnableSave();
      }
    }

    private async void NowClicked(object sender, RoutedEventArgs e)
    {
      if (ReadSettings() != _savedSettings)
      {
        new MessageWindow("Save changes before archiving so Archive Now uses the settings shown.", "Archive Now",
          MessageWindow.IconType.Warn).ShowDialog();
        return;
      }

      var path = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
      {
        new MessageWindow("Archive Folder not defined or missing!", "Archive Now").ShowDialog();
        return;
      }

      var logFiles = await LogArchiveManager.GetOpenLogFilesAsync();

      if (logFiles.Count > 0)
      {
        await Task.Run(async () =>
        {
          MessageWindow running = null;
          try
          {
            await Dispatcher.InvokeAsync(() =>
            {
              running = new MessageWindow($"Running Archive Process for {logFiles.Count} Files.", "Archive Now", MessageWindow.IconType.Info, null, null, false, true);
              running.Show();
            });

            // need time to display message
            await Task.Delay(1000);
            await LogArchiveManager.ArchiveNowAsync(logFiles);

            _ = Dispatcher.InvokeAsync(() =>
            {
              running?.Close();
              new MessageWindow($"Archiving Complete.", "Archive Now", MessageWindow.IconType.Info).ShowDialog();
            });
          }
          catch (Exception ex)
          {
            Log.Error(ex);
            _ = Dispatcher.InvokeAsync(() =>
            {
              running?.Close();
              new MessageWindow("Problem archiving logs. Check Error Log for details.", "Archive Now").ShowDialog();
            });
          }
        });
      }
      else
      {
        new MessageWindow("No Open or Configured Files to Archive.", "Archive Now").ShowDialog();
      }
    }

    private void EnableCheckBoxOnChecked(object sender, RoutedEventArgs e) => EnableSave();

    private void EnableCheckBoxOnUnchecked(object sender, RoutedEventArgs e) => EnableSave();
  }
}

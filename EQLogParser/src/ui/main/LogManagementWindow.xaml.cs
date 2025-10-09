using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace EQLogParser
{
  public partial class LogManagementWindow
  {
    public static string CompressNo => "No";
    public static string CompressYes => "Yes";
    public static string TypeActivity => "Activity";
    public static string TypeSchedule => "Selected Day/Time";
    public static string OrganizeInFiles => "Individual Files";
    public static string OrganizeInFolders => "Server and Character Folders";
    private readonly bool _ready;

    internal LogManagementWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      enableCheckBox.IsChecked = ConfigUtil.IfSet("LogManagementEnabled");
      type.IsEnabled = day.IsEnabled = hour.IsEnabled = min.IsEnabled = ampm.IsEnabled = fileSizes.IsEnabled =
        fileAges.IsEnabled = compress.IsEnabled = organize.IsEnabled = enableCheckBox.IsChecked == true;

      // read archive folder
      var savedFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (!string.IsNullOrEmpty(savedFolder) && Path.GetDirectoryName(savedFolder) != null)
      {
        txtFolderPath.Text = savedFolder;
      }

      // read saved settings
      UpdateComboBox(fileSizes, "LogManagementMinFileSize", "500M");
      UpdateComboBox(fileAges, "LogManagementMinFileAge", "1 Week");
      UpdateComboBox(compress, "LogManagementCompressArchive", CompressYes);
      UpdateComboBox(organize, "LogManagementOrganize", OrganizeInFiles);
      UpdateComboBox(type, "LogManagementType", TypeActivity);
      UpdateComboBox(day, "LogManagementScheduleDay", "Sunday");
      UpdateComboBox(hour, "LogManagementScheduleHour", "12");
      UpdateComboBox(min, "LogManagementScheduleMinute", "00");
      UpdateComboBox(ampm, "LogManagementScheduleAMPM", "AM");

      ActivityChanged();
      _ready = true;
    }

    private void OptionsChanged(object sender, RoutedEventArgs e) => EnableSave();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void EnableSave()
    {
      if (_ready)
      {
        ActivityChanged();
        closeButton.Content = "Cancel";
        saveButton.IsEnabled = !(enableCheckBox.IsChecked == true && fileAges.SelectedIndex == 0 && fileSizes.SelectedIndex == 0 && type.SelectedIndex == 0);
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
      ConfigUtil.SetSetting("LogManagementEnabled", enableCheckBox.IsChecked == true);
      ConfigUtil.SetSetting("LogManagementArchiveFolder", txtFolderPath.Text);

      // ignore invalid settings
      if (fileAges.SelectedIndex != 0 || fileSizes.SelectedIndex != 0)
      {
        if (fileSizes.SelectedItem is ComboBoxItem item)
        {
          ConfigUtil.SetSetting("LogManagementMinFileSize", item.Content.ToString());
        }

        if (fileAges.SelectedItem is ComboBoxItem item2)
        {
          ConfigUtil.SetSetting("LogManagementMinFileAge", item2.Content.ToString());
        }
      }

      if (compress.SelectedItem is ComboBoxItem item3)
      {
        ConfigUtil.SetSetting("LogManagementCompressArchive", item3.Content.ToString());
      }

      if (organize.SelectedItem is ComboBoxItem item4)
      {
        ConfigUtil.SetSetting("LogManagementOrganize", item4.Content.ToString());
      }

      if (day.SelectedItem is ComboBoxItem item5)
      {
        ConfigUtil.SetSetting("LogManagementScheduleDay", item5.Content.ToString());
      }

      if (hour.SelectedItem is ComboBoxItem item6)
      {
        ConfigUtil.SetSetting("LogManagementScheduleHour", item6.Content.ToString());
      }

      if (min.SelectedItem is ComboBoxItem item7)
      {
        ConfigUtil.SetSetting("LogManagementScheduleMinute", item7.Content.ToString());
      }

      if (ampm.SelectedItem is ComboBoxItem item8)
      {
        ConfigUtil.SetSetting("LogManagementScheduleAMPM", item8.Content.ToString());
      }

      if (type.SelectedItem is ComboBoxItem item9)
      {
        ConfigUtil.SetSetting("LogManagementType", item9.Content.ToString());
      }

      FileUtil.SetArchiveSchedule();
      Close();
    }

    private static void UpdateComboBox(ComboBox combo, string setting, string defaultValue)
    {
      var index = 0;
      var saved = ConfigUtil.GetSetting(setting, defaultValue);
      for (var i = 0; i < combo.Items.Count; i++)
      {
        if (combo.Items[i] is ComboBoxItem item && saved.Equals(item.Content.ToString(), StringComparison.Ordinal))
        {
          index = i;
          break;
        }
      }

      combo.SelectedIndex = index;
    }

    private void ChooseFolderClicked(object sender, RoutedEventArgs e)
    {
      using var dialog = new CommonOpenFileDialog();
      dialog.IsFolderPicker = true;
      dialog.InitialDirectory = string.IsNullOrEmpty(txtFolderPath.Text)
        ? string.Empty
        : Path.GetDirectoryName(txtFolderPath.Text);

      var handle = new WindowInteropHelper(this).Handle;
      if (dialog.ShowDialog(handle) == CommonFileDialogResult.Ok)
      {
        txtFolderPath.FontStyle = FontStyles.Normal;
        txtFolderPath.Text = dialog.FileName;
        EnableSave();
      }
    }

    private async void NowClicked(object sender, RoutedEventArgs e)
    {
      var path = txtFolderPath.Text;
      if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
      {
        new MessageWindow("Archive Folder not defined or missing!", "Archive Now").ShowDialog();
        return;
      }

      var logFiles = await FileUtil.GetOpenLogFilesAsync();

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
            await FileUtil.ArchiveNowAsync(logFiles);

            _ = Dispatcher.InvokeAsync(() =>
            {
              running?.Close();
              new MessageWindow($"Archiving Complete.", "Archive Now", MessageWindow.IconType.Info).ShowDialog();
            });
          }
          catch (Exception)
          {
            _ = Dispatcher.InvokeAsync(() =>
            {
              running?.Close();
              new MessageWindow("Archive Process Failed. Check the Error Log for Details.", "Archive Now").ShowDialog();
            });
          }
        });
      }
      else
      {
        new MessageWindow("No Open or Configured Files to Archive.", "Archive Now").ShowDialog();
      }
    }

    private void EnableCheckBoxOnChecked(object sender, RoutedEventArgs e)
    {
      type.IsEnabled = day.IsEnabled = hour.IsEnabled = min.IsEnabled = ampm.IsEnabled = fileSizes.IsEnabled =
        fileAges.IsEnabled = compress.IsEnabled = organize.IsEnabled = true;
      titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
      titleLabel.Content = "Log Management Active";

      if (!ConfigUtil.IfSet("LogManagementEnabled"))
      {
        EnableSave();
      }
    }

    private void EnableCheckBoxOnUnchecked(object sender, RoutedEventArgs e)
    {
      type.IsEnabled = day.IsEnabled = hour.IsEnabled = min.IsEnabled = ampm.IsEnabled = fileSizes.IsEnabled =
        fileAges.IsEnabled = compress.IsEnabled = organize.IsEnabled = false;
      titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
      titleLabel.Content = "Enable Log Management";

      if (ConfigUtil.IfSet("LogManagementEnabled"))
      {
        EnableSave();
      }
    }
  }
}

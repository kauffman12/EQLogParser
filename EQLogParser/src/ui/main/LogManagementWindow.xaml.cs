using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LogManagementWindow.xaml
  /// </summary>
  public partial class LogManagementWindow
  {
    public static string CompressNo => "No";
    public static string CompressYes => "Yes";
    public static string OrganizeInFiles => "Individual Files";
    public static string OrganizeInFolders => "Server and Character Folders";
    private readonly bool _ready;

    internal LogManagementWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      enableCheckBox.IsChecked = ConfigUtil.IfSet("LogManagementEnabled");
      fileSizes.IsEnabled = fileAges.IsEnabled = compress.IsEnabled =
        organize.IsEnabled = enableCheckBox.IsChecked == true;

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
      _ready = true;
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (_ready)
      {
        UpdateSettings();
      }
    }

    private void UpdateSettings()
    {
      if (_ready)
      {
        ConfigUtil.SetSetting("LogManagementEnabled", enableCheckBox.IsChecked == true);
        closeButton.IsEnabled = !(enableCheckBox.IsChecked == true && fileAges.SelectedIndex == 0 && fileSizes.SelectedIndex == 0);

        // ignore invalid settings
        if (fileAges.SelectedIndex == 0 && fileSizes.SelectedIndex == 0)
        {
          return;
        }

        if (fileSizes.SelectedItem is ComboBoxItem item)
        {
          ConfigUtil.SetSetting("LogManagementMinFileSize", item.Content.ToString());
        }

        if (fileAges.SelectedItem is ComboBoxItem item2)
        {
          ConfigUtil.SetSetting("LogManagementMinFileAge", item2.Content.ToString());
        }

        if (compress.SelectedItem is ComboBoxItem item3)
        {
          ConfigUtil.SetSetting("LogManagementCompressArchive", item3.Content.ToString());
        }

        if (organize.SelectedItem is ComboBoxItem item4)
        {
          ConfigUtil.SetSetting("LogManagementOrganize", item4.Content.ToString());
        }
      }
    }

    private static void UpdateComboBox(ComboBox combo, string setting, string defaultValue)
    {
      var index = 0;
      var saved = ConfigUtil.GetSetting(setting, defaultValue);
      for (var i = 0; i < combo.Items.Count; i++)
      {
        if (combo.Items[i] is ComboBoxItem item && saved.Equals(item.Content.ToString(), StringComparison.CurrentCulture))
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

      if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
      {
        txtFolderPath.FontStyle = FontStyles.Normal;
        txtFolderPath.Text = dialog.FileName;
        ConfigUtil.SetSetting("LogManagementArchiveFolder", dialog.FileName);
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

      if (await TriggerStateManager.Instance.GetConfig() is var config)
      {
        var logFiles = new HashSet<string>();
        if (MainWindow.CurrentLogFile is { } currentFile)
        {
          logFiles.Add(currentFile);
        }

        if (config.IsAdvanced)
        {
          foreach (var file in config.Characters.Select(character => character.FilePath))
          {
            logFiles.Add(file);
          }
        }

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

              await Task.Delay(500);
              FileUtil.ArchiveNow(logFiles);

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
    }

    private void EnableCheckBoxOnChecked(object sender, RoutedEventArgs e)
    {
      fileSizes.IsEnabled = fileAges.IsEnabled = compress.IsEnabled = organize.IsEnabled = true;
      titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
      titleLabel.Content = "Log Management Active";
      UpdateSettings();
    }

    private void EnableCheckBoxOnUnchecked(object sender, RoutedEventArgs e)
    {
      fileSizes.IsEnabled = fileAges.IsEnabled = compress.IsEnabled = organize.IsEnabled = false;
      titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
      titleLabel.Content = "Enable Log Management";
      UpdateSettings();
    }
  }
}

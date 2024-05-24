using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LogManagementWindow.xaml
  /// </summary>
  public partial class LogManagementWindow
  {
    private readonly bool _ready;

    internal LogManagementWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      enableCheckBox.IsChecked = ConfigUtil.IfSet("LogManagementEnabled");
      txtFolderPath.IsEnabled =
        fileSizes.IsEnabled = fileAges.IsEnabled = compress.IsEnabled = enableCheckBox.IsChecked == true;

      // read archive folder
      var savedFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (!string.IsNullOrEmpty(savedFolder) && Path.GetDirectoryName(savedFolder) != null)
      {
        txtFolderPath.Text = savedFolder;
      }

      // read saved settings
      UpdateComboBox(fileSizes, "LogManagementMinFileSize", "500M");
      UpdateComboBox(fileAges, "LogManagementMinFileAge", "1 Week");
      UpdateComboBox(compress, "LogManagementCompressArchive", "Yes");
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
      }
    }

    private static void UpdateComboBox(ComboBox combo, string setting, string defaultValue)
    {
      // read compress settings
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

    private void EnableCheckBoxOnChecked(object sender, RoutedEventArgs e)
    {
      txtFolderPath.IsEnabled = fileSizes.IsEnabled = fileAges.IsEnabled = compress.IsEnabled = true;
      titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
      titleLabel.Content = "Log Management Active";
      UpdateSettings();
    }

    private void EnableCheckBoxOnUnchecked(object sender, RoutedEventArgs e)
    {
      txtFolderPath.IsEnabled = fileSizes.IsEnabled = fileAges.IsEnabled = compress.IsEnabled = false;
      titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
      titleLabel.Content = "Enable Log Management";
      UpdateSettings();
    }
  }
}

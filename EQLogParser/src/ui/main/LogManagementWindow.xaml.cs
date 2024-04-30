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
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();

      Owner = Application.Current.MainWindow;
      enableCheckBox.IsChecked = ConfigUtil.IfSet("LogManagementEnabled");

      // read archive folder
      var savedFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (!string.IsNullOrEmpty(savedFolder) && Path.GetDirectoryName(savedFolder) != null)
      {
        txtFolderPath.Text = savedFolder;
      }

      // read save file min size
      var fileSizeIndex = 6;
      var savedFileSize = ConfigUtil.GetSetting("LogManagementMinFileSize", "500M");
      for (var i = 0; i < fileSizes.Items.Count; i++)
      {
        if (fileSizes.Items[i] is ComboBoxItem item &&
            savedFileSize.Equals(item.Content.ToString(), StringComparison.OrdinalIgnoreCase))
        {
          fileSizeIndex = i;
          break;
        }
      }

      fileSizes.SelectedIndex = fileSizeIndex;

      // read save file min date
      var fileAgeIndex = 1;
      var savedFileAge = ConfigUtil.GetSetting("LogManagementMinFileAge", "1 Week");
      for (var i = 0; i < fileAges.Items.Count; i++)
      {
        if (fileAges.Items[i] is ComboBoxItem item &&
            savedFileAge.Equals(item.Content.ToString(), StringComparison.CurrentCulture))
        {
          fileAgeIndex = i;
          break;
        }
      }

      fileAges.SelectedIndex = fileAgeIndex;

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
      if (fileSizes.SelectedItem is ComboBoxItem item)
      {
        ConfigUtil.SetSetting("LogManagementMinFileSize", item.Content.ToString());
      }
      else if (fileAges.SelectedItem is ComboBoxItem item2)
      {
        ConfigUtil.SetSetting("LogManagementMinFileAge", item2.Content.ToString());
      }
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
      txtFolderPath.IsEnabled = fileSizes.IsEnabled = fileAges.IsEnabled = true;
      titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
      titleLabel.Content = "Log Management Active";
      ConfigUtil.SetSetting("LogManagementEnabled", true);
      UpdateSettings();
    }

    private void EnableCheckBoxOnUnchecked(object sender, RoutedEventArgs e)
    {
      txtFolderPath.IsEnabled = fileSizes.IsEnabled = fileAges.IsEnabled = false;
      titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
      titleLabel.Content = "Enable Log Management";
      ConfigUtil.SetSetting("LogManagementEnabled", false);
    }
  }
}

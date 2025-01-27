
using log4net;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;

namespace EQLogParser
{
  public partial class WavCreatorWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly List<string> _deviceIdList;
    private readonly List<string> _deviceNameList;

    internal WavCreatorWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      voices.ItemsSource = AudioManager.Instance.GetVoiceList();
      // Update audio device list
      var deviceInfo = AudioManager.GetDeviceList();
      _deviceIdList = deviceInfo.idList;
      _deviceNameList = deviceInfo.nameList;
      deviceList.ItemsSource = _deviceNameList;
      volumeSlider.Value = 100;
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void OptionsChanged(object sender, RoutedEventArgs e) => HandleChanged();
    private void TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => HandleChanged();

    private void VolumeButtonClick(object sender, RoutedEventArgs e)
    {
      volumePopup.IsOpen = true;
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
      var saveFileDialog = new SaveFileDialog();
      saveFileDialog.Filter = "WAV Files (*.wav)|*.wav";

      if (IsValid() && saveFileDialog.ShowDialog() == true)
      {
        var volume = (int)Math.Round(volumeSlider.Value) / 100.0f;
        AudioManager.Instance.SpeakOrSaveTtsAsync(tts.Text, voices.SelectedValue as string, _deviceIdList[deviceList.SelectedIndex], volume,
          rateOption.SelectedIndex, saveFileDialog.FileName);
      }
    }

    private void TestClicked(object sender, RoutedEventArgs e)
    {
      if (IsValid())
      {
        var volume = (int)Math.Round(volumeSlider.Value) / 100.0f;
        AudioManager.Instance.SpeakOrSaveTtsAsync(tts.Text, voices.SelectedValue as string, _deviceIdList[deviceList.SelectedIndex], volume, rateOption.SelectedIndex);
      }
    }

    private void HandleChanged()
    {
      if (testButton != null && exportButton != null)
      {
        exportButton.IsEnabled = testButton.IsEnabled = IsValid();
      }
    }

    private bool IsValid()
    {
      return voices.SelectedValue is string && !string.IsNullOrEmpty(tts.Text) && deviceList.SelectedIndex > -1 && rateOption.SelectedIndex > -1;
    }
  }
}

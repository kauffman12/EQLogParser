using EQLogParser.Audio;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class TtsEngineWindow
  {
    private const string SettingKey = "TtsEngine";
    private CancellationTokenSource _cts;
    private bool _downloading;
    private bool _ready;

    internal TtsEngineWindow()
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      RefreshState();
    }

    private void RefreshState()
    {
      _ready = false;

      var engines = AudioManager.GetAvailableEngines();
      engineList.ItemsSource = engines;

      var saved = ConfigUtil.GetSetting(SettingKey);
      var selected = !string.IsNullOrEmpty(saved) && engines.Contains(saved) ? saved : AudioManager.Instance.GetActiveEngine();
      engineList.SelectedItem = engines.Contains(selected) ? selected : engines[0];

      UpdateEngineHint();

      if (AudioManager.Instance.IsKokoroModelAvailable())
      {
        downloadButton.IsEnabled = false;
        downloadButton.Content = "Already Downloaded";
        statusText.Text = string.Empty;
      }
      else
      {
        downloadButton.IsEnabled = true;
        downloadButton.Content = "Download Kokoro";
        statusText.Text = string.Empty;
      }

      _ready = true;
    }

    private void UpdateEngineHint()
    {
      if (engineList.SelectedItem is string selected)
      {
        engineHintText.Text = selected == AudioManager.Instance.GetActiveEngine()
          ? "This is the engine currently in use."
          : "Restart EQLogParser for this change to take effect.";
      }
    }

    private void EngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_ready && engineList.SelectedItem is string selected)
      {
        ConfigUtil.SetSetting(SettingKey, selected);
        UpdateEngineHint();
      }
    }

    private async void DownloadClicked(object sender, RoutedEventArgs e)
    {
      if (_downloading)
      {
        return;
      }

      _downloading = true;
      _cts = new CancellationTokenSource();
      downloadButton.IsEnabled = false;
      progressBar.Visibility = Visibility.Visible;
      progressBar.Value = 0;
      statusText.Text = "Downloading...";

      var success = await AudioManager.Instance.DownloadKokoroModelAsync(
        progress => Dispatcher.Invoke(() => progressBar.Value = Math.Clamp(progress * 100, 0, 100)), _cts.Token);

      _downloading = false;
      progressBar.Visibility = Visibility.Collapsed;

      if (success)
      {
        statusText.Text = "Download complete.";
        RefreshState();
      }
      else
      {
        statusText.Text = "Download failed. Check the Error Log for details and try again.";
        downloadButton.IsEnabled = true;
      }
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
      _cts?.Cancel();
      base.OnClosed(e);
    }
  }
}

using EQLogParser.Audio;
using log4net;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class TtsEngineWindow
  {
    private const string SettingKey = "TtsEngine";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private CancellationTokenSource _cts;
    private bool _downloading;
    private bool _switching;
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
      if (engineList.SelectedItem is not string selected)
      {
        return;
      }

      if (selected == AudioManager.Instance.GetActiveEngine())
      {
        engineHintText.Text = "This is the engine currently in use.";
      }
      else if (selected == AudioManager.KokoroEngine && !AudioManager.Instance.IsKokoroModelAvailable())
      {
        engineHintText.Text = "Download Kokoro below. The switch happens as soon as the download finishes.";
      }
      else
      {
        engineHintText.Text = "Takes effect right away, starting with the next callout.";
      }
    }

    private async void EngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready || _downloading || engineList.SelectedItem is not string selected)
      {
        return;
      }

      ConfigUtil.SetSetting(SettingKey, selected);

      // anything already on disk can be switched to live; the saved setting still decides what the next start uses
      if (selected != AudioManager.Instance.GetActiveEngine())
      {
        await ApplyEngineAsync(selected);
      }
      else
      {
        UpdateEngineHint();
      }
    }

    /*
     * Switching applies to the running session: Kokoro builds an inference session over its model, Piper reads its
     * voice pack and Windows proves its voices, so this takes a moment and is reported in statusText. A switch that
     * cannot be honored leaves the current engine speaking.
     */
    private async Task ApplyEngineAsync(string selected)
    {
      if (_switching)
      {
        return;
      }

      _switching = true;
      engineList.IsEnabled = false;
      downloadButton.IsEnabled = false;
      statusText.Text = $"Switching to {selected}...";

      try
      {
        var switched = await AudioManager.Instance.SwitchEngineAsync(selected);
        RefreshState();

        if (switched)
        {
          statusText.Text = $"Now using {selected}.";
        }
        else
        {
          ShowStaysOnMessage();
        }
      }
      catch (Exception ex)
      {
        Log.Debug($"Unable to switch the TTS engine to {selected}", ex);
        ShowStaysOnMessage();
      }
      finally
      {
        _switching = false;
        engineList.IsEnabled = true;
        downloadButton.IsEnabled = !AudioManager.Instance.IsKokoroModelAvailable();
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
      engineList.IsEnabled = false;
      progressBar.Visibility = Visibility.Visible;
      progressBar.Value = 0;
      statusText.Text = "Downloading...";

      var success = await AudioManager.Instance.DownloadKokoroModelAsync(
        progress => Dispatcher.Invoke(() => progressBar.Value = Math.Clamp(progress * 100, 0, 100)), _cts.Token);

      _downloading = false;
      progressBar.Visibility = Visibility.Collapsed;
      engineList.IsEnabled = true;

      if (success)
      {
        RefreshState();
        statusText.Text = "Download complete.";

        // a downloaded Kokoro is what the user wanted, so start speaking with it now rather than after a restart
        // switch once, explicitly: setting the selection may or may not raise the handler
        _ready = false;
        ConfigUtil.SetSetting(SettingKey, AudioManager.KokoroEngine);
        engineList.SelectedItem = AudioManager.KokoroEngine;
        _ready = true;
        await ApplyEngineAsync(AudioManager.KokoroEngine);
      }
      else
      {
        statusText.Text = "Download failed. Check the Error Log for details and try again.";
        downloadButton.IsEnabled = true;
      }
    }

    // a selection that could not be applied must not claim to be in use
    private void ShowStaysOnMessage()
    {
      var active = AudioManager.Instance.GetActiveEngine();
      engineHintText.Text = $"EQLogParser is still using {active}.";
      statusText.Text = $"Could not switch. {active} keeps speaking.";
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
      _cts?.Cancel();
      base.OnClosed(e);
    }
  }
}

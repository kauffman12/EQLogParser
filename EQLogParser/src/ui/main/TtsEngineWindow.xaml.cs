using EQLogParser.Audio;
using log4net;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /*
   * Speech engine picker. Piper and Kokoro are not carried by the installer: selecting one of them offers a download
   * of its runtime pack, and an installed engine can be removed again to reclaim the space. Whatever is on disk is
   * switched to live, so the change applies to the next callout rather than after a restart. See docs/TtsPacks.md.
   */
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

    /* One row in the picker. CanPick is what greys an entry out in the drop down. */
    private sealed record EngineOption(string Name, bool CanPick)
    {
      public override string ToString() => Name;
    }

    private string SelectedEngine => (engineList.SelectedItem as EngineOption)?.Name;

    private void RefreshState()
    {
      _ready = false;

      // Every engine is listed, installed or not: this is the only place a runtime pack gets downloaded from, so an
      // engine that needs one has to be reachable here. One row is greyed out only when there is neither a way to use
      // it nor a way to get it, which in practice means the Windows voices are missing from this machine.
      var options = AudioManager.GetAllEngines()
        .Select(name => new EngineOption(name, CanPick(name)))
        .ToList();
      engineList.ItemsSource = options;

      var active = AudioManager.Instance.GetActiveEngine();
      var names = options.Select(option => option.Name).ToList();
      var saved = ConfigUtil.GetSetting(SettingKey);
      var selected = !string.IsNullOrEmpty(saved) && names.Contains(saved) ? saved : active;
      if (!names.Contains(selected))
      {
        selected = names.Contains(active) ? active : AudioManager.WindowsEngine;
      }

      engineList.SelectedItem = options.First(option => option.Name == selected);
      UpdateEngineText();
      UpdateButtons(selected);
      _ready = true;
    }

    /* An engine you can speak with now, or download and then speak with, is an engine worth selecting. */
    private static bool CanPick(string engine) =>
      AudioManager.Instance.IsEngineAvailable(engine) || AudioManager.Instance.GetEngineDownloadBytes(engine) > 0;

    /*
     * What each engine is like to live with, because three names in a dropdown decide both the sound and whether the
     * machine keeps up. One line each, tradeoff first: a user should be able to pick correctly without reading a
     * manual, and the reason to keep Kokoro off a ten year old box has to be visible before the download, not after.
     */
    private static string GetEngineDescription(string engine) => engine switch
    {
      AudioManager.PiperEngine =>
        "Fast and light: speaks the instant a callout fires and costs almost nothing. The voices sound synthetic next " +
        "to Kokoro.",
      AudioManager.KokoroEngine =>
        "The best sounding voices here, and the heaviest: a few hundred MB of memory and real CPU, so it takes a moment " +
        "to start speaking. An older machine may fall behind.",
      AudioManager.WindowsEngine =>
        "Nothing to download and no cost at all. These voices come from Windows itself: they are not there under Wine, " +
        "and how good they sound varies with the voice packs a machine has.",
      _ => "Speech runs locally on this machine."
    };

    /* Both text lines under the picker follow the selection: what the engine is like, then whether it can be used. */
    private void UpdateEngineText()
    {
      if (SelectedEngine is not string selected)
      {
        return;
      }

      infoText.Text = GetEngineDescription(selected);

      var available = AudioManager.Instance.IsEngineAvailable(selected);
      var downloadable = AudioManager.Instance.GetEngineDownloadBytes(selected) > 0;

      if (selected == AudioManager.Instance.GetActiveEngine())
      {
        engineHintText.Text = "This is the engine currently in use.";
      }
      else if (available)
      {
        engineHintText.Text = "Takes effect right away, starting with the next callout.";
      }
      else if (downloadable)
      {
        engineHintText.Text = $"{selected} is not installed. Enabling it downloads about " +
          $"{FormatSize(AudioManager.Instance.GetEngineDownloadBytes(selected))} from GitHub into your local app data.";
      }
      else
      {
        // Proven unusable rather than merely unused: this is either not real Windows -- Wine has no voices to give --
        // or the speech runtime is missing. One of those has no fix at all, so say what the way out is.
        engineHintText.Text = $"The {selected} voices are not available here: they come from Windows itself, and " +
          "neither Wine nor a Windows image without the speech runtime has any. Enable Piper or Kokoro below.";
      }
    }

    /* The download and remove buttons belong to whichever engine is selected. */
    private void UpdateButtons(string engine)
    {
      var hasPack = AudioManager.Instance.GetEngineDownloadBytes(engine) > 0;
      var available = AudioManager.Instance.IsEngineAvailable(engine);

      downloadButton.Visibility = hasPack ? Visibility.Visible : Visibility.Collapsed;
      downloadButton.Content = available ? "Installed" : $"Download {engine} ({FormatSize(AudioManager.Instance.GetEngineDownloadBytes(engine))})";
      downloadButton.IsEnabled = hasPack && !available && !_downloading && !_switching;

      // Only files we downloaded ourselves can be deleted, and only by a quiet engine: a voice pack that came from an
      // older installer sits beside the program, where uninstalling is the way to get rid of it.
      var downloaded = AudioManager.Instance.IsEngineDownloaded(engine);
      removeButton.Visibility = hasPack && downloaded ? Visibility.Visible : Visibility.Collapsed;
      removeButton.IsEnabled = downloaded && engine != AudioManager.Instance.GetActiveEngine() && !_downloading && !_switching;
    }

    private async void EngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready || _downloading || SelectedEngine is not string selected)
      {
        return;
      }

      UpdateEngineText();
      UpdateButtons(selected);

      // An engine with nothing on disk cannot be switched to and is not remembered either: the saved setting decides
      // what the next start uses, so it has to name something that can actually speak.
      if (!AudioManager.Instance.IsEngineAvailable(selected))
      {
        statusText.Text = string.Empty;
        return;
      }

      ConfigUtil.SetSetting(SettingKey, selected);

      if (selected != AudioManager.Instance.GetActiveEngine())
      {
        await ApplyEngineAsync(selected);
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
      UpdateButtons(selected);
      statusText.Text = $"Switching to {selected}...";

      try
      {
        var switched = await AudioManager.Instance.SwitchEngineAsync(selected);

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
        RefreshState();
      }
    }

    private async void DownloadClicked(object sender, RoutedEventArgs e)
    {
      if (_downloading || SelectedEngine is not { } engine)
      {
        return;
      }

      _downloading = true;
      _cts = new CancellationTokenSource();
      engineList.IsEnabled = false;
      UpdateButtons(engine);
      progressBar.Visibility = Visibility.Visible;
      progressBar.Value = 0;
      statusText.Text = $"Downloading {engine}...";

      // Progress<T> posts back to the UI thread it was created on, which is what makes the bar move.
      var progress = new Progress<float>(value => progressBar.Value = Math.Clamp(value * 100, 0, 100));
      var success = await AudioManager.Instance.InstallEngineAsync(engine, progress, _cts.Token);

      _downloading = false;
      progressBar.Visibility = Visibility.Collapsed;
      engineList.IsEnabled = true;

      if (!success)
      {
        statusText.Text = "Download failed. Check the Error Log for details and try again.";
        UpdateButtons(engine);
        return;
      }

      RefreshState();

      // having chosen the engine, the user wants to speak with it now rather than after a restart; set the selection
      // once and switch explicitly, because assigning it may or may not raise the handler
      _ready = false;
      ConfigUtil.SetSetting(SettingKey, engine);
      engineList.SelectedItem = new EngineOption(engine, true);
      _ready = true;
      await ApplyEngineAsync(engine);
    }

    private void RemoveClicked(object sender, RoutedEventArgs e)
    {
      if (SelectedEngine is not { } engine)
      {
        return;
      }

      if (AudioManager.Instance.RemoveEngineFiles(engine))
      {
        RefreshState();
        statusText.Text = $"{engine} files removed. The engine can be downloaded again at any time.";
        return;
      }

      // Either the active engine, which AudioManager refuses for, or a running copy has the native libraries mapped.
      statusText.Text = $"Could not remove the {engine} files. They are in use by a running copy of EQLogParser; " +
        "close it and start again to free the space.";
      UpdateButtons(engine);
    }

    // a selection that could not be applied must not claim to be in use
    private void ShowStaysOnMessage()
    {
      var active = AudioManager.Instance.GetActiveEngine();
      engineHintText.Text = $"EQLogParser is still using {active}.";
      statusText.Text = $"Could not switch. {active} keeps speaking.";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
      <= 0 => string.Empty,
      >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
      _ => $"{bytes / (1024.0 * 1024):0} MB"
    };

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
      _cts?.Cancel();
      base.OnClosed(e);
    }
  }
}

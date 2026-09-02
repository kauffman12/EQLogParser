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

    // TtsPackManager spends the first nine tenths of the bar on bytes off the network and the last tenth on hashing and
    // unpacking, which is why the wording changes there rather than at 100%.
    private const double VerifyPhaseFraction = 0.9;
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

      /*
       * Open on the engine that is actually speaking, not on the one that was asked for. The saved setting can name an
       * engine that never came up -- its pack is missing, a model refused to load, Wine has no Windows voices -- and a
       * picker resting on that name claims a choice that was never made while hiding the fallback underneath it. Where
       * the two disagree the line under the picker says so.
       */
      var active = AudioManager.Instance.GetActiveEngine();
      var saved = ConfigUtil.GetSetting(SettingKey);
      var selected = options.FirstOrDefault(row => row.Name == active)
        ?? options.FirstOrDefault(row => string.Equals(row.Name, saved, StringComparison.OrdinalIgnoreCase))
        ?? options.FirstOrDefault(row => row.Name == AudioManager.WindowsEngine)
        ?? options[0];

      engineList.SelectedItem = selected;
      UpdateEngineText();
      UpdateButtons(selected.Name);
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
        var saved = ConfigUtil.GetSetting(SettingKey);
        engineHintText.Text = string.IsNullOrEmpty(saved) || saved == selected
          ? "This is the engine currently in use."
          : $"{selected} is what EQLogParser speaks with. The saved choice, {saved}, would not start here.";
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
     * Move the picker without that counting as a choice, so assigning a row does not re-enter the handler and start a
     * second switch. The row is taken from the list rather than built fresh: a value the list does not contain leaves
     * the drop down with nothing highlighted, and clicking that name again then raises no change at all.
     */
    private void SelectRow(string engine)
    {
      _ready = false;
      engineList.SelectedItem = engineList.Items.Cast<EngineOption>().FirstOrDefault(row => row.Name == engine)
        ?? engineList.SelectedItem;
      UpdateEngineText();
      _ready = true;
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
      var totalBytes = AudioManager.Instance.GetEngineDownloadBytes(engine);
      progressBar.Visibility = Visibility.Visible;
      progressBar.Value = 0;
      statusText.Text = $"Downloading {engine}, about {FormatSize(totalBytes)} from GitHub...";

      /*
       * Progress<T> posts back to the UI thread it was created on, which is what makes the bar move. The byte count is
       * spelled out as well because a bar on its own cannot say whether 348 MB is nearly there or barely started, and
       * because the last tenth of the run is hashing and unpacking rather than the network.
       */
      var progress = new Progress<float>(value =>
      {
        progressBar.Value = Math.Clamp(value * 100, 0, 100);
        statusText.Text = value < VerifyPhaseFraction
          ? $"{engine}: about {FormatSize((long) (totalBytes * (value / VerifyPhaseFraction)))} of {FormatSize(totalBytes)} downloaded"
          : $"{engine}: checking the files and installing them...";
      });
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

      // Having chosen the engine, the user wants to speak with it now rather than after a restart.
      ConfigUtil.SetSetting(SettingKey, engine);
      SelectRow(engine);
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

    /*
     * A selection that could not be applied must not claim to be in use, and must not survive as a preference either:
     * this setting decides the engine at the next start, so leaving it on something that just failed to initialize
     * would open every later session on silence. Unwind it to what is speaking; RefreshState then puts the picker back
     * on the same row, and the failure is carried by the status line below it.
     */
    private void ShowStaysOnMessage()
    {
      var active = AudioManager.Instance.GetActiveEngine();
      ConfigUtil.SetSetting(SettingKey, active);
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

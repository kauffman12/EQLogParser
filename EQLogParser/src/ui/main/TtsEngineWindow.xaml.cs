using EQLogParser.Audio;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /*
   * Speech engine picker. Piper and Kokoro are not carried by the installer: an engine with no runtime offers to
   * download one, an installed engine is started with Use, and either can be removed again to reclaim the space.
   * Switching is live, so it applies to the next callout rather than after a restart. Looking around the list changes
   * nothing on its own. See docs/TtsPacks.md.
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

      // Containers exist only once the generator has run, which is after layout and again whenever the drop down is
      // opened, so the enabled state is applied as they appear rather than once here.
      engineList.ItemContainerGenerator.StatusChanged += (_, _) => UpdateRowEnabledState();
      engineList.DropDownOpened += (_, _) => UpdateRowEnabledState();
      RefreshState();
    }

    /* One row in the picker. CanPick is what greys an entry out in the drop down. */
    private sealed record EngineOption(string Name, bool CanPick)
    {
      public override string ToString() => Name;
    }

    private List<EngineOption> _options = [];

    private string SelectedEngine => (engineList.SelectedItem as EngineOption)?.Name;

    private void RefreshState()
    {
      _ready = false;

      // Every engine is listed, installed or not: this is the only place a runtime pack gets downloaded from, so an
      // engine that needs one has to be reachable here. One row is greyed out only when there is neither a way to use
      // it nor a way to get it, which in practice means the Windows voices are missing from this machine.
      _options = AudioManager.GetAllEngines()
        .Select(name => new EngineOption(name, CanPick(name)))
        .ToList();
      engineList.ItemsSource = _options;
      UpdateRowEnabledState();

      /*
       * Open on the engine that is actually speaking, not on the one that was asked for. The saved setting can name an
       * engine that never came up -- its pack is missing, a model refused to load, Wine has no Windows voices -- and a
       * picker resting on that name claims a choice that was never made while hiding the fallback underneath it. Where
       * the two disagree the line under the picker says so.
       */
      var active = AudioManager.Instance.GetActiveEngine();
      var saved = ConfigUtil.GetSetting(SettingKey);
      var selected = _options.FirstOrDefault(row => row.Name == active)
        ?? _options.FirstOrDefault(row => string.Equals(row.Name, saved, StringComparison.OrdinalIgnoreCase))
        ?? _options.FirstOrDefault(row => row.Name == AudioManager.WindowsEngine)
        ?? _options[0];

      engineList.SelectedItem = selected;
      UpdateEngineText();
      UpdateButtons(selected.Name);
      _ready = true;
    }

    /* An engine you can speak with now, or download and then speak with, is an engine worth selecting. */
    private static bool CanPick(string engine) =>
      AudioManager.Instance.IsEngineAvailable(engine) || AudioManager.Instance.GetEngineDownloadBytes(engine) > 0;

    /*
     * Grey out rows this machine cannot use by setting IsEnabled on the generated containers.
     *
     * The obvious way is an ItemContainerStyle with a binding, and it does not work here: assigning a container style
     * replaces it rather than adding to it, so the themed ComboBoxItem is gone and what comes back is a WPF default item
     * that reads as broken against the skin. BasedOn cannot bridge that in this codebase, which is why no other combo
     * uses it. Setting the property on the container the theme actually produced keeps its own disabled look, greyed in
     * whichever colors the current skin uses.
     */
    private void UpdateRowEnabledState()
    {
      var generator = engineList.ItemContainerGenerator;
      foreach (var option in _options)
      {
        if (generator.ContainerFromItem(option) is ComboBoxItem item)
        {
          item.IsEnabled = option.CanPick;
        }
      }
    }

    /*
     * What each engine is like to live with, because three names in a dropdown decide both the sound and whether the
     * machine keeps up. One line each, tradeoff first: a user should be able to pick correctly without reading a
     * manual, and the reason to keep Kokoro off a ten year old box has to be visible before the download, not after.
     */
    private static string GetEngineDescription(string engine) => engine switch
    {
      AudioManager.PiperEngine =>
        "Fast and lightweight. Speech starts almost instantly and uses very few system resources, but the voices sound " +
        "more synthetic than Kokoro.",

      AudioManager.KokoroEngine =>
        "The most natural-sounding voices, but also the most demanding. Uses a few hundred MB of memory and more CPU, " +
        "so speech may take a moment to start and slower systems may struggle to keep up.",

      AudioManager.WindowsEngine =>
        "Built into Windows with nothing extra to download. Voice quality depends on the installed Windows voice. " +
        "This engine is not available when running under Linux/Wine.",

      _ => "Speech is generated locally on this machine."
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
        if (string.IsNullOrEmpty(saved) || saved == selected)
        {
          SetHint("This is the engine currently in use.");
        }
        else
        {
          // Something is speaking that is not what was asked for, which is worth colouring: it means an engine failed
          // to come up and the fallback took over quietly.
          SetHint($"{selected} is in use; the saved choice, {saved}, would not start.", WarnBrush);
        }
      }
      else if (available)
      {
        SetHint("Press Use to switch, starting with the next callout.");
      }
      else if (downloadable)
      {
        // Size and action both live on the button, so this only has to say why the button is there.
        SetHint($"{selected} is not installed. Use Download below.");
      }
      else
      {
        // Proven unusable rather than merely unused: not real Windows -- Wine has no voices to give -- or the speech
        // runtime is missing. One of those has no fix at all, so say what the way out is.
        SetHint($"The {selected} voices come from Windows itself and are absent under Wine. Enable Piper or Kokoro below.", WarnBrush);
      }
    }

    /*
     * Body text and status lines resolve their colour through the theme dictionaries rather than picking one up in
     * code, so switching themes mid-session repaints them. Passing no brush key hands the element back to the theme's
     * own text colour: ClearValue restores inheritance, where assigning a colour would not.
     */
    private void SetHint(string text, string brushKey = null) => SetText(engineHintText, text, brushKey);

    private void SetStatus(string text, string brushKey = null) => SetText(statusText, text, brushKey);

    private const string GoodBrush = "EQGoodForegroundBrush";
    private const string WarnBrush = "EQWarnForegroundBrush";
    private const string StopBrush = "EQStopForegroundBrush";

    private static void SetText(TextBlock target, string text, string brushKey)
    {
      target.Text = text;
      if (string.IsNullOrEmpty(brushKey))
      {
        // Body text as the skin defines it, which is what the XAML opens with. ClearValue would do something similar by
        // inheriting from the window, but this keeps one obvious answer for "plain text in a dialog".
        target.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
      }
      else
      {
        target.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
      }
    }

    /* The download and remove buttons belong to whichever engine is selected. */
    private void UpdateButtons(string engine)
    {
      var bytes = AudioManager.Instance.GetEngineDownloadBytes(engine);
      var available = AudioManager.Instance.IsEngineAvailable(engine);
      var active = engine == AudioManager.Instance.GetActiveEngine();
      var busy = _downloading || _switching;

      // One button for whatever comes next, because two would mean a second one that is disabled half the time.
      actionButton.Visibility = bytes > 0 ? Visibility.Visible : Visibility.Collapsed;
      actionButton.Content = DescribeAction(engine, bytes, available, active);
      actionButton.IsEnabled = !busy && (!available || !active);

      // Reclaiming a pack is only possible while nothing is speaking with it: the engine in use keeps its native
      // libraries mapped until EQLogParser closes, so deleting that directory would leave half of it behind. Packs
      // downloaded by an older installer sit beside the program and are not offered here either.
      var downloaded = AudioManager.Instance.IsEngineDownloaded(engine);
      removeButton.Visibility = bytes > 0 && downloaded ? Visibility.Visible : Visibility.Collapsed;
      removeButton.IsEnabled = downloaded && !active && !busy;
    }

    /* Browsing changes nothing. Applying is a deliberate press of Use. */
    private void EngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready || _downloading || SelectedEngine is not string selected)
      {
        return;
      }

      // Selecting used to switch engines on its own, which made an installed engine impossible to reach for removal:
      // whichever row was on screen had just become the active one, and Remove refuses the active engine because its
      // libraries stay mapped until the app closes. Browsing is free now, so there is a way to stand next to an engine
      // without speaking through it.
      UpdateEngineText();
      UpdateButtons(selected);
      SetStatus(string.Empty);
    }

    /* The one action button: get the runtime if it is missing, otherwise speak with this engine from here on. */
    private async void ActionClicked(object sender, RoutedEventArgs e)
    {
      if (_downloading || _switching || SelectedEngine is not { } engine)
      {
        return;
      }

      if (!AudioManager.Instance.IsEngineAvailable(engine))
      {
        await DownloadEngineAsync(engine);
        return;
      }

      await ApplyEngineAsync(engine);
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

      // The setting decides which engine the next start uses, so it is written where using one has actually been asked
      // for rather than merely highlighted. A switch that fails unwinds it again.
      ConfigUtil.SetSetting(SettingKey, selected);

      UpdateButtons(selected);
      SetStatus("switching...");

      try
      {
        var switched = await AudioManager.Instance.SwitchEngineAsync(selected);

        if (switched)
        {
          SetStatus($"Now using {selected}.", GoodBrush);
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

    private async Task DownloadEngineAsync(string engine)
    {
      _downloading = true;
      _cts = new CancellationTokenSource();
      engineList.IsEnabled = false;
      UpdateButtons(engine);
      var totalBytes = AudioManager.Instance.GetEngineDownloadBytes(engine);
      progressBar.Visibility = Visibility.Visible;
      progressBar.Value = 0;
      SetStatus($"Downloading {FormatSize(totalBytes)}...");

      /*
       * Progress<T> posts back to the UI thread it was created on, which is what makes the bar move. Bytes rather than
       * a percentage because a bar alone cannot say whether 224 MB is nearly there or barely started. Nothing names the
       * engine: they clicked its Download button, and this row has room for one short line.
       */
      var progress = new Progress<float>(value =>
      {
        progressBar.Value = Math.Clamp(value * 100, 0, 100);
        SetStatus(value < VerifyPhaseFraction
          ? $"{FormatSize((long)(totalBytes * (value / VerifyPhaseFraction)))} of {FormatSize(totalBytes)}"
          : "validating files...");
      });
      var success = await AudioManager.Instance.InstallEngineAsync(engine, progress, _cts.Token);

      _downloading = false;
      progressBar.Visibility = Visibility.Collapsed;
      engineList.IsEnabled = true;

      if (!success)
      {
        SetStatus("Download failed. See the Error Log and try again.", StopBrush);
        UpdateButtons(engine);
        return;
      }

      RefreshState();

      // Someone who just downloaded an engine wants to speak with it, not to press one more button for it. This is the
      // one path that applies without Use, and it only runs on an engine they chose a moment ago.
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
        // The saved setting decides the next start; leaving it pointing at a directory that no longer exists buys a
        // silent session and a warning about a choice that can no longer be made.
        if (string.Equals(ConfigUtil.GetSetting(SettingKey), engine, StringComparison.OrdinalIgnoreCase))
        {
          ConfigUtil.SetSetting(SettingKey, AudioManager.Instance.GetActiveEngine());
        }

        RefreshState();
        SetStatus($"{engine} files removed.", GoodBrush);
        return;
      }

      // Either the active engine, which AudioManager refuses for, or a running copy has the native libraries mapped.
      SetStatus($"Could not remove the {engine} files: another copy of EQLogParser is using them. Close it and try again.", StopBrush);
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
      SetStatus($"Could not switch: {active} keeps speaking.", StopBrush);
    }

    // What the next press would do, on the button that does it.
    private static string DescribeAction(string engine, long bytes, bool available, bool active)
    {
      if (!available)
      {
        return $"Download {engine} ({FormatSize(bytes)})";
      }

      return active ? "In use" : $"Use {engine}";
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

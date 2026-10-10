using FontAwesome5;
using log4net;
using Microsoft.Win32;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

using EQLogParser;

using Application = System.Windows.Application;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  public partial class MainWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private DateTime _startLoadTime;
    private DamageOverlayWindow _damageOverlay;
    private FctOverlayWindow _fctOverlay;
    private readonly DispatcherTimer _saveTimer;

    /*
     * One summary-board build at a time (see SummaryBuildGate for the three laws). Every announcement of a selection — a
     * command, a menu closing, the settle timer, a derive pass whose content moved — used to spawn its own task here, and a
     * whole-capture selection materializes in seconds, so "twice" meant two full record sets allocated at once and two
     * builder runs serialized behind them, and on a live raid it meant one per pass without bound.
     *
     * The key holds only what this window can name: the rows, the capture's content stamp (owned by the pane) and the tanking
     * board's damage-type filter. The six DamageValidator settings change the answer without moving any of those — they are read
     * fresh out of AppSettings by every build, and their door is the pane's own rebuild, not this gate. An identity event (a pet
     * pair learned) was wired into a generation bump here until 2026-11 measurement showed it rebuilt all three boards to
     * byte-identical totals behind one select-all — no derived board reads those stores (docs/DesignNotes.md -> "Name every door").
     */
    private readonly SummaryBuildGate _summaryGate = new(static work => Task.Run(work));

    /*
     * The main window's periodic work, named for the heartbeat (PerfCounters, UiBeatMonitor). Both run on the UI thread and both are
     * quiet suspects for a freeze somewhere else: settings.ini gets written from here every half minute (a file write is one
     * antivirus scan away from a second), and an open chart takes a data-point update at whatever rate the parser produces them.
     * A stall line that names one of these is a different conversation from one that names nothing.
     */
    private static readonly int SaveId = PerfCounters.Register("ui.configSave");

    /*
     * The board build and the materialization inside it, both `uiThread: false`: they run on a pool thread, so naming them in a stall's
     * "in progress" would send a reader to the wrong window — but a UI freeze that happens while one of them is running is exactly the
     * correlation worth having (builders raise their grid updates back onto the dispatcher), and StatsBuildTrace's per-builder spans
     * (stats.damage / stats.tanking / stats.healing) line up against these two.
     */
    private static readonly int BoardBuildId = PerfCounters.Register("boards.build", false);
    private static readonly int BoardMaterializeId = PerfCounters.Register("boards.materialize", false);
    private static readonly int ChartUpdateId = PerfCounters.Register("chart.update");

    /* How many times a redraw request arrived while one was already waiting to run; see QueueChartUpdate. */
    private static readonly int ChartBacklogId = PerfCounters.Register("chart.backlog");
    private int _chartUpdatesWaiting;

    /*
     * Opening a log file, and the file dialog inside it. A measured session caught 1.6 s of blocked UI thread right after a load finished,
     * and switching logs is something a player does mid-raid; the dialog gets its own name because the question "does a modal Win32 dialog
     * starve the beat?" has to be answered from evidence before anyone decides whether such a stall counts as one.
     */
    /*
     * The constructor's own pieces. A field run on a real machine reported `slow UI pass app.mainwindow: 4443.5 ms` with
     * `cpu 2640 ms` of samples behind it, and the span covered the whole window - a huge XAML tree plus every docked
     * window declared in it plus this class's setup - which is true-but-useless. These four name what a reader can act on,
     * and whatever is left after subtracting them is the settings/visibility block between them: dozens of cached
     * dictionary reads and property sets, expected to be the smallest term, so if the remainder ever dominates that IS the
     * finding (docs/DesignNotes.md -> "The Windows field run").
     */
    private static readonly int MwXamlId = PerfCounters.Register("mw.xaml");
    private static readonly int MwPanesId = PerfCounters.Register("mw.panes");
    private static readonly int MwAutoOpenId = PerfCounters.Register("mw.autoopen");
    private static readonly int MwThemeId = PerfCounters.Register("mw.theme");

    private static readonly int OpenLogId = PerfCounters.Register("ui.openlogfile");
    private static readonly int PickFileId = PerfCounters.Register("ui.pickfile");

    private LogReader _eqLogReader;
    private DeriveEngine _engine;

    /// <summary>DeriveEngine.SessionId of the capture now open (0 = none), so a worker build can ask whether it still owns the boards.</summary>
    private int _engineSessionId;
    private readonly List<bool> _logWindows = [];
    private readonly List<string> _recentFiles = [];
    private readonly string _activeWindow;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly SolidColorBrush _hoverBrush = UiUtil.GetBrush("#505050");
    private readonly SolidColorBrush _redHoverBrush = UiUtil.GetBrush("#E81123");
    private bool _resetWindowState;
    private volatile bool _isStarting;
    private volatile bool _appLoadingComplete;

    public MainWindow()
    {
      // The XAML tree: this window and every docked window declared inside it. Named because it is the prime suspect for
      // the multi-second pass above, and a lambda is cheaper than guessing.
      PerfCounters.Run(MwXamlId, () => InitializeComponent());

      // set main / themes
      MainActions.SetMainWindow(this);

      /*
       * An operator's pet->owner claim asks Core for the pass that routes it (PetAssignment: the pair becomes an ownership
       * interval through RegistrySeed, and the fold happens where the boards are built, not in a click handler). The lambda
       * asks `DeriveEngine.Active` at the moment of the click rather than closing over an engine: no session wired means no
       * pass owed — the pair is simply saved for the next capture — and a dead session can never be reached through it.
       */
      PetAssignment.Reroute = () => DeriveEngine.Active?.RederiveAsync();

      // update titles
      versionText.Text = $"v{App.Version}";

      // AoE healing
      AppSettings.IsAoEHealingEnabled = ConfigUtil.IfSetOrElse("IncludeAoEHealing", AppSettings.IsAoEHealingEnabled);
      enableAoEHealingIcon.Visibility = AppSettings.IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;

      // Healing Swarm Pets
      AppSettings.IsHealingSwarmPetsEnabled = ConfigUtil.IfSetOrElse("IncludeHealingSwarmPets", AppSettings.IsHealingSwarmPetsEnabled);
      enableHealingSwarmPetsIcon.Visibility = AppSettings.IsHealingSwarmPetsEnabled ? Visibility.Visible : Visibility.Hidden;

      // Assassinate Damage
      AppSettings.IsAssassinateDamageEnabled = ConfigUtil.IfSetOrElse("IncludeAssassinateDamage", AppSettings.IsAssassinateDamageEnabled);
      enableAssassinateDamageIcon.Visibility = AppSettings.IsAssassinateDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Bane Damage
      AppSettings.IsBaneDamageEnabled = ConfigUtil.IfSetOrElse("IncludeBaneDamage", AppSettings.IsBaneDamageEnabled);
      enableBaneDamageIcon.Visibility = AppSettings.IsBaneDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Damage Shield Damage
      AppSettings.IsDamageShieldDamageEnabled = ConfigUtil.IfSetOrElse("IncludeDamageShieldDamage", AppSettings.IsDamageShieldDamageEnabled);
      enableDamageShieldDamageIcon.Visibility = AppSettings.IsDamageShieldDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Finishing Blow Damage
      AppSettings.IsFinishingBlowDamageEnabled = ConfigUtil.IfSetOrElse("IncludeFinishingBlowDamage", AppSettings.IsFinishingBlowDamageEnabled);
      enableFinishingBlowDamageIcon.Visibility = AppSettings.IsFinishingBlowDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Headshot Damage
      AppSettings.IsHeadshotDamageEnabled = ConfigUtil.IfSetOrElse("IncludeHeadshotDamage", AppSettings.IsHeadshotDamageEnabled);
      enableHeadshotDamageIcon.Visibility = AppSettings.IsHeadshotDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Slay Undead Damage
      AppSettings.IsSlayUndeadDamageEnabled = ConfigUtil.IfSetOrElse("IncludeSlayUndeadDamage", AppSettings.IsSlayUndeadDamageEnabled);
      enableSlayUndeadDamageIcon.Visibility = AppSettings.IsSlayUndeadDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Hide window when minimized
      enableHideOnMinimizeIcon.Visibility = ConfigUtil.IfSet("HideWindowOnMinimize") ? Visibility.Visible : Visibility.Hidden;

      // Hide splash screen
      enableHideSplashScreenIcon.Visibility = ConfigUtil.IfSet("HideSplashScreen") ? Visibility.Visible : Visibility.Hidden;

      // Minimize at startup
      enableStartMinimizedIcon.Visibility = ConfigUtil.IfSet("StartWithWindowMinimized") ? Visibility.Visible : Visibility.Hidden;

      // Allow Ctrl+C for SendToEQ
      AppSettings.IsMapSendToEqEnabled = ConfigUtil.IfSet("MapSendToEQAsCtrlC");
      enableMapSendToEQIcon.Visibility = AppSettings.IsMapSendToEqEnabled ? Visibility.Visible : Visibility.Hidden;

      // Chat Archive on/off
      enableChatArchiveIcon.Visibility = ConfigUtil.IfSetOrElse("ChatArchiveEnabled", true) ? Visibility.Visible : Visibility.Hidden;

      // Export Formatted CSV (numbers with commas, etc)
      exportFormattedCsvIcon.Visibility = ConfigUtil.IfSetOrElse("ExportFormattedCsv", true) ? Visibility.Visible : Visibility.Hidden;

      // Damage Overlay
      enableDamageOverlayIcon.Visibility = ConfigUtil.IfSet("IsDamageOverlayEnabled") ? Visibility.Visible : Visibility.Hidden;
      enableDamageOverlay.Header = ConfigUtil.IfSet("IsDamageOverlayEnabled") ? "Disable _Meter" : "Enable _Meter";

      // FCT Overlay: same convention as the meter. Restored here so the menu is honest even when the overlay stays shut.
      SetFctOverlayMenu(ConfigUtil.IfSet(FctOverlaySettings.EnabledKey), false);

      // Auto Monitor
      enableAutoMonitorIcon.Visibility = ConfigUtil.IfSet("AutoMonitor") ? Visibility.Visible : Visibility.Hidden;

      // Check for Updates
      checkUpdatesIcon.Visibility = ConfigUtil.IfSet("CheckUpdatesAtStartup") ? Visibility.Visible : Visibility.Hidden;

      // Hardware Acceleration
      hardwareAccelIcon.Visibility = ConfigUtil.IfSet("HardwareAcceleration") ? Visibility.Visible : Visibility.Hidden;

      // Enable EMU parsing
      AppSettings.IsEmuParsingEnabled = ConfigUtil.IfSet("EnableEmuParsing");
      emuParsingIcon.Visibility = AppSettings.IsEmuParsingEnabled ? Visibility.Visible : Visibility.Hidden;

      /*
       * Selecting rows in the fight list rebuilds the DAMAGE summary from the engine's own facts. One board
       * on purpose (see FightTable.DerivedSelectionChanged).
       */
      if (npcWindow?.Content is FightTable fightTable)
      {
        fightTable.DerivedSelectionChanged += DerivedSelectionChanged;
        _fightPane = fightTable;
      }

      // upgrade
      if (ConfigUtil.IfSet("TriggersWatchForGINA"))
      {
        ConfigUtil.SetSetting("TriggersWatchForQuickShare", true);
      }

      // upgrade
      if (ConfigUtil.IfSet("OverlayShowCritRate"))
      {
        ConfigUtil.SetSetting("OverlayEnableCritRate", "3");
      }

      // Load recent files
      if (ConfigUtil.GetSetting("RecentFiles") is { } recentFiles && !string.IsNullOrEmpty(recentFiles))
      {
        var files = recentFiles.Split(',');
        if (files.Length > 0)
        {
          _recentFiles.AddRange(files);
          UpdateRecentFiles();
        }
      }

      // create menu items for reading log files
      MainActions.CreateOpenLogMenuItems(fileOpenMenu, MenuItemSelectLogFileClick);

      // delete chat menu
      MainActions.UpdateDeleteChatMenu(deleteChat);

      // create font families menu items
      ThemeConfig.CreateFontFamiliesMenuItems(appFontFamilies, MenuItemFontFamilyClicked);

      // create font sizes menu items
      ThemeConfig.CreateFontSizesMenuItems(appFontSizes, MenuItemFontSizeClicked);

      // add tabs to the right
      ((DocumentContainer)dockSite.DocContainer).AddTabDocumentAtLast = true;

      // init theme before loading docs
      MainActions.UpdateStatus("Loading Themes");
      ThemeConfig.InitThemes();

      // save active window before adding
      _activeWindow = ConfigUtil.GetSetting("ActiveWindow");
      MainActions.AddDocumentWindows(dockSite);

      /*
       * One thing is left to populate at startup: the class list. It used to sit inside `InitVerifiedPlayers`, which also wired
       * the three retired panes' live-event pumps (a registry sighting posted onto the UI thread to insert one name into a sorted
       * ObservableCollection, per sighting). Those windows are gone — the identity pane reads the same facts out of the derive —
       * but `MainActions.ClassList` outlives them: it is what every class dropdown offers, here and in Player/NPC Identity.
       */
      PerfCounters.Run(MwPanesId, MainActions.InitClassList);

      // add notify icon
      // this attaches to state change events so do toward the end
      _notifyIcon = WinFormsUtil.CreateTrayIcon(this);

      // general events
      SystemEvents.PowerModeChanged += SystemEventsPowerModeChanged;

      _saveTimer = UiUtil.CreateTimer(SaveTimerTick, 30000, true, DispatcherPriority.Background);

      // check need monitor
      var previousFile = ConfigUtil.GetSetting("LastOpenedFile");
      if (enableAutoMonitorIcon.Visibility == Visibility.Visible && File.Exists(previousFile))
      {
        // OpenLogFile with update status.
        //
        // This runs INSIDE the window constructor, and it bootstraps a whole session (clear, engine start, identity
        // stores, reader) there - so it is measured separately rather than buried in app.mainwindow. `lastMins: 0` means
        // follow from end of file: an old last-opened file yields zero lines here, which is correct for a monitor but still
        // announces "Finished Loading Log File" to the log.
        PerfCounters.Run(MwAutoOpenId, () => OpenLogFile(previousFile, 0, "startup auto-monitor"));
      }

      // workaround to set initial theme properly
      MainActions.UpdateStatus("Setting " + ThemeConfig.CurrentTheme);
      PerfCounters.Run(MwThemeId, () => ThemeConfig.SetTheme());
    }

    private async void MainWindowOnLoaded(object sender, RoutedEventArgs args)
    {
      try
      {
        if (File.Exists(Path.Combine(ConfigUtil.ConfigDir, "dockSite.xml")))
        {
          try
          {
            using var reader = XmlReader.Create(Path.Combine(ConfigUtil.ConfigDir, "dockSite.xml"));
            dockSite.LoadDockState(reader);
          }
          catch (Exception ex)
          {
            Log.Debug("Error reading dockSite.xml", ex);
            dockSite.ResetState();
          }
        }

        /*
         * The fight list IS a core window, so it docks at startup whatever the saved state says: during the experiment
         * an operator could have hidden "the old one" and shown the derived pane instead, and arriving at a restart
         * with no fight list at all reads as the feature breaking - the exact failure the dock-at-startup law was
         * written for. The vestigial experimental pane (an empty ContentControl that exists only so old dockSite.xml
         * entries still resolve - LoadDockState throws on a missing window and the catch resets EVERYTHING) is hidden
         * again even if the saved state restored it.
         */
        DockingManager.SetState(npcWindow, DockState.Dock);
        DockingManager.SetState(mirrorFightWindow, DockState.Hidden);

        /*
         * Same law for Pet Owners, which is a name-only stub from 2026-10-09 (the markup explains why the shell stays): a saved
         * layout that had it auto-hidden on the right would otherwise slide an empty tab out beside Player/NPC Identity forever,
         * and no menu item exists to close it. Hiding at load is what makes the absorb-the-stale-name trick safe.
         */
        DockingManager.SetState(petMappingWindow, DockState.Hidden);
        MigrateIdentityPaneIntoRightStrip();

        DamageStatsBuilder.Instance.EventsUpdateDataPoint += data => QueueChartUpdate(damageChartIcon, data);
        HealingStatsBuilder.Instance.EventsUpdateDataPoint += data => QueueChartUpdate(healingChartIcon, data);
        TankingStatsBuilder.Instance.EventsUpdateDataPoint += data => QueueChartUpdate(tankingChartIcon, data);
        MainActions.EventsDamageSelectionChanged += DamageSummarySelectionChanged;
        MainActions.EventsHealingSelectionChanged += HealingSummarySelectionChanged;
        MainActions.EventsTankingSelectionChanged += TankingSummarySelectionChanged;

        // give some time for dock state to load
        await Task.Delay(250);

        // activate the saved window
        if (!string.IsNullOrEmpty(_activeWindow))
        {
          dockSite.ActivateWindow(_activeWindow);
        }

        // listen for tab changes
        dockSite.ActiveWindowChanged += (_, _) => SyncFusionUtil.DockSiteSaveActiveWindow(dockSite);
        dockSite.DockStateChanged += (_, _) => SyncFusionUtil.DockSiteSaveActiveWindow(dockSite);

        // the FCT overlay comes back showing if it was showing when the app closed (after the dock is up, so it
        // never opens on top of a half-built main window)
        if (ConfigUtil.IfSet(FctOverlaySettings.EnabledKey))
        {
          SetFctOverlayVisible(true);
        }
      }
      catch (Exception e)
      {
        Log.Error(e);
      }
      finally
      {
        _appLoadingComplete = true;
      }
    }

    internal void SetErrorText(string text) => errorText.Text = text;
    internal void ConnectLocationChanged() => LocationChanged += LocationChangedEvent;
    internal void DisconnectLocationChanged() => LocationChanged -= LocationChangedEvent;

    internal void SaveWindowSize()
    {
      if (_appLoadingComplete == true && WindowState == WindowState.Normal)
      {
        ConfigUtil.SetSetting("WindowLeft", Left);
        ConfigUtil.SetSetting("WindowTop", Top);
        ConfigUtil.SetSetting("WindowHeight", Height);
        ConfigUtil.SetSetting("WindowWidth", Width);
      }
    }

    internal void UpdateWindowBorder()
    {
      //maxRestoreText.Text = WindowState == WindowState.Maximized ? "🗗" : "🗖"; // restore/maximize
      if (WindowState == WindowState.Maximized)
      {
        maxRestorePath.Data = Geometry.Parse("M3,4 L9,4 L9,10 L3,10 Z M5,2 L11,2 L11,8 L9,8 L9,4 L5,4 Z");
      }
      else
      {
        maxRestorePath.Data = Geometry.Parse("M3,3 L11,3 L11,11 L3,11 Z");
      }
    }

    /*
     * The app's fight-range provider: the spell/taunt/death/export paths read fights from here - legacy-shaped
     * rows materialized off the capture's facts (see DeriveEngine.MaterializeFights). No live capture answers
     * EMPTY: the export of a log that is not running has no fights, and there is no second engine to have an
     * opinion - a fallback here would hide a dead session behind plausible numbers.
     */
    internal List<Fight> GetFights(bool selected = false)
      => npcWindow?.Content is FightTable fightTable && fightTable.SessionActive
        ? fightTable.GetFights(selected)
        : [];

    // The scoped variant for consumers that only need fights overlapping a span (the death viewer's 20-second
    // window around a kill): the engine materializes only the rows whose activity windows touch it. Same empty
    // answer as GetFights when no capture is running.
    internal List<Fight> GetFightsOverlapping(double fromT, double toT)
      => npcWindow?.Content is FightTable fightTable && fightTable.SessionActive
        ? fightTable.GetFightsOverlapping(fromT, toT)
        : [];

    internal void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.DamageParse, combined, selected, true);
      });
    }

    internal void AddAndCopyTankParse(CombinedStats combined, List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.TankParse, combined, selected, true);
      });
    }

    internal void CopyToEqClick(string type)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (playerParseTextWindow.Content is ParsePreview preview)
        {
          preview.CopyToEqClick(type);
        }
      });
    }

    internal void ShowTriggersEnabled(bool active)
    {
      Dispatcher.InvokeAsync(() =>
      {
        statusTriggersText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
      }, DispatcherPriority.Render);
    }

    internal void CloseDamageOverlay(bool reopen)
    {
      Dispatcher.InvokeAsync(() =>
      {
        _damageOverlay?.Close();
        _damageOverlay = null;
        AppSettings.IsDamageOverlayOpen = false;

        if (reopen)
        {
          OpenDamageOverlayIfEnabled(false, true);
        }
      });
    }

    internal void OpenDamageOverlayIfEnabled(bool reset, bool configure)
    {
      if (configure)
      {
        _damageOverlay = new DamageOverlayWindow(true)
        {
          ShowActivated = false
        };

        _damageOverlay.Show();
      }
      // delay opening overlay so group IDs get populated
      else if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        /*
         * "Was anything happening?" asked of LiveFights over the capture's rows, not an overlay-fight set: the window
         * opens when the capture's last moments hold a fight instead of when the log ever contained one — and if
         * nothing was live, NewFightObserved opens it on the next pull instead. No session running answers false: a
         * capture that is not running has no facts, and this path does not fall back to have something to say.
         */
        if (DerivedMeter.HasLiveFight())
        {
          _damageOverlay?.Close();
          _damageOverlay = new DamageOverlayWindow(false, reset);
          _damageOverlay.Opacity = 0;
          _damageOverlay.ShowActivated = false;
          _damageOverlay.Show();
          _damageOverlay.UpdateLayout();
          _damageOverlay.Opacity = 1.0;
          AppSettings.IsDamageOverlayOpen = true;
        }
      }
    }

    private void CloseButtonUp(object sender, MouseButtonEventArgs e) => Close();
    private void MinimizeButtonUp(object sender, MouseButtonEventArgs e) => WindowState = WindowState.Minimized;
    private void ConfigureOverlayClick(object sender, RoutedEventArgs e) => CloseDamageOverlay(true);
    private void MainWindowSizeChanged(object sender, EventArgs e) => SaveWindowSize();
    private void RestoreTableColumnsClick(object sender, RoutedEventArgs e) => DataGridUtil.RestoreAllTableColumns();
    private void AboutClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault($"{App.ParserHome}");
    private void RestoreClick(object sender, RoutedEventArgs e) => MainActions.Restore();
    private void OpenCreateWavClick(object sender, RoutedEventArgs e) => new WavCreatorWindow().ShowDialog();
    private void OpenSoundsFolderClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault("\"" + @"data\sounds" + "\"");

    /* Live-record FCT overlay (real logs, monitor lines only) - see FctOverlayWindow. */
    private void ToggleFctOverlayClick(object sender, RoutedEventArgs e) => SetFctOverlayVisible(fctOverlayIcon.Visibility != Visibility.Visible);

    /*
     * Show/hide the FCT overlay. Hiding instead of closing keeps the window (and its position) around, while
     * the window stops its canvas and gates FctManager whenever it is invisible - an overlay nobody is looking
     * at does no raster work and no parser-side event traffic.
     */
    private void SetFctOverlayVisible(bool show)
    {
      if (!show && _fctOverlay is null)
      {
        SetFctOverlayMenu(false, false);
        return;
      }

      if (_fctOverlay is null)
      {
        _fctOverlay = new FctOverlayWindow();

        /* Closing drops the reference so the next toggle builds a fresh one. The menu writes the enabled key and does not
           run when the window closes itself (its own close button, alt+F4, an app shutdown), so this path has to clear it too:
           left set, an overlay the player deliberately closed would reopen on the next launch. */
        _fctOverlay.EventsClosed += () =>
        {
          _fctOverlay = null;
          ConfigUtil.SetSetting(FctOverlaySettings.EnabledKey, false);
          SetFctOverlayMenu(false, false);
        };

        // Save and Cancel change the state while the menu is looking the other way; the menu's Setup item is the same switch seen
        // from the other side, so it follows the window rather than remembering what it was last asked to do
        _fctOverlay.EventsLockChanged += locked => SetFctOverlayMenu(_fctOverlay?.IsVisible == true, !locked);
      }

      /* The first time this feature is switched on it opens on its own controls. Every one of these settings has a defensible default, but they are
         this build's opinions, and an overlay that appears with numbers already moving keeps a player from learning that size, speed and motion exist
         to be set — the demo loop shows all three inside a few seconds. Once anybody has pressed Save (FctOverlaySettings.IsConfigured) it opens as
         usual; Cancel does not count, so the offer comes back next time rather than being forced on somebody who looked and decided. */
      /* The overlay goes on screen BEFORE anything may unlock it — WPF refuses to make a window that has never been
         shown anybody's Owner, and unlocking builds the settings window immediately. A setting-less first run drove
         straight through here (configure offer → SetLocked(false) → EnsureSettings → Owner = this) with the overlay
         still hidden, and the dispatcher ate an exception; showing a frame locked is nothing next to that. */

      if (show)
      {
        _fctOverlay.Show();
      }
      else
      {
        _fctOverlay.Hide();
      }

      if (show && !FctOverlaySettings.IsConfigured())
      {
        _fctOverlay.SetLocked(false);
      }

      SetFctOverlayMenu(show, !_fctOverlay.Locked);
      ConfigUtil.SetSetting(FctOverlaySettings.EnabledKey, show);

      // configuring wants the window foreground so its panel can be typed in; a locked one must never take focus from the game
      if (show && !_fctOverlay.Locked)
      {
        _fctOverlay.Activate();
      }
    }

    /* Asked to set up, or to stop: the overlay's lock state is the answer, so the menu never has to remember one of its own. */
    private void ToggleConfigureFctOverlayClick(object sender, RoutedEventArgs e) => SetFctOverlayConfiguring(_fctOverlay is null || _fctOverlay.Locked);

    /*
     * View -> FCT Overlay -> Reset Position: the same three steps as the Damage Meter's, in the order this overlay needs. Closing
     * first because a closing window writes where it was, which would restore what is about to be cleared; then forgetting the
     * geometry; then rebuilding only if it was on screen, so resetting an overlay you are not using does not switch one on.
     * Rebuilding rather than moving is the point — the shipped size and centring live in RestoreSettings, and a reset that only
     * moved the current window would keep a size nobody can drag back from off-screen.
     */
    private void ResetFctOverlayClick(object sender, RoutedEventArgs e)
    {
      var wasShowing = _fctOverlay?.IsVisible == true;

      _fctOverlay?.Close();
      FctOverlayWindow.ForgetStoredGeometry();

      if (wasShowing)
      {
        SetFctOverlayVisible(true);
      }
    }

    /*
     * Setup is the only way in. Locked means click-through (WS_EX_TRANSPARENT) with no header and no panel — numbers over EverQuest
     * and nothing else — and it is where the overlay always opens and what Cancel and Save return you to: a state that outlived its
     * session would turn an overlay into a click-eating rectangle the next time the game starts, so there is no setting for it.
     * Asking from the menu is deliberate, because while locked the window takes no input at all, and this overlay lives mid-screen,
     * where permanent settings furniture would fight the game.
     */
    private void SetFctOverlayConfiguring(bool configuring)
    {
      if (_fctOverlay is null)
      {
        if (!configuring)
        {
          SetFctOverlayMenu(false, false);
          return;
        }

        // configuring a hidden overlay shows it first: an item that silently did nothing is a worse lie than one that pops up empty
        SetFctOverlayVisible(true);
      }

      /* EventsLockChanged repaints the menu from the window's real state, including this call. */
      _fctOverlay?.SetLocked(!configuring);
    }

    /*
     * Menu wording and check marks in one place. These menus use an icon plus an Enable/Disable header rather than a checkable item
     * — everything else here does, including the Damage Meter this submenu sits under — and Setup reads Finish Setup while the
     * overlay has the mouse, because that is the item you come back to when you are done.
     */
    private void SetFctOverlayMenu(bool enabled, bool configuring)
    {
      fctOverlayIcon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;

      // the short form once it is running: the menu is already called Floating Combat Text, and "Disable Floating Combat Text" is a sentence
      fctOverlay.Header = enabled ? "Disable FCT" : "Enable _Floating Combat Text";
      configureFctOverlay.Header = configuring ? "_Finish Setup" : "_Setup";
    }

    private void ReportProblemClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault("http://github.com/kauffman12/EQLogParser/issues");
    private void ViewReleaseNotesClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault(App.ReleaseNotesUrl);
    private async void MigrateNagDbClick(object sender, RoutedEventArgs e)
    {
      var dirPath = TriggerUtil.SelectNagDatabaseDirectory();
      if (dirPath is null)
        return;

      var progressWindow = new MessageWindow("Migrate NAG database. This may take a moment for large databases.", "NAG Migrate", MessageWindow.IconType.Info, noButtons: true);
      progressWindow.Show();

      try
      {
        await TriggerUtil.ImportNagTriggers(dirPath);
        if (SyncFusionUtil.GetOpenWindows(dockSite).TryGetValue("triggersWindow", out var value) &&
            value.Content is TriggersView triggersView)
        {
          await triggersView.theTreeView.RefreshTriggers();
          await triggersView.theTreeView.RefreshOverlays();
        }
      }
      finally
      {
        progressWindow.Close();
      }
    }

    private void OpenLogManager(object sender, RoutedEventArgs e) => new LogManagementWindow().ShowDialog();
    private void DockSiteCloseButtonClick(object sender, CloseButtonEventArgs e) => SyncFusionUtil.CloseTab(dockSite, e.TargetItem as ContentControl, _logWindows);
    private void DockSiteWindowClosing(object sender, WindowClosingEventArgs e) => SyncFusionUtil.CloseTab(dockSite, e.TargetItem as ContentControl, _logWindows);
    private void WindowClose(object sender, EventArgs e) => Close();
    private void ToggleMaterialDarkClick(object sender, RoutedEventArgs e) => ThemeConfig.SetTheme("MaterialDark");
    private void ToggleMaterialLightClick(object sender, RoutedEventArgs e) => ThemeConfig.SetTheme("MaterialLight");
    private void ToggleStartMinimizedClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("StartWithWindowMinimized", enableStartMinimizedIcon);
    private void ToggleHideSplashScreenClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("HideSplashScreen", enableHideSplashScreenIcon);
    private void ToggleAutoMonitorClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("AutoMonitor", enableAutoMonitorIcon);
    private void ToggleCheckUpdatesClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("CheckUpdatesAtStartup", checkUpdatesIcon);
    private void ToggleHardwareAccelClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("HardwareAcceleration", hardwareAccelIcon);
    private void ToggleExportFormattedCsvClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("ExportFormattedCsv", exportFormattedCsvIcon);
    private void ToggleHideOnMinimizeClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("HideWindowOnMinimize", enableHideOnMinimizeIcon);
    private void LocationChangedEvent(object sender, EventArgs e) => SaveWindowSize();
    private void CloseLogClick(object sender, EventArgs e) => CloseLogFile(true);

    private void SaveTimerTick(object sender, EventArgs e)
    {
      // save once loaded but also if backup isnt trying to shutdown
      if (!_isStarting && Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
      {
        /* Timed because it writes settings.ini to disk on the UI thread, once a half minute — which is close enough to "every so often"
           to be worth ruling in or out by measurement rather than by argument. */
        PerfCounters.Run(SaveId, ConfigUtil.Save);
      }
    }

    private async void SystemEventsPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
      switch (e.Mode)
      {
        case PowerModes.Suspend:
          Log.Warn("Suspending");
          _saveTimer?.Stop();
          ConfigUtil.Save();
          await TriggerManager.Instance.DisposeAsync();
          UnsubscribeOverlayFights();
          CloseDamageOverlay(false);
          break;
        case PowerModes.Resume:
          Log.Warn("Resume");
          _saveTimer?.Start();
          await TriggerManager.Instance.StartAsync();
          OpenDamageOverlayIfEnabled(true, false);
          SubscribeOverlayFights();
          break;
      }
    }

    /*
     * The auto-open rule. It raises on a worker thread (the derive thread), so the window is still created through the
     * dispatcher and re-checked there — the meter opens once, at most, however many passes announce the same pull.
     *
     * This is also what gives the meter's X its meaning: closing the window does not disable anything, so damage brings the
     * board back (and back to the same numbers — a closed window does not reset the meter's start). "Disable Meter" is how you
     * keep it shut. The old engine announced on every damage line of a fight, which is why its X behaved that way; the derive
     * raises at cadence instead, and AutoOpenMeter's already-open check swallows all but the first.
     */
    private void SubscribeOverlayFights()
    {
      DeriveEngine.LiveDamageObserved += OnLiveDamage;
    }

    // Removes unconditionally: `-=` on a handler that was never attached is a no-op. The pair used to be
    // dial-keyed, and the dial got read AFTER a toggle had flipped it - so an off flip left the OTHER engine's
    // handler pinned to a static event for the life of the process. One publisher remains; the lesson that
    // removal should never need a condition stands with the pair.
    private void UnsubscribeOverlayFights()
    {
      DeriveEngine.LiveDamageObserved -= OnLiveDamage;
    }

    // Damage arrived, and which row carries it is not the meter's business: it opens and reads whatever the snapshot says.
    private void OnLiveDamage() => AutoOpenMeter();

    private void AutoOpenMeter()
    {
      // another lazy optimization to avoid extra dispatches
      if (_damageOverlay == null && ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Dispatcher.InvokeAsync(() =>
        {
          if (_damageOverlay == null)
          {
            OpenDamageOverlayIfEnabled(false, false);
          }
        });
      }
    }

    private void ToggleChatArchiveClick(object sender, RoutedEventArgs e)
    {
      var enabled = MainActions.ToggleSetting("ChatArchiveEnabled", enableChatArchiveIcon);
      if (enabled)
      {
        ChatDB.Instance.Init();
      }
      else
      {
        ChatDB.Instance.Stop();
      }
    }

    private void ToggleEmuParsingClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsEmuParsingEnabled = MainActions.ToggleSetting("EnableEmuParsing", emuParsingIcon);
    }

    private void ToggleMapSendToEqClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsMapSendToEqEnabled = MainActions.ToggleSetting("MapSendToEQAsCtrlC", enableMapSendToEQIcon);
    }

    private async void CreateBackupClick(object sender, RoutedEventArgs e)
    {
      await MainActions.CreateBackupAsync();
    }

    /*
     * A data point event arrives on a builder's thread and its redraw runs on the UI thread, so rebuilds that land close together queue full
     * redraws behind each other - three builders can ask for three. Counting how often a request found one already waiting is the only way to
     * see that shape afterwards: it is what one merged redraw would take away, and nothing else in the log distinguishes "each redraw is too
     * slow" from "too many redraws were asked for".
     */
    /*
     * The icon is handed over as an object and its Tag is read inside the dispatched callback, not here. Reading a property of that control
     * from this thread throws - `InvalidOperationException: the calling thread cannot access this object` - because the event arrives on a
     * stats builder's thread, and this shape is not incidental: the lambdas that used to sit here wrapped the whole `icon.Tag as string`
     * expression inside InvokeAsync precisely so the read happened on the UI thread. Measuring how many redraws queue up cannot be allowed to
     * move a property read across threads to do it.
     */
    private void QueueChartUpdate(FrameworkElement icon, DataPointEvent e)
    {
      if (Interlocked.Increment(ref _chartUpdatesWaiting) > 1)
      {
        PerfCounters.Note(ChartBacklogId);
      }

      /* The count is released by the redraw itself; a callback the dispatcher never ran means the application was closing anyway. */
      Dispatcher.InvokeAsync(() =>
      {
        Interlocked.Decrement(ref _chartUpdatesWaiting);
        HandleChartUpdate(icon.Tag as string, e);
      });
    }

    private void HandleChartUpdate(string key, DataPointEvent e)
    {
      /* One span per data point rather than one counter: an open chart is the only other thing in this application that redraws
         continuously during a fight, and how much a single update costs decides whether it can starve an overlay. */
      PerfCounters.Run(ChartUpdateId, () =>
      {
        if (SyncFusionUtil.GetOpenWindows(dockSite).TryGetValue(key, out var value) &&
          value.Content is LineChart chart)
        {
          chart.HandleUpdateEvent(e);
        }
      });
    }

    /*
     * The board path, from the ONLY fight list: the builders get ordinary Fight objects whose blocks were rebuilt
     * from captured facts, so no summary has to know anything new. Damage and tanking are both fed here, off the
     * same materialized rows — a derived row carries what the raid did to it in DamageBlocks and what it did to
     * the raid in TankingBlocks, which is the split TankingStatsBuilder already reads.
     *
     * The healing board arrives by a different door, because it reads records rather than fights: the session
     * materializes the heal facts inside this selection's window and hands them in as options.Heals (see
     * HealSummarySource). Same click, same AllRanges window, so all three boards answer to one clock — and when
     * no fight is selected each of them is told to clear, healing included.
     *
     * An empty selection still reaches the builders: zero npcs is how they are told to clear their boards, which
     * is what the legacy list does with an empty selection too.
     */
    /// <summary>
    /// The fight list pane as a plain reference, so the board build can ask it to watch its rows. Getting it means reading
    /// <c>npcWindow.Content</c>, and that is a DependencyObject read: the same law the tanking filter below obeys — nothing
    /// on the summary gate's worker thread walks the dock site. Assign ON THE UI THREAD only. (The first version read
    /// <c>.Content</c> inside the build, threw InvalidOperationException there, and NO board shipped — "selection worked but
    /// nothing ever built".)
    /// </summary>
    private FightTable _fightPane;

    private void DerivedSelectionChanged(BoardRequest request)
    {
      var session = _engine;
      if (session is null) return;

      // Re-read the pane while still on the UI thread: another open could have replaced it.
      if (npcWindow?.Content is FightTable pane) _fightPane = pane;

      /*
       * Read the tanking board's NPC filter off the open window BEFORE leaving the UI thread - the dock site is
       * not something a worker task may walk.
       */
      var tankingDamageType = 0;
      if (SyncFusionUtil.GetOpenWindows(dockSite).TryGetValue((tankingSummaryIcon.Tag as string)!, out var tankControl)
          && tankControl != null)
      {
        tankingDamageType = ((TankingSummary)tankControl.Content).DamageType;
      }

      /*
       * Materializing allocates one record per selected fact, so it belongs on the worker with the build; the UI thread's
       * part ends at "these fights" plus the key that says what this answer is computed FROM — the rows, the capture's
       * content stamp (facts + identity verdicts, owned by the pane because a pass is what moves it) and the tanking board's
       * damage-type filter. The gate drops a request whose key has already been built or is
       * building, queues at most one newer one behind a run, and never lets two materializations overlap.
       */
      var key = SummaryKeyFor(session.SessionId, request.Fights, request.ContentStamp, tankingDamageType);
      var askedAt = Stopwatch.GetTimestamp();
      var outcome = _summaryGate.Request(key, () => BuildBoards(session, request, tankingDamageType, askedAt));

      /*
       * One line per ask, whatever the gate decided, because "the damage grid filled three times" is a question about DOORS and this is
       * the only place that sees both the door (which the pane names) and what the single-flight rule made of it. Info rather than Debug:
       * asks are one-per-gesture, not per frame, and a player reporting multiple builds should not have to know how to turn Debug on.
       * Asks from the panes' own doors do NOT come through here — they call the builders directly, and StatsBuildTrace is what names them.
       */
      Log.Info($"board ask [{request.Reason}]{(request.Detail is null ? string.Empty : $" {request.Detail}")}"
               + $": {request.Fights.Count} fight(s), stamp {request.ContentStamp}, tank type {tankingDamageType}"
               + $" -> {outcome}");
    }

    /*
     * Every input the three builders read, in one long. Order matters as much as content (a selection of rows 1,2 is the
     * same question as 2,1 but the pane walks them in list order and so does this), and 0 is reserved for "never built" by
     * SummaryBuildGate, so a computed 0 becomes 1.
     *
     * A hash means two different questions could collide and one would be skipped. At 64 bits over a session's few hundred
     * announcements that is not a risk worth an exact key (the exact alternative is a string over up to thousands of ids,
     * built per announcement, on the UI thread) — but it IS why the gate logs its two non-Started outcomes: a board that
     * refuses to update while the pane says it announced is diagnosable from eqlogparser.log rather than argued about.
     */
    internal static long SummaryKeyFor(int sessionId, IReadOnlyList<DerivedFight> selected, long contentStamp, int tankingDamageType)
    {
      var hash = unchecked((contentStamp * 397) ^ tankingDamageType);
      foreach (var fight in selected) hash = unchecked(hash * 17 + fight.Id);
      // The capture this question belongs to, mixed in LAST so nothing else about a session can collide with it. Two logs
      // can hand out the same ids, stamp and filter — and an empty selection over two of them is the everyday case: closing
      // one capture and opening another asks the SAME key for a board the new capture has never painted, which the gate would
      // otherwise answer "already built" to. See BoardKeyTest.
      hash = unchecked(hash * 31 + sessionId);
      return hash == 0 ? 1 : hash;
    }

    /*
     * The worker's half of one announcement: materialize, then feed all three boards off the SAME rows. `askedAt` is the moment the
     * pane asked, so the line shows how long this build waited behind another one — "waited 3400 ms" IS the single-flight rule working,
     * and a triple would read as three lines that each waited on something.
     */
    private void BuildBoards(DeriveEngine session, BoardRequest request, int tankingDamageType, long askedAt)
    {
      var buildSpan = PerfCounters.Begin(BoardBuildId);
      var waitedMs = StatsBuildTrace.ElapsedSince(askedAt);
      SummaryInput input = null;

      /*
       * Whose capture is this build for, still? A session that has been closed answers with an EMPTY summary (Dispose drops
       * the snapshot, and BuildSummaryInput reads that as "clear every board"), so a build that outlived its own log would
       * blank the boards of whatever opened next — including Clear All's own fresh session. Newest question wins: the gate
       * never un-STARTS work, so it stops before painting instead. `_engineSessionId` is written on the UI thread only.
       */
      if (session.SessionId != Volatile.Read(ref _engineSessionId))
      {
        Log.Debug($"board build abandoned [{request.Reason}]: capture {session.SessionId} closed while this was queued");
        return;
      }

      /*
       * The door label every builder prints (StatsBuildTrace). It carries the reason and the content stamp so two lines can be compared:
       * identical labels twice in a row means one door asked twice; different reasons mean two gestures, and different stamps mean the
       * capture genuinely moved under the selection.
       */
      var door = $"derived [{request.Reason}]"
                 + (request.Detail is null ? string.Empty : $" {request.Detail}")
                 + $" stamp {request.ContentStamp} fights {request.Fights.Count}";

      try
      {
        var materializeSpan = PerfCounters.Begin(BoardMaterializeId);
        input = session.BuildSummaryInput(request.Fights);
        var materializeMs = PerfCounters.End(materializeSpan);

        GenerateStatsOptions damageStatsOptions = new() { Source = door };
        damageStatsOptions.Npcs.AddRange(input.Fights);
        damageStatsOptions.AllRanges = input.AllRanges;
        damageStatsOptions.MinSeconds = 0;

        if (session.SessionId != Volatile.Read(ref _engineSessionId))
        {
          Log.Debug($"board build abandoned [{request.Reason}]: capture closed while this materialized");
          return;
        }

        var records = input.Fights.Sum(static f => f.DamageBlocks.Sum(static b => b.Actions.Count));
        var tankRecords = input.Fights.Sum(static f => f.TankingBlocks.Sum(static b => b.Actions.Count));
        // Debug, not Info: `board ask [reason] -> outcome` above already tells the story at the level a player's log
        // keeps, and this restates the same event with counters. It stays because those counters are what a Debug run
        // reads when a board's numbers look wrong (how many records reached the builders, how long materialize took).
        Log.Debug($"Derived damage summary [{request.Reason}]: {input.Fights.Count} fight(s), {records:N0} record(s), "
                 + $"{tankRecords:N0} taken"
                 + (input.WithoutDamage > 0 ? $", {input.WithoutDamage} selected fight(s) no facts at all" : string.Empty)
                 + $" | materialized in {materializeMs:F0} ms, waited {waitedMs:F0} ms behind any earlier build");

        DamageStatsBuilder.Instance.BuildTotalStats(damageStatsOptions);

        // Same rows, other direction: TankingStatsBuilder walks fight.TankingBlocks and takes each player's
        // activity window from TankSegments, both of which the materializer fills off the same facts.
        GenerateStatsOptions tankingStatsOptions = new() { Source = door };
        tankingStatsOptions.Npcs.AddRange(input.Fights);
        tankingStatsOptions.AllRanges = input.AllRanges;
        tankingStatsOptions.MinSeconds = 0;
        tankingStatsOptions.DamageType = tankingDamageType;

        TankingStatsBuilder.Instance.BuildTotalStats(tankingStatsOptions);

        // Healing: no Fight objects involved anywhere on this path. The window is the selection's, the records
        // are the capture's, and the builder's own filters (AoE healing, swarm pets) run over them unchanged —
        // so the healing tab stops being the one board that ignored which list was clicked.
        GenerateStatsOptions healingStatsOptions = new() { Source = door };
        healingStatsOptions.Npcs.AddRange(input.Fights);
        healingStatsOptions.AllRanges = input.AllRanges;
        healingStatsOptions.MinSeconds = 0;
        healingStatsOptions.Heals = input.Heals;

        HealingStatsBuilder.Instance.BuildTotalStats(healingStatsOptions);
      }
      catch (Exception ex)
      {
        // Logged here rather than left to the task: an announcement that threw must still return the gate to idle (its own
        // finally does that), and the boards keep whatever they had — which is why a partial board beats no board.
        Log.Error("Derived damage summary error", ex);
      }
      finally
      {
        PerfCounters.End(buildSpan);
      }

      // Watching happens AFTER the boards, under its own guard: it only decides whether a later derive tick is worth
      // announcing, so losing it must cost that and nothing else. It used to sit first inside the try above, where one
      // throw of its own — a dock-site read from this worker thread — silently took every board with it.
      WatchBoardNames(input);
    }

    /// <summary>
    /// Tell the fight list which names this build put on screen, so a later derive pass can ask the ONLY question that
    /// entitles it to rebuild them: did any of these names change what it IS? New damage never qualifies (the operator's
    /// rule, 2026-10-08 — "id rather it be like a snapshot of what was selected at the time except for the pet changes or
    /// player turning npc"), and IdentityWatch measured why the scope is names rather than the identity digest: replaying a
    /// capture as growing prefixes moved the digest on 6 of 6 passes while flipping ZERO verdicts, every move being a name
    /// seen for the first time — digest-gating would rebuild once per pass and look exactly like the flicker this replaces.
    /// </summary>
    /// <remarks>
    /// The walk covers records this build already materialized (one field read plus a hash-set add each; ~50 ms on a
    /// whole-capture select-all that costs seconds anyway, and builds are rare by design). The set is deliberately wider than
    /// the grid's own rows: a name involved in a selection flipping is worth a rebuild even if the grid folds it under
    /// `X +Pets`. The pane comes from <see cref="_fightPane"/>, captured on the UI thread — this runs on the summary gate's
    /// worker, where no window may be touched, and its own guard keeps a lost watch from costing a board.
    /// </remarks>
    private void WatchBoardNames(SummaryInput input)
    {
      if (input is null) return;
      try
      {
        var involved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fight in input.Fights)
        {
          if (!string.IsNullOrEmpty(fight.Name)) involved.Add(fight.Name);

          foreach (var block in fight.DamageBlocks)
            foreach (var action in block.Actions)
              if (action is DamageRecord damage && !string.IsNullOrEmpty(damage.Attacker)) involved.Add(damage.Attacker);

          foreach (var block in fight.TankingBlocks)
            foreach (var action in block.Actions)
              if (action is DamageRecord taken && !string.IsNullOrEmpty(taken.Attacker)) involved.Add(taken.Attacker);
        }

        // Healers too: the healing board lists names no damage board necessarily showed.
        foreach (var (_, heal) in input.Heals)
          if (!string.IsNullOrEmpty(heal.Healer)) involved.Add(heal.Healer);

        _fightPane?.WatchBoardNames([.. involved]);
      }
      catch (Exception ex)
      {
        Log.Debug($"board watch capture skipped: {ex.Message}");
      }
    }


    private void RestoreButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (WindowState == WindowState.Maximized)
      {
        WindowState = WindowState.Normal;
        maxRestorePath.Data = Geometry.Parse("M3,3 L11,3 L11,11 L3,11 Z");
      }
      else
      {
        WindowState = WindowState.Maximized;
        maxRestorePath.Data = Geometry.Parse("M3,4 L9,4 L9,10 L3,10 Z M5,2 L11,2 L11,8 L9,8 L9,4 L5,4 Z");
      }
    }

    private void ButtonBorderMouseEnterRed(object sender, MouseEventArgs e)
    {
      if (sender is Border border)
      {
        border.Background = _redHoverBrush;
      }
    }

    private void ButtonBorderMouseEnter(object sender, MouseEventArgs e)
    {
      if (sender is Border border)
      {
        border.Background = _hoverBrush;
      }
    }

    private void ButtonBorderMouseLeave(object sender, MouseEventArgs e)
    {
      if (sender is Border border)
      {
        border.Background = Brushes.Transparent;
      }
    }

    private void MenuItemClearOpenRecentClick(object sender, RoutedEventArgs e)
    {
      _recentFiles.Clear();
      ConfigUtil.SetSetting("RecentFiles", "");
      UpdateRecentFiles();
    }

    private void MenuItemExportHtmlClick(object sender, RoutedEventArgs e)
    {
      var opened = SyncFusionUtil.GetOpenWindows(dockSite);
      var tables = new Dictionary<string, SummaryTable>();

      if (opened.TryGetValue((damageSummaryIcon.Tag as string)!, out var control))
      {
        tables.Add(DockingManager.GetHeader(control) as string ?? string.Empty, (DamageSummary)control.Content);
      }

      if (opened.TryGetValue((healingSummaryIcon.Tag as string)!, out var control2))
      {
        tables.Add(DockingManager.GetHeader(control2) as string ?? string.Empty, (HealingSummary)control2.Content);
      }

      if (opened.TryGetValue((tankingSummaryIcon.Tag as string)!, out var control3))
      {
        tables.Add(DockingManager.GetHeader(control3) as string ?? string.Empty, (TankingSummary)control3.Content);
      }

      if (tables.Count > 0)
      {
        MainActions.ExportAsHtml(tables);
      }
      else
      {
        new MessageWindow("No Summary Views are Open. Nothing to Save.", Resource.FILEMENU_EXPORT_SUMMARY).ShowDialog();
      }
    }

    private void MenuItemExportFightsClick(object sender, RoutedEventArgs e)
    {
      var filtered = GetFights(true).OrderBy(npc => npc.Id).ToList();

      if (string.IsNullOrEmpty(AppSettings.CurrentLogFile))
      {
        new MessageWindow("No Log File Opened. Nothing to Save.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
      else if (filtered.Count > 0)
      {
        MainActions.ExportFights(AppSettings.CurrentLogFile, filtered);
      }
      else
      {
        new MessageWindow("No Fights Selected. Nothing to Save.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
    }

    private void MenuItemExportNpcNamesClick(object sender, RoutedEventArgs e)
    {
      var pickedFile = FileDialogUtil.SaveFile(this, null, "npc names export", "Text Files (*.txt)|*.txt");

      if (pickedFile != null)
      {
        try
        {
          File.WriteAllLines(pickedFile, GetFights(true).Select(f => f.Name).Distinct().OrderBy(n => n));
        }
        catch (Exception ex)
        {
          new MessageWindow("Save NPC Names", "Error saving NPC names: " + ex.Message).ShowDialog();
        }
      }
    }

    private void ResetWindowStateClick(object sender, RoutedEventArgs e)
    {
      try
      {
        dockSite.DeleteDockState(Path.Combine(ConfigUtil.ConfigDir, "dockSite.xml"));
      }
      catch (Exception)
      {
        // ignore
      }

      _resetWindowState = true;
      new MessageWindow("Window State will be reset after application restart.", Resource.RESET_WINDOW_STATE).ShowDialog();
    }

    private void ViewErrorLogClick(object sender, RoutedEventArgs e) => MainActions.ViewErrorLog();

    private void ToggleDamageOverlayClick(object sender, RoutedEventArgs e)
    {
      enableDamageOverlayIcon.Visibility = enableDamageOverlayIcon.Visibility == Visibility.Hidden ? Visibility.Visible : Visibility.Hidden;
      var enabled = enableDamageOverlayIcon.Visibility == Visibility.Visible;
      ConfigUtil.SetSetting("IsDamageOverlayEnabled", enabled);

      if (enabled)
      {
        OpenDamageOverlayIfEnabled(true, false);
      }
      else
      {
        // Close only. The legacy call that also cleared the engine's per-fight overlay set is gone with
        // the engine: a derived board holds nothing between ticks, and the seconds a reopened board adds
        // up from are deliberately NOT reset by closing the window (only the clear button moves them).
        CloseDamageOverlay(false);
      }

      enableDamageOverlay.Header = enabled ? "Disable _Meter" : "Enable _Meter";
    }

    private void ResetOverlayClick(object sender, RoutedEventArgs e)
    {
      CloseDamageOverlay(false);
      ConfigUtil.SetSetting("OverlayTop", "");
      ConfigUtil.SetSetting("OverlayLeft", "");
      OpenDamageOverlayIfEnabled(false, true);
    }

    private void ToggleAssassinateDamageClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsAssassinateDamageEnabled = MainActions.ToggleSetting("IncludeAssassinateDamage", enableAssassinateDamageIcon);
      MainActions.FireDamageSummaryOptionsChanged("IncludeAssassinateDamage");
    }

    private void ToggleBaneDamageClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsBaneDamageEnabled = MainActions.ToggleSetting("IncludeBaneDamage", enableBaneDamageIcon);
      MainActions.FireDamageSummaryOptionsChanged("IncludeBaneDamage");
    }

    private void ToggleDamageShieldDamageClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsDamageShieldDamageEnabled = MainActions.ToggleSetting("IncludeDamageShieldDamage", enableDamageShieldDamageIcon);
      MainActions.FireDamageSummaryOptionsChanged("IncludeDamageShieldDamage");
    }

    private void ToggleFinishingBlowDamageClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsFinishingBlowDamageEnabled = MainActions.ToggleSetting("IncludeFinishingBlowDamage", enableFinishingBlowDamageIcon);
      MainActions.FireDamageSummaryOptionsChanged("IncludeFinishingBlowDamage");
    }

    private void ToggleHeadshotDamageClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsHeadshotDamageEnabled = MainActions.ToggleSetting("IncludeHeadshotDamage", enableHeadshotDamageIcon);
      MainActions.FireDamageSummaryOptionsChanged("IncludeHeadshotDamage");
    }

    private void ToggleSlayUndeadDamageClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsSlayUndeadDamageEnabled = MainActions.ToggleSetting("IncludeSlayUndeadDamage", enableSlayUndeadDamageIcon);
      MainActions.FireDamageSummaryOptionsChanged("IncludeSlayUndeadDamage");
    }

    private void ToggleAoEHealingClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsAoEHealingEnabled = MainActions.ToggleSetting("IncludeAoEHealing", enableAoEHealingIcon);
      MainActions.FireHealingSummaryOptionsChanged("IncludeAoEHealing");
    }

    private void ToggleHealingSwarmPetsClick(object sender, RoutedEventArgs e)
    {
      AppSettings.IsHealingSwarmPetsEnabled = MainActions.ToggleSetting("IncludeHealingSwarmPets", enableHealingSwarmPetsIcon);
      MainActions.FireHealingSummaryOptionsChanged("IncludeHealingSwarmPets");
    }

    // Main Menu
    /*
     * The Player/NPC Identity window (see NamesTable). Docking it is not enough on its own: the census is built on demand, so a
     * window reopened after an hour should answer about the log that is open now rather than about the one that was
     * open when it was last looked at.
     */
    /*
     * ToggleWindow, not SetState: a pane with a declared side belongs to its edge, and the util decides the state (see
     * SyncFusionUtil.ShowStateFor). The right-hand strip holds this pane alone since Pet Owners was retired.
     * their menu items identically. SetState(Dock) used to sit here, which is not a show/hide at all: it pulled the pane out
     * of its strip into the middle of the layout, so a menu item people press to PEEK at a list relocated it.
     *
     * No Refresh call here, and that is deliberate: showing the pane fires IsVisibleChanged, which subscribes and rebuilds.
     * This handler used to force a census on the way through as well - two full classification passes per click (~460 ms on
     * a 5.5k-name capture), for a list that was about to be rebuilt anyway by the event the toggle itself raises.
     */
    private void MenuItemNamesClick(object sender, RoutedEventArgs e)
    {
      SyncFusionUtil.ToggleWindow(dockSite, nameof(namesWindow));
    }

    /*
     * One-time repair of a saved layout for the identity pane.
     *
     * Player/NPC Identity moved out of the fight list's tab group into the right-hand strip. A dockSite.xml
     * written before that says "tabbed with npcWindow", and an operator would keep getting the OLD arrangement with no way
     * to notice the new one exists - so the saved state is corrected once, then left alone: anyone who moves the pane after
     * this build starts keeps their choice, because nothing here runs twice. Options / Reset Window State stays the escape
     * hatch for anything else a stale layout got wrong.
     *
     * The tab link is set BEFORE the state, and the whole thing is guarded: an arrangement this build cannot express is
     * logged, not thrown - MainWindowOnLoaded is the startup path, and a dock exception there takes the window down with it.
     */
    private void MigrateIdentityPaneIntoRightStrip()
    {
      const string migratedKey = "IdentityStripMigrated";
      if (ConfigUtil.IfSet(migratedKey)) return;
      ConfigUtil.SetSetting(migratedKey, true);

      try
      {
        /*
         * Side first, then state, and nothing else: two windows auto-hidden on the SAME side share that side's panel and
         * tab together (that grouping is Syncfusion's own — it looks for auto-hidden siblings rather than a target name), so
         * naming a partner would be a second opinion about a layout the side already decides.
         */
        DockingManager.SetSideInDockedMode(namesWindow, DockSide.Right);
        DockingManager.SetState(namesWindow, DockState.AutoHidden);
      }
      catch (Exception ex)
      {
        Log.Debug("Identity pane could not be moved into the right-hand strip", ex);
      }
    }

    private void MenuItemWindowClick(object sender, RoutedEventArgs e)
    {
      if (ReferenceEquals(e.Source, eqLogMenuItem))
      {
        var found = _logWindows.FindIndex(used => !used);
        if (found == -1)
        {
          _logWindows.Add(true);
          found = _logWindows.Count;
        }
        else
        {
          _logWindows[found] = true;
          found += 1;
        }

        SyncFusionUtil.OpenWindow(out _, typeof(EqLogViewer), "eqLogWindow", "Log Search " + found);
      }
      else if (sender is MenuItem { Icon: ImageAwesome { Tag: string name2 } })
      {
        SyncFusionUtil.ToggleWindow(dockSite, name2);
      }
    }

    private void DamageSummarySelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      DamageStatsBuilder.Instance.FireChartEvent("SELECT", data.Selected, data.SelectedGroups);
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(Labels.DamageParse, data.Selected);
    }

    private void HealingSummarySelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      HealingStatsBuilder.Instance.FireChartEvent("SELECT", data.Selected);
      var addTopParse = data.Selected?.Count == 1 && data.Selected[0].SubStats?.Count > 0;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(data, addTopParse, Labels.HealParse, Labels.TopHealParse);
    }

    private void TankingSummarySelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      var addReceiveParse = data.Selected?.Count == 1 && data.Selected[0].MoreStats != null;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(data, addReceiveParse, Labels.TankParse, Labels.ReceivedHealParse);
    }

    private static void MenuItemFontFamilyClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Header: string header, Parent: MenuItem parent } menuItem)
      {
        ThemeConfig.UpdateCheckedMenuItem(menuItem, parent.Items);
        ThemeConfig.ChangeThemeFontFamily(header);
      }
    }

    private static void MenuItemFontSizeClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Parent: MenuItem parent, Tag: double tag } menuItem)
      {
        ThemeConfig.UpdateCheckedMenuItem(menuItem, parent.Items);
        ThemeConfig.ChangeThemeFontSizes(tag);
      }
    }

    private void MenuItemSelectLogFileClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem item)
      {
        var lastMin = -1;
        string fileName = null;
        if (item.Tag is string tag && !string.IsNullOrEmpty(tag))
        {
          lastMin = Convert.ToInt32(item.Tag.ToString(), CultureInfo.CurrentCulture) * 60;
        }

        if (item.Parent == recent1File && _recentFiles.Count > 0)
        {
          fileName = _recentFiles[0];
        }
        else if (item.Parent == recent2File && _recentFiles.Count > 1)
        {
          fileName = _recentFiles[1];
        }
        else if (item.Parent == recent3File && _recentFiles.Count > 2)
        {
          fileName = _recentFiles[2];
        }
        else if (item.Parent == recent4File && _recentFiles.Count > 3)
        {
          fileName = _recentFiles[3];
        }
        else if (item.Parent == recent5File && _recentFiles.Count > 4)
        {
          fileName = _recentFiles[4];
        }
        else if (item.Parent == recent6File && _recentFiles.Count > 5)
        {
          fileName = _recentFiles[5];
        }

        if (!string.IsNullOrEmpty(fileName) && !File.Exists(fileName))
        {
          new MessageWindow("Log File No Longer Exists!", Resource.FILEMENU_OPEN_LOG).ShowDialog();
          return;
        }

        OpenLogFile(fileName, lastMin, "recent-file menu");
      }
    }

    private void UpdateLoadingProgress()
    {
      Dispatcher.InvokeAsync(async () =>
      {
        if (_eqLogReader != null)
        {
          _isStarting = true;
          var seconds = Math.Round((DateTime.Now - _startLoadTime).TotalSeconds);
          var filePercent = Math.Round(_eqLogReader.GetProgress());

          // The derived fight list has no rows of its own until the first snapshot (bulk ingest parks the derive
          // lanes by design), so without this it would sit as a silent empty grid through the whole load. It gets the
          // handover count rather than filePercent: the percent is THIS line's job (one copy per pump, at the top of the
          // window), while the panel only needs to know whether a first build is owed at all — a follow-from-end open
          // (Clear All, the startup auto-monitor) reads no history and its empty list is the correct answer.
          if (npcWindow?.Content is FightTable pumpTable) pumpTable.ReportCaptureProgress(_eqLogReader.HandedOverLines);

          statusText.Text = filePercent < 100.0 ? $"Reading Log.. {filePercent}% in {seconds} seconds" : $"Additional Processing... {seconds} seconds";
          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (filePercent >= 100)
          {
            statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;

            /*
             * Two different states reach 100 %, and only one of them has read a log. Following from end of file
             * (`lastMins: 0` - the startup auto-monitor open) seeks straight to EOF, so progress is 100 % immediately with
             * nothing handed over. Saying "Finished Loading Log File in 1 seconds" there is how an empty fight list starts
             * looking like a bug: the reader did its job, it just had no history to read. ONE phrase either way: both
             * states are "monitoring now", and the parenthetical this used to append said nothing an operator could act
             * on (removed 2026-10-09 on request - nobody could say where it came from, which is the same finding). Where
             * the two states genuinely differ still shows in eqlogparser.log and in the tidy decision below, which is
             * where a distinction earns its keep.
             */
            var followedFromEnd = _eqLogReader.HandedOverLines == 0;
            statusText.Text = "Monitoring Log";

            ConfigUtil.SetSetting("LastOpenedFile", AppSettings.CurrentLogFile);
            Log.Info(followedFromEnd
              ? "Monitoring from the end of the file - no history was read."
              : $"Finished Loading Log File in {seconds} seconds.");
            MainActions.UpdateStatus("Monitoring Last Log");

            await Task.Delay(500);
            MainActions.FireLogLoadingEvent(AppSettings.CurrentLogFile, true);
            _isStarting = false;
            await Dispatcher.InvokeAsync(() =>
            {
              closeLogFile.IsEnabled = true;
              saveLogFile.IsEnabled = true;
              OpenDamageOverlayIfEnabled(true, false);
              SubscribeOverlayFights();
            }, DispatcherPriority.DataBind);

            /*
             * The parse is finished and its garbage is gone by definition: the split strings and per-line temporaries that make a load
             * expensive for the collector. Asked for after the overlays have allocated their steady state, and collected on a pool thread,
             * so the UI thread never sits inside a stop-the-world holding its own work.
             *
             * Only if a load happened. Following from end of file reaches 100 % without reading a line, and an aggressive compacting pass
             * at that moment stops every thread to hand back startup allocations — there is no parse churn to reclaim, because none was
             * created. The one verb that DOES drop a whole capture (Clear All) asks for its own tidy where it tears the session down
             * (ClearAllFights), so skipping here leaves nothing untidied.
             */
            if (LogReader.LoadAllocatedGarbage(_eqLogReader.HandedOverLines)) GcTidyUp.Request("log loaded");
          }
          else
          {
            await Task.Delay(500);
            UpdateLoadingProgress();
          }
        }
      }, DispatcherPriority.DataBind);
    }

    /*
     * `origin` names the door this open came through, and rides down into the reader's load line (see LogReader). The
     * three doors behave differently enough that "an open happened" stopped being an answer: a PerfReport run showed a
     * read loop with no session beside it, and a follow-from-end open holding 196 MB of row slots it could not fill.
     */
    private void OpenLogFile(string previousFile, int lastMins, string origin = "open")
    {
      var openMark = PerfCounters.Begin(OpenLogId);
      try
      {
        string theFile = null;
        if (previousFile != null)
        {
          theFile = previousFile;
        }
        else
        {
          // Where this character's log lives, when we know it; otherwise the newest recent file whose folder is
          // still on disk, and failing that whatever Windows wants to show. ResolveDirectory drops every
          // candidate that does not exist, so the chooser is never handed a folder it cannot open — that was
          // the crash.
          var start = new[] { AppSettings.CurrentLogFile }.Concat(_recentFiles)
            .Select(FileDialogUtil.ResolveDirectory).FirstOrDefault(dir => dir != null);

          // Cancel and failure both come back as "no file", so there is nothing here that can end the app.
          PerfCounters.Run(PickFileId, () =>
          {
            theFile = FileDialogUtil.PickFile(this, start, "log file", "eqlog_Player_server|*.txt;*.gz;*.log");
          });
        }

        if (!string.IsNullOrEmpty(theFile))
        {
          Log.Info($"Selected Log File: {theFile}");
          FileUtil.ParseFileName(theFile, out var name, out var server);
          var changed = ConfigUtil.ServerName != server;

          Dispatcher.Invoke(() =>
          {
            if (DockingManager.GetState(npcWindow) == DockState.Hidden)
            {
              DockingManager.SetState(npcWindow, DockState.Dock);
            }


            CloseLogFile(changed);
            fileText.Text = $"-- {theFile}";
            _startLoadTime = DateTime.Now;
            ConfigUtil.ServerName = server;
            ConfigUtil.PlayerName = name;

            if (changed)
            {
              /*
               * The three per-server identity stores load in the order that reads them: the operator's own verdicts
               * (identity-overrides.txt, R10 — loaded before the engine's first derive or the rules answer alone), then
               * the sighting ledger (identity-priors.txt), then the roster, which seeds itself from that ledger. The order
               * is not cosmetic: PlayerRegistry reads the ledger's roster lane, and IdentityPriorStore files what it is
               * given under the server name IT holds — while this block still ran with the ledger pointing at the server
               * we just left, one server's names landed in another's file.
               */
              IdentityOverrideStore.Instance.Init(server);
              IdentityPriorStore.Instance.Init(server);

              // players.txt -> the ledger's roster lane, once per server folder (LegacyPlayerImport: why once, and why the
              // source file is left where it is). Runs before PlayerRegistry.Init so that the registry's memory comes up
              // carrying this folder's roster whether or not the file still exists.
              LegacyPlayerImport.ImportPlayersFileOnce(server);

              // petmapping.txt -> the ledger's ownership lane, once per server folder (same gate, same reasons; see
              // LegacyPlayerImport). Both migrations sit here so the two legacy files retire on one seam: after this line the
              // ledger holds membership, class and ownership for this folder, and PlayerRegistry below is a mirror of it.
              LegacyPlayerImport.ImportPetMapOnce(server);

              /*
               * The registry comes up as a mirror of that ledger: roster membership, class, and the pet→owner pairs. Nothing here
               * pushes those into window-side collections any more. The three lists this call used to feed (Verified Players, Verified
               * Pets, Pet Owners) each held a COPY of identity state, sorted on the UI thread as the parse learned names, and each was
               * stale the moment a verdict moved; Player/NPC Identity reads the derive's own census, and an owner rides in its Owner
               * column (IdentityLookup is still the single answer to "who is one of ours" — it just has no copies underneath it now).
               */
              PlayerRegistry.Instance.Init();
            }

            _recentFiles.Remove(theFile);
            _recentFiles.Insert(0, theFile);
            ConfigUtil.SetSetting("RecentFiles", string.Join(",", _recentFiles));
            UpdateRecentFiles();
            AppSettings.CurrentLogFile = theFile;

            // Mirror session subscribes to the parser statics before any line flows, and shares
            // the single chat sink slot via fan-out (archive first, derivation second — D8 seam).
            _engine?.Dispose();
            _engine = null;
            _engineSessionId = 0;
            IChatSink chatSink = new ChatDbSink();
            /*
             * The engine sizes its fact arrays from this file when the whole of it will be read (see FactCapacity):
             * a gigabyte capture used to arrive at 4.8 M damage facts sitting in 8.4 M slots, and a 1 % undershoot
             * costs a full doubling, so an estimate beats starting at 100 K and growing there.
             *
             * WHICH opens that is belongs to FactCapacity.HintForOpen now, because `lastMins` has three states and the
             * middle one is the trap: negative reads the whole file, positive seeks back N seconds by timestamp, and ZERO
             * — the auto-monitor, Open Monitor, Clear All — goes straight to end of file and captures nothing. The old
             * two-way test put zero on the hinted side, and a PerfReport run printed the result for a 951 MB file:
             * `facts rows=0 slack=127.0 MB … row arrays est=196.1 MB` — a fifth of a gigabyte reserved against no facts,
             * with no route back, since compaction runs from the pass that classified and a capture that never grows
             * schedules none. An idle monitor was the most expensive session this app had.
             */
            var hintBytes = FactCapacity.HintForOpen(lastMins, LogReader.FileSizeOrZero(theFile));
            _engine = new DeriveEngine(hintBytes);
            _engineSessionId = _engine.SessionId;
            _engine.Start();
            chatSink = new CompositeChatSink(chatSink, _engine.ChatSink);
            // One line per open: if the derived list ever silently fails to fill, this is the line that is
            // missing from the log (session created) or that arrives without rows following it (derive stuck).
            // Mode and door ride on this line because both were unanswerable from a log: which of the three ways of
            // reading a file an open chose, and who asked. `sized-from` is the input to the capacity hint (0 means the
            // tables start small and double), so "why is the working set this big on an idle monitor" reads off one line.
            Log.Info($"capture: started ({Path.GetFileName(theFile)}) {FactCapacity.ModeWord(lastMins)}"
                     + $" from {origin} | sized-from={hintBytes / (1024 * 1024):N0} MB");

            _eqLogReader = new LogReader(new LogProcessor(theFile, chatSink, new TriggerHookAdapter()), theFile, origin, lastMins);
            /*
             * Start the read loop OFF this thread - and Task.Run is needed even though LogReader made all of its own
             * awaits context-free, because the first segment runs on whoever called and everything here is already inside a
             * dispatcher callback. A bulk open spends most of its life parked in the reader's full-queue Add: reading a
             * 350 MB capture used to do that on the thread that paints this window, which is what made the numbers freeze
             * while a big log loaded. The meter keeps its own dispatcher work - only who runs the reader changed.
             */
            _ = Task.Run(_eqLogReader.StartAsync);
            UpdateLoadingProgress();
          }, DispatcherPriority.Render);
        }
      }
      catch (Exception e)
      {
        // Logged and swallowed on purpose. The old version rethrew anything that was not a cast, argument or
        // format problem, which meant the unexpected failures were exactly the ones that reached the user; the
        // dispatcher handler would only have caught them again, with less context in the log.
        Log.Error("Problem Opening Log File", e);
      }
      finally
      {
        PerfCounters.End(openMark);
      }
    }

    private void CloseLogFile(bool changed)
    {
      try
      {
        /*
         * There is no window-side list to blank on a log change any more (the three retired panes' collections are deleted), so the
         * fan-out to the grids is all this path needs: `LifecycleManager.Clear` is still the ONLY raiser of ActiveDataCleared.
         */
        LifecycleManager.Clear(changed);
        var closedFile = AppSettings.CurrentLogFile;
        AppSettings.CurrentLogFile = null;
        statusText.Text = string.Empty;
        _eqLogReader?.Dispose();
        _eqLogReader = null;
        _engine?.Dispose();
        _engine = null;
        _engineSessionId = 0;
        fileText.Text = string.Empty;
        ConfigUtil.ServerName = null;
        ConfigUtil.PlayerName = null;
        UnsubscribeOverlayFights();
        CloseDamageOverlay(false);
        closeLogFile.IsEnabled = false;
        saveLogFile.IsEnabled = false;
        MainActions.FireLogLoadingEvent(closedFile, false);
      }
      catch (Exception)
      {
        // ignore
      }
    }

    /*
     * The fight list's "Clear All", in the only shape a projection can honour. Legacy's button wiped FightManager's store;
     * there is no store here - every row is folded from the captured facts, so deleting rows would put them all back on the
     * next pass. What this means is instead what the operator does by hand when they want this state: File / Open Monitor on
     * the file that is already open. `lastMins: 0` seeks to end of file and follows from now, so afterwards the app holds
     * exactly what a fresh monitor open holds - no facts, no rows, the seven views blanked (CloseLogFile fans out
     * LifecycleManager.Clear, which raises ActiveDataCleared), the parser rewired to a new session - and it keeps precisely
     * the thing a fresh open reads from disk: identity-overrides.txt and identity-priors.txt (roster, class and ownership
     * lanes) plus what PlayerRegistry mirrors out of them, because the server did not change and this path re-initialises
     * those stores only when it does.
     *
     * The consequence worth stating where the button lives: the past is unloaded, not destroyed. The file on disk is
     * untouched, so opening it again - not in monitor mode - reads the night back.
     */
    internal void ClearAllFights()
    {
      var theFile = AppSettings.CurrentLogFile;

      // Nothing open means nothing to clear; a log that moved or went away is not cleared into an error dialog either.
      if (string.IsNullOrEmpty(theFile) || !File.Exists(theFile)) return;

      Log.Info($"clear all: re-opening {Path.GetFileName(theFile)} as a monitor session (from end of file)");

      /*
       * The capture about to die is the biggest thing this process holds — fact arrays, projected rows, boards, parsed records — and
       * Clear All is the one verb that drops it while the app keeps running, which is where returned segments matter most: the next load
       * lands in free space rather than in fragmentation. Asked BEFORE the re-open so what the collection can see is the dead session.
       *
       * Deliberately here and not inside CloseLogFile. A normal open closes the old session and immediately starts reading a new file,
       * and a tidy armed at that instant would land 1.5 s into the bulk read — stopping the world in the middle of the load, and spending
       * the rate limit on the worst possible moment. This path is safe because it re-opens from end of file: no read follows, so nothing
       * else allocates while the collector is doing its job.
       */
      GcTidyUp.Request("log closed");

      OpenLogFile(theFile, 0, "clear all");
    }

    private void UpdateRecentFiles()
    {
      SetRecentVisible(recent1File, 0);
      SetRecentVisible(recent2File, 1);
      SetRecentVisible(recent3File, 2);
      SetRecentVisible(recent4File, 3);
      SetRecentVisible(recent5File, 4);
      SetRecentVisible(recent6File, 5);
      return;

      void SetRecentVisible(MenuItem menuItem, int count)
      {
        if (_recentFiles.Count > count)
        {
          var m = 75;
          var theFile = _recentFiles[count].Length > m ? "... " + _recentFiles[count][(_recentFiles[count].Length - m)..] : _recentFiles[count];
          var escapedFile = theFile.Replace("_", "__");
          menuItem.Header = count + 1 + ": " + escapedFile;
          menuItem.Visibility = Visibility.Visible;

          if (menuItem.Items.Count == 0)
          {
            MainActions.CreateOpenLogMenuItems(menuItem, MenuItemSelectLogFileClick);
          }

          if (count == 0)
          {
            recentSeparator.Visibility = Visibility.Visible;
          }
        }
        else
        {
          menuItem.Visibility = Visibility.Collapsed;

          if (count == 0)
          {
            recentSeparator.Visibility = Visibility.Collapsed;
          }
        }
      }
    }

    private void WindowIconLoaded(object sender, RoutedEventArgs e)
    {
      if (sender is FrameworkElement icon)
      {
        if (icon.Tag is string name && !string.IsNullOrEmpty(name))
        {
          var opened = SyncFusionUtil.GetOpenWindows(dockSite);
          if (opened.TryGetValue(name, out var control))
          {
            icon.Visibility = DockingManager.GetState(control) != DockState.Hidden ? Visibility.Visible : Visibility.Hidden;
          }
          else
          {
            icon.Visibility = Visibility.Hidden;
          }
        }
        else if (icon == themeDarkIcon)
        {
          icon.Visibility = ThemeConfig.CurrentTheme == "MaterialDark" ? Visibility.Visible : Visibility.Hidden;
        }
        else if (icon == themeLightIcon)
        {
          icon.Visibility = ThemeConfig.CurrentTheme == "MaterialLight" ? Visibility.Visible : Visibility.Hidden;
        }
      }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "It's a callback function")]
    private void DockSiteDockStateChanging(FrameworkElement sender, DockStateChangingEventArgs e)
    {
      // Somehow this fixes the problem where trying to dock a floating window to a document state
      // would cause a second drop area to be created
      // note that the issue only happens when using native floating windows
      // without native floating windows you can't resize the width
      if (e.PresentState == DockState.Float && e.TargetState == DockState.Document)
      {
        e.Cancel = true;
      }
    }

    private void WindowStateChanged(object sender, EventArgs e)
    {
      if (WindowState == WindowState.Minimized)
      {
        if (ConfigUtil.IfSet("HideWindowOnMinimize") && Visibility != Visibility.Hidden)
        {
          Hide();
        }
      }
      else
      {
        if (Visibility == Visibility.Hidden)
        {
          Show();
        }

        // workaround to bring window to front
        Topmost = true;
        Topmost = false;
      }

      if (WindowState != WindowState.Minimized)
      {
        App.LastWindowState = WindowState;
      }

      UpdateWindowBorder();
      MainActions.FireWindowStateChanged(WindowState);
    }

    private void WindowClosing(object sender, EventArgs e)
    {
      // restore from backup will use explicit mode
      if (Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
      {
        // avoid saving window related settings if app never fully loaded
        if (_appLoadingComplete)
        {
          if (!_resetWindowState && Directory.Exists(ConfigUtil.ConfigDir))
          {
            try
            {
              using var writer = XmlWriter.Create(Path.Combine(ConfigUtil.ConfigDir, "dockSite.xml"));
              dockSite.SaveDockState(writer);
            }
            catch (Exception)
            {
              // ignore
            }
          }

          ConfigUtil.SetSetting("WindowState", App.LastWindowState.ToString());
        }

        ConfigUtil.Save();
      }

      _saveTimer?.Stop();
      _eqLogReader?.Dispose();
      _notifyIcon?.Dispose();
      SystemEvents.PowerModeChanged -= SystemEventsPowerModeChanged;

      // restore from backup will use explicit mode
      if (Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
      {
        Application.Current.Shutdown();
      }
    }

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      // grab the one HwndSource for this window
      var source = (HwndSource)PresentationSource.FromVisual(this)!;
      if (source != null)
      {
        // hook in order
        source.AddHook(NativeMethods.BandAidHook);
        source.AddHook(NativeMethods.ProblemHook);
        source.AddHook(NativeMethods.MaximizeHook);
        source.AddHook(NativeMethods.WindowPosHook);
      }
    }
  }
}

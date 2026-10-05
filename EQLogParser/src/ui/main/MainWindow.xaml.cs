using FontAwesome5;
using log4net;
using Microsoft.Win32;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Dynamic;
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
    private DispatcherTimer _computeStatsTimer;
    private readonly DispatcherTimer _saveTimer;

    /*
     * The main window's periodic work, named for the heartbeat (PerfCounters, UiBeatMonitor). All three run on the UI thread and all
     * three are quiet suspects for a freeze somewhere else: settings.ini gets written from here every half minute (a file write is one
     * antivirus scan away from a second), stats are recomputed when a fight changes, and an open chart takes a data-point update at
     * whatever rate the parser produces them. A stall line that names one of these is a different conversation from one that names
     * nothing.
     */
    private static readonly int SaveId = PerfCounters.Register("ui.configSave");
    private static readonly int ComputeStatsId = PerfCounters.Register("ui.computeStats");
    private static readonly int ChartUpdateId = PerfCounters.Register("chart.update");

    /* How many times a redraw request arrived while one was already waiting to run; see QueueChartUpdate. */
    private static readonly int ChartBacklogId = PerfCounters.Register("chart.backlog");
    private int _chartUpdatesWaiting;

    /*
     * Opening a log file, and the file dialog inside it. A measured session caught 1.6 s of blocked UI thread right after a load finished,
     * and switching logs is something a player does mid-raid; the dialog gets its own name because the question "does a modal Win32 dialog
     * starve the beat?" has to be answered from evidence before anyone decides whether such a stall counts as one.
     */
    private static readonly int OpenLogId = PerfCounters.Register("ui.openlogfile");
    private static readonly int PickFileId = PerfCounters.Register("ui.pickfile");

    private PetMapping _currentEditMapping;
    private dynamic _currentEditPlayerClass;
    private LogReader _eqLogReader;
    private DeriveEngine _engine;
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
      InitializeComponent();

      // set main / themes
      MainActions.SetMainWindow(this);

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
      if (npcWindow?.Content is FightTable fightTable) fightTable.DerivedSelectionChanged += DerivedSelectionChanged;

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

      // populate windows that need data
      MainActions.InitPetOwners(this, petMappingWindow);
      MainActions.InitVerifiedPlayers(verifiedPlayersWindow, petMappingWindow);
      MainActions.InitVerifiedPets(this, verifiedPetsWindow, petMappingWindow);

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
        // OpenLogFile with update status. `true` = the app opened this on its own, which is the one case where the
        // fight list shows its loading band (see FightTable.AllowsLoadBand).
        OpenLogFile(previousFile, 0, true);
      }

      // workaround to set initial theme properly
      MainActions.UpdateStatus("Setting " + ThemeConfig.CurrentTheme);
      ThemeConfig.SetTheme();
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
        MigrateIdentityPaneIntoRightStrip();

        DamageStatsBuilder.Instance.EventsUpdateDataPoint += data => QueueChartUpdate(damageChartIcon, data);
        HealingStatsBuilder.Instance.EventsUpdateDataPoint += data => QueueChartUpdate(healingChartIcon, data);
        TankingStatsBuilder.Instance.EventsUpdateDataPoint += data => QueueChartUpdate(tankingChartIcon, data);
        MainActions.EventsDamageSelectionChanged += DamageSummarySelectionChanged;
        MainActions.EventsHealingSelectionChanged += HealingSummarySelectionChanged;
        MainActions.EventsTankingSelectionChanged += TankingSummarySelectionChanged;
        MainActions.EventsFightSelectionChanged += (_) => ComputeStats();
        ThemeConfig.EventsThemeChanged += _ => DataGridUtil.RefreshTableColumns(petMappingGrid);
        _computeStatsTimer = UiUtil.CreateTimer(ComputeStatsTick, 500, false);

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
        if (MirrorMeter.HasLiveFight())
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

    private void ComputeStatsTick(object sender, EventArgs e)
    {
      if (!_isStarting)
      {
        PerfCounters.Run(ComputeStatsId, ComputeStats);
        _computeStatsTimer.Stop();
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

    internal void CheckComputeStats()
    {
      if (_computeStatsTimer != null)
      {
        _computeStatsTimer.Stop();
        _computeStatsTimer.Start();
      }
    }

    private void ComputeStats()
    {
      // The timer's rebuild (damage validation toggles, load settling) re-announces the CURRENT selection through
      // the exact path a click takes, so a settings change can never build boards from a different world than the
      // one the list is pointing at. No capture, no selection, nothing to rebuild.
      if (npcWindow?.Content is FightTable table && table.SessionActive)
        DerivedSelectionChanged(table.GetSelectedFights());
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
    private void DerivedSelectionChanged(IReadOnlyList<DerivedFight> selected)
    {
      var session = _engine;
      if (session is null) return;

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
       * Materializing allocates one record per selected fact, so it belongs on the worker with the build; the
       * UI thread's part ends at "these fights". No single-flight guard here: BuildTotalStats serialises on its
       * own lock, and the grid's settle timer upstream keeps a dragged range to one announcement.
       */
      _ = Task.Run(() =>
      {
        try
        {
          var input = session.BuildSummaryInput(selected);

          GenerateStatsOptions damageStatsOptions = new();
          damageStatsOptions.Npcs.AddRange(input.Fights);
          damageStatsOptions.AllRanges = input.AllRanges;
          damageStatsOptions.MinSeconds = 0;

          var records = input.Fights.Sum(static f => f.DamageBlocks.Sum(static b => b.Actions.Count));
          var tankRecords = input.Fights.Sum(static f => f.TankingBlocks.Sum(static b => b.Actions.Count));
          Log.Info($"Derived damage summary: {input.Fights.Count} fight(s), {records:N0} record(s), "
                   + $"{tankRecords:N0} taken"
                   + (input.WithoutDamage > 0 ? $", {input.WithoutDamage} selected fight(s) no facts at all" : string.Empty));

          DamageStatsBuilder.Instance.BuildTotalStats(damageStatsOptions);

          // Same rows, other direction: TankingStatsBuilder walks fight.TankingBlocks and takes each player's
          // activity window from TankSegments, both of which the materializer fills off the same facts.
          GenerateStatsOptions tankingStatsOptions = new();
          tankingStatsOptions.Npcs.AddRange(input.Fights);
          tankingStatsOptions.AllRanges = input.AllRanges;
          tankingStatsOptions.MinSeconds = 0;
          tankingStatsOptions.DamageType = tankingDamageType;

          TankingStatsBuilder.Instance.BuildTotalStats(tankingStatsOptions);

          // Healing: no Fight objects involved anywhere on this path. The window is the selection's, the records
          // are the capture's, and the builder's own filters (AoE healing, swarm pets) run over them unchanged —
          // so the healing tab stops being the one board that ignored which list was clicked.
          GenerateStatsOptions healingStatsOptions = new();
          healingStatsOptions.Npcs.AddRange(input.Fights);
          healingStatsOptions.AllRanges = input.AllRanges;
          healingStatsOptions.MinSeconds = 0;
          healingStatsOptions.Heals = input.Heals;

          HealingStatsBuilder.Instance.BuildTotalStats(healingStatsOptions);
        }
        catch (Exception ex)
        {
          Log.Error("Derived damage summary error", ex);
        }
      });
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
     * The same door as "Pet Owners" directly below it: SyncFusionUtil.ToggleWindow, so the two panes that share the
     * right-hand strip answer their menu items identically. SetState(Dock) used to sit here, which is not a show/hide at
     * all - it pulled the pane out of the strip and into the middle of the layout, so a menu item people press to PEEK at
     * a list relocated the window they were peeking at. The census is built on demand, so the explicit Refresh stays.
     */
    private void MenuItemNamesClick(object sender, RoutedEventArgs e)
    {
      SyncFusionUtil.ToggleWindow(dockSite, nameof(namesWindow));
      if (namesWindow?.Content is NamesTable namesTable) namesTable.Refresh();
    }

    /*
     * One-time repair of a saved layout for the identity pane.
     *
     * Player/NPC Identity moved out of the fight list's tab group into the right-hand strip beside Pet Owners. A dockSite.xml
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

        OpenLogFile(fileName, lastMin);
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
          // lanes by design), so without this it would sit as a silent empty grid through the whole load. The
          // band owns itself from here: EOF flips it to "building", the first snapshot takes it down.
          if (npcWindow?.Content is FightTable pumpTable) pumpTable.ReportCaptureProgress(filePercent);

          statusText.Text = filePercent < 100.0 ? $"Reading Log.. {filePercent}% in {seconds} seconds" : $"Additional Processing... {seconds} seconds";
          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (filePercent >= 100)
          {
            statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
            statusText.Text = "Monitoring Log";

            ConfigUtil.SetSetting("LastOpenedFile", AppSettings.CurrentLogFile);
            Log.Info($"Finished Loading Log File in {seconds} seconds.");
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

            // The parse is finished and its garbage is gone by definition: the split strings and per-line temporaries that make a load
            // expensive for the collector. Asked for after the overlays have allocated their steady state, and collected on a pool thread,
            // so the UI thread never sits inside a stop-the-world holding its own work.
            GcTidyUp.Request("log loaded");
          }
          else
          {
            await Task.Delay(500);
            UpdateLoadingProgress();
          }
        }
      }, DispatcherPriority.DataBind);
    }

    private void OwnerEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome ia || ia.DataContext is not PetMapping mapping)
        return;

      var cell = UiElementUtil.FindGridCell(ia);
      if (cell is null)
        return;

      _currentEditMapping = mapping;
      // value is the string, item is the expand-o object
      ownerEditComboBox.SelectedValue = mapping.Owner;
      UiElementUtil.OpenCellPopup(ownerEditPopup, ownerEditComboBox, cell, () =>
      {
        _currentEditPlayerClass = null;
        classEditComboBox?.SetValue(ComboBox.SelectedValueProperty, null);
      });
    }

    private void ClassEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome ia || ia.DataContext is not ExpandoObject obj)
        return;

      var cell = UiElementUtil.FindGridCell(ia);
      if (cell is null)
        return;

      _currentEditPlayerClass = obj;
      classEditComboBox.SelectedItem = _currentEditPlayerClass.PlayerClass;
      UiElementUtil.OpenCellPopup(classEditPopup, classEditComboBox, cell, () =>
      {
        _currentEditMapping = null;
        ownerEditComboBox?.SetValue(ComboBox.SelectedValueProperty, null);
      });
    }

    private void OwnerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is not ComboBox combo || combo.SelectedValue is not string name || string.IsNullOrEmpty(name))
        return;

      if (_currentEditMapping == null || _currentEditMapping.Owner == name)
        return;

      PlayerRegistry.Instance.AddPetToPlayer(_currentEditMapping.Pet, name);
      ownerEditPopup.IsOpen = false;
    }

    private void ClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is not ComboBox combo || combo.SelectedValue is not string className || string.IsNullOrEmpty(className))
        return;

      if (_currentEditPlayerClass == null || _currentEditPlayerClass.PlayerClass == className)
        return;

      PlayerRegistry.Instance.SetDefaultPlayerClass(_currentEditPlayerClass.Name, className);
      classEditPopup.IsOpen = false;
    }

    /*
     * `openedAutomatically` distinguishes the startup open (auto-monitor restored the last log; nobody is looking at
     * this window yet and no scan was asked for by hand) from an open through File / Recent Files. The only thing it
     * changes is whether the fight list may raise its loading band over the grid for this session.
     */
    private void OpenLogFile(string previousFile, int lastMins, bool openedAutomatically = false)
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
              // update pet/player windows all at once
              PlayerRegistry.Instance.Init();

              // R10: the operator's own verdicts on names are per server too (mirror-overrides.txt), and they
              // have to be loaded before the engine's first derive or the rules answer alone.
              IdentityOverrideStore.Instance.Init(server);

              // Same per-server reasoning for the sighting ledger (identity-priors.txt): what older logs on THIS
              // server concluded, kept for names a later capture has no evidence about.
              IdentityPriorStore.Instance.Init(server);
              MainActions.LoadVerified(verifiedPlayersWindow, verifiedPetsWindow, PlayerRegistry.Instance.GetVerifiedPlayers(),
                PlayerRegistry.Instance.GetVerifiedPets());
              MainActions.LoadPetOwners(petMappingWindow, PlayerRegistry.Instance.GetPetMappings());
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
            IChatSink chatSink = new ChatDbSink();
            _engine = new DeriveEngine();
            _engine.Start();
            chatSink = new CompositeChatSink(chatSink, _engine.ChatSink);
            // One line per open: if the derived list ever silently fails to fill, this is the line that is
            // missing from the log (session created) or that arrives without rows following it (derive stuck).
            Log.Info($"capture: started ({Path.GetFileName(theFile)})");

            // Set before the reader starts so the first pump tick already knows whose open this is.
            if (npcWindow?.Content is FightTable bandPane) bandPane.AllowsLoadBand = openedAutomatically;

            _eqLogReader = new LogReader(new LogProcessor(theFile, chatSink, new TriggerHookAdapter()), theFile, lastMins);
            _ = _eqLogReader.StartAsync();
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
        if (changed)
        {
          MainActions.Clear(verifiedPetsWindow, verifiedPlayersWindow, petMappingWindow);
        }

        LifecycleManager.Clear(changed);
        var closedFile = AppSettings.CurrentLogFile;
        AppSettings.CurrentLogFile = null;
        statusText.Text = string.Empty;
        _eqLogReader?.Dispose();
        _eqLogReader = null;
        _engine?.Dispose();
        _engine = null;
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

    private void RemovePetMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome ia || ia.DataContext is not ExpandoObject sortable)
        return;

      PlayerRegistry.Instance.RemoveVerifiedPet(((dynamic)sortable).Name);
    }

    private void RemovePlayerMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome ia || ia.DataContext is not ExpandoObject sortable)
        return;

      PlayerRegistry.Instance.RemoveVerifiedPlayer(((dynamic)sortable).Name);
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
      petMappingGrid?.Dispose();
      verifiedPetsGrid?.Dispose();
      verifiedPlayersGrid?.Dispose();
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

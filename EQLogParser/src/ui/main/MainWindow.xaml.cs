using EQLogParser;
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
using Application = System.Windows.Application;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  public partial class MainWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private DateTime _startLoadTime;
    private readonly WindowInstanceSlot<DamageOverlayWindow> _damageOverlays = new(window => window.Close());
    private FctOverlayWindow _fctOverlay;
    private readonly DispatcherTimer _saveTimer;

        /*
         * One summary-board build at a time (SummaryBuildGate). The key holds only what this window can name: the rows, the capture's content stamp and
         * the tanking filter; the six DamageValidator settings are read fresh by each build at the pane's own door. An identity-event bump was wired in
         * here until measurement showed it rebuilt all three boards to identical totals (docs/DesignNotes.md → "Name every door").
         */
    private readonly SummaryBuildGate _summaryGate = new(static work => Task.Run(work));
    private long _summaryGestureGeneration;

    /*
     * Opening a log file, and the file dialog inside it. A measured session caught 1.6 s of blocked UI thread right after a load finished,
     * and switching logs is something a player does mid-raid; the dialog gets its own name because the question "does a modal Win32 dialog
     * starve the beat?" has to be answered from evidence before anyone decides whether such a stall counts as one.
     */

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
      // The XAML tree: this window and every docked window declared inside it - the biggest thing startup builds.
      InitializeComponent();

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
       * but `MainActions.ClassList` outlives them: it is what every class dropdown offers, here and in the Identity Table.
       */
      MainActions.InitClassList();

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
        // This runs INSIDE the window constructor, and it bootstraps a whole session (clear, engine start, identity
        // stores, reader) there. `lastMins: 0` means follow from end of file: an old last-opened file yields zero lines
        // here, which is correct for a monitor but still announces "Finished Loading Log File" to the log.
        OpenLogFile(previousFile, 0, "startup auto-monitor");
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
                 * The fight list is a core window, so it docks at startup whatever the saved state says — arriving with no fight list reads as the feature
                 * breaking. The vestigial experimental pane stays hidden; it exists only so old dockSite.xml entries still resolve.
                 */
        DockingManager.SetState(npcWindow, DockState.Dock);
        DockingManager.SetState(mirrorFightWindow, DockState.Hidden);

        /*
         * Same law for Pet Owners, which is a name-only stub from 2026-10-09 (the markup explains why the shell stays): a saved
         * layout that had it auto-hidden on the right would otherwise slide an empty tab out beside the Identity Table forever,
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

    internal void CloseDamageOverlay(bool reopen, DamageOverlayWindow expected = null)
    {
      var closing = expected ?? _damageOverlays.Current;
      void CloseOwnedWindow()
      {
        if (closing is null)
        {
          if (reopen && _damageOverlays.Current is null) OpenDamageOverlayIfEnabled(false, true);
          return;
        }
        if (!_damageOverlays.Close(closing)) return;
        AppSettings.IsDamageOverlayOpen = false;
        if (reopen) OpenDamageOverlayIfEnabled(false, true);
      }
      if (Dispatcher.CheckAccess()) CloseOwnedWindow();
      else Dispatcher.InvokeAsync(CloseOwnedWindow);
    }

    internal void DamageOverlayClosed(DamageOverlayWindow window)
    {
      if (_damageOverlays.Forget(window)) AppSettings.IsDamageOverlayOpen = false;
    }

    internal void OpenDamageOverlayIfEnabled(bool reset, bool configure)
    {
      if (configure)
      {
        var window = new DamageOverlayWindow(true) { ShowActivated = false };
        _damageOverlays.Replace(window);
        window.Show();
      }
      // delay opening overlay so group IDs get populated
      else if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        // Recent completed damage may open the overlay too: a one-shot can be dead before the first derive lands.
        if (DerivedMeter.HasActivity())
        {
          var window = new DamageOverlayWindow(false, reset) { Opacity = 0, ShowActivated = false };
          _damageOverlays.Replace(window);
          window.Show();
          window.UpdateLayout();
          window.Opacity = 1.0;
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
         * View → FCT Overlay → Reset Position: close first (a closing window writes where it was), forget the geometry, then rebuild only if it was on
         * screen. Rebuild rather than move, because the shipped size and centring live in RestoreSettings.
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
         * Setup is the only way in. Locked means click-through with no header and no panel, and it is where the overlay always opens and what Cancel and
         * Save return to — a state that outlived its session would leave a click-eating rectangle, so there is no setting for it.
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
        /* Writes settings.ini from the UI thread, once a half minute. */
        ConfigUtil.Save();
      }
    }

    private async void SystemEventsPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
      switch (e.Mode)
      {
        case PowerModes.Suspend:
          // Info, not Warn: the machine going to sleep is a thing that happened to the session (triggers off, config written), not a defect —
          // and a bare "Suspending" at Warn read like an error in an otherwise quiet log while explaining nothing about what the app did.
          Log.Info("system: suspending - triggers stopped, settings written, overlays closed");
          _saveTimer?.Stop();
          ConfigUtil.Save();
          await TriggerManager.Instance.DisposeAsync();
          UnsubscribeOverlayFights();
          CloseDamageOverlay(false);
          break;
        case PowerModes.Resume:
          Log.Info("system: resumed - triggers restarted, monitor overlays re-opened");
          _saveTimer?.Start();
          await TriggerManager.Instance.StartAsync();
          OpenDamageOverlayIfEnabled(true, false);
          SubscribeOverlayFights();
          break;
      }
    }

        /*
         * The auto-open rule: raised on the derive thread, so creation goes through the dispatcher and is re-checked there — the meter opens at most once
         * however many passes announce the same pull. This is also what gives the X its meaning: closing changes nothing, damage brings the board back at
         * the same numbers; "Disable Meter" is how you keep it shut.
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
      if (_damageOverlays.Current is null && ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Dispatcher.InvokeAsync(() =>
        {
          if (_damageOverlays.Current is null)
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
         * A data point event arrives on a builder's thread and redraws on the UI thread, so rebuilds landing close together queue full redraws. The icon
         * crosses as an object and its Tag is read INSIDE the dispatched callback — reading that control here throws.
         */
    private void QueueChartUpdate(FrameworkElement icon, DataPointEvent e)
    {
      Dispatcher.InvokeAsync(() => HandleChartUpdate(icon.Tag as string, e));
    }

    private void HandleChartUpdate(string key, DataPointEvent e)
    {
      if (SyncFusionUtil.GetOpenWindows(dockSite).TryGetValue(key, out var value) &&
        value.Content is LineChart chart)
      {
        chart.HandleUpdateEvent(e);
      }
    }

        /*
         * The board path, from the one fight list: builders get ordinary Fight objects, damage and tanking both fed off the same materialized rows. The
         * healing board arrives by another door (it reads records): the session materializes this window's heal facts into options.Heals
         * (HealSummarySource). Same click, same AllRanges window; an empty selection still reaches the builders, which is how they clear.
         */
    /// <summary>
        /// <summary>
        /// The fight list pane as a plain reference for the board build. Reading <c>npcWindow.Content</c> is a DependencyObject read, so this is assigned
        /// ON THE UI THREAD only — the first version read it inside the build, threw there, and no board shipped.
        /// </summary>
    private FightTable _fightPane;

    private void DerivedSelectionChanged(BoardRequest request, DerivedSnapshot snapshot)
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
             * Materializing belongs on the worker with the build; the UI thread's part ends at the rows plus the key this answer is computed from. The gate
             * drops a duplicate key, queues at most one newer request, and never overlaps two materializations.
             */
      if (request.Reason == BoardReason.Manual) _summaryGestureGeneration++;
      var key = SummaryKeyFor(session.SessionId, request.Fights, request.ContentStamp, tankingDamageType, _summaryGestureGeneration);
      var askedAt = Stopwatch.GetTimestamp();
      var buildPane = _fightPane;
      var outcome = _summaryGate.Request(key, () => BuildBoards(session, snapshot, buildPane, request, tankingDamageType, askedAt));

            /*
             * One Debug line per ask, whatever the gate decided: this is the only place that sees both the door (named by the pane) and what single-flight
             * made of it. Pane doors that call builders directly do not come through here — StatsBuildTrace names those.
             */
      Log.Debug($"board ask [{request.Reason}]{(request.Detail is null ? string.Empty : $" {request.Detail}")}"
               + $": {request.Fights.Count} fight(s), stamp {request.ContentStamp}, tank type {tankingDamageType}"
               + $" -> {outcome}");
    }

        /*
         * Every builder input in one long. Order matters as much as content (the pane walks rows in list order), and 0 is reserved for "never built", so a
         * computed 0 becomes 1. A hash could collide in principle; at 64 bits over a session's announcements that beats an exact key built from thousands
         * of ids on the UI thread — which is why the gate logs its non-Started outcomes instead of arguing later.
         */
    internal static long SummaryKeyFor(int sessionId, IReadOnlyList<DerivedFight> selected, long contentStamp, int tankingDamageType,
      long gestureGeneration = 0)
    {
      var hash = unchecked((contentStamp * 397) ^ tankingDamageType);
      foreach (var fight in selected) hash = unchecked(hash * 17 + fight.Id);
      // The capture this question belongs to, mixed in LAST so nothing else about a session can collide with it. Two logs
      // can hand out the same ids, stamp and filter — and an empty selection over two of them is the everyday case: closing
      // one capture and opening another asks the SAME key for a board the new capture has never painted, which the gate would
      // otherwise answer "already built" to. See BoardKeyTest.
      hash = unchecked(hash * 31 + sessionId);
      hash = unchecked(hash * 31 + gestureGeneration);
      return hash == 0 ? 1 : hash;
    }

    /*
     * Whether the capture this build was asked for is still the one on screen. Reads an int the UI thread writes, with Volatile.Read:
     * the plain-data form of the law that a worker may not walk the dock site or touch `_engine`, and the question every board worker
     * asks before it starts (BoardFanout's law 1) as well as the two checks this method makes on its own thread.
     */
    private bool SessionStillOpen(DeriveEngine session) => session.SessionId == Volatile.Read(ref _engineSessionId);

        /*
         * The worker's half: materialize, then feed all three boards off the SAME rows. `askedAt` shows how long this build waited behind another — a
         * waited-3400 ms line IS single-flight working. Below BoardFanout.MinOutcomes the three builds run here in order; above it, side by side.
         */
    private bool BuildBoards(DeriveEngine session, DerivedSnapshot snapshot, FightTable pane, BoardRequest request, int tankingDamageType, long askedAt)
    {
      var waitedMs = StatsBuildTrace.ElapsedSince(askedAt);
      SummaryInput input = null;
      var succeeded = false;

      /*
       * Whose capture is this build for, still? A session that has been closed answers with an EMPTY summary (Dispose drops
       * the snapshot, and BuildSummaryInput reads that as "clear every board"), so a build that outlived its own log would
       * blank the boards of whatever opened next — including Clear All's own fresh session. Newest question wins: the gate
       * never un-STARTS work, so it stops before painting instead. `_engineSessionId` is written on the UI thread only.
       */
      if (!SessionStillOpen(session))
      {
        Log.Debug($"board build abandoned [{request.Reason}]: capture {session.SessionId} closed while this was queued");
        return false;
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
        var materializeWatch = Stopwatch.StartNew();
        input = DeriveEngine.BuildSummaryInput(snapshot, request.Fights);
        materializeWatch.Stop();

        if (!SessionStillOpen(session))
        {
          Log.Debug($"board build abandoned [{request.Reason}]: capture closed while this materialized");
          return false;
        }

                /*
                 * The three option objects are built up front because they may go to three workers: each board gets its own options (and its own BuildFailed
                 * result), all carrying ONE trace group so siblings never warn about each other (StatsBuildTrace rule 2).
                 */
        var buildGroup = StatsBuildTrace.NextGroupId();

        GenerateStatsOptions damageStatsOptions = new()
          { Source = door, BuildGroup = buildGroup, SessionId = session.SessionId };
        damageStatsOptions.Npcs.AddRange(input.Fights);
        damageStatsOptions.AllRanges = input.AllRanges;
        damageStatsOptions.MinSeconds = 0;

        // Same rows, other direction: TankingStatsBuilder walks fight.TankingBlocks and takes each player's
        // activity window from TankSegments, both of which the materializer fills off the same facts.
        GenerateStatsOptions tankingStatsOptions = new()
          { Source = door, BuildGroup = buildGroup, SessionId = session.SessionId };
        tankingStatsOptions.Npcs.AddRange(input.Fights);
        tankingStatsOptions.AllRanges = input.AllRanges;
        tankingStatsOptions.MinSeconds = 0;
        tankingStatsOptions.DamageType = tankingDamageType;

        // Healing: no Fight objects involved anywhere on this path. The window is the selection's, the records
        // are the capture's, and the builder's own filters (AoE healing, swarm pets) run over them unchanged —
        // so the healing tab stops being the one board that ignored which list was clicked.
        GenerateStatsOptions healingStatsOptions = new()
          { Source = door, BuildGroup = buildGroup, SessionId = session.SessionId };
        healingStatsOptions.Npcs.AddRange(input.Fights);
        healingStatsOptions.AllRanges = input.AllRanges;
        healingStatsOptions.MinSeconds = 0;
        healingStatsOptions.Heals = input.Heals;

        var records = input.Fights.Sum(static f => f.DamageBlocks.Sum(static b => b.Actions.Count));
        var tankRecords = input.Fights.Sum(static f => f.TankingBlocks.Sum(static b => b.Actions.Count));
        // Debug, like the `board ask [reason] -> outcome` line above: together they restate one click with counters,
        // and that is exactly the level a player's log does NOT carry. It stays because those counters are what a Debug run
        // reads when a board's numbers look wrong (how many records reached the builders, how long materialize took).
        Log.Debug($"Derived damage summary [{request.Reason}]: {input.Fights.Count} fight(s), {records:N0} record(s), "
                 + $"{tankRecords:N0} taken"
                 + (input.WithoutDamage > 0 ? $", {input.WithoutDamage} selected fight(s) no facts at all" : string.Empty)
                 + $" | materialized in {materializeWatch.Elapsed.TotalMilliseconds:F0} ms, waited {waitedMs:F0} ms behind any earlier build");

                /*
                 * Serial or fanned out, the boards are the same three calls over the same options; only the worker count changes, decided at the measured
                 * threshold (docs/DesignNotes.md → "Three boards, three workers"). The decision reads UnaskedRefresh.OutcomeCount — the same meter the refresh
                 * budget uses — so the two cannot drift apart. Fanning happens inside this one request: the gate still has one build in flight, so there is
                 * never a second materialization.
                 */
        var outcomes = UnaskedRefresh.OutcomeCount(request.Fights);
        if (BoardFanout.Decide(outcomes) == BoardFanoutMode.Parallel)
        {
                    /*
                     * Each worker re-asks that the capture is still open before starting (a build that outlived its log must return before painting), and each
                     * carries its own failure — the one deliberate behaviour change: serially a damage throw suppressed tanking and healing, fanned out one bad
                     * record costs one grid (BoardFanout law 2).
                     */
          Log.Debug($"board fan-out [{request.Reason}]: {outcomes:N0} outcomes over three workers");
          succeeded = BoardFanout.RunParallel([
            new("damage", request.Reason.ToString(), () => SessionStillOpen(session),
                () => DamageStatsBuilder.Instance.BuildTotalStats(damageStatsOptions)),
            new("tanking", request.Reason.ToString(), () => SessionStillOpen(session),
                () => TankingStatsBuilder.Instance.BuildTotalStats(tankingStatsOptions)),
            new("healing", request.Reason.ToString(), () => SessionStillOpen(session),
                () => HealingStatsBuilder.Instance.BuildTotalStats(healingStatsOptions))
          ]);
        }
        else
        {
          DamageStatsBuilder.Instance.BuildTotalStats(damageStatsOptions);
          TankingStatsBuilder.Instance.BuildTotalStats(tankingStatsOptions);
          HealingStatsBuilder.Instance.BuildTotalStats(healingStatsOptions);
          succeeded = true;
        }
        succeeded &= !damageStatsOptions.BuildFailed && !tankingStatsOptions.BuildFailed && !healingStatsOptions.BuildFailed
                     && SessionStillOpen(session);

      }
      catch (Exception ex)
      {
        // Logged here rather than left to the task: an announcement that threw must still return the gate to idle (its own
        // finally does that), and the boards keep whatever they had — which is why a partial board beats no board.
        succeeded = false;
        Log.Error("Derived damage summary error", ex);
      }

      // Watching happens AFTER the boards, under its own guard: it only decides whether a later derive tick is worth
      // announcing, so losing it must cost that and nothing else. It used to sit first inside the try above, where one
      // throw of its own — a dock-site read from this worker thread — silently took every board with it.
      if (succeeded) WatchBoardNames(pane, input, snapshot?.Timeline, session.SessionId);

      /*
       * The capture's owed load cleanup, if it has one: all three builders have answered and the materializer has let go, so whatever the read,
       * the first full derive and this build allocated and dropped is dead for good - the seam with something to collect. It runs on THIS worker
       * (a pool thread; a blocking compaction does not belong inside the message loop) and before the grids repaint, so the pause lands behind the
       * band the load left standing. Deliberately not gated on `succeeded`: a board that threw allocated anyway.
       */
      PayOwedLoadTidy(session);
      return succeeded;
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
    private static void WatchBoardNames(FightTable pane, SummaryInput input, EntityTimeline builtUnder, int sessionId)
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
              if (action is DamageRecord damage)
              {
                if (!string.IsNullOrEmpty(damage.Attacker)) involved.Add(damage.Attacker);
                if (!string.IsNullOrEmpty(damage.Defender)) involved.Add(damage.Defender);
              }

          foreach (var block in fight.TankingBlocks)
            foreach (var action in block.Actions)
              if (action is DamageRecord taken)
              {
                if (!string.IsNullOrEmpty(taken.Attacker)) involved.Add(taken.Attacker);
                if (!string.IsNullOrEmpty(taken.Defender)) involved.Add(taken.Defender);
              }
        }

        // Healers too: the healing board lists names no damage board necessarily showed.
        foreach (var (_, heal) in input.Heals)
        {
          if (!string.IsNullOrEmpty(heal.Healer)) involved.Add(heal.Healer);
          if (!string.IsNullOrEmpty(heal.Healed)) involved.Add(heal.Healed);
        }

        pane?.WatchBoardNames(builtUnder, [.. involved], sessionId);
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
        new MessageWindow("No Log File Opened. Nothing to Export.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
      else if (filtered.Count > 0)
      {
        MainActions.ExportFights(AppSettings.CurrentLogFile, filtered);
      }
      else
      {
        new MessageWindow("No Fights Selected. Nothing to Export.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
    }

    /*
     * The detailed export (issue #191): the selected fights as one .xlsx workbook of rows. Selection comes from the
     * fight list exactly as the operator filtered it, and "no selection" is refused in words rather than exporting the
     * whole night by surprise - a 200k-row file nobody asked for is a bad default.
     */
    private void MenuItemExportExcelClick(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(AppSettings.CurrentLogFile))
      {
        new MessageWindow("No Log File Opened. Nothing to Export.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
        return;
      }

      if (npcWindow?.Content is not FightTable fightTable || !fightTable.SessionActive) return;
      var (snapshot, selected) = fightTable.GetExportSelection();
      if (selected.Count == 0)
      {
        new MessageWindow("No Fights Selected. Nothing to Export.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
        return;
      }

      var customTitle = playerParseTextWindow.Content is ParsePreview preview ? preview.CustomTitle : null;
      MainActions.ExportWorkbook(snapshot, selected, customTitle);
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

    private async void ScanUiAnimationsClick(object sender, RoutedEventArgs e)
    {
      scanUiAnimations.IsEnabled = false;
      try
      {
        await UiAnimationScan.RunAsync();
      }
      catch (Exception ex)
      {
        Log.Error("UI animation scan failed", ex);
      }
      finally
      {
        scanUiAnimations.IsEnabled = true;
      }
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
     * The Identity Table window (see NamesTable). Docking it is not enough on its own: the census is built on demand, so a
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
     * The Identity Table moved out of the fight list's tab group into the right-hand strip. A dockSite.xml
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

          // One Debug line per tick, and it is the only honest answer to "the progress text vanished during a
          // load": either the pump ticked and the reader reported this percent (reader-side question), or the
          // ticks stop appearing while the load keeps running (the UI thread never got here). Cheap, bounded
          // by the load's wall time, and Debug-only like every other `load:` line.
          Log.Debug($"load: progress tick {filePercent:F0}% | lines={_eqLogReader.HandedOverLines:N0} | {seconds}s");

          // The derived fight list has no rows of its own until the first snapshot (bulk ingest parks the derive
          // lanes by design), so without this it would sit as a silent empty grid through the whole load. It gets the
          // handover count rather than filePercent: the percent is THIS line's job (one copy per pump, at the top of the
          // window), while the panel only needs to know whether a first build is owed at all — a follow-from-end open
          // (Clear All, the startup auto-monitor) reads no history and its empty list is the correct answer.
          if (npcWindow?.Content is FightTable pumpTable) pumpTable.ReportCaptureProgress(_eqLogReader.HandedOverLines);

          statusText.Text = filePercent < 100.0 ? $"Reading Log.. {filePercent}% in {seconds} seconds" : $"Additional Processing... {seconds} seconds";
          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (filePercent >= 100 && _eqLogReader.InitialLoadComplete)
          {
            // Arm the band's no-fights guard at the completed read, even if no later snapshot arrives.
            // This pump stops here; cleanup can defer hiding only until the band's own deadline.
            if (npcWindow?.Content is FightTable finishedTable) finishedTable.ReportReadFinished();

            statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;

                        /*
                         * The load's cleanup is ARMED here and PAID LATER (PayOwedLoadTidy): at this seam there is nothing left to collect — Server GC swept the
                         * read while it ran, and the first derive plus its boards (the real allocators) lie ahead. Field numbers: docs/DesignNotes.md ->
                         * "Garbage collection after loading a log". Arming sets the mark that holds the fight list's band over the pause, and the owed session id
                         * says WHICH capture owes it so a close or a new open drops the debt at its own door. Nothing else waits: monitoring, the
                         * loading-complete event and the overlays land while the first pass runs. Only if a load happened — LogReader.LoadAllocatedGarbage keeps a
                         * follow-from-end from collecting startup garbage for nothing.
                         */
            if (LogReader.LoadAllocatedGarbage(_eqLogReader.HandedOverLines) && _engine is { } loadedSession) ArmLoadTidy(loadedSession);

            /*
             * Two different states reach 100 %, and only one of them has read a log. Following from end of file
             * (`lastMins: 0` - the startup auto-monitor open) seeks straight to EOF, so progress is 100 % immediately with
             * nothing handed over. Saying "Finished Loading Log File in 1 seconds" there is how an empty fight list starts
             * looking like a bug: the reader did its job, it just had no history to read. ONE phrase either way: both
             * states are "monitoring now", and the parenthetical this used to append said nothing an operator could act
             * on (removed 2026-10-09 on request - nobody could say where it came from, which is the same finding). Where
             * the two states genuinely differ shows in eqlogparser.log, which is where a distinction earns its keep.
             */
            var followedFromEnd = _eqLogReader.HandedOverLines == 0;
            statusText.Text = "Monitoring Log";

            ConfigUtil.SetSetting("LastOpenedFile", AppSettings.CurrentLogFile);
            /*
             * The legacy phrase stays at Info - it is the line a player already knows. "We read nothing because this open
             * follows from end of file" is diagnostic context for a reader of the log, not news, and on a startup auto-monitor or a
             * Clear All it printed every single open; Debug=True still shows it beside the capture line below.
             */
            if (followedFromEnd) Log.Debug("Monitoring from the end of the file - no history was read.");
            else Log.Info($"Finished Loading Log File in {seconds} seconds.");
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
          theFile = FileDialogUtil.PickFile(this, start, "log file", "eqlog_Player_server|*.txt;*.gz;*.log");
        }

        if (!string.IsNullOrEmpty(theFile))
        {
          Log.Info($"Selected Log File: {theFile}");
          Volatile.Write(ref _loadTidyOwedSessionId, 0);   // ...and neither may its unpaid cleanup be billed to this file
          PostLoadCleanup.Reset();   // a cleanup marked by the previous capture must not hold this one's band or land in its read
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
               * stale the moment a verdict moved; the Identity Table reads the derive's own census, and an owner rides in its Owner
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
            // Debug, not Info: mode, door and `sized-from` are exactly what a capacity or "why is the working set this big"
            // question needs, and nothing about a working session belongs in a player's log. With Debug=True the line is
            // unchanged - which of the three ways of reading a file an open chose, who asked, and the capacity hint it fed.
            Log.Debug($"capture: started ({Path.GetFileName(theFile)}) {FactCapacity.ModeWord(lastMins)}"
                     + $" from {origin} | sized-from={hintBytes / (1024 * 1024):N0} MB");

            // One reader, one lane, file order: the session's own capture is what the processor fills (see LogReader's
            // class comment for why reading stays single-threaded).
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
    }

    private void CloseLogFile(bool changed)
    {
      Volatile.Write(ref _loadTidyOwedSessionId, 0);   // a debt dies with its capture; the next file arms its own
      PostLoadCleanup.Reset();   // a close ends the cleanup mark: whatever opens next is not holding on for this capture
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

      OpenLogFile(theFile, 0, "clear all");

      /*
       * The capture that just died is the biggest thing this process holds - fact arrays, projected rows, boards, parsed records - and
       * Clear All is the one verb that drops it while the app keeps running, which is where returned segments matter most: the next load
       * lands in free space rather than in fragmentation. Tidied AFTER the re-open so what the pass sees is the dead session, and because
       * the monitor seeks to end of file, nothing allocates behind the collector. Here rather than inside CloseLogFile because a close is
       * nearly always the first half of an OPEN, and a pass between them lands in the middle of that bulk read.
       *
       * Asked through the same door a load uses - labelled "Cleaning up..", collected on a pool thread, and dropped if this capture is
       * not the open answer when the collector starts. It belongs to the session the re-open above made, not to the one that died.
       */
      _ = PayLoadTidyAsync("capture cleared");
    }

        /*
         * The two moments GC law allows (GcTidyUp owns why these are the only two): Clear All pays here at once, a load arms at 100 % and pays at its
         * first answered board trio (PayOwedLoadTidy).
         *
         * The collect runs on a POOL thread — not because the UI keeps painting (a blocking compaction suspends every managed thread) but so our code,
         * not the message loop, sits inside the pause. Labelled either way: a named pause reads as work.
         *
         * Ownership is asked at the collector's own door, because between handoff and start a person can open another log and a pass inside the next
         * file's bulk read is the one forbidden thing; the count cannot cancel a begun pass, so it skips instead of relocating. Mark/Clear bracket ONE
         * pass (that mark is also what holds the fight list's band over the pause) and Clear runs in a finally; FightTable caps its wait by itself.
         */
    private async Task<bool> PayLoadTidyAsync(string reason)
    {
      // The status line and the band are Dispatcher state, and both are written before the await below: the law is "asked from a click", so say it
      // where it can be enforced instead of in a comment someone will stop reading.
      Dispatcher.VerifyAccess();

      const string notice = PostLoadCleanup.Notice;
      if (_engine is not { } session) return false;

      var prior = statusText.Text;
      PostLoadCleanup.Mark();
      statusText.Text = notice;
      try
      {
        await Task.Run(() =>
        {
          if (!SessionStillOpen(session))
          {
            Log.Debug($"gc.tidy skipped [{reason}]: the capture changed before the collector started");
            return;
          }

          GcTidyUp.TidyNow(reason);
        });
      }
      finally
      {
        // Released only while this pass still owns the capture: a door Reset already dropped the hold, and releasing after it would take the
        // replacement capture's hold away. Before the status line either way - the fight list waits on it to bring its bar down.
        if (SessionStillOpen(session)) PostLoadCleanup.Clear();

        // Restore the words only if they are still ours to restore: another path may have answered, and a capture that closed
        // while this ran already wrote its own status line.
        if (SessionStillOpen(session) && statusText.Text == notice) statusText.Text = prior;
      }

      return SessionStillOpen(session);
    }

    // The capture whose load owes its cleanup pass; 0 means none. Written on the UI thread, taken once by whichever build answers first.
    private int _loadTidyOwedSessionId;

    /*
     * Arm - do not pay - the loaded capture's cleanup. See the 100 % pump for why this seam has nothing to collect; see PayOwedLoadTidy for the
     * seam that does. Marking here is what holds the fight list's band from this moment, so the pause cannot arrive under a grid that just answered.
     */
    private void ArmLoadTidy(DeriveEngine session)
    {
      Volatile.Write(ref _loadTidyOwedSessionId, session.SessionId);
      PostLoadCleanup.Mark();
      Log.Debug($"gc.tidy armed for capture {session.SessionId} - paid when its first boards answer");
    }

    /*
     * The load's cleanup, paid where the load has stopped allocating: the first board trio of THIS capture answered (once per capture; the exchange
     * is what makes a later pass over a held selection pay nothing).
     *
     * The label and the band's words go to the dispatcher BEFORE the collection starts - a name that appears after the pause is a silent pause wearing
     * a hat - which means this worker waits on them; the gate's work always arrives here from Task.Run, so no UI thread is waiting back on us. After the
     * pass, the hold releases and the pane brings its bar down (FightTable.ReportCleanupState), then the status line goes back if it is still ours.
     */
    private void PayOwedLoadTidy(DeriveEngine session)
    {
      if (!PostLoadCleanup.TryTake(ref _loadTidyOwedSessionId, session.SessionId)) return;

      // Closed underneath us: the door's Reset already took the mark and this capture's band with it, so nothing here releases or collects.
      if (!SessionStillOpen(session))
      {
        Log.Debug("gc.tidy log loaded skipped: the capture closed before its first boards answered");
        return;
      }

      var prior = string.Empty;
      PostLoadCleanup.RunOwned(() => SessionStillOpen(session),
        action => Dispatcher.Invoke(action), action => Dispatcher.BeginInvoke(action),
        begin: () =>
        {
          prior = statusText.Text;
          statusText.Text = PostLoadCleanup.Notice;
          if (npcWindow?.Content is FightTable table) table.ReportCleanupState(true);
        },
        end: () =>
        {
          if (statusText.Text == PostLoadCleanup.Notice) statusText.Text = prior;
          if (npcWindow?.Content is FightTable table) table.ReportCleanupState(false);
        },
        collect: () => GcTidyUp.TidyNow("log loaded"));
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

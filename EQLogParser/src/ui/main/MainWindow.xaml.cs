using FontAwesome5;
using log4net;
using log4net.Appender;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Application = System.Windows.Application;

namespace EQLogParser
{
  public partial class MainWindow
  {
    // global settings
    internal static string CurrentLogFile;
    internal static bool IsAoEHealingEnabled = true;
    internal static bool IsHealingSwarmPetsEnabled = true;
    internal static bool IsAssassinateDamageEnabled = true;
    internal static bool IsBaneDamageEnabled = true;
    internal static bool IsDamageShieldDamageEnabled = true;
    internal static bool IsFinishingBlowDamageEnabled = true;
    internal static bool IsHeadshotDamageEnabled = true;
    internal static bool IsSlayUndeadDamageEnabled = true;
    internal static bool IsMapSendToEqEnabled;
    internal static bool IsEmuParsingEnabled;
    internal static bool IsDamageOverlayOpen;
    internal const int ActionIndex = 27;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private DateTime _startLoadTime;
    private DamageOverlayWindow _damageOverlay;
    private DispatcherTimer _computeStatsTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly NpcDamageManager _npcDamageManager = new();
    private LogReader _eqLogReader;
    private readonly List<bool> _logWindows = [];
    private readonly List<string> _recentFiles = [];
    private readonly string _activeWindow;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly SolidColorBrush _hoverBrush = UiUtil.GetBrush("#505050");
    private readonly SolidColorBrush _redHoverBrush = UiUtil.GetBrush("#E81123");
    private bool _resetWindowState;
    private bool _isStarting;
    private bool _appLoadingComplete;

    public MainWindow()
    {
      InitializeComponent();

      // set main / themes
      MainActions.SetMainWindow(this);

      // update titles
      versionText.Text = $"v{App.Version}";

      // AoE healing
      IsAoEHealingEnabled = ConfigUtil.IfSetOrElse("IncludeAoEHealing", IsAoEHealingEnabled);
      enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;

      // Healing Swarm Pets
      IsHealingSwarmPetsEnabled = ConfigUtil.IfSetOrElse("IncludeHealingSwarmPets", IsHealingSwarmPetsEnabled);
      enableHealingSwarmPetsIcon.Visibility = IsHealingSwarmPetsEnabled ? Visibility.Visible : Visibility.Hidden;

      // Assassinate Damage
      IsAssassinateDamageEnabled = ConfigUtil.IfSetOrElse("IncludeAssassinateDamage", IsAssassinateDamageEnabled);
      enableAssassinateDamageIcon.Visibility = IsAssassinateDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Bane Damage
      IsBaneDamageEnabled = ConfigUtil.IfSetOrElse("IncludeBaneDamage", IsBaneDamageEnabled);
      enableBaneDamageIcon.Visibility = IsBaneDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Damage Shield Damage
      IsDamageShieldDamageEnabled = ConfigUtil.IfSetOrElse("IncludeDamageShieldDamage", IsDamageShieldDamageEnabled);
      enableDamageShieldDamageIcon.Visibility = IsDamageShieldDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Finishing Blow Damage
      IsFinishingBlowDamageEnabled = ConfigUtil.IfSetOrElse("IncludeFinishingBlowDamage", IsFinishingBlowDamageEnabled);
      enableFinishingBlowDamageIcon.Visibility = IsFinishingBlowDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Headshot Damage
      IsHeadshotDamageEnabled = ConfigUtil.IfSetOrElse("IncludeHeadshotDamage", IsHeadshotDamageEnabled);
      enableHeadshotDamageIcon.Visibility = IsHeadshotDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Slay Undead Damage
      IsSlayUndeadDamageEnabled = ConfigUtil.IfSetOrElse("IncludeSlayUndeadDamage", IsSlayUndeadDamageEnabled);
      enableSlayUndeadDamageIcon.Visibility = IsSlayUndeadDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      // Hide window when minimized
      enableHideOnMinimizeIcon.Visibility = ConfigUtil.IfSet("HideWindowOnMinimize") ? Visibility.Visible : Visibility.Hidden;

      // Hide splash screen
      enableHideSplashScreenIcon.Visibility = ConfigUtil.IfSet("HideSplashScreen") ? Visibility.Visible : Visibility.Hidden;

      // Minimize at startup
      enableStartMinimizedIcon.Visibility = ConfigUtil.IfSet("StartWithWindowMinimized") ? Visibility.Visible : Visibility.Hidden;

      // Allow Ctrl+C for SendToEQ
      IsMapSendToEqEnabled = ConfigUtil.IfSet("MapSendToEQAsCtrlC");
      enableMapSendToEQIcon.Visibility = IsMapSendToEqEnabled ? Visibility.Visible : Visibility.Hidden;

      // Chat Archive on/off
      enableChatArchiveIcon.Visibility = ConfigUtil.IfSetOrElse("ChatArchiveEnabled", true) ? Visibility.Visible : Visibility.Hidden;

      // Export Formatted CSV (numbers with commas, etc)
      exportFormattedCsvIcon.Visibility = ConfigUtil.IfSetOrElse("ExportFormattedCsv", true) ? Visibility.Visible : Visibility.Hidden;

      // Damage Overlay
      enableDamageOverlayIcon.Visibility = ConfigUtil.IfSet("IsDamageOverlayEnabled") ? Visibility.Visible : Visibility.Hidden;
      enableDamageOverlay.Header = ConfigUtil.IfSet("IsDamageOverlayEnabled") ? "Disable _Meter" : "Enable _Meter";

      // Auto Monitor
      enableAutoMonitorIcon.Visibility = ConfigUtil.IfSet("AutoMonitor") ? Visibility.Visible : Visibility.Hidden;

      // Check for Updates
      checkUpdatesIcon.Visibility = ConfigUtil.IfSet("CheckUpdatesAtStartup") ? Visibility.Visible : Visibility.Hidden;

      // Hardware Acceleration
      hardwareAccelIcon.Visibility = ConfigUtil.IfSet("HardwareAcceleration") ? Visibility.Visible : Visibility.Hidden;

      // Enable EMU parsing
      IsEmuParsingEnabled = ConfigUtil.IfSet("EnableEmuParsing");
      emuParsingIcon.Visibility = IsEmuParsingEnabled ? Visibility.Visible : Visibility.Hidden;

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
      MainActions.CreateFontFamiliesMenuItems(appFontFamilies, MenuItemFontFamilyClicked);

      // create font sizes menu items
      MainActions.CreateFontSizesMenuItems(appFontSizes, MenuItemFontSizeClicked);

      // add tabs to the right
      ((DocumentContainer)dockSite.DocContainer).AddTabDocumentAtLast = true;

      // init theme before loading docs
      ConfigUtil.UpdateStatus("Loading Themes");
      MainActions.InitThemes();

      // save active window before adding
      _activeWindow = ConfigUtil.GetSetting("ActiveWindow");
      MainActions.AddDocumentWindows(dockSite);

      // populate windows that need data
      MainActions.InitPetOwners(this, petMappingGrid, ownerList, petMappingWindow);
      MainActions.InitVerifiedPlayers(this, verifiedPlayersGrid, classList, verifiedPlayersWindow, petMappingWindow);
      MainActions.InitVerifiedPets(this, verifiedPetsGrid, verifiedPetsWindow, petMappingWindow);

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
        // OpenLogFile with update status
        OpenLogFile(previousFile, 0);
      }

      // workaround to set initial theme properly
      ConfigUtil.UpdateStatus("Setting " + MainActions.CurrentTheme);
      MainActions.SetTheme();
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

        DamageStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(damageChartIcon.Tag as string, data));
        HealingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(healingChartIcon.Tag as string, data));
        TankingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(tankingChartIcon.Tag as string, data));
        MainActions.EventsDamageSelectionChanged += DamageSummarySelectionChanged;
        MainActions.EventsHealingSelectionChanged += HealingSummarySelectionChanged;
        MainActions.EventsTankingSelectionChanged += TankingSummarySelectionChanged;
        MainActions.EventsFightSelectionChanged += (_) => ComputeStats();
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
      maxRestoreText.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; ;
    }

    internal List<Fight> GetFights(bool selected = false)
    {
      if (npcWindow?.Content is FightTable table)
      {
        return selected ? table.GetSelectedFights() : table.GetFights();
      }

      return [];
    }

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
        (playerParseTextWindow.Content as ParsePreview)?.CopyToEqClick(type);
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
        IsDamageOverlayOpen = false;

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
        _damageOverlay = new DamageOverlayWindow(true);
        _damageOverlay.Show();
      }
      // delay opening overlay so group IDs get populated
      else if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        if (DataManager.Instance.HasOverlayFights())
        {
          _damageOverlay?.Close();
          _damageOverlay = new DamageOverlayWindow(false, reset);
          _damageOverlay.Show();
          IsDamageOverlayOpen = true;
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
    private void ReportProblemClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault("http://github.com/kauffman12/EQLogParser/issues");
    private void ViewReleaseNotesClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault(App.ReleaseNotesUrl);
    private void OpenLogManager(object sender, RoutedEventArgs e) => new LogManagementWindow().ShowDialog();
    private void DockSiteCloseButtonClick(object sender, CloseButtonEventArgs e) => SyncFusionUtil.CloseTab(dockSite, e.TargetItem as ContentControl, _logWindows);
    private void DockSiteWindowClosing(object sender, WindowClosingEventArgs e) => SyncFusionUtil.CloseTab(dockSite, e.TargetItem as ContentControl, _logWindows);
    private void WindowClose(object sender, EventArgs e) => Close();
    private void ToggleMaterialDarkClick(object sender, RoutedEventArgs e) => MainActions.SetTheme("MaterialDark");
    private void ToggleMaterialLightClick(object sender, RoutedEventArgs e) => MainActions.SetTheme("MaterialLight");
    private void ToggleStartMinimizedClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("StartWithWindowMinimized", enableStartMinimizedIcon);
    private void ToggleHideSplashScreenClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("HideSplashScreen", enableHideSplashScreenIcon);
    private void ToggleAutoMonitorClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("AutoMonitor", enableAutoMonitorIcon);
    private void ToggleCheckUpdatesClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("CheckUpdatesAtStartup", checkUpdatesIcon);
    private void ToggleHardwareAccelClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("HardwareAcceleration", hardwareAccelIcon);
    private void ToggleExportFormattedCsvClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("ExportFormattedCsv", exportFormattedCsvIcon);
    private void ToggleHideOnMinimizeClick(object sender, RoutedEventArgs e) => MainActions.ToggleSetting("HideWindowOnMinimize", enableHideOnMinimizeIcon);
    private void LocationChangedEvent(object sender, EventArgs e) => SaveWindowSize();

    private void SaveTimerTick(object sender, EventArgs e)
    {
      // save once loaded but also if backup isnt trying to shutdown
      if (!_isStarting && Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
      {
        ConfigUtil.Save();
      }
    }

    private void ComputeStatsTick(object sender, EventArgs e)
    {
      if (!_isStarting)
      {
        ComputeStats();
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
          await TriggerManager.Instance.StopAsync();
          DataManager.Instance.EventsNewOverlayFight -= EventsNewOverlayFight;
          CloseDamageOverlay(false);
          break;
        case PowerModes.Resume:
          Log.Warn("Resume");
          _saveTimer?.Start();
          await TriggerManager.Instance.StartAsync();
          DataManager.Instance.ResetOverlayFights(true);
          OpenDamageOverlayIfEnabled(true, false);
          DataManager.Instance.EventsNewOverlayFight += EventsNewOverlayFight;
          break;
      }
    }

    private void EventsNewOverlayFight(object sender, Fight e)
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
        ChatManager.Instance.Init();
      }
      else
      {
        ChatManager.Instance.Stop();
      }
    }

    private void ToggleEmuParsingClick(object sender, RoutedEventArgs e)
    {
      IsEmuParsingEnabled = MainActions.ToggleSetting("EnableEmuParsing", emuParsingIcon);
    }

    private void ToggleMapSendToEqClick(object sender, RoutedEventArgs e)
    {
      IsMapSendToEqEnabled = MainActions.ToggleSetting("MapSendToEQAsCtrlC", enableMapSendToEQIcon);
    }

    private async void CreateBackupClick(object sender, RoutedEventArgs e)
    {
      await MainActions.CreateBackupAsync();
    }

    private void HandleChartUpdate(string key, DataPointEvent e)
    {
      var opened = SyncFusionUtil.GetOpenWindows(dockSite);
      if (opened.TryGetValue(key, out var value))
      {
        (value.Content as LineChart)?.HandleUpdateEvent(e);
      }
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
      if (npcWindow?.Content is FightTable table && table.GetSelectedFights() is { } fights && table.GetAllRanges() is { } allRanges)
      {
        var filtered = fights.OrderBy(npc => npc.Id);
        var opened = SyncFusionUtil.GetOpenWindows(dockSite);

        GenerateStatsOptions damageStatsOptions = new();
        damageStatsOptions.Npcs.AddRange(filtered);
        damageStatsOptions.AllRanges = allRanges;
        damageStatsOptions.MinSeconds = 0;
        Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageStatsOptions));

        GenerateStatsOptions healingStatsOptions = new();
        healingStatsOptions.Npcs.AddRange(filtered);
        healingStatsOptions.AllRanges = allRanges;
        healingStatsOptions.MinSeconds = 0;
        Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingStatsOptions));

        GenerateStatsOptions tankingStatsOptions = new();
        tankingStatsOptions.Npcs.AddRange(filtered);
        tankingStatsOptions.AllRanges = allRanges;
        tankingStatsOptions.MinSeconds = 0;

        if (opened.TryGetValue((tankingSummaryIcon.Tag as string)!, out var control) && control != null)
        {
          tankingStatsOptions.DamageType = ((TankingSummary)control.Content).DamageType;
        }

        Task.Run(() => TankingStatsManager.Instance.BuildTotalStats(tankingStatsOptions));
      }
    }

    private void RestoreButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (WindowState == WindowState.Maximized)
      {
        WindowState = WindowState.Normal;
        maxRestoreText.Text = "\uE922";
      }
      else
      {
        WindowState = WindowState.Maximized;
        maxRestoreText.Text = "\uE923";
      }
    }

    private void ButtonBorderMouseEnterRed(object sender, MouseEventArgs e)
    {
      if (sender is Border { } border)
      {
        border.Background = _redHoverBrush;
      }
    }

    private void ButtonBorderMouseEnter(object sender, MouseEventArgs e)
    {
      if (sender is Border { } border)
      {
        border.Background = _hoverBrush;
      }
    }

    private void ButtonBorderMouseLeave(object sender, MouseEventArgs e)
    {
      if (sender is Border { } border)
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

      if (string.IsNullOrEmpty(CurrentLogFile))
      {
        new MessageWindow("No Log File Opened. Nothing to Save.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
      else if (filtered.Count > 0)
      {
        MainActions.ExportFights(CurrentLogFile, filtered);
      }
      else
      {
        new MessageWindow("No Fights Selected. Nothing to Save.", Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
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

    private void ViewErrorLogClick(object sender, RoutedEventArgs e)
    {
      var appender = Log.Logger.Repository.GetAppenders().FirstOrDefault();
      if (appender is FileAppender fileAppender)
      {
        MainActions.OpenFileWithDefault("\"" + fileAppender.File + "\"");
      }
    }

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
        // close and clear old data
        CloseDamageOverlay(false);
        DataManager.Instance.ResetOverlayFights();
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
      IsAssassinateDamageEnabled = MainActions.UpdateDamageOption(enableAssassinateDamageIcon, "IncludeAssassinateDamage");
    }

    private void ToggleBaneDamageClick(object sender, RoutedEventArgs e)
    {
      IsBaneDamageEnabled = MainActions.UpdateDamageOption(enableBaneDamageIcon, "IncludeBaneDamage");
    }

    private void ToggleDamageShieldDamageClick(object sender, RoutedEventArgs e)
    {
      IsDamageShieldDamageEnabled = MainActions.UpdateDamageOption(enableDamageShieldDamageIcon, "IncludeDamageShieldDamage");
    }

    private void ToggleFinishingBlowDamageClick(object sender, RoutedEventArgs e)
    {
      IsFinishingBlowDamageEnabled = MainActions.UpdateDamageOption(enableFinishingBlowDamageIcon, "IncludeFinishingBlowDamage");
    }

    private void ToggleHeadshotDamageClick(object sender, RoutedEventArgs e)
    {
      IsHeadshotDamageEnabled = MainActions.UpdateDamageOption(enableHeadshotDamageIcon, "IncludeHeadshotDamage");
    }

    private void ToggleSlayUndeadDamageClick(object sender, RoutedEventArgs e)
    {
      IsSlayUndeadDamageEnabled = MainActions.UpdateDamageOption(enableSlayUndeadDamageIcon, "IncludeSlayUndeadDamage");
    }

    private void ToggleAoEHealingClick(object sender, RoutedEventArgs e)
    {
      IsAoEHealingEnabled = MainActions.UpdateHealingOption(enableAoEHealingIcon, "IncludeAoEHealing");
    }

    private void ToggleHealingSwarmPetsClick(object sender, RoutedEventArgs e)
    {
      IsHealingSwarmPetsEnabled = MainActions.UpdateHealingOption(enableHealingSwarmPetsIcon, "IncludeHealingSwarmPets");
    }

    // Main Menu
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
      else if (sender as MenuItem is { Icon: ImageAwesome { Tag: string name2 } })
      {
        SyncFusionUtil.ToggleWindow(dockSite, name2);
      }
    }

    private void DamageSummarySelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      DamageStatsManager.Instance.FireChartEvent("SELECT", data.Selected);
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(Labels.DamageParse, data.Selected);
    }

    private void HealingSummarySelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      HealingStatsManager.Instance.FireChartEvent("SELECT", data.Selected);
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
      if (sender is MenuItem menuItem)
      {
        MainActions.UpdateCheckedMenuItem(menuItem, (menuItem.Parent as MenuItem)?.Items);
        MainActions.ChangeThemeFontFamily(menuItem.Header as string);
      }
    }

    private static void MenuItemFontSizeClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem)
      {
        MainActions.UpdateCheckedMenuItem(menuItem, (menuItem.Parent as MenuItem)?.Items);
        MainActions.ChangeThemeFontSizes((double)menuItem.Tag);
      }
    }

    private void MenuItemSelectLogFileClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem item)
      {
        var lastMin = -1;
        string fileName = null;
        if (!string.IsNullOrEmpty(item.Tag as string))
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
          statusText.Text = filePercent < 100.0 ? $"Reading Log.. {filePercent}% in {seconds} seconds" : $"Additional Processing... {seconds} seconds";
          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (filePercent >= 100)
          {
            statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
            statusText.Text = "Monitoring Log";

            ConfigUtil.SetSetting("LastOpenedFile", CurrentLogFile);
            Log.Info($"Finished Loading Log File in {seconds} seconds.");
            ConfigUtil.UpdateStatus("Monitoring Last Log");

            await Task.Delay(500);
            MainActions.FireLoadingEvent(CurrentLogFile);
            _isStarting = false;
            await Dispatcher.InvokeAsync(() =>
            {
              DataManager.Instance.ResetOverlayFights(true);
              OpenDamageOverlayIfEnabled(true, false);
              DataManager.Instance.EventsNewOverlayFight += EventsNewOverlayFight;
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

    private void PlayerClassDropDownSelectionChanged(object sender, CurrentCellDropDownSelectionChangedEventArgs e)
    {
      if (sender is SfDataGrid dataGrid && e.RowColumnIndex.RowIndex > 0 && dataGrid.View.GetRecordAt(e.RowColumnIndex.RowIndex - 1).Data is ExpandoObject obj)
      {
        dataGrid.SelectionController.CurrentCellManager.EndEdit();
        PlayerManager.Instance.SetPlayerClass(((dynamic)obj).Name, ((dynamic)obj).PlayerClass, "Class selected by user.");
      }
    }

    private void PetMappingDropDownSelectionChanged(object sender, CurrentCellDropDownSelectionChangedEventArgs e)
    {
      if (sender is SfDataGrid dataGrid && e.RowColumnIndex.RowIndex > 0 && dataGrid.View.GetRecordAt(e.RowColumnIndex.RowIndex - 1).Data is PetMapping mapping)
      {
        dataGrid.SelectionController.CurrentCellManager.EndEdit();
        PlayerManager.Instance.AddPetToPlayer(mapping.Pet, mapping.Owner);
      }
    }

    private void OpenLogFile(string previousFile, int lastMins)
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
          var initialPath = string.IsNullOrEmpty(CurrentLogFile) ? string.Empty : Path.GetDirectoryName(CurrentLogFile);

          var dialog = new CommonOpenFileDialog
          {
            // Set to false because we're opening a file, not selecting a folder
            IsFolderPicker = false,
            // Set the initial directory
            InitialDirectory = initialPath ?? "",
          };

          // Show dialog and read result
          dialog.Filters.Add(new CommonFileDialogFilter("eqlog_Player_server", "*.txt;*.gz;*.log"));

          if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
          {
            theFile = dialog.FileName; // Get the selected file name
          }
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

            _eqLogReader?.Dispose();
            fileText.Text = $"-- {theFile}";
            _startLoadTime = DateTime.Now;

            if (changed)
            {
              // save before switching
              if (!string.IsNullOrEmpty(ConfigUtil.ServerName))
              {
                PlayerManager.Instance.Save();
              }
            }

            ConfigUtil.ServerName = server;
            ConfigUtil.PlayerName = name;

            if (changed)
            {
              // update pet/player windows all at once
              MainActions.Clear(verifiedPetsWindow, verifiedPlayersWindow, petMappingWindow);
              PlayerManager.Instance.Init();
              MainActions.LoadVerified(verifiedPlayersWindow, verifiedPetsWindow, PlayerManager.Instance.GetVerifiedPlayers(),
                PlayerManager.Instance.GetVerifiedPets());
              MainActions.LoadPetOwners(petMappingWindow, PlayerManager.Instance.GetPetMappings());
            }

            _recentFiles.Remove(theFile);
            _recentFiles.Insert(0, theFile);
            ConfigUtil.SetSetting("RecentFiles", string.Join(",", _recentFiles));
            UpdateRecentFiles();

            DataManager.Instance.EventsNewOverlayFight -= EventsNewOverlayFight;
            CloseDamageOverlay(false);
            DataManager.Instance.Clear();
            RecordManager.Instance.Clear();
            CurrentLogFile = theFile;
            _npcDamageManager.Reset();
            _eqLogReader = new LogReader(new LogProcessor(theFile), theFile, lastMins);
            _eqLogReader.Start();
            UpdateLoadingProgress();
          }, DispatcherPriority.Render);
        }
      }
      catch (Exception e)
      {
        if (e is not (InvalidCastException or ArgumentException or FormatException))
        {
          throw;
        }

        Log.Error("Problem During Initialization", e);
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
        if (icon.Tag is string name)
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
          icon.Visibility = MainActions.CurrentTheme == "MaterialDark" ? Visibility.Visible : Visibility.Hidden;
        }
        else if (icon == themeLightIcon)
        {
          icon.Visibility = MainActions.CurrentTheme == "MaterialLight" ? Visibility.Visible : Visibility.Hidden;
        }
      }
    }

    private void PlayerCellToolTipOpening(object sender, GridCellToolTipOpeningEventArgs e)
    {
      if (e.Record is ExpandoObject)
      {
        var data = (dynamic)e.Record;
        e.ToolTip.Content = PlayerManager.Instance.GetPlayerClassReason(data.Name);
      }

      e.ToolTip.FontSize = 13;
    }

    private void RemovePetMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is Border { DataContext: ExpandoObject sortable })
      {
        PlayerManager.Instance.RemoveVerifiedPet(((dynamic)sortable).Name);
      }
    }

    private void RemovePlayerMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is Border { DataContext: ExpandoObject sortable })
      {
        PlayerManager.Instance.RemoveVerifiedPlayer(((dynamic)sortable).Name);
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
        PlayerManager.Instance?.Save();
      }

      PlayerManager.Instance?.Stop();
      _saveTimer?.Stop();
      _eqLogReader?.Dispose();
      _notifyIcon?.Dispose();
      petMappingGrid?.Dispose();
      verifiedPetsGrid?.Dispose();
      verifiedPlayersGrid?.Dispose();
      RecordManager.Instance.Stop();
      ChatManager.Instance.Stop();

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
      }
    }
  }
}

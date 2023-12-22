using FontAwesome5;
using log4net;
using log4net.Appender;
using Microsoft.Win32;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.Windows.Shared;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace EQLogParser
{
  public partial class MainWindow : ChromelessWindow
  {
    // global settings
    internal static string CurrentLogFile = null;
    internal static bool IsAoEHealingEnabled = true;
    internal static bool IsHealingSwarmPetsEnabled = true;
    internal static bool IsAssassinateDamageEnabled = true;
    internal static bool IsBaneDamageEnabled = true;
    internal static bool IsDamageShieldDamageEnabled = true;
    internal static bool IsFinishingBlowDamageEnabled = true;
    internal static bool IsHeadshotDamageEnabled = true;
    internal static bool IsSlayUndeadDamageEnabled = true;
    internal static bool IsHideOnMinimizeEnabled;
    internal static bool IsMapSendToEqEnabled;
    internal const int ActionIndex = 27;
    internal static string CurrentTheme;
    internal static string CurrentFontFamily;
    internal static double CurrentFontSize;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // progress window
    private static DateTime _startLoadTime;
    private DamageOverlayWindow _damageOverlay;
    private readonly DispatcherTimer _computeStatsTimer;
    private readonly NpcDamageManager _npcDamageManager = new();
    private LogReader _eqLogReader;
    private readonly List<bool> _logWindows = new();
    private readonly List<string> _recentFiles = new();
    private bool _resetWindowState;

    public MainWindow()
    {
      try
      {
        // load theme and fonts
        CurrentFontFamily = ConfigUtil.GetSetting("ApplicationFontFamily", "Segoe UI");
        CurrentFontSize = ConfigUtil.GetSettingAsDouble("ApplicationFontSize", 12);
        CurrentTheme = ConfigUtil.GetSetting("CurrentTheme", "MaterialDark");

        if (UiElementUtil.GetSystemFontFamilies().FirstOrDefault(font => font.Source == CurrentFontFamily) == null)
        {
          Log.Info(CurrentFontFamily + " Not Found, Trying Default");
          CurrentFontFamily = "Segoe UI";
        }

        Application.Current.Resources["EQChatFontSize"] = 16.0; // changed when chat archive loads
        Application.Current.Resources["EQChatFontFamily"] = new FontFamily("Segoe UI");
        Application.Current.Resources["EQLogFontSize"] = 16.0; // changed when chat archive loads
        Application.Current.Resources["EQLogFontFamily"] = new FontFamily("Segoe UI");
        MainActions.LoadTheme(this, CurrentTheme);

        // DPI and sizing
        var defaultHeight = SystemParameters.PrimaryScreenHeight * 0.75;
        var defaultWidth = SystemParameters.PrimaryScreenWidth * 0.85;
        Height = ConfigUtil.GetSettingAsDouble("WindowHeight", defaultHeight);
        Width = ConfigUtil.GetSettingAsDouble("WindowWidth", defaultWidth);

        var top = ConfigUtil.GetSettingAsDouble("WindowTop", double.NaN);
        var left = ConfigUtil.GetSettingAsDouble("WindowLeft", double.NaN);

        if ((!double.IsNaN(top) && top < 0) || (!double.IsNaN(left) && left < 0))
        {
          top = 0;
          left = 0;
        }

        Top = top;
        Left = left;

        Log.Info($"Window Pos ({Top}, {Left})");
        Log.Info($"Window Size ({Width}, {Height})");

        WindowState = ConfigUtil.GetSetting("WindowState", "Normal") switch
        {
          "Maximized" => WindowState.Maximized,
          _ => WindowState.Normal
        };

        InitializeComponent();

        // add tabs to the right
        ((DocumentContainer)dockSite.DocContainer).AddTabDocumentAtLast = true;

        // update titles
        versionText.Text = "v" + Application.ResourceAssembly.GetName().Version!.ToString()[..^2];

        MainActions.InitPetOwners(this, petMappingGrid, ownerList, petMappingWindow);
        MainActions.InitVerifiedPlayers(this, verifiedPlayersGrid, classList, verifiedPlayersWindow, petMappingWindow);
        MainActions.InitVerifiedPets(this, verifiedPetsGrid, verifiedPetsWindow, petMappingWindow);

        MainActions.EventsFightSelectionChanged += (_) => ComputeStats();
        DamageStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(damageChartIcon.Tag as string, data));
        HealingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(healingChartIcon.Tag as string, data));
        TankingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(tankingChartIcon.Tag as string, data));
        MainActions.EventsDamageSelectionChanged += DamageSummary_SelectionChanged;
        MainActions.EventsHealingSelectionChanged += HealingSummary_SelectionChanged;
        MainActions.EventsTankingSelectionChanged += TankingSummary_SelectionChanged;

        UpdateDeleteChatMenu();

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
        IsHideOnMinimizeEnabled = ConfigUtil.IfSet("HideWindowOnMinimize");
        enableHideOnMinimizeIcon.Visibility = IsHideOnMinimizeEnabled ? Visibility.Visible : Visibility.Hidden;

        // Allow Ctrl+C for SendToEQ
        IsMapSendToEqEnabled = ConfigUtil.IfSet("MapSendToEQAsCtrlC");
        enableMapSendToEQIcon.Visibility = IsMapSendToEqEnabled ? Visibility.Visible : Visibility.Hidden;

        // Damage Overlay
        enableDamageOverlayIcon.Visibility = ConfigUtil.IfSet("IsDamageOverlayEnabled") ? Visibility.Visible : Visibility.Hidden;
        enableDamageOverlay.Header = ConfigUtil.IfSet("IsDamageOverlayEnabled") ? "Disable _Meter" : "Enable _Meter";

        // create menu items for reading log files
        MainActions.CreateOpenLogMenuItems(fileOpenMenu, MenuItemSelectLogFileClick);

        // create font families menu items
        MainActions.CreateFontFamiliesMenuItems(appFontFamilies, MenuItemFontFamilyClicked, CurrentFontFamily);

        // crate f ont sizes menu items
        MainActions.CreateFontSizesMenuItems(appFontSizes, MenuItemFontSizeClicked, CurrentFontSize);

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

        Log.Info("Initialized Components");
        if (ConfigUtil.IfSet("CheckUpdatesAtStartup"))
        {
          // check version
          checkUpdatesIcon.Visibility = Visibility.Visible;
          MainActions.CheckVersion(errorText);
        }
        else
        {
          checkUpdatesIcon.Visibility = Visibility.Hidden;
        }

        if (ConfigUtil.IfSet("AutoMonitor"))
        {
          enableAutoMonitorIcon.Visibility = Visibility.Visible;
          var previousFile = ConfigUtil.GetSetting("LastOpenedFile");
          if (File.Exists(previousFile))
          {
            OpenLogFile(previousFile, 0);
          }
        }
        else
        {
          enableAutoMonitorIcon.Visibility = Visibility.Hidden;
        }

        _computeStatsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 500) };
        _computeStatsTimer.Tick += (_, _) =>
        {
          ComputeStats();
          _computeStatsTimer.Stop();
        };

        SystemEvents.PowerModeChanged += SystemEventsPowerModeChanged;

        // data old stuff
        if (ConfigUtil.IfSet("TriggersWatchForGINA"))
        {
          ConfigUtil.SetSetting("TriggersWatchForQuickShare", true);
        }

        // upgrade
        if (ConfigUtil.IfSet("OverlayShowCritRate"))
        {
          ConfigUtil.SetSetting("OverlayEnableCritRate", "3");
        }

        // Init Trigger Manager
        TriggerManager.Instance.Start();

        // load document state
        DockingManager.SetState(petMappingWindow, DockState.AutoHidden);
        MainActions.AddDocumentWindows(dockSite);

        // make sure file exists
        if (File.Exists(ConfigUtil.ConfigDir + "/dockSite.xml"))
        {
          try
          {
            dockSite.LoadDockState(new BinaryFormatter(), StorageFormat.Xml, ConfigUtil.ConfigDir + "/dockSite.xml");
          }
          catch (Exception ex)
          {
            Log.Debug("Error reading docSite.xml", ex);
            dockSite.ResetState();
          }
        }

        // not used anymore. time to cleanup
        Dispatcher.Invoke(() =>
        {
          ConfigUtil.RemoveSetting("AudioTriggersWatchForGINA");
          ConfigUtil.RemoveSetting("TriggersWatchForGINA");
          ConfigUtil.RemoveSetting("AudioTriggersEnabled");
          ConfigUtil.RemoveSetting("OverlayRankColor1");
          ConfigUtil.RemoveSetting("OverlayRankColor2");
          ConfigUtil.RemoveSetting("OverlayRankColor3");
          ConfigUtil.RemoveSetting("OverlayRankColor4");
          ConfigUtil.RemoveSetting("OverlayRankColor5");
          ConfigUtil.RemoveSetting("OverlayRankColor6");
          ConfigUtil.RemoveSetting("OverlayRankColor7");
          ConfigUtil.RemoveSetting("OverlayRankColor8");
          ConfigUtil.RemoveSetting("OverlayRankColor9");
          ConfigUtil.RemoveSetting("OverlayRankColor10");
          ConfigUtil.RemoveSetting("OverlayShowCritRate");
          ConfigUtil.RemoveSetting("EnableHardwareAcceleration");
          ConfigUtil.RemoveSetting("TriggersVoiceRate");
          ConfigUtil.RemoveSetting("TriggersSelectedVoice");
          ConfigUtil.RemoveSetting("ShowDamageSummaryAtStartup");
          ConfigUtil.RemoveSetting("ShowHealingSummaryAtStartup");
          ConfigUtil.RemoveSetting("ShowTankingSummaryAtStartup");
        }, DispatcherPriority.Background);

        // cleanup downloads
        Dispatcher.InvokeAsync(MainActions.Cleanup, DispatcherPriority.Background);
      }
      catch (Exception e)
      {
        Log.Error(e);
        throw;
      }
    }

    private void SystemEventsPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
      switch (e.Mode)
      {
        case PowerModes.Suspend:
          Log.Warn("Suspending");
          TriggerManager.Instance.Stop();
          DataManager.Instance.EventsNewOverlayFight -= EventsNewOverlayFight;
          CloseDamageOverlay();
          break;
        case PowerModes.Resume:
          Log.Warn("Resume");
          TriggerManager.Instance.Start();
          DataManager.Instance.ResetOverlayFights(true);
          OpenDamageOverlayIfEnabled(true, false);
          DataManager.Instance.EventsNewOverlayFight += EventsNewOverlayFight;
          break;
      }
    }

    private void EventsNewOverlayFight(object sender, Fight e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (_damageOverlay == null)
        {
          OpenDamageOverlayIfEnabled(false, false);
        }
      });
    }

    internal void CopyToEqClick(string type) => (playerParseTextWindow.Content as ParsePreview)?.CopyToEqClick(type);
    internal FightTable GetFightTable() => npcWindow?.Content as FightTable;
    private void RestoreTableColumnsClick(object sender, RoutedEventArgs e) => DataGridUtil.RestoreAllTableColumns();
    private void AboutClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault("http://github.com/kauffman12/EQLogParser/#readme");
    private void OpenSoundsFolderClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault("\"" + @"data\sounds" + "\"");
    private void ReportProblemClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault("http://github.com/kauffman12/EQLogParser/issues");
    private void ViewReleaseNotesClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault(@"data\releasenotes.rtf");
    private void TriggerVariablesHelpClick(object sender, RoutedEventArgs e) => MainActions.OpenFileWithDefault(@"data\triggerVariables.rtf");

    internal void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.DamageParse, DamageStatsManager.Instance, combined, selected, true);
    }

    internal void AddAndCopyTankParse(CombinedStats combined, List<PlayerStats> selected)
    {
      (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.TankParse, TankingStatsManager.Instance, combined, selected, true);
    }

    internal void DisableTriggers()
    {
      Dispatcher.InvokeAsync(() =>
      {
        triggersMenuItem.IsEnabled = false;
        triggerTestMenuItem.IsEnabled = false;
        triggerLogMenuItem.IsEnabled = false;
        quickShareLogMenuItem.IsEnabled = false;
      });
    }

    internal void ShowTriggersEnabled(bool active)
    {
      Dispatcher.InvokeAsync(() => statusTriggersText.Visibility = active ? Visibility.Visible : Visibility.Collapsed);
    }

    internal void CloseDamageOverlay()
    {
      _damageOverlay?.Close();
      _damageOverlay = null;
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
          if (_damageOverlay != null)
          {
            _damageOverlay?.Close();
          }

          _damageOverlay = new DamageOverlayWindow(false, reset);
          _damageOverlay.Show();
        }
      }
    }

    private void HandleChartUpdate(string key, DataPointEvent e)
    {
      var opened = SyncFusionUtil.GetOpenWindows(dockSite);
      if (opened.TryGetValue(key, out var value))
      {
        (value.Content as LineChart)?.HandleUpdateEvent(e);
      }
    }

    private void UpdateDeleteChatMenu()
    {
      deleteChat.Items.Clear();
      ChatManager.Instance.GetArchivedPlayers().ForEach(player =>
      {
        var item = new MenuItem { IsEnabled = true, Header = player };
        deleteChat.Items.Add(item);

        item.Click += (_, _) =>
        {
          var msgDialog = new MessageWindow($"Clear Chat Archive for {player}?", Resource.CLEAR_CHAT,
            MessageWindow.IconType.Warn, "Yes");
          msgDialog.ShowDialog();

          if (msgDialog.IsYes1Clicked)
          {
            if (!ChatManager.Instance.DeleteArchivedPlayer(player))
            {
              deleteChat.Items.Remove(item);
              deleteChat.IsEnabled = deleteChat.Items.Count > 0;
            }
          }
        };
      });

      deleteChat.IsEnabled = deleteChat.Items.Count > 0;
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
      var filtered = MainActions.GetSelectedFights().OrderBy(npc => npc.Id);
      var opened = SyncFusionUtil.GetOpenWindows(dockSite);

      var damageStatsOptions = new GenerateStatsOptions();
      damageStatsOptions.Npcs.AddRange(filtered);
      Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageStatsOptions));

      var healingStatsOptions = new GenerateStatsOptions();
      healingStatsOptions.Npcs.AddRange(filtered);
      Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingStatsOptions));

      var tankingStatsOptions = new GenerateStatsOptions();
      tankingStatsOptions.Npcs.AddRange(filtered);
      if (opened.TryGetValue((tankingSummaryIcon.Tag as string)!, out var control) && control != null)
      {
        tankingStatsOptions.DamageType = ((TankingSummary)control.Content).DamageType;
      }
      Task.Run(() => TankingStatsManager.Instance.BuildTotalStats(tankingStatsOptions));
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
      var filtered = MainActions.GetSelectedFights().OrderBy(npc => npc.Id).ToList();

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
      dockSite.DeleteDockState(ConfigUtil.ConfigDir + "/dockSite.xml");
      _resetWindowState = true;
      new MessageWindow("Window State will be reset after application restart.", Resource.RESET_WINDOW_STATE).ShowDialog();
    }

    private void ViewErrorLogClick(object sender, RoutedEventArgs e)
    {
      if (Log.Logger.Repository.GetAppenders().FirstOrDefault() is { } appender)
      {
        MainActions.OpenFileWithDefault("\"" + ((FileAppender)appender).File + "\"");
      }
    }

    private void ToggleHideOnMinimizeClick(object sender, RoutedEventArgs e)
    {
      IsHideOnMinimizeEnabled = !IsHideOnMinimizeEnabled;
      ConfigUtil.SetSetting("HideWindowOnMinimize", IsHideOnMinimizeEnabled);
      enableHideOnMinimizeIcon.Visibility = IsHideOnMinimizeEnabled ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleAoEHealingClick(object sender, RoutedEventArgs e)
    {
      IsAoEHealingEnabled = !IsAoEHealingEnabled;
      ConfigUtil.SetSetting("IncludeAoEHealing", IsAoEHealingEnabled);
      enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;
      Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats());
    }

    private void ToggleHealingSwarmPetsClick(object sender, RoutedEventArgs e)
    {
      IsHealingSwarmPetsEnabled = !IsHealingSwarmPetsEnabled;
      ConfigUtil.SetSetting("IncludeHealingSwarmPets", IsHealingSwarmPetsEnabled);
      enableHealingSwarmPetsIcon.Visibility = IsHealingSwarmPetsEnabled ? Visibility.Visible : Visibility.Hidden;
      Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats());
    }

    private void ToggleAutoMonitorClick(object sender, RoutedEventArgs e)
    {
      ConfigUtil.SetSetting("AutoMonitor", enableAutoMonitorIcon.Visibility == Visibility.Hidden);
      enableAutoMonitorIcon.Visibility = enableAutoMonitorIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
      ConfigUtil.SetSetting("CheckUpdatesAtStartup", checkUpdatesIcon.Visibility == Visibility.Hidden);
      checkUpdatesIcon.Visibility = checkUpdatesIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleMapSendToEqClick(object sender, RoutedEventArgs e)
    {
      IsMapSendToEqEnabled = !IsMapSendToEqEnabled;
      ConfigUtil.SetSetting("MapSendToEQAsCtrlC", enableMapSendToEQIcon.Visibility == Visibility.Hidden);
      enableMapSendToEQIcon.Visibility = enableMapSendToEQIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleDamageOverlayClick(object sender, RoutedEventArgs e)
    {
      enableDamageOverlayIcon.Visibility = enableDamageOverlayIcon.Visibility == Visibility.Hidden ? Visibility.Visible : Visibility.Hidden;
      var enabled = enableDamageOverlayIcon.Visibility == Visibility.Visible;
      ConfigUtil.SetSetting("IsDamageOverlayEnabled", enabled);

      if (enabled)
      {
        OpenDamageOverlayIfEnabled(false, false);
      }

      enableDamageOverlay.Header = enabled ? "Disable _Meter" : "Enable _Meter";
    }

    private void ConfigureOverlayClick(object sender, RoutedEventArgs e)
    {
      CloseDamageOverlay();
      OpenDamageOverlayIfEnabled(false, true);
    }

    private void ResetOverlayClick(object sender, RoutedEventArgs e)
    {
      CloseDamageOverlay();
      ConfigUtil.SetSetting("OverlayTop", "");
      ConfigUtil.SetSetting("OverlayLeft", "");
      OpenDamageOverlayIfEnabled(false, true);
    }

    private void ToggleAssassinateDamageClick(object sender, RoutedEventArgs e)
    {
      IsAssassinateDamageEnabled = !IsAssassinateDamageEnabled;
      UpdateDamageOption(enableAssassinateDamageIcon, IsAssassinateDamageEnabled, "IncludeAssassinateDamage");
    }

    private void ToggleBaneDamageClick(object sender, RoutedEventArgs e)
    {
      IsBaneDamageEnabled = !IsBaneDamageEnabled;
      UpdateDamageOption(enableBaneDamageIcon, IsBaneDamageEnabled, "IncludeBaneDamage");
    }

    private void ToggleDamageShieldDamageClick(object sender, RoutedEventArgs e)
    {
      IsDamageShieldDamageEnabled = !IsDamageShieldDamageEnabled;
      UpdateDamageOption(enableDamageShieldDamageIcon, IsDamageShieldDamageEnabled, "IncludeDamageShieldDamage");
    }

    private void ToggleFinishingBlowDamageClick(object sender, RoutedEventArgs e)
    {
      IsFinishingBlowDamageEnabled = !IsFinishingBlowDamageEnabled;
      UpdateDamageOption(enableFinishingBlowDamageIcon, IsFinishingBlowDamageEnabled, "IncludeFinishingBlowDamage");
    }

    private void ToggleHeadshotDamageClick(object sender, RoutedEventArgs e)
    {
      IsHeadshotDamageEnabled = !IsHeadshotDamageEnabled;
      UpdateDamageOption(enableHeadshotDamageIcon, IsHeadshotDamageEnabled, "IncludeHeadshotDamage");
    }

    private void ToggleSlayUndeadDamageClick(object sender, RoutedEventArgs e)
    {
      IsSlayUndeadDamageEnabled = !IsSlayUndeadDamageEnabled;
      UpdateDamageOption(enableSlayUndeadDamageIcon, IsSlayUndeadDamageEnabled, "IncludeSlayUndeadDamage");
    }

    private void ToggleMaterialDarkClick(object sender, RoutedEventArgs e)
    {
      if (CurrentTheme != "MaterialDark")
      {
        CurrentTheme = "MaterialDark";
        MainActions.LoadTheme(this, CurrentTheme);
        ConfigUtil.SetSetting("CurrentTheme", CurrentTheme);
      }
    }

    private void ToggleMaterialLightClick(object sender, RoutedEventArgs e)
    {
      if (CurrentTheme != "MaterialLight")
      {
        CurrentTheme = "MaterialLight";
        MainActions.LoadTheme(this, CurrentTheme);
        ConfigUtil.SetSetting("CurrentTheme", CurrentTheme);
      }
    }

    private void UpdateDamageOption(ImageAwesome icon, bool enabled, string option)
    {
      ConfigUtil.SetSetting(option, enabled);
      icon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
      var options = new GenerateStatsOptions();
      Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(options));
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

        SyncFusionUtil.OpenWindow(dockSite, null, out _, typeof(EqLogViewer), "eqLogWindow", "Log Search " + found);
      }
      else if (sender as MenuItem is { Icon: ImageAwesome { Tag: string name } })
      {
        SyncFusionUtil.ToggleWindow(dockSite, name);
      }
    }

    private void DamageSummary_SelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "SELECT", data.Selected);
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(Labels.DamageParse, data.Selected);
    }

    private void HealingSummary_SelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      HealingStatsManager.Instance.FireChartEvent("SELECT", data.Selected);
      var addTopParse = data.Selected?.Count == 1 && data.Selected[0].SubStats?.Count > 0;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(data, HealingStatsManager.Instance, addTopParse, Labels.HealParse, Labels.TopHealParse);
    }

    private void TankingSummary_SelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "SELECT", data.Selected);
      var addReceiveParse = data.Selected?.Count == 1 && data.Selected[0].MoreStats != null;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(data, TankingStatsManager.Instance, addReceiveParse, Labels.TankParse, Labels.ReceivedHealParse);
    }

    private void MenuItemFontFamilyClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem)
      {
        MainActions.UpdateCheckedMenuItem(menuItem, (menuItem.Parent as MenuItem)?.Items);
        CurrentFontFamily = menuItem.Header as string;
        ConfigUtil.SetSetting("ApplicationFontFamily", CurrentFontFamily);
        MainActions.LoadTheme(this, CurrentTheme);
      }
    }

    private void MenuItemFontSizeClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem)
      {
        MainActions.UpdateCheckedMenuItem(menuItem, (menuItem.Parent as MenuItem)?.Items);
        CurrentFontSize = (double)menuItem.Tag;
        ConfigUtil.SetSetting("ApplicationFontSize", CurrentFontSize);
        MainActions.LoadTheme(this, CurrentTheme);
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
      Dispatcher.InvokeAsync(() =>
      {
        if (_eqLogReader != null)
        {
          var seconds = Math.Round((DateTime.Now - _startLoadTime).TotalSeconds);
          var filePercent = Math.Round(_eqLogReader.Progress);
          statusText.Text = filePercent < 100.0 ? $"Reading Log.. {filePercent}% in {seconds} seconds" : $"Additional Processing... {seconds} seconds";
          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (filePercent >= 100)
          {
            statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
            statusText.Text = "Monitoring Active";

            ConfigUtil.SetSetting("LastOpenedFile", CurrentLogFile);
            Log.Info($"Finished Loading Log File in {seconds} seconds.");

            Task.Delay(1000).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
            {
              MainActions.FireLoadingEvent(CurrentLogFile);
              Dispatcher.InvokeAsync(() =>
              {
                DataManager.Instance.ResetOverlayFights(true);
                OpenDamageOverlayIfEnabled(true, false);
                DataManager.Instance.EventsNewOverlayFight += EventsNewOverlayFight;
              }, DispatcherPriority.Background);
            }));
          }
          else
          {
            Task.Delay(500).ContinueWith(_ => UpdateLoadingProgress(), TaskScheduler.Default);
          }
        }
      });
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
        string theFile;
        var success = true;
        if (previousFile != null)
        {
          theFile = previousFile;
        }
        else
        {
          // WPF doesn't have its own file chooser so use Win32 Version
          var dialog = new OpenFileDialog
          {
            // filter to txt files
            DefaultExt = ".txt",
            Filter = "eqlog_Player_server (.txt .txt.gz)|*.txt;*.txt.gz",
          };

          // show dialog and read result
          success = dialog.ShowDialog() == true;
          theFile = dialog.FileName;
        }

        if (success)
        {
          Dispatcher.InvokeAsync(() =>
          {
            if (DockingManager.GetState(npcWindow) == DockState.Hidden)
            {
              DockingManager.SetState(npcWindow, DockState.Dock);
            }

            _eqLogReader?.Dispose();
            fileText.Text = $"-- {theFile}";
            _startLoadTime = DateTime.Now;

            var name = "You";
            var server = "Unknown";
            if (theFile.Length > 0)
            {
              Log.Info("Selected Log File: " + theFile);
              FileUtil.ParseFileName(theFile, ref name, ref server);
            }

            var changed = ConfigUtil.ServerName != server;
            if (changed)
            {
              MainActions.Clear(verifiedPetsWindow, verifiedPlayersWindow);

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
              PlayerManager.Instance.Init();
            }

            if (_recentFiles.Contains(theFile))
            {
              _recentFiles.Remove(theFile);
            }

            _recentFiles.Insert(0, theFile);
            ConfigUtil.SetSetting("RecentFiles", string.Join(",", _recentFiles));
            UpdateRecentFiles();

            DataManager.Instance.EventsNewOverlayFight -= EventsNewOverlayFight;
            CloseDamageOverlay();
            DataManager.Instance.Clear();
            RecordManager.Instance.Clear();
            CurrentLogFile = theFile;
            _npcDamageManager.Reset();
            _eqLogReader = new LogReader(new LogProcessor(theFile), theFile, lastMins);
            UpdateLoadingProgress();
          });
        }
      }
      catch (Exception e)
      {
        if (!(e is InvalidCastException || e is ArgumentException || e is FormatException))
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
          icon.Visibility = CurrentTheme == "MaterialDark" ? Visibility.Visible : Visibility.Hidden;
        }
        else if (icon == themeLightIcon)
        {
          icon.Visibility = CurrentTheme == "MaterialLight" ? Visibility.Visible : Visibility.Hidden;
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

    private void NotifyIconClick(object sender, EventArgs e)
    {
      if (Visibility == Visibility.Hidden)
      {
        Show();
      }

      Activate();
      if (WindowState == WindowState.Minimized)
      {
        WindowState = WindowState.Normal;
      }
    }

    private void WindowStateChanged(object sender, EventArgs e)
    {
      if (WindowState == WindowState.Minimized)
      {
        if (IsHideOnMinimizeEnabled && Visibility != Visibility.Hidden)
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
    }

    private void MainWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (WindowState == WindowState.Normal)
      {
        ConfigUtil.SetSetting("WindowLeft", Left);
        ConfigUtil.SetSetting("WindowTop", Top);
        ConfigUtil.SetSetting("WindowHeight", Height);
        ConfigUtil.SetSetting("WindowWidth", Width);
      }
    }

    private void WindowClosing(object sender, EventArgs e)
    {
      ConfigUtil.SetSetting("WindowState", WindowState.ToString());

      if (!_resetWindowState)
      {
        dockSite.SaveDockState(new BinaryFormatter(), StorageFormat.Xml, ConfigUtil.ConfigDir + "/dockSite.xml");
      }

      ConfigUtil.Save();
      _eqLogReader?.Dispose();
      petMappingGrid?.Dispose();
      verifiedPetsGrid?.Dispose();
      verifiedPlayersGrid?.Dispose();
      PlayerManager.Instance?.Save();
      RecordManager.Instance.Stop();
      ChatManager.Instance.Stop();
      TriggerManager.Instance.Stop();
      TriggerStateManager.Instance.Stop();
      Application.Current.Shutdown();
    }

    // This is where closing summary tables and line charts will get disposed
    private void CloseTab(ContentControl window)
    {
      if (window.Content is EqLogViewer)
      {
        if (DockingManager.GetHeader(window) is string title)
        {
          var last = title.LastIndexOf(" ", StringComparison.Ordinal);
          if (last > -1)
          {
            var value = title.Substring(last, title.Length - last);
            if (int.TryParse(value, out var result) && result > 0 && _logWindows.Count >= result)
            {
              _logWindows[result - 1] = false;
            }
          }
        }

        (window.Content as IDisposable)?.Dispose();
      }
      else
      {
        SyncFusionUtil.CloseWindow(dockSite, window);
      }
    }

    private void DockSiteCloseButtonClick(object sender, CloseButtonEventArgs e) => CloseTab(e.TargetItem as ContentControl);
    private void DockSiteWindowClosing(object sender, WindowClosingEventArgs e) => CloseTab(e.TargetItem as ContentControl);
    private void WindowClose(object sender, EventArgs e) => Close();

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);
      var source = (HwndSource)PresentationSource.FromVisual(this)!;
      source.AddHook(BandAidHook); // Make sure this is hooked first. That ensures it runs last
      source.AddHook(ProblemHook);
    }

    IntPtr ProblemHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == 0x000D) // WM_GETTEXT
      {
        Marshal.SetLastSystemError(122);
      }
      return IntPtr.Zero;
    }

    IntPtr BandAidHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == 0x000D) // WM_GETTEXT
      {
        Marshal.SetLastSystemError(0);
      }
      return IntPtr.Zero;
    }
  }
}

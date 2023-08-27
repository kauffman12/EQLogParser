using FontAwesome5;
using log4net;
using log4net.Core;
using Microsoft.Win32;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.Windows.Shared;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class MainWindow : ChromelessWindow, IDisposable
  {
    internal event EventHandler<bool> EventsLogLoadingComplete;
    internal event EventHandler<string> EventsThemeChanged;

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
    internal static bool IsHideOnMinimizeEnabled = false;
    internal static bool IsMapSendToEQEnabled = false;
    internal const int ACTION_INDEX = 27;
    internal static string CurrentTheme;
    internal static string CurrentFontFamily;
    internal static double CurrentFontSize;

    private static readonly ILog LOG = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Regex ParseFileName = new Regex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.txt", RegexOptions.Singleline);
    private static readonly List<string> DAMAGE_CHOICES = new List<string>()
    { "Aggregate DPS", "Aggregate Av Hit", "Aggregate Damage", "Aggregate Crit Rate", "Aggregate Twincast Rate", "DPS", "Rolling DPS", "Rolling Damage", "# Attempts", "# Crits", "# Hits", "# Twincasts" };
    private static readonly List<string> HEALING_CHOICES = new List<string>()
    { "Aggregate HPS", "Aggregate Av Heal", "Aggregate Healing", "Aggregate Crit Rate", "Aggregate Twincast Rate", "HPS", "Rolling HPS", "Rolling Healing", "# Crits", "# Heals", "# Twincasts" };
    private static readonly List<string> TANKING_CHOICES = new List<string>()
    { "Aggregate DPS", "Aggregate Av Hit", "Aggregate Damaged", "DPS", "Rolling DPS", "Rolling Damage", "# Attempts", "# Hits", "# Twincasts" };

    private static long LineCount = 0;
    private static long FilePosition = 0;
    private static ActionProcessor CastProcessor = null;
    private static ActionProcessor DamageProcessor = null;
    private static ActionProcessor HealingProcessor = null;
    private static ActionProcessor MiscProcessor = null;

    // progress window
    private static DateTime StartLoadTime;
    private DamageOverlayWindow DamageOverlay;
    private readonly DispatcherTimer ComputeStatsTimer;
    private ChatManager PlayerChatManager = null;
    private readonly NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private DocumentTabControl ChartTab = null;
    private LogReader EQLogReader = null;
    private List<bool> LogWindows = new List<bool>();
    private bool DoneLoading = false;

    private List<string> RecentFiles = new List<string>();

    public MainWindow()
    {
      try
      {
        var dotNetVersion = Environment.Version;
        LOG.Info("Using DotNet " + dotNetVersion); var domain = AppDomain.CurrentDomain;
        domain.UnhandledException += DomainUnhandledException;

        // DPI and sizing
        var dpi = UIElementUtil.GetDpi();
        var resolution = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        var defaultHeight = resolution.Height * 0.75 / dpi;
        var defaultWidth = resolution.Width * 0.85 / dpi;
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

        LOG.Info("Window Pos (" + Top + ", " + Left + ")");
        LOG.Info("Window Size (" + Width + ", " + Height + ")");

        switch (ConfigUtil.GetSetting("WindowState", "Normal"))
        {
          case "Maximized":
            WindowState = WindowState.Maximized;
            break;
          default:
            WindowState = WindowState.Normal;
            break;
        }

        // load theme and fonts
        CurrentFontFamily = ConfigUtil.GetSetting("ApplicationFontFamily", "Segoe UI");
        CurrentFontSize = ConfigUtil.GetSettingAsDouble("ApplicationFontSize", 12);
        CurrentTheme = ConfigUtil.GetSetting("CurrentTheme", "MaterialDark");

        if (Helpers.GetSystemFontFamilies().FirstOrDefault(font => font.Source == CurrentFontFamily) == null)
        {
          LOG.Info(CurrentFontFamily + " Not Found, Trying Default");
          CurrentFontFamily = "Segoe UI";
        }

        MainActions.LoadTheme(this, CurrentTheme);
        Application.Current.Resources["EQChatFontSize"] = 16.0; // changed when chat archive loads
        Application.Current.Resources["EQChatFontFamily"] = new FontFamily("Segoe UI");
        Application.Current.Resources["EQLogFontSize"] = 16.0; // changed when chat archive loads
        Application.Current.Resources["EQLogFontFamily"] = new FontFamily("Segoe UI");

        InitializeComponent();

        // add tabs to the right
        ((DocumentContainer)dockSite.DocContainer).AddTabDocumentAtLast = true;

        var version = Application.ResourceAssembly.GetName().Version.ToString();
        version = version.Substring(0, version.Length - 2);

        // update titles
        versionText.Text = "v" + version;

        MainActions.InitPetOwners(this, petMappingGrid, ownerList, petMappingWindow);
        MainActions.InitVerifiedPlayers(this, verifiedPlayersGrid, classList, verifiedPlayersWindow, petMappingWindow);
        MainActions.InitVerifiedPets(this, verifiedPetsGrid, verifiedPetsWindow, petMappingWindow);

        (npcWindow.Content as FightTable).EventsSelectionChange += (_, __) => ComputeStats();
        DamageStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(damageChartIcon.Tag as string, data));
        HealingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(healingChartIcon.Tag as string, data));
        TankingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(tankingChartIcon.Tag as string, data));

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
        IsMapSendToEQEnabled = ConfigUtil.IfSet("MapSendToEQAsCtrlC");
        enableMapSendToEQIcon.Visibility = IsMapSendToEQEnabled ? Visibility.Visible : Visibility.Hidden;

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
        if (ConfigUtil.GetSetting("RecentFiles") is string recentFiles && !string.IsNullOrEmpty(recentFiles))
        {
          var files = recentFiles.Split(',');
          if (files.Length > 0)
          {
            RecentFiles.AddRange(files);
            UpdateRecentFiles();
          }
        }

        LOG.Info("Initialized Components");

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

        ComputeStatsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 500) };
        ComputeStatsTimer.Tick += (sender, e) =>
        {
          ComputeStats();
          ComputeStatsTimer.Stop();
        };

        DockingManager.SetState(petMappingWindow, DockState.AutoHidden);

        if (ConfigUtil.IfSet("Debug"))
        {
          LOG.Info("Debug Enabled. Saving Unprocessed Lines to " + ConfigUtil.LogsDir);
          ConfigUtil.Debug = true;
          ((log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository()).Root.Level = Level.Debug;
          ((log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository()).RaiseConfigurationChanged(EventArgs.Empty);
        }

        if (ConfigUtil.IfSetOrElse("TriggersEnabled", false))
        {
          TriggerManager.Instance.Start();
        }

        SystemEvents.PowerModeChanged += SystemEventsPowerModeChanged;

        // cleanup downloads
        Dispatcher.InvokeAsync(() => MainActions.Cleanup());
        TriggerManager.Instance.Init();
      }
      catch (Exception e)
      {
        LOG.Error(e);
        throw;
      }
    }

    private void SystemEventsPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
      switch (e.Mode)
      {
        case PowerModes.Suspend:
          LOG.Warn("Suspending");
          TriggerManager.Instance.Stop(false);
          DataManager.Instance.EventsNewOverlayFight -= EventsNewOverlayFight;
          CloseDamageOverlay();
          break;
        case PowerModes.Resume:
          LOG.Warn("Resume");
          DataManager.Instance.ResetOverlayFights(true);
          OpenDamageOverlayIfEnabled(true, false);
          DataManager.Instance.EventsNewOverlayFight += EventsNewOverlayFight;

          if (ConfigUtil.IfSetOrElse("TriggersEnabled", false))
          {
            TriggerManager.Instance.Start();
          }
          break;
      }
    }

    private void EventsNewOverlayFight(object sender, Fight e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (DamageOverlay == null)
        {
          OpenDamageOverlayIfEnabled(false, false);
        }
      });
    }

    private void DomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
      var exception = e.ExceptionObject as Exception;
      LOG.Error(exception.Message, exception);
    }

    internal void CopyToEQClick(string type) => (playerParseTextWindow.Content as ParsePreview)?.CopyToEQClick(type);
    internal FightTable GetFightTable() => npcWindow?.Content as FightTable;
    private void RestoreTableColumnsClick(object sender, RoutedEventArgs e) => DataGridUtil.RestoreAllTableColumns();
    private void TabGroupCreated(object sender, TabGroupEventArgs e) => ChartTab = e.CurrentTabGroup;

    internal void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.DAMAGEPARSE, DamageStatsManager.Instance, combined, selected, true);
    }

    internal void AddAndCopyTankParse(CombinedStats combined, List<PlayerStats> selected)
    {
      (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.TANKPARSE, TankingStatsManager.Instance, combined, selected, true);
    }

    internal void ShowTriggersEnabled(bool active)
    {
      Dispatcher.InvokeAsync(() => statusTriggersText.Visibility = active ? Visibility.Visible : Visibility.Collapsed);
    }

    internal void CloseDamageOverlay()
    {
      DamageOverlay?.Close();
      DamageOverlay = null;
    }

    internal void OpenDamageOverlayIfEnabled(bool reset, bool configure)
    {
      if (configure)
      {
        DamageOverlay = new DamageOverlayWindow(true);
        DamageOverlay.Show();
      }
      // delay opening overlay so group IDs get populated
      else if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        if (DataManager.Instance.HasOverlayFights())
        {
          if (DamageOverlay != null)
          {
            DamageOverlay?.Close();
          }

          DamageOverlay = new DamageOverlayWindow(false, reset);
          DamageOverlay.Show();
        }
      }
    }

    private void DockSiteLoaded(object sender, RoutedEventArgs e)
    {
      if (!DoneLoading)
      {
        // Show Healing Summary at startup
        ConfigUtil.IfSet("ShowDamageSummaryAtStartup", OpenDamageSummary, true);
        // Show Healing Summary at startup
        ConfigUtil.IfSet("ShowHealingSummaryAtStartup", OpenHealingSummary);
        // Show Tanking Summary at startup
        ConfigUtil.IfSet("ShowTankingSummaryAtStartup", OpenTankingSummary);
        DoneLoading = true;
      }
    }

    private void HandleChartUpdate(string key, DataPointEvent e)
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (opened.ContainsKey(key))
      {
        (opened[key].Content as LineChart)?.HandleUpdateEvent(e);
      }
    }

    private void UpdateDeleteChatMenu()
    {
      deleteChat.Items.Clear();
      ChatManager.GetArchivedPlayers().ForEach(player =>
      {
        var item = new MenuItem() { IsEnabled = true, Header = player };
        deleteChat.Items.Add(item);

        item.Click += (object sender, RoutedEventArgs e) =>
        {
          var msgDialog = new MessageWindow("Clear Chat Archive for " + player + "?", EQLogParser.Resource.CLEAR_CHAT, MessageWindow.IconType.Question, "Yes");
          msgDialog.ShowDialog();

          if (msgDialog.IsYes1Clicked && ChatManager.DeleteArchivedPlayer(player))
          {
            if (PlayerChatManager != null && PlayerChatManager.GetCurrentPlayer().Equals(player, StringComparison.Ordinal))
            {
              PlayerChatManager.Reset();
            }
            else
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
      if (ComputeStatsTimer != null)
      {
        ComputeStatsTimer.Stop();
        ComputeStatsTimer.Start();
      }
    }

    private void ComputeStats()
    {
      var filtered = (npcWindow?.Content as FightTable)?.GetSelectedFights().OrderBy(npc => npc.Id);
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);

      var damageOptions = new GenerateStatsOptions();
      damageOptions.Npcs.AddRange(filtered);
      Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageOptions));

      var healingOptions = new GenerateStatsOptions();
      healingOptions.Npcs.AddRange(filtered);
      Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingOptions));

      var tankingOptions = new GenerateStatsOptions();
      tankingOptions.Npcs.AddRange(filtered);
      if (opened.TryGetValue(tankingSummaryIcon.Tag as string, out var control) && control != null)
      {
        tankingOptions.DamageType = ((TankingSummary)control.Content).DamageType;
      }
      Task.Run(() => TankingStatsManager.Instance.BuildTotalStats(tankingOptions));
    }

    private void MenuItemClearOpenRecentClick(object sender, RoutedEventArgs e)
    {
      RecentFiles.Clear();
      ConfigUtil.SetSetting("RecentFiles", "");
      UpdateRecentFiles();
    }

    private void MenuItemExportHTMLClick(object sender, RoutedEventArgs e)
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      var tables = new Dictionary<string, SummaryTable>();

      if (opened.TryGetValue(damageSummaryIcon.Tag as string, out var control))
      {
        tables.Add(DockingManager.GetHeader(control) as string, (DamageSummary)control.Content);
      }

      if (opened.TryGetValue(healingSummaryIcon.Tag as string, out var control2))
      {
        tables.Add(DockingManager.GetHeader(control2) as string, (HealingSummary)control2.Content);
      }

      if (opened.TryGetValue(tankingSummaryIcon.Tag as string, out var control3))
      {
        tables.Add(DockingManager.GetHeader(control3) as string, (TankingSummary)control3.Content);
      }

      if (tables.Count > 0)
      {
        MainActions.ExportAsHTML(tables);
      }
      else
      {
        new MessageWindow("No Summary Views are Open. Nothing to Save.", EQLogParser.Resource.FILEMENU_EXPORT_SUMMARY).ShowDialog();
      }
    }

    private void MenuItemExportFightsClick(object sender, RoutedEventArgs e)
    {
      var filtered = (npcWindow?.Content as FightTable)?.GetSelectedFights().OrderBy(npc => npc.Id).ToList();

      if (string.IsNullOrEmpty(CurrentLogFile))
      {
        new MessageWindow("No Log File Opened. Nothing to Save.", EQLogParser.Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
      else if (filtered.Count > 0)
      {
        MainActions.ExportFights(filtered);
      }
      else
      {
        new MessageWindow("No Fights Selected. Nothing to Save.", EQLogParser.Resource.FILEMENU_SAVE_FIGHTS).ShowDialog();
      }
    }

    private void ViewErrorLogClick(object sender, RoutedEventArgs e)
    {
      using (var fileopener = new Process())
      {
        var appender = LOG.Logger.Repository.GetAppenders().FirstOrDefault(test => "file".Equals(test.Name, StringComparison.OrdinalIgnoreCase));
        if (appender != null)
        {
          fileopener.StartInfo.FileName = "explorer";
          fileopener.StartInfo.Arguments = "\"" + (appender as log4net.Appender.FileAppender).File + "\"";
          fileopener.Start();
        }
      }
    }

    private void OpenSoundsFolderClick(object sender, RoutedEventArgs e)
    {
      using (var fileopener = new Process())
      {
        fileopener.StartInfo.FileName = "explorer";
        fileopener.StartInfo.Arguments = "\"" + @"data\sounds" + "\"";
        fileopener.Start();
      }
    }

    private void ReportProblemClick(object sender, RoutedEventArgs e)
    {
      var uri = "http://github.com/kauffman12/EQLogParser/issues";
      var psi = new System.Diagnostics.ProcessStartInfo();
      psi.UseShellExecute = true;
      psi.FileName = uri;
      System.Diagnostics.Process.Start(psi);
    }

    private void AboutClick(object sender, RoutedEventArgs e)
    {
      var uri = "http://github.com/kauffman12/EQLogParser/#readme";
      var psi = new System.Diagnostics.ProcessStartInfo();
      psi.UseShellExecute = true;
      psi.FileName = uri;
      System.Diagnostics.Process.Start(psi);
    }

    private void ViewReleaseNotesClick(object sender, RoutedEventArgs e)
    {
      var notesWindow = new DocumentViewer(@"data\releasenotes.rtf");
      notesWindow.Show();
    }

    private void ToggleHideOnMinimizeClick(object sender, RoutedEventArgs e)
    {
      IsHideOnMinimizeEnabled = !IsHideOnMinimizeEnabled;
      ConfigUtil.SetSetting("HideWindowOnMinimize", IsHideOnMinimizeEnabled.ToString());
      enableHideOnMinimizeIcon.Visibility = IsHideOnMinimizeEnabled ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleAoEHealingClick(object sender, RoutedEventArgs e)
    {
      IsAoEHealingEnabled = !IsAoEHealingEnabled;
      ConfigUtil.SetSetting("IncludeAoEHealing", IsAoEHealingEnabled.ToString());
      enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;
      Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats());
    }

    private void ToggleHealingSwarmPetsClick(object sender, RoutedEventArgs e)
    {
      IsHealingSwarmPetsEnabled = !IsHealingSwarmPetsEnabled;
      ConfigUtil.SetSetting("IncludeHealingSwarmPets", IsHealingSwarmPetsEnabled.ToString());
      enableHealingSwarmPetsIcon.Visibility = IsHealingSwarmPetsEnabled ? Visibility.Visible : Visibility.Hidden;
      Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats());
    }

    private void ToggleAutoMonitorClick(object sender, RoutedEventArgs e)
    {
      ConfigUtil.SetSetting("AutoMonitor", (enableAutoMonitorIcon.Visibility == Visibility.Hidden).ToString());
      enableAutoMonitorIcon.Visibility = enableAutoMonitorIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
      ConfigUtil.SetSetting("CheckUpdatesAtStartup", (checkUpdatesIcon.Visibility == Visibility.Hidden).ToString());
      checkUpdatesIcon.Visibility = checkUpdatesIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleMapSendToEQClick(object sender, RoutedEventArgs e)
    {
      IsMapSendToEQEnabled = !IsMapSendToEQEnabled;
      ConfigUtil.SetSetting("MapSendToEQAsCtrlC", (enableMapSendToEQIcon.Visibility == Visibility.Hidden).ToString());
      enableMapSendToEQIcon.Visibility = enableMapSendToEQIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleDamageOverlayClick(object sender, RoutedEventArgs e)
    {
      enableDamageOverlayIcon.Visibility = enableDamageOverlayIcon.Visibility == Visibility.Hidden ? Visibility.Visible : Visibility.Hidden;
      var enabled = enableDamageOverlayIcon.Visibility == Visibility.Visible;
      ConfigUtil.SetSetting("IsDamageOverlayEnabled", enabled.ToString());

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
        EventsThemeChanged?.Invoke(this, CurrentTheme);
      }
    }

    private void ToggleMaterialLightClick(object sender, RoutedEventArgs e)
    {
      if (CurrentTheme != "MaterialLight")
      {
        CurrentTheme = "MaterialLight";
        MainActions.LoadTheme(this, CurrentTheme);
        ConfigUtil.SetSetting("CurrentTheme", CurrentTheme);
        EventsThemeChanged?.Invoke(this, CurrentTheme);
      }
    }

    private void UpdateDamageOption(ImageAwesome icon, bool enabled, string option)
    {
      ConfigUtil.SetSetting(option, enabled.ToString(CultureInfo.CurrentCulture));
      icon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
      var options = new GenerateStatsOptions();
      Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(options));
    }

    // Main Menu
    private void MenuItemWindowClick(object sender, RoutedEventArgs e)
    {
      if (e.Source == damageChartMenuItem)
      {
        OpenDamageChart();
      }
      else if (e.Source == healingChartMenuItem)
      {
        OpenHealingChart();
      }
      else if (e.Source == tankingChartMenuItem)
      {
        OpenTankingChart();
      }
      else if (e.Source == damageSummaryMenuItem)
      {
        OpenDamageSummary();
      }
      else if (e.Source == healingSummaryMenuItem)
      {
        OpenHealingSummary();
      }
      else if (e.Source == tankingSummaryMenuItem)
      {
        OpenTankingSummary();
      }
      else if (e.Source == chatMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(ChatViewer), chatIcon.Tag as string, "Chat Archive");
      }
      else if (e.Source == eventMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(EventViewer), eventIcon.Tag as string, "Misc Events");
      }
      else if (e.Source == randomsMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(RandomViewer), randomsIcon.Tag as string, "Random Rolls");
      }
      else if (e.Source == playerLootMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(LootViewer), playerLootIcon.Tag as string, "Looted Items");
      }
      else if (e.Source == triggersMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(TriggersView), triggersIcon.Tag as string, "Trigger Manager");
      }
      else if (e.Source == eqLogMenuItem)
      {
        var found = LogWindows.FindIndex(used => !used);
        if (found == -1)
        {
          LogWindows.Add(true);
          found = LogWindows.Count;
        }
        else
        {
          LogWindows[found] = true;
          found += 1;
        }

        Helpers.OpenWindow(dockSite, null, out _, typeof(EQLogViewer), "eqLogWindow", "Log Search " + found);
      }
      else if (e.Source == spellResistsMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(NpcStatsViewer), spellResistsIcon.Tag as string, "Spell Resists");
      }
      else if (e.Source == spellDamageStatsMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(SpellDamageStatsViewer), npcSpellDamageIcon.Tag as string, "Spell Damage");
      }
      else if (e.Source == tauntStatsMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(TauntStatsViewer), tauntStatsIcon.Tag as string, "Taunt Usage");
      }
      else if ((sender as MenuItem)?.Icon is ImageAwesome icon && icon.Tag is string name)
      {
        // any other core windows that just get hidden
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        if (opened.TryGetValue(name, out var control) && control.Tag.ToString() == "Hide")
        {
          var state = (DockingManager.GetState(control) == DockState.Hidden) ? DockState.Dock : DockState.Hidden;
          DockingManager.SetState(control, state);
        }
      }
    }

    private void OpenDamageChart()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenChart(opened, dockSite, damageChartIcon.Tag as string, DAMAGE_CHOICES, "DPS Chart", ChartTab, true))
      {
        List<PlayerStats> selected = null;
        if (opened.TryGetValue(damageSummaryIcon.Tag as string, out var window) && window.Content is DamageSummary summary)
        {
          selected = summary?.GetSelectedStats();
        }

        DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "UPDATE", selected);
      }
    }

    private void OpenHealingChart()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenChart(opened, dockSite, healingChartIcon.Tag as string, HEALING_CHOICES, "Healing Chart", ChartTab, false))
      {
        List<PlayerStats> selected = null;
        if (opened.TryGetValue(healingSummaryIcon.Tag as string, out var window) && window.Content is HealingSummary summary)
        {
          selected = summary?.GetSelectedStats();
        }

        HealingStatsManager.Instance.FireChartEvent("UPDATE", selected);
      }
    }

    private void OpenTankingChart()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenChart(opened, dockSite, tankingChartIcon.Tag as string, TANKING_CHOICES, "Tanking Chart", ChartTab, false))
      {
        List<PlayerStats> selected = null;
        if (opened.TryGetValue(tankingSummaryIcon.Tag as string, out var window) && window.Content is TankingSummary summary)
        {
          selected = summary?.GetSelectedStats();
        }

        TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "UPDATE", selected);
      }
    }

    private void OpenDamageSummary()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenWindow(dockSite, opened, out var control, typeof(DamageSummary), damageSummaryIcon.Tag as string, "DPS Summary"))
      {
        (control.Content as DamageSummary).EventsSelectionChange += DamageSummary_SelectionChanged;
        if (DamageStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var damageOptions = new GenerateStatsOptions();
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(damageOptions));
        }
      }
    }

    private void OpenHealingSummary()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenWindow(dockSite, opened, out var control, typeof(HealingSummary), healingSummaryIcon.Tag as string, "Healing Summary"))
      {
        (control.Content as HealingSummary).EventsSelectionChange += HealingSummary_SelectionChanged;
        if (HealingStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats());
        }
      }
    }

    private void OpenTankingSummary()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenWindow(dockSite, opened, out var control, typeof(TankingSummary), tankingSummaryIcon.Tag as string, "Tanking Summary"))
      {
        (control.Content as TankingSummary).EventsSelectionChange += TankingSummary_SelectionChanged;
        if (TankingStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var tankingOptions = new GenerateStatsOptions { DamageType = (control.Content as TankingSummary).DamageType };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    private void DamageSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "SELECT", data.Selected);
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(Labels.DAMAGEPARSE, data.Selected);
    }

    private void HealingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      HealingStatsManager.Instance.FireChartEvent("SELECT", data.Selected);
      var addTopParse = data.Selected?.Count == 1 && data.Selected[0].SubStats?.Count > 0;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview.UpdateParse(data, HealingStatsManager.Instance, addTopParse, Labels.HEALPARSE, Labels.TOPHEALSPARSE);
    }

    private void TankingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "SELECT", data.Selected);
      var addReceiveParse = data.Selected?.Count == 1 && data.Selected[0].MoreStats != null;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview.UpdateParse(data, TankingStatsManager.Instance, addReceiveParse, Labels.TANKPARSE, Labels.RECEIVEDHEALPARSE);
    }

    private void MenuItemFontFamilyClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem)
      {
        MainActions.UpdateCheckedMenuItem(menuItem, (menuItem.Parent as MenuItem).Items);
        CurrentFontFamily = menuItem.Header as string;
        ConfigUtil.SetSetting("ApplicationFontFamily", CurrentFontFamily);
        MainActions.LoadTheme(this, CurrentTheme);
        EventsThemeChanged?.Invoke(this, CurrentTheme);
      }
    }

    private void MenuItemFontSizeClicked(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem)
      {
        MainActions.UpdateCheckedMenuItem(menuItem, (menuItem.Parent as MenuItem).Items);
        CurrentFontSize = (double)menuItem.Tag;
        ConfigUtil.SetSetting("ApplicationFontSize", CurrentFontSize.ToString());
        MainActions.LoadTheme(this, CurrentTheme);
        EventsThemeChanged?.Invoke(this, CurrentTheme);
      }
    }

    private void MenuItemSelectLogFileClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem item)
      {
        var lastMins = -1;
        string fileName = null;
        if (!string.IsNullOrEmpty(item.Tag as string))
        {
          lastMins = Convert.ToInt32(item.Tag.ToString(), CultureInfo.CurrentCulture) * 60;
        }

        if (item.Parent == recent1File && RecentFiles.Count > 0)
        {
          fileName = RecentFiles[0];
        }
        else if (item.Parent == recent2File && RecentFiles.Count > 1)
        {
          fileName = RecentFiles[1];
        }
        else if (item.Parent == recent3File && RecentFiles.Count > 2)
        {
          fileName = RecentFiles[2];
        }
        else if (item.Parent == recent4File && RecentFiles.Count > 3)
        {
          fileName = RecentFiles[3];
        }
        else if (item.Parent == recent5File && RecentFiles.Count > 4)
        {
          fileName = RecentFiles[4];
        }
        else if (item.Parent == recent6File && RecentFiles.Count > 5)
        {
          fileName = RecentFiles[5];
        }

        if (!string.IsNullOrEmpty(fileName) && !File.Exists(fileName))
        {
          new MessageWindow("Log File No Longer Exists!", EQLogParser.Resource.FILEMENU_OPEN_LOG).ShowDialog();
          return;
        }

        OpenLogFile(fileName, lastMins);
      }
    }

    private void UpdateLoadingProgress()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (EQLogReader != null)
        {
          var seconds = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds);
          double filePercent = EQLogReader.FileSize > 0 ? Math.Min(Convert.ToInt32((double)FilePosition / EQLogReader.FileSize * 100), 100) : 100;

          if (filePercent < 100)
          {
            statusText.Text = string.Format("Reading Log.. {0}% in {1} seconds", filePercent, seconds);
          }
          else
          {
            var procPercent = Convert.ToInt32(Math.Min(CastProcessor.GetPercentComplete(), DamageProcessor.GetPercentComplete()));
            statusText.Text = string.Format("Processing... {0}% in {1} seconds", procPercent, seconds);
          }

          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (filePercent >= 100 && CastProcessor.GetPercentComplete() >= 100 && DamageProcessor.GetPercentComplete() >= 100
            && HealingProcessor.GetPercentComplete() >= 100 && MiscProcessor.GetPercentComplete() >= 100 && EQLogReader.FileLoadComplete)
          {
            if (filePercent >= 100)
            {
              statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
              statusText.Text = "Monitoring Active";
            }

            ConfigUtil.SetSetting("LastOpenedFile", CurrentLogFile);
            LOG.Info("Finished Loading Log File in " + seconds.ToString() + " seconds.");
            Task.Delay(1500).ContinueWith(task => Dispatcher.InvokeAsync(() =>
            {
              EventsLogLoadingComplete?.Invoke(this, true);
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
            Task.Delay(500).ContinueWith(task => UpdateLoadingProgress(), TaskScheduler.Default);
          }
        }
      });
    }

    private void PlayerClassDropDownSelectionChanged(object sender, CurrentCellDropDownSelectionChangedEventArgs e)
    {
      if (sender is SfDataGrid dataGrid && e.RowColumnIndex.RowIndex > 0 && dataGrid.View.GetRecordAt(e.RowColumnIndex.RowIndex - 1).Data is ExpandoObject obj)
      {
        dataGrid.SelectionController.CurrentCellManager.EndEdit();
        PlayerManager.Instance.SetPlayerClass(((dynamic)obj)?.Name, ((dynamic)obj)?.PlayerClass, "Class selected by user.");
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
          if (DockingManager.GetState(npcWindow) == DockState.Hidden)
          {
            DockingManager.SetState(npcWindow, DockState.Dock);
          }

          StopProcessing();
          CastProcessor = new ActionProcessor(CastLineParser.Process);
          DamageProcessor = new ActionProcessor(DamageLineParser.Process);
          HealingProcessor = new ActionProcessor(HealingLineParser.Process);
          MiscProcessor = new ActionProcessor(MiscLineParser.Process);

          fileText.Text = "-- " + theFile;
          StartLoadTime = DateTime.Now;
          FilePosition = LineCount = 0;

          var name = "You";
          var server = "Unknown";
          if (theFile.Length > 0)
          {
            LOG.Info("Selected Log File: " + theFile);

            var file = System.IO.Path.GetFileName(theFile);
            var matches = ParseFileName.Matches(file);
            if (matches.Count == 1)
            {
              if (matches[0].Groups.Count > 1)
              {
                name = matches[0].Groups[1].Value;
              }

              if (matches[0].Groups.Count > 2)
              {
                server = matches[0].Groups[2].Value;
              }
            }
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

          if (RecentFiles.Contains(theFile))
          {
            RecentFiles.Remove(theFile);
          }

          RecentFiles.Insert(0, theFile);
          ConfigUtil.SetSetting("RecentFiles", string.Join(",", RecentFiles));
          UpdateRecentFiles();

          DataManager.Instance.EventsNewOverlayFight -= EventsNewOverlayFight;
          CloseDamageOverlay();
          DataManager.Instance.Clear();
          PlayerChatManager = new ChatManager();
          CurrentLogFile = theFile;
          NpcDamageManager.Reset();
          EQLogReader = new LogReader(theFile, FileLoadingCallback, lastMins);
          EQLogReader.Start();
          UpdateLoadingProgress();
        }
      }
      catch (Exception e)
      {
        if (!(e is InvalidCastException || e is ArgumentException || e is FormatException))
        {
          throw;
        }
        else
        {
          LOG.Error("Problem During Initialization", e);
        }
      }
    }

    private void FileLoadingCallback(string line, long position, double dateTime)
    {
      if (double.IsNaN(dateTime))
      {
        return;
      }

      Interlocked.Exchange(ref FilePosition, position);
      Interlocked.Add(ref LineCount, 1);

      string splitLine = null;
      if (!string.IsNullOrEmpty(line) && line.Length > 30)
      {
        var lineData = new LineData { Line = line, Action = line.Substring(ACTION_INDEX), LineNumber = LineCount, BeginTime = dateTime };

        if (EQLogReader.FileLoadComplete)
        {
          TriggerManager.Instance.AddAction(lineData);
        }

        // avoid having other things parse chat by accident
        if (ChatLineParser.Process(lineData, line) is ChatType chatType)
        {
          PlayerChatManager.Add(chatType);
        }
        // populates lineData.Action
        else
        {
          // only if it's not a chat line check if two lines are on the same line
          var multiLine = line.IndexOf("[", ACTION_INDEX + 1);
          if (multiLine > -1 && line.Length > (multiLine + ACTION_INDEX - 1) && line[multiLine + ACTION_INDEX - 2] == ']' &&
            char.IsDigit(line[multiLine + ACTION_INDEX - 3]) && char.IsDigit(line[multiLine + ACTION_INDEX - 6]))
          {
            splitLine = line.Substring(multiLine);
            lineData.Action = line.Substring(0, multiLine).Substring(ACTION_INDEX);
          }

          if (PreLineParser.NeedProcessing(lineData) && lineData.Action != null)
          {
            CastProcessor.Add(lineData);
            HealingProcessor.Add(lineData);
            MiscProcessor.Add(lineData);
            DamageProcessor.Add(lineData);
          }
        }
      }

      if (splitLine != null)
      {
        FileLoadingCallback(splitLine, position, dateTime);
      }
    }

    private void UpdateRecentFiles()
    {
      setRecentVisible(recent1File, 0);
      setRecentVisible(recent2File, 1);
      setRecentVisible(recent3File, 2);
      setRecentVisible(recent4File, 3);
      setRecentVisible(recent5File, 4);
      setRecentVisible(recent6File, 5);

      void setRecentVisible(MenuItem menuItem, int count)
      {
        if (RecentFiles.Count > count)
        {
          var m = 75;
          var theFile = RecentFiles[count].Length > m ? "... " + RecentFiles[count].Substring(RecentFiles[count].Length - m) : RecentFiles[count];
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

    private void StopProcessing()
    {
      EQLogReader?.Stop();
      CastProcessor?.Stop();
      DamageProcessor?.Stop();
      HealingProcessor?.Stop();
      MiscProcessor?.Stop();
    }

    private void WindowIconLoaded(object sender, RoutedEventArgs e)
    {
      if (sender is FrameworkElement icon)
      {
        if (icon.Tag is string name)
        {
          var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
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
        var data = e.Record as dynamic;
        e.ToolTip.Content = PlayerManager.Instance.GetPlayerClassReason(data.Name);
      }

      e.ToolTip.FontSize = 13;
    }

    private void RemovePetMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is System.Windows.Controls.Border border && border.DataContext is ExpandoObject sortable)
      {
        PlayerManager.Instance.RemoveVerifiedPet(((dynamic)sortable)?.Name);
      }
    }

    private void RemovePlayerMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is System.Windows.Controls.Border border && border.DataContext is ExpandoObject sortable)
      {
        PlayerManager.Instance.RemoveVerifiedPlayer(((dynamic)sortable)?.Name);
      }
    }

    private void TestTriggersClick(object sender, RoutedEventArgs e)
    {
      if (testButton.Content.ToString() == "Run Test")
      {
        try
        {
          if (testTriggersBox.Lines?.Count > 0)
          {
            var allLines = testTriggersBox.Lines.ToList().Where(line => !string.IsNullOrEmpty(line.Text) && line.Text.Length > ACTION_INDEX)
              .Select(line => line.Text).ToList();
            if (allLines.Count > 0)
            {
              TriggerUtil.RunTest(allLines, realTime.IsChecked == true);
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Error(ex);
        }
      }
      else if (testButton.Content.ToString() == "Stop Test")
      {
        testButton.Content = "Stopping Test";
      }
    }

    private void NotifyIcon_Click(object sender, EventArgs e)
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

    private void ChromelessWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      if (testButton.Content.ToString() == "Stop Test")
      {
        testButton.Content = "Stopping Test";
      }
    }

    private void WindowClosed(object sender, EventArgs e)
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      ConfigUtil.SetSetting("ShowDamageSummaryAtStartup", opened.ContainsKey(damageSummaryIcon.Tag as string).ToString());
      ConfigUtil.SetSetting("ShowHealingSummaryAtStartup", opened.ContainsKey(healingSummaryIcon.Tag as string).ToString());
      ConfigUtil.SetSetting("ShowTankingSummaryAtStartup", opened.ContainsKey(tankingSummaryIcon.Tag as string).ToString());
      ConfigUtil.SetSetting("WindowState", WindowState.ToString());

      if (WindowState != WindowState.Maximized)
      {
        ConfigUtil.SetSetting("WindowLeft", Left.ToString());
        ConfigUtil.SetSetting("WindowTop", Top.ToString());
        ConfigUtil.SetSetting("WindowHeight", Height.ToString());
        ConfigUtil.SetSetting("WindowWidth", Width.ToString());
      }

      StopProcessing();
      PlayerChatManager?.Dispose();
      TriggerManager.Instance.Stop(false);
      ConfigUtil.Save();
      PlayerManager.Instance?.Save();
      Application.Current.Shutdown();
    }

    // This is where closing summary tables and line charts will get disposed
    private void CloseTab(ContentControl window)
    {
      var content = window.Content;
      if (content is EQLogViewer)
      {
        var title = DockingManager.GetHeader(window) as string;
        var last = title.LastIndexOf(" ");
        if (last > -1)
        {
          var value = title.Substring(last, title.Length - last);
          if (int.TryParse(value, out var result) && result > 0 && LogWindows.Count >= result)
          {
            LogWindows[result - 1] = false;
          }
        }

        (window.Content as IDisposable)?.Dispose();
      }
      else
      {
        Helpers.CloseWindow(dockSite, window);
      }
    }

    private void DockSiteCloseButtonClick(object sender, CloseButtonEventArgs e) => CloseTab(e.TargetItem as ContentControl);
    private void DockSiteWindowClosing(object sender, WindowClosingEventArgs e) => CloseTab(e.TargetItem as ContentControl);
    private void WindowClose(object sender, EventArgs e) => Close();

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        PlayerChatManager?.Dispose();
        petMappingGrid?.Dispose();
        verifiedPetsGrid?.Dispose();
        verifiedPlayersGrid?.Dispose();
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}

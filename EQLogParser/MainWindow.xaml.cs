using FontAwesome5;
using log4net;
using log4net.Core;
using Syncfusion.SfSkinManager;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.Windows.Shared;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // global settings
    internal static string CurrentLogFile;
    internal static bool IsAoEHealingEnabled = true;
    internal static bool IsAssassinateDamageEnabled = true;
    internal static bool IsBaneDamageEnabled = true;
    internal static bool IsFinishingBlowDamageEnabled = true;
    internal static bool IsHeadshotDamageEnabled = true;
    internal static bool IsSlayUndeadDamageEnabled = true;
    internal static bool IsHideOnMinimizeEnabled = false;
    internal static bool IsIgnoreCharmPetsEnabled = false;
    internal static readonly int ACTION_INDEX = 27;

    private static readonly ILog LOG = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private enum LogOption { OPEN, MONITOR };
    private static readonly Regex ParseFileName = new Regex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.txt", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly List<string> DAMAGE_CHOICES = new List<string>() { "DPS", "Damage", "Av Hit", "% Crit" };
    private static readonly List<string> HEALING_CHOICES = new List<string>() { "HPS", "Healing", "Av Heal", "% Crit" };
    private static readonly List<string> TANKING_CHOICES = new List<string>() { "DPS", "Damaged", "Av Hit" };
    private const string VERSION = "v1.9.20";

    private static long LineCount = 0;
    private static long FilePosition = 0;
    private static ActionProcessor CastProcessor = null;
    private static ActionProcessor DamageProcessor = null;
    private static ActionProcessor HealingProcessor = null;
    private static ActionProcessor MiscProcessor = null;

    // progress window
    private static DateTime StartLoadTime;
    private static LogOption CurrentLogOption;

    private readonly DispatcherTimer ComputeStatsTimer;
    private ChatManager PlayerChatManager = null;
    private readonly NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private DocumentTabControl ChartTab = null;
    private LogReader EQLogReader = null;
    private List<bool> LogWindows = new List<bool>();
    private string CurrentTheme = "MaterialDark";

    public MainWindow()
    {
      try
      {
        // DPI and sizing
        var dpi = VisualTreeHelper.GetDpi(this);
        System.Drawing.Rectangle resolution = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Width = resolution.Width * 0.85 / dpi.DpiScaleX;
        Height = resolution.Height * 0.75 / dpi.DpiScaleY;

        // set theme
        if (CurrentTheme == "MaterialDark")
        {
          SfSkinManager.SetTheme(this, new Theme("MaterialDarkCustom;MaterialDark"));
          BorderBrush = Application.Current.Resources["ContentBackgroundAlt2"] as SolidColorBrush;
          Helpers.LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/MSControl/CheckBox.xaml");
          Helpers.LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/SfDataGrid/SfDataGrid.xaml");
          Helpers.LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/Common/Brushes.xaml");
        }

        InitializeComponent();

        // update titles
        versionText.Text = VERSION;

        MainActions.InitPetOwners(this, petMappingGrid, ownerList, petMappingWindow);
        MainActions.InitVerifiedPlayers(this, verifiedPlayersGrid, verifiedPlayersWindow, petMappingWindow);
        MainActions.InitVerifiedPets(this, verifiedPetsGrid, verifiedPetsWindow, petMappingWindow);

        (npcWindow.Content as FightTable).EventsSelectionChange += (_, __) => ComputeStats();
        DamageStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(damageChartIcon.Tag as string, data));
        HealingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(healingChartIcon.Tag as string, data));
        TankingStatsManager.Instance.EventsUpdateDataPoint += (_, data) => Dispatcher.InvokeAsync(() => HandleChartUpdate(tankingChartIcon.Tag as string, data));

        UpdateDeleteChatMenu();

        // Ignore Charm Pets
        IsIgnoreCharmPetsEnabled = ConfigUtil.IfSet("IgnoreCharmPets");
        ignoreCharmPetsIcon.Visibility = IsIgnoreCharmPetsEnabled ? Visibility.Visible : Visibility.Hidden;

        // AoE healing
        IsAoEHealingEnabled = ConfigUtil.IfSetOrElse("IncludeAoEHealing", IsAoEHealingEnabled);
        enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;

        // Assassinate Damage
        IsAssassinateDamageEnabled = ConfigUtil.IfSetOrElse("IncludeAssassinateDamage", IsAssassinateDamageEnabled);
        enableAssassinateDamageIcon.Visibility = IsAssassinateDamageEnabled ? Visibility.Visible : Visibility.Hidden;

        // Bane Damage
        IsBaneDamageEnabled = ConfigUtil.IfSetOrElse("IncludeBaneDamage", IsBaneDamageEnabled);
        enableBaneDamageIcon.Visibility = IsBaneDamageEnabled ? Visibility.Visible : Visibility.Hidden;

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

        // Damage Overlay
        enableDamageOverlayIcon.Visibility = OverlayUtil.LoadSettings() ? Visibility.Visible : Visibility.Hidden;

        LOG.Info("Initialized Components");

        if (ConfigUtil.IfSet("AutoMonitor"))
        {
          enableAutoMonitorIcon.Visibility = Visibility.Visible;
          var previousFile = ConfigUtil.GetSetting("LastOpenedFile");
          if (File.Exists(previousFile))
          {
            OpenLogFile(LogOption.MONITOR, previousFile);
          }
        }
        else
        {
          enableAutoMonitorIcon.Visibility = Visibility.Hidden;
        }

        ComputeStatsTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 800) };
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
      }
      catch (Exception e)
      {
        LOG.Error(e);
        throw;
      }
    }

    internal void CopyToEQClick(string type) => (playerParseTextWindow.Content as ParsePreview)?.CopyToEQClick(type);
    internal FightTable GetFightTable() => npcWindow?.Content as FightTable;
    private void RestoreTableColumnsClick(object sender, RoutedEventArgs e) => DataGridUtil.RestoreAllTableColumns();
    private void TabGroupCreated(object sender, TabGroupEventArgs e) => ChartTab = e.CurrentTabGroup;

    internal void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.DAMAGEPARSE, DamageStatsManager.Instance, combined, selected, true);
    }
    private void DockSiteLoaded(object sender, RoutedEventArgs e)
    {
      // Show Tanking Summary at startup
      ConfigUtil.IfSet("ShowTankingSummaryAtStartup", OpenTankingSummary);
      // Show Healing Summary at startup
      ConfigUtil.IfSet("ShowHealingSummaryAtStartup", OpenHealingSummary);
      // Show Healing Summary at startup
      ConfigUtil.IfSet("ShowDamageSummaryAtStartup", OpenDamageSummary, true);
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
        MenuItem item = new MenuItem() { IsEnabled = true, Header = player };
        deleteChat.Items.Add(item);

        item.Click += (object sender, RoutedEventArgs e) =>
        {
          if (MessageBox.Show("Clear all chat for " + player + ", are you sure?", "Clear Chat Archive", MessageBoxButton.YesNo) == MessageBoxResult.Yes
            && ChatManager.DeleteArchivedPlayer(player))
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
      string name = filtered?.FirstOrDefault()?.Name;
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);

      var damageOptions = new GenerateStatsOptions { Name = name, RequestChartData = opened.ContainsKey(damageChartIcon.Tag as string) };
      damageOptions.Npcs.AddRange(filtered);
      damageOptions.RequestSummaryData = opened.ContainsKey(damageSummaryIcon.Tag as string);

      var healingOptions = new GenerateStatsOptions { Name = name, RequestChartData = opened.ContainsKey(healingChartIcon.Tag as string) };
      healingOptions.Npcs.AddRange(filtered);
      healingOptions.RequestSummaryData = opened.ContainsKey(healingSummaryIcon.Tag as string);

      var tankingOptions = new GenerateStatsOptions { Name = name, RequestChartData = opened.ContainsKey(tankingChartIcon.Tag as string) };
      tankingOptions.Npcs.AddRange(filtered);

      if (opened.TryGetValue(tankingSummaryIcon.Tag as string, out ContentControl control))
      {
        tankingOptions.RequestSummaryData = true;
        tankingOptions.DamageType = ((TankingSummary)control.Content).DamageType;
      }

      Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageOptions));
      Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingOptions));
      Task.Run(() => TankingStatsManager.Instance.BuildTotalStats(tankingOptions));
    }

    private void MenuItemExportHTMLClick(object sender, RoutedEventArgs e)
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      var tables = new Dictionary<string, SummaryTable>();

      if (opened.TryGetValue(damageSummaryIcon.Tag as string, out ContentControl control))
      {
        tables.Add(DockingManager.GetHeader(control) as string, (DamageSummary)control.Content);
      }

      if (opened.TryGetValue(healingSummaryIcon.Tag as string, out ContentControl control2))
      {
        tables.Add(DockingManager.GetHeader(control2) as string, (HealingSummary)control2.Content);
      }

      if (opened.TryGetValue(tankingSummaryIcon.Tag as string, out ContentControl control3))
      {
        tables.Add(DockingManager.GetHeader(control3) as string, (TankingSummary)control3.Content);
      }

      if (tables.Count > 0)
      {
        TextFormatUtils.ExportAsHTML(tables);
      }
      else
      {
        _ = MessageBox.Show("Nothing to Save. Display a Summary View and Try Again.", EQLogParser.Resource.FILEMENU_EXPORT_SUMMARY,
          MessageBoxButton.OK, MessageBoxImage.Exclamation);
      }
    }

    private void ViewErrorLogClick(object sender, RoutedEventArgs e)
    {
      using (Process fileopener = new Process())
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

    private void ToggleHideOnMinimizeClick(object sender, RoutedEventArgs e)
    {
      IsHideOnMinimizeEnabled = !IsHideOnMinimizeEnabled;
      ConfigUtil.SetSetting("HideWindowOnMinimize", IsHideOnMinimizeEnabled.ToString(CultureInfo.CurrentCulture));
      enableHideOnMinimizeIcon.Visibility = IsHideOnMinimizeEnabled ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleAoEHealingClick(object sender, RoutedEventArgs e)
    {
      IsAoEHealingEnabled = !IsAoEHealingEnabled;
      ConfigUtil.SetSetting("IncludeAoEHealing", IsAoEHealingEnabled.ToString(CultureInfo.CurrentCulture));
      enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;

      var options = new GenerateStatsOptions() { RequestChartData = true, RequestSummaryData = true };
      Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(options, true));
    }

    private void ToggleAutoMonitorClick(object sender, RoutedEventArgs e)
    {
      ConfigUtil.SetSetting("AutoMonitor", (enableAutoMonitorIcon.Visibility == Visibility.Hidden).ToString(CultureInfo.CurrentCulture));
      enableAutoMonitorIcon.Visibility = enableAutoMonitorIcon.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private void ToggleDamageOverlayClick(object sender, RoutedEventArgs e)
    {
      var enabled = OverlayUtil.ToggleOverlay(Dispatcher);
      enableDamageOverlayIcon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
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

    private void ToggleIgnoreCharmPetsClick(object sender, RoutedEventArgs e)
    {
      IsIgnoreCharmPetsEnabled = !IsIgnoreCharmPetsEnabled;
      ConfigUtil.SetSetting("IgnoreCharmPets", IsIgnoreCharmPetsEnabled.ToString(CultureInfo.CurrentCulture));
      ignoreCharmPetsIcon.Visibility = IsIgnoreCharmPetsEnabled ? Visibility.Visible : Visibility.Hidden;
      MessageBox.Show("Restart EQLogParser when changing the Ignore Charm Pets setting for it to take effect.");
    }

    private void UpdateDamageOption(ImageAwesome icon, bool enabled, string option)
    {
      ConfigUtil.SetSetting(option, enabled.ToString(CultureInfo.CurrentCulture));
      icon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
      var options = new GenerateStatsOptions() { RequestChartData = true, RequestSummaryData = true };
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
        Helpers.OpenWindow(dockSite, opened, out _, typeof(EventViewer), eventIcon.Tag as string, "Special Events");
      }
      else if (e.Source == playerLootMenuItem)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        Helpers.OpenWindow(dockSite, opened, out _, typeof(LootViewer), playerLootIcon.Tag as string, "Looted Items");
      }
      else if (e.Source == eqLogMenuItem)
      {
        int found = LogWindows.FindIndex(used => !used);
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
      else if ((sender as MenuItem)?.Icon is ImageAwesome icon && icon.Tag is string name)
      {
        // any other core windows that just get hidden
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        if (opened.TryGetValue(name, out ContentControl control) && control.Tag.ToString() == "Hide")
        {
          var state = (DockingManager.GetState(control) == DockState.Hidden) ? DockState.Dock : DockState.Hidden;
          DockingManager.SetState(control, state);
        }
      }
    }

    private void OpenDamageChart()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenChart(opened, dockSite, damageChartIcon.Tag as string, DAMAGE_CHOICES, "Damage Chart", ChartTab, true, out ContentControl control))
      {
        var summary = control.Content as DamageSummary;
        var options = new GenerateStatsOptions { RequestChartData = true };
        DamageStatsManager.Instance.FireChartEvent(options, "UPDATE", summary?.GetSelectedStats());
      }
    }

    private void OpenHealingChart()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenChart(opened, dockSite, healingChartIcon.Tag as string, HEALING_CHOICES, "Healing Chart", ChartTab, false, out ContentControl control))
      {
        var summary = control.Content as HealingSummary;
        var options = new GenerateStatsOptions { RequestChartData = true };
        HealingStatsManager.Instance.FireChartEvent(options, "UPDATE", summary?.GetSelectedStats());
      }
    }

    private void OpenTankingChart()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenChart(opened, dockSite, tankingChartIcon.Tag as string, TANKING_CHOICES, "Tanking Chart", ChartTab, false, out ContentControl control))
      {
        var summary = control.Content as TankingSummary;
        var options = new GenerateStatsOptions { RequestChartData = true };
        TankingStatsManager.Instance.FireChartEvent(options, "UPDATE", summary?.GetSelectedStats());
      }
    }

    private void OpenDamageSummary()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenWindow(dockSite, opened, out ContentControl control, typeof(DamageSummary), damageSummaryIcon.Tag as string, "Damage Summary"))
      {
        (control.Content as DamageSummary).EventsSelectionChange += DamageSummary_SelectionChanged;
        if (DamageStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var damageOptions = new GenerateStatsOptions() { RequestSummaryData = true };
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(damageOptions));
        }
      }
    }

    private void OpenHealingSummary()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenWindow(dockSite, opened, out ContentControl control, typeof(HealingSummary), healingSummaryIcon.Tag as string, "Healing Summary"))
      {
        (control.Content as HealingSummary).EventsSelectionChange += HealingSummary_SelectionChanged;
        if (HealingStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var healingOptions = new GenerateStatsOptions() { RequestSummaryData = true };
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(healingOptions));
        }
      }
    }

    private void OpenTankingSummary()
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      if (Helpers.OpenWindow(dockSite, opened, out ContentControl control, typeof(TankingSummary), tankingSummaryIcon.Tag as string, "Tanking Summary"))
      {
        (control.Content as TankingSummary).EventsSelectionChange += TankingSummary_SelectionChanged;
        if (TankingStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var tankingOptions = new GenerateStatsOptions() { RequestSummaryData = true, DamageType = (control.Content as TankingSummary).DamageType };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    private void DamageSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "SELECT", data.Selected);
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview?.UpdateParse(Labels.DAMAGEPARSE, data.Selected);
    }

    private void HealingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      HealingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions() { RequestChartData = true }, "SELECT", data.Selected);
      bool addTopParse = data.Selected?.Count == 1 && data.Selected[0].SubStats?.Count > 0;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview.UpdateParse(data, HealingStatsManager.Instance, addTopParse, Labels.HEALPARSE, Labels.TOPHEALSPARSE);
    }

    private void TankingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "SELECT", data.Selected);
      bool addReceiveParse = data.Selected?.Count == 1 && data.Selected[0].SubStats2?.Count > 0;
      var preview = playerParseTextWindow.Content as ParsePreview;
      preview.UpdateParse(data, TankingStatsManager.Instance, addReceiveParse, Labels.TANKPARSE, Labels.RECEIVEDHEALPARSE);
    }

    private void MenuItemSelectLogFileClick(object sender, RoutedEventArgs e)
    {
      int lastMins = -1;
      if (sender is MenuItem item && !string.IsNullOrEmpty(item.Tag as string))
      {
        lastMins = Convert.ToInt32(item.Tag.ToString(), CultureInfo.CurrentCulture) * 60;
      }

      OpenLogFile(LogOption.OPEN, null, lastMins);
    }

    private void UpdateLoadingProgress()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (EQLogReader != null)
        {
          OverlayUtil.CloseOverlay();

          var seconds = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds);
          double filePercent = EQLogReader.FileSize > 0 ? Math.Min(Convert.ToInt32((double)FilePosition / EQLogReader.FileSize * 100), 100) : 100;

          if (filePercent < 100)
          {
            statusText.Text = string.Format(CultureInfo.CurrentCulture, "Reading Log.. {0}% in {1} seconds", filePercent, seconds);
          }
          else
          {
            var procPercent = Convert.ToInt32(Math.Min(CastProcessor.GetPercentComplete(), DamageProcessor.GetPercentComplete()));
            statusText.Text = string.Format(CultureInfo.CurrentCulture, "Processing... {0}% in {1} seconds", procPercent, seconds);
          }

          statusText.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;

          if (((filePercent >= 100 && CastProcessor.GetPercentComplete() >= 100 && DamageProcessor.GetPercentComplete() >= 100
            && HealingProcessor.GetPercentComplete() >= 100 && MiscProcessor.GetPercentComplete() >= 100) ||
            CurrentLogOption == LogOption.MONITOR) && EQLogReader.FileLoadComplete)
          {
            if (filePercent >= 100 || CurrentLogOption == LogOption.MONITOR)
            {
              statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
              statusText.Text = "Monitoring Active";
            }

            ConfigUtil.SetSetting("LastOpenedFile", CurrentLogFile);
            OverlayUtil.OpenIfEnabled(Dispatcher);
            LOG.Info("Finished Loading Log File in " + seconds.ToString(CultureInfo.CurrentCulture) + " seconds.");
            EventsLogLoadingComplete?.Invoke(this, true);
          }
          else
          {
            _ = Task.Delay(500).ContinueWith(task => UpdateLoadingProgress(), TaskScheduler.Default);
          }
        }
      });
    }

    private void PetMappingDropDownSelectionChanged(object sender, CurrentCellDropDownSelectionChangedEventArgs e)
    {
      if (sender is SfDataGrid dataGrid && e.RowColumnIndex.RowIndex > 0 && dataGrid.View.GetRecordAt(e.RowColumnIndex.RowIndex - 1).Data is PetMapping mapping)
      {
        dataGrid.SelectionController.CurrentCellManager.EndEdit();
        PlayerManager.Instance.AddPetToPlayer(mapping.Pet, mapping.Owner);
      }
    }

    private void OpenLogFile(LogOption option, string previousFile = null, int lastMins = -1)
    {
      CurrentLogOption = option;

      try
      {
        string theFile;
        bool success = true;
        if (previousFile != null)
        {
          theFile = previousFile;
        }
        else
        {
          // WPF doesn't have its own file chooser so use Win32 Version
          Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
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

          string name = "You";
          string server = "Unknown";
          if (theFile.Length > 0)
          {
            LOG.Info("Selected Log File: " + theFile);

            string file = Path.GetFileName(theFile);
            MatchCollection matches = ParseFileName.Matches(file);
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

          DataManager.Instance.Clear();
          PlayerChatManager = new ChatManager();
          CurrentLogFile = theFile;
          NpcDamageManager.ResetTime();
          EQLogReader = new LogReader(theFile, FileLoadingCallback, CurrentLogOption == LogOption.MONITOR, lastMins);
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

      if (!string.IsNullOrEmpty(line) && line.Length > 30)
      {
        var lineData = new LineData { Action = line.Substring(ACTION_INDEX), LineNumber = LineCount, BeginTime = dateTime };

        // avoid having other things parse chat by accident
        if (ChatLineParser.Process(lineData, line) is ChatType chatType)
        {
          PlayerChatManager.Add(chatType);
        }
        // populates lineData.Action
        else if (PreLineParser.NeedProcessing(lineData) && lineData.Action != null)
        {
          CastProcessor.Add(lineData);
          HealingProcessor.Add(lineData);
          MiscProcessor.Add(lineData);
          DamageProcessor.Add(lineData);
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
      if (sender is FrameworkElement icon && icon.Tag is string name)
      {
        var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
        if (opened.TryGetValue(name, out ContentControl control))
        {
          icon.Visibility = DockingManager.GetState(control) != DockState.Hidden ? Visibility.Visible : Visibility.Hidden;
        }
        else
        {
          icon.Visibility = Visibility.Hidden;
        }
      }
    }

    private void RemovePetMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is ImageAwesome image && image.DataContext is SortableName sortable)
      {
        PlayerManager.Instance.RemoveVerifiedPet(sortable.Name);
      }
    }

    private void RemovePlayerMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is ImageAwesome image && image.DataContext is SortableName sortable)
      {
        PlayerManager.Instance.RemoveVerifiedPlayer(sortable.Name);
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
      if (WindowState != WindowState.Minimized)
      {
        ShowInTaskbar = true;
      }
      else if (IsHideOnMinimizeEnabled)
      {
        ShowInTaskbar = false;
        Hide();
      }
    }

    private void WindowClosed(object sender, EventArgs e)
    {
      var opened = MainActions.GetOpenWindows(dockSite, ChartTab);
      ConfigUtil.SetSetting("ShowDamageSummaryAtStartup", opened.ContainsKey(damageSummaryIcon.Tag as string).ToString());
      ConfigUtil.SetSetting("ShowHealingSummaryAtStartup", opened.ContainsKey(healingSummaryIcon.Tag as string).ToString());
      ConfigUtil.SetSetting("ShowTankingSummaryAtStartup", opened.ContainsKey(tankingSummaryIcon.Tag as string).ToString());

      StopProcessing();
      OverlayUtil.CloseOverlay();
      PlayerChatManager?.Dispose();
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
        string title = DockingManager.GetHeader(window) as string;
        int last = title.LastIndexOf(" ");
        if (last > -1)
        {
          string value = title.Substring(last, title.Length - last);
          if (int.TryParse(value, out int result) && result > 0 && LogWindows.Count >= result)
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

    private void dockSite_CloseButtonClick(object sender, CloseButtonEventArgs e) => CloseTab(e.TargetItem as ContentControl);

    private void dockSite_WindowClosing(object sender, WindowClosingEventArgs e) => CloseTab(e.TargetItem as ContentControl);

    private void MenuItemSelectMonitorLogFileClick(object sender, RoutedEventArgs e) => OpenLogFile(LogOption.MONITOR);

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

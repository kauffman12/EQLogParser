using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
  public partial class MainWindow : Window, IDisposable
  {
    // binding property
    public ObservableCollection<SortableName> VerifiedPlayersProperty { get; } = new ObservableCollection<SortableName>();

    // global settings
    internal static bool IsAoEHealingEnabled = true;
    internal static bool IsBaneDamageEnabled = false;
    internal static bool IsDamageOverlayEnabled = false;
    internal static bool IsIgnoreIntialPullDamageEnabled = false;
    internal static bool IsHideOverlayOtherPlayersEnabled = false;
    internal static bool IsHideOnMinimizeEnabled = false;
    internal static readonly SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    internal static readonly SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    internal static readonly SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    internal static readonly SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private enum LogOption { OPEN, MONITOR, ARCHIVE };
    private static readonly Regex ParseFileName = new Regex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.txt", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly List<string> DAMAGE_CHOICES = new List<string>() { "DPS", "Damage", "Av Hit", "% Crit" };
    private static readonly List<string> HEALING_CHOICES = new List<string>() { "HPS", "Healing", "Av Heal", "% Crit" };
    private static readonly List<string> TANKING_CHOICES = new List<string>() { "DPS", "Damaged", "Av Hit" };

    private const string APP_NAME = "EQ Log Parser";
    private const string VERSION = "v1.6.25";
    private const string PLAYER_LIST_TITLE = "Verified Player List ({0})";
    private const string PETS_LIST_TITLE = "Verified Pet List ({0})";

    private static long FilePosition = 0;
    private static ActionProcessor<string> CastProcessor = null;
    private static ActionProcessor<string> DamageProcessor = null;
    private static ActionProcessor<string> HealingProcessor = null;
    private static ActionProcessor<string> MiscProcessor = null;

    // progress window
    private static DateTime StartLoadTime;
    private static LogOption CurrentLogOption;

    private readonly ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private readonly ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();

    private ChatManager PlayerChatManager = null;
    private readonly NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private readonly Dictionary<string, DockingWindow> IconToWindow;
    private DocumentWindow ChatWindow = null;
    private DocumentWindow DamageWindow = null;
    private DocumentWindow HealingWindow = null;
    private DocumentWindow TankingWindow = null;
    private DocumentWindow DamageChartWindow = null;
    private DocumentWindow HealingChartWindow = null;
    private DocumentWindow TankingChartWindow = null;
    private DocumentWindow EventWindow = null;
    private DocumentWindow LootWindow = null;

    private LogReader EQLogReader = null;
    private OverlayWindow Overlay = null;
    private int BusyCount = 0;

    public MainWindow()
    {
      try
      {
        InitializeComponent();

        // update titles
        Title = APP_NAME + " " + VERSION;

        ConfigUtil.Debug = false;     

        // upate helper
        Helpers.SetDispatcher(Dispatcher);

        // used for setting menu icons based on open windows
        IconToWindow = new Dictionary<string, DockingWindow>()
        {
          { npcIcon.Name, npcWindow }, { verifiedPlayersIcon.Name, verifiedPlayersWindow },
          { verifiedPetsIcon.Name, verifiedPetsWindow }, { petMappingIcon.Name, petMappingWindow },
          { playerParseIcon.Name, playerParseTextWindow }, { fileProgessIcon.Name, progressWindow }
        };

        // Clear/Reset
        DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;

        // verified pets table
        verifiedPetsGrid.ItemsSource = VerifiedPetsView;
        PlayerManager.Instance.EventsNewVerifiedPet += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          Helpers.InsertNameIntoSortedList(name, VerifiedPetsView);
          verifiedPetsWindow.Title = string.Format(CultureInfo.CurrentCulture, PETS_LIST_TITLE, VerifiedPetsView.Count);
        });

        // pet -> players
        petMappingGrid.ItemsSource = PetPlayersView;
        PlayerManager.Instance.EventsNewPetMapping += (sender, mapping) =>
        {
          Dispatcher.InvokeAsync(() =>
          {
            var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(mapping.Pet, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.Owner != mapping.Owner)
            {
              existing.Owner = mapping.Owner;
            }
            else
            {
              PetPlayersView.Add(mapping);
            }

            petMappingWindow.Title = "Pet Owners (" + PetPlayersView.Count + ")";
          });

          CheckComputeStats();
        };

        PlayerManager.Instance.EventsRemoveVerifiedPet += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          var found = VerifiedPetsView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPetsView.Remove(found);
            verifiedPetsWindow.Title = string.Format(CultureInfo.CurrentCulture, PETS_LIST_TITLE, VerifiedPetsView.Count);

            var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              petMappingWindow.Title = "Pet Owners (" + PetPlayersView.Count + ")";
            }
            CheckComputeStats();
          }
        });

        // verified player table
        verifiedPlayersGrid.ItemsSource = VerifiedPlayersProperty;
        PlayerManager.Instance.EventsNewVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          Helpers.InsertNameIntoSortedList(name, VerifiedPlayersProperty);
          verifiedPlayersWindow.Title = string.Format(CultureInfo.CurrentCulture, PLAYER_LIST_TITLE, VerifiedPlayersProperty.Count);
        });

        PlayerManager.Instance.EventsRemoveVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          var found = VerifiedPlayersProperty.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPlayersProperty.Remove(found);
            verifiedPlayersWindow.Title = string.Format(CultureInfo.CurrentCulture, PLAYER_LIST_TITLE, VerifiedPlayersProperty.Count);

            var existing = PetPlayersView.FirstOrDefault(item => item.Owner.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              petMappingWindow.Title = "Pet Owners (" + PetPlayersView.Count + ")";
            }
            CheckComputeStats();
          }
        });

        (npcWindow.Content as FightTable).EventsSelectionChange += (sender, data) => ComputeStats();
        DamageStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(DamageChartWindow, sender, data);
        HealingStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(HealingChartWindow, sender, data);
        TankingStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(TankingChartWindow, sender, data);

        // Setup themes
        ThemeManager.BeginUpdate();
        ThemeManager.AreNativeThemesEnabled = true;
        SharedThemeCatalogRegistrar.Register();
        DockingThemeCatalogRegistrar.Register();
        ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();

        // Bane Damage
        string value = ConfigUtil.GetApplicationSetting("IncludeBaneDamage");
        IsBaneDamageEnabled = value != null && bool.TryParse(value, out bool bValue) && bValue;
        enableBaneDamageIcon.Visibility = IsBaneDamageEnabled ? Visibility.Visible : Visibility.Hidden;

        // Damage Overlay
        value = ConfigUtil.GetApplicationSetting("IsDamageOverlayEnabled");
        IsDamageOverlayEnabled = value != null && bool.TryParse(value, out bValue) && bValue;
        enableDamageOverlayIcon.Visibility = IsDamageOverlayEnabled ? Visibility.Visible : Visibility.Hidden;

        // Ignore Intitial Pull
        value = ConfigUtil.GetApplicationSetting("IngoreInitialPullDamage");
        IsIgnoreIntialPullDamageEnabled = value != null && bool.TryParse(value, out bool bValue2) && bValue2;
        enableIgnoreInitialPullDamageIcon.Visibility = IsIgnoreIntialPullDamageEnabled ? Visibility.Visible : Visibility.Hidden;

        // AoE healing
        value = ConfigUtil.GetApplicationSetting("IncludeAoEHealing");
        IsAoEHealingEnabled = value == null || bool.TryParse(value, out bValue) && bValue;
        enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;

        // Hide other player names on overlay
        value = ConfigUtil.GetApplicationSetting("HideOverlayOtherPlayers");
        IsHideOverlayOtherPlayersEnabled = value != null && bool.TryParse(value, out bValue2) && bValue2;
        enableHideOverlayOtherPlayersIcon.Visibility = IsHideOverlayOtherPlayersEnabled ? Visibility.Visible : Visibility.Hidden;

        UpdateDeleteChatMenu();

        // Show Tanking Summary at startup
        value = ConfigUtil.GetApplicationSetting("ShowTankingSummaryAtStartup");
        if (value != null && bool.TryParse(value, out bValue2) && bValue2)
        {
          OpenTankingSummary();
        }

        // Show Healing Summary at startup
        value = ConfigUtil.GetApplicationSetting("ShowHealingSummaryAtStartup");
        if (value != null && bool.TryParse(value, out bValue2) && bValue2)
        {
          OpenHealingSummary();
        }

        // Show Healing Summary at startup
        value = ConfigUtil.GetApplicationSetting("ShowDamageSummaryAtStartup");
        if (value == null || (bool.TryParse(value, out bValue2) && bValue2))
        {
          OpenDamageSummary();
        }

        // Show Tanking Summary at startup
        value = ConfigUtil.GetApplicationSetting("ShowTankingChartAtStartup");
        if (value != null && bool.TryParse(value, out bValue2) && bValue2)
        {
          OpenTankingChart();
        }

        // Show Healing Summary at startup
        value = ConfigUtil.GetApplicationSetting("ShowHealingChartAtStartup");
        if (value != null && bool.TryParse(value, out bValue2) && bValue2)
        {
          OpenHealingChart();
        }

        // Show Healing Summary at startup
        value = ConfigUtil.GetApplicationSetting("ShowDamageChartAtStartup");
        if (value != null && bool.TryParse(value, out bValue2) && bValue2)
        {
          OpenDamageChart();
        }

        // Hide window when minimized
        value = ConfigUtil.GetApplicationSetting("HideWindowOnMinimize");
        IsHideOnMinimizeEnabled = value != null && bool.TryParse(value, out bValue2) && bValue2;

        LOG.Info("Initialized Components");
      }
      catch (Exception e)
      {
        LOG.Error(e);
        throw;
      }
      finally
      {
        ThemeManager.EndUpdate();
      }
    }

    internal void Busy(bool state)
    {
      BusyCount += state ? 1 : -1;
      BusyCount = BusyCount < 0 ? 0 : BusyCount;
      busyIcon.Visibility = BusyCount == 0 ? Visibility.Hidden : Visibility.Visible;
    }

    internal void CopyToEQClick(string type)
    {
      (playerParseTextWindow.Content as ParsePreview)?.CopyToEQClick(type);
    }

    internal void OpenOverlay(bool configure = false, bool saveFirst = false)
    {
      if (saveFirst)
      {
        ConfigUtil.Save();
      }

      Dispatcher.InvokeAsync(() =>
      {
        Overlay?.Close();
        Overlay = new OverlayWindow(configure);
        Overlay.Show();
      });
    }

    internal void ResetOverlay()
    {
      Overlay?.Close();

      if (IsDamageOverlayEnabled)
      {
        OpenOverlay();
      }
    }

    internal void CloseOverlay()
    {
      Overlay?.Close();
    }

    internal void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      (playerParseTextWindow.Content as ParsePreview)?.AddParse(Labels.DAMAGEPARSE, DamageStatsManager.Instance, combined, selected, true);
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
          if (MessageBox.Show("Clear all chat for " + player + ", are you sure?", "Clear Chat Archive", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
          {
            if (ChatManager.DeleteArchivedPlayer(player))
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
          }
        };
      });

      deleteChat.IsEnabled = deleteChat.Items.Count > 0;
    }

    private void CheckComputeStats()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if ((npcWindow?.Content as FightTable)?.HasSelected() == true)
        {
          ComputeStats();
        }
      });
    }

    private void Instance_EventsClearedActiveData(object _, bool _2)
    {
      (DamageWindow?.Content as DamageSummary)?.Clear();
      (HealingWindow?.Content as HealingSummary)?.Clear();
      (TankingWindow?.Content as TankingSummary)?.Clear();
      (DamageChartWindow?.Content as LineChart)?.Clear();
      (HealingChartWindow?.Content as LineChart)?.Clear();
      (TankingChartWindow?.Content as LineChart)?.Clear();
    }

    private void Window_Close(object sender, EventArgs e)
    {
      Close();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
      ConfigUtil.SetApplicationSetting("ShowDamageSummaryAtStartup", (DamageWindow?.IsOpen == true).ToString(CultureInfo.CurrentCulture));
      ConfigUtil.SetApplicationSetting("ShowHealingSummaryAtStartup", (HealingWindow?.IsOpen == true).ToString(CultureInfo.CurrentCulture));
      ConfigUtil.SetApplicationSetting("ShowTankingSummaryAtStartup", (TankingWindow?.IsOpen == true).ToString(CultureInfo.CurrentCulture));
      ConfigUtil.SetApplicationSetting("ShowDamageChartAtStartup", (DamageChartWindow?.IsOpen == true).ToString(CultureInfo.CurrentCulture));
      ConfigUtil.SetApplicationSetting("ShowHealingChartAtStartup", (HealingChartWindow?.IsOpen == true).ToString(CultureInfo.CurrentCulture));
      ConfigUtil.SetApplicationSetting("ShowTankingChartAtStartup", (TankingChartWindow?.IsOpen == true).ToString(CultureInfo.CurrentCulture));

      StopProcessing();
      CloseOverlay();
      taskBarIcon?.Dispose();
      PlayerChatManager?.Dispose();
      ConfigUtil.Save();
      PlayerManager.Instance?.Save();
      Application.Current.Shutdown();
    }

    private void HandleChartUpdateEvent(DocumentWindow window, object _, DataPointEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (window?.IsOpen == true)
        {
          (window.Content as LineChart)?.HandleUpdateEvent(e);
        }
      });
    }

    private void ComputeStats()
    {
      var filtered = (npcWindow?.Content as FightTable)?.GetSelectedItems().OrderBy(npc => npc.ID);
      string name = filtered?.FirstOrDefault()?.Name;

      var damageOptions = new GenerateStatsOptions() { Name = name, RequestChartData = DamageChartWindow?.IsOpen == true };
      damageOptions.Npcs.AddRange(filtered);
      if (DamageWindow?.Content is DamageSummary damageSummary && DamageWindow?.IsOpen == true)
      {
        damageOptions.RequestSummaryData = true;
      }

      var healingOptions = new GenerateStatsOptions() { Name = name, RequestChartData = HealingChartWindow?.IsOpen == true };
      healingOptions.Npcs.AddRange(filtered);
      if (HealingWindow?.Content is HealingSummary healingSummary && HealingWindow?.IsOpen == true)
      {
        healingOptions.RequestSummaryData = true;
      }

      var tankingOptions = new GenerateStatsOptions() { Name = name, RequestChartData = TankingChartWindow?.IsOpen == true };
      tankingOptions.Npcs.AddRange(filtered);
      if (TankingWindow?.Content is TankingSummary tankingSummary && TankingWindow?.IsOpen == true)
      {
        tankingOptions.RequestSummaryData = true;
      }

      Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageOptions));
      Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingOptions));
      Task.Run(() => TankingStatsManager.Instance.BuildTotalStats(tankingOptions));
    }

    private void PlayerParseTextWindow_Loaded(object sender, RoutedEventArgs e)
    {
      playerParseTextWindow.State = DockingWindowState.AutoHide;
    }

    private void MenuItemExportHTMLClick(object sender, RoutedEventArgs e)
    {
      var tables = new Dictionary<string, SummaryTable>();
      if (DamageWindow?.Content is DamageSummary damageSummary && DamageWindow?.IsOpen == true)
      {
        tables.Add(DamageWindow.Title, damageSummary);
      }

      if (HealingWindow?.Content is HealingSummary healingSummary && HealingWindow?.IsOpen == true)
      {
        tables.Add(HealingWindow.Title, healingSummary);
      }

      if (TankingWindow?.Content is TankingSummary tankingSummary && TankingWindow?.IsOpen == true)
      {
        tables.Add(TankingWindow.Title, tankingSummary);
      }

      if (tables.Count > 0)
      {
        TextFormatUtils.ExportAsHTML(tables);
      }
      else
      {
        _ = MessageBox.Show("Nothing to Save. Display a Summary View and Try Again.", Properties.Resources.FILEMENU_EXPORT_SUMMARY, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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

    private void ToggleHideOverlayOtherPlayersClick(object sender, RoutedEventArgs e)
    {
      IsHideOverlayOtherPlayersEnabled = !IsHideOverlayOtherPlayersEnabled;
      ConfigUtil.SetApplicationSetting("HideOverlayOtherPlayers", IsHideOverlayOtherPlayersEnabled.ToString(CultureInfo.CurrentCulture));
      enableHideOverlayOtherPlayersIcon.Visibility = IsHideOverlayOtherPlayersEnabled ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleHideOnMinimizeClick(object sender, RoutedEventArgs e)
    {
      IsHideOnMinimizeEnabled = !IsHideOnMinimizeEnabled;
      ConfigUtil.SetApplicationSetting("HideWindowOnMinimize", IsHideOnMinimizeEnabled.ToString(CultureInfo.CurrentCulture));
      enableHideOnMinimizeIcon.Visibility = IsHideOnMinimizeEnabled ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleAoEHealingClick(object sender, RoutedEventArgs e)
    {
      IsAoEHealingEnabled = !IsAoEHealingEnabled;
      ConfigUtil.SetApplicationSetting("IncludeAoEHealing", IsAoEHealingEnabled.ToString(CultureInfo.CurrentCulture));
      enableAoEHealingIcon.Visibility = IsAoEHealingEnabled ? Visibility.Visible : Visibility.Hidden;

      var options = new GenerateStatsOptions() { RequestChartData = true, RequestSummaryData = true };
      Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(options, true));
    }

    private void ToggleDamageOverlayClick(object sender, RoutedEventArgs e)
    {
      IsDamageOverlayEnabled = !IsDamageOverlayEnabled;
      ConfigUtil.SetApplicationSetting("IsDamageOverlayEnabled", IsDamageOverlayEnabled.ToString(CultureInfo.CurrentCulture));
      enableDamageOverlayIcon.Visibility = IsDamageOverlayEnabled ? Visibility.Visible : Visibility.Hidden;

      if (IsDamageOverlayEnabled)
      {
        OpenOverlay(true, false);
      }
      else
      {
        CloseOverlay();
      }
    }

    private void ToggleBaneDamageClick(object sender, RoutedEventArgs e)
    {
      IsBaneDamageEnabled = !IsBaneDamageEnabled;
      ConfigUtil.SetApplicationSetting("IncludeBaneDamage", IsBaneDamageEnabled.ToString(CultureInfo.CurrentCulture));
      enableBaneDamageIcon.Visibility = IsBaneDamageEnabled ? Visibility.Visible : Visibility.Hidden;

      var options = new GenerateStatsOptions() { RequestChartData = true, RequestSummaryData = true };
      Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(options));
    }

    private void ToggleIgnoreInitialPullDamageClick(object sender, RoutedEventArgs e)
    {
      IsIgnoreIntialPullDamageEnabled = !IsIgnoreIntialPullDamageEnabled;
      ConfigUtil.SetApplicationSetting("IngoreInitialPullDamage", IsIgnoreIntialPullDamageEnabled.ToString(CultureInfo.CurrentCulture));
      enableIgnoreInitialPullDamageIcon.Visibility = IsIgnoreIntialPullDamageEnabled ? Visibility.Visible : Visibility.Hidden;

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
        OpenChat();
      }
      else if (e.Source == eventMenuItem)
      {
        OpenEventViewer();
      }
      else if (e.Source == playerLootMenuItem)
      {
        OpenLootList();
      }
      else
      {
        if ((sender as MenuItem)?.Icon is ImageAwesome icon && IconToWindow.ContainsKey(icon.Name))
        {
          Helpers.OpenWindow(IconToWindow[icon.Name]);
        }
      }
    }

    private bool OpenLineChart(DocumentWindow window, DocumentWindow other1, DocumentWindow other2, FrameworkElement icon, string title, List<string> choices, out DocumentWindow newWindow)
    {
      bool updated = false;
      newWindow = window;

      if (newWindow?.IsOpen == true)
      {
        newWindow.Close();
        newWindow = null;
      }
      else
      {
        updated = true;
        var chart = new LineChart(choices);
        newWindow = new DocumentWindow(dockSite, title, title, null, chart);
        IconToWindow[icon.Name] = newWindow;

        Helpers.OpenWindow(newWindow);
        newWindow.CanFloat = true;
        newWindow.CanClose = true;

        if (other1?.IsOpen == true || other2?.IsOpen == true)
        {
          newWindow.MoveToNextContainer();
        }
        else
        {
          newWindow.MoveToNewHorizontalContainer();
        }
      }

      return updated;
    }

    private void OpenDamageChart()
    {
      if (OpenLineChart(DamageChartWindow, HealingChartWindow, TankingChartWindow, damageChartIcon, "Damage Chart", DAMAGE_CHOICES, out DamageChartWindow))
      {
        var summary = DamageWindow?.Content as DamageSummary;
        var options = new GenerateStatsOptions() { RequestChartData = true };
        DamageStatsManager.Instance.FireUpdateEvent(options, summary?.GetSelectedStats(), summary?.GetFilter());
      }
    }

    private void OpenHealingChart()
    {
      if (OpenLineChart(HealingChartWindow, DamageChartWindow, TankingChartWindow, healingChartIcon, "Healing Chart", HEALING_CHOICES, out HealingChartWindow))
      {
        var summary = HealingWindow?.Content as HealingSummary;
        var options = new GenerateStatsOptions() { RequestChartData = true };
        HealingStatsManager.Instance.FireUpdateEvent(options, summary?.GetSelectedStats(), summary?.GetFilter());
      }
    }

    private void OpenTankingChart()
    {
      if (OpenLineChart(TankingChartWindow, DamageChartWindow, HealingChartWindow, tankingChartIcon, "Tanking Chart", TANKING_CHOICES, out TankingChartWindow))
      {
        var summary = TankingWindow?.Content as TankingSummary;
        var options = new GenerateStatsOptions() { RequestChartData = true };
        TankingStatsManager.Instance.FireUpdateEvent(options, summary?.GetSelectedStats(), summary?.GetFilter());
      }
    }

    private void OpenDamageSummary()
    {
      if (DamageWindow?.IsOpen == true)
      {
        DamageWindow.Close();
      }
      else
      {
#pragma warning disable CA2000 // Dispose objects before losing scope
        var damageSummary = new DamageSummary();
#pragma warning restore CA2000 // Dispose objects before losing scope

        damageSummary.EventsSelectionChange += DamageSummary_SelectionChanged;
        DamageWindow = new DocumentWindow(dockSite, "damageSummary", "Damage Summary", null, damageSummary);
        IconToWindow[damageSummaryIcon.Name] = DamageWindow;

        Helpers.OpenWindow(DamageWindow);
        if (HealingWindow?.IsOpen == true || TankingWindow?.IsOpen == true)
        {
          DamageWindow.MoveToPreviousContainer();
        }

        RepositionCharts(DamageWindow);

        if (DamageStatsManager.Instance.DamageGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var damageOptions = new GenerateStatsOptions() { RequestSummaryData = true };
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(damageOptions));
        }
      }
    }

    private void OpenHealingSummary()
    {
      if (HealingWindow?.IsOpen == true)
      {
        HealingWindow.Close();
      }
      else
      {
#pragma warning disable CA2000 // Dispose objects before losing scope
        var healingSummary = new HealingSummary();
#pragma warning restore CA2000 // Dispose objects before losing scope

        healingSummary.EventsSelectionChange += HealingSummary_SelectionChanged;
        HealingWindow = new DocumentWindow(dockSite, "healingSummary", "Healing Summary", null, healingSummary);
        IconToWindow[healingSummaryIcon.Name] = HealingWindow;

        Helpers.OpenWindow(HealingWindow);
        if (DamageWindow?.IsOpen == true || TankingWindow?.IsOpen == true)
        {
          HealingWindow.MoveToPreviousContainer();
        }

        RepositionCharts(HealingWindow);

        if (HealingStatsManager.Instance.HealingGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var healingOptions = new GenerateStatsOptions() { RequestSummaryData = true };
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(healingOptions));
        }
      }
    }

    private void OpenTankingSummary()
    {
      if (TankingWindow?.IsOpen == true)
      {
        TankingWindow.Close();
      }
      else
      {
#pragma warning disable CA2000 // Dispose objects before losing scope
        var tankingSummary = new TankingSummary();
#pragma warning restore CA2000 // Dispose objects before losing scope

        tankingSummary.EventsSelectionChange += TankingSummary_SelectionChanged;
        TankingWindow = new DocumentWindow(dockSite, "tankingSummary", "Tanking Summary", null, tankingSummary);
        IconToWindow[tankingSummaryIcon.Name] = TankingWindow;

        Helpers.OpenWindow(TankingWindow);
        if (DamageWindow?.IsOpen == true || HealingWindow?.IsOpen == true)
        {
          TankingWindow.MoveToPreviousContainer();
        }

        RepositionCharts(TankingWindow);

        if (TankingStatsManager.Instance.TankingGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var tankingOptions = new GenerateStatsOptions() { RequestSummaryData = true };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    private void OpenChat()
    {
      if (ChatWindow?.IsOpen == true)
      {
        ChatWindow.Close();
      }
      else
      {
#pragma warning disable CA2000 // Dispose objects before losing scope
        ChatWindow = new DocumentWindow(dockSite, "chatWindow", "Chat Archive", null, new ChatViewer());
#pragma warning restore CA2000 // Dispose objects before losing scope

        IconToWindow[chatIcon.Name] = ChatWindow;
        Helpers.OpenWindow(ChatWindow);
      }
    }

    private void OpenEventViewer()
    {
      if (EventWindow?.IsOpen == true)
      {
        EventWindow.Close();
      }
      else
      {
        EventWindow = new DocumentWindow(dockSite, "eventWindow", "Event Log", null, new EventViewer());
        IconToWindow[eventIcon.Name] = EventWindow;
        Helpers.OpenWindow(EventWindow);
      }
    }

    private void OpenLootList()
    {
      if (LootWindow?.IsOpen == true)
      {
        LootWindow.Close();
      }
      else
      {
        LootWindow = new DocumentWindow(dockSite, "lootWindow", "Loot Log", null, new LootViewer());
        IconToWindow[playerLootIcon.Name] = LootWindow;
        Helpers.OpenWindow(LootWindow);
      }
    }

    private void DamageSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      var options = new GenerateStatsOptions() { RequestChartData = true };
      DamageStatsManager.Instance.FireSelectionEvent(options, data.Selected);
      (playerParseTextWindow.Content as ParsePreview)?.UpdateParse(Labels.DAMAGEPARSE, data.Selected);
    }

    private void HealingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      var options = new GenerateStatsOptions() { RequestChartData = true };
      HealingStatsManager.Instance.FireSelectionEvent(options, data.Selected);
      (playerParseTextWindow.Content as ParsePreview)?.UpdateParse(Labels.HEALPARSE, data.Selected);
    }

    private void TankingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEventArgs data)
    {
      var options = new GenerateStatsOptions() { RequestChartData = true };
      TankingStatsManager.Instance.FireSelectionEvent(options, data.Selected);

      var preview = playerParseTextWindow.Content as ParsePreview;

      // change the update order based on whats displayed
      if (preview.parseList.SelectedItem?.ToString() == Labels.RECEIVEDHEALPARSE)
      {
        preview?.UpdateParse(Labels.TANKPARSE, data.Selected);
        if (data.Selected?.Count == 1 && (data.Selected[0] as PlayerStats).SubStats2?.ContainsKey("receivedHealing") == true)
        {
          preview?.AddParse(Labels.RECEIVEDHEALPARSE, TankingStatsManager.Instance, data.CurrentStats, data.Selected);
        }
      }
      else
      {
        if (data.Selected?.Count == 1 && (data.Selected[0] as PlayerStats).SubStats2?.ContainsKey("receivedHealing") == true)
        {
          preview?.AddParse(Labels.RECEIVEDHEALPARSE, TankingStatsManager.Instance, data.CurrentStats, data.Selected);
        }
        preview?.UpdateParse(Labels.TANKPARSE, data.Selected);
      }
    }

    private void RepositionCharts(DocumentWindow window)
    {
      if (window.ParentContainer is TabbedMdiContainer tabControl)
      {
        bool moved = false;
        foreach (var child in tabControl.Windows.Reverse().ToList())
        {
          if (child == DamageChartWindow || child == HealingChartWindow || child == TankingChartWindow)
          {
            if (child.IsOpen && !moved)
            {
              moved = true;
              child.MoveToNewHorizontalContainer();
            }
            else if (child.IsOpen && moved)
            {
              child.MoveToNextContainer();
            }

            (child.Content as LineChart)?.FixSize();
          }
        }
      }
    }

    // Main Menu Op File
    private void MenuItemSelectMonitorLogFileClick(object sender, RoutedEventArgs e)
    {
      CurrentLogOption = LogOption.MONITOR;
      OpenLogFile();
    }

    private void MenuItemSelectArchiveChatClick(object sender, RoutedEventArgs e)
    {
      CurrentLogOption = LogOption.ARCHIVE;
      OpenLogFile();
    }

    private void MenuItemSelectLogFileClick(object sender, RoutedEventArgs e)
    {
      int lastMins = -1;
      if (sender is MenuItem item && !string.IsNullOrEmpty(item.Tag as string))
      {
        lastMins = Convert.ToInt32(item.Tag.ToString(), CultureInfo.CurrentCulture) * 60;
      }

      CurrentLogOption = LogOption.OPEN;
      OpenLogFile(lastMins);
    }

    private void UpdateLoadingProgress()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (EQLogReader != null)
        {
          Busy(true);
          CloseOverlay();

          bytesReadTitle.Content = "Reading:";
          processedTimeLabel.Content = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds, 1) + " sec";
          double filePercent = EQLogReader.FileSize > 0 ? Math.Min(Convert.ToInt32((double)FilePosition / EQLogReader.FileSize * 100), 100) : 100;
          bytesReadLabel.Content = filePercent + "%";

          if (EQLogReader.FileLoadComplete)
          {
            if ((filePercent >= 100 && CurrentLogOption != LogOption.ARCHIVE) || CurrentLogOption == LogOption.MONITOR)
            {
              bytesReadTitle.Content = "Monitoring:";
              bytesReadLabel.Content = "Active";
            }
            else if (filePercent >= 100 && CurrentLogOption == LogOption.ARCHIVE)
            {
              bytesReadTitle.Content = "Archiving:";
              bytesReadLabel.Content = "Complete";
            }
          }

          if (((filePercent >= 100 && CastProcessor.GetPercentComplete() >= 100 && DamageProcessor.GetPercentComplete() >= 100
            && HealingProcessor.GetPercentComplete() >= 100 && MiscProcessor.GetPercentComplete() >= 100) ||
            CurrentLogOption == LogOption.MONITOR || CurrentLogOption == LogOption.ARCHIVE) && EQLogReader.FileLoadComplete)
          {
            bytesReadLabel.Foreground = GOOD_BRUSH;

            if (IsDamageOverlayEnabled)
            {
              OpenOverlay();
            }

            Helpers.SpellAbbrvCache.Clear(); // only really needed during big parse
            LOG.Info("Finished Loading Log File");

            if (DataManager.Instance.GetUnhandledLines() is List<string> lines && lines.Count > 0)
            {
              Clipboard.SetDataObject(string.Join("\n", lines));
            }
          }
          else
          {
            _ = Task.Delay(500).ContinueWith(task => UpdateLoadingProgress(), TaskScheduler.Default);
          }

          Busy(false);
        }
      });
    }

    private void PetMapping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox comboBox)
      {
        if (comboBox.DataContext is PetMapping mapping && comboBox.SelectedItem is SortableName selected && selected.Name != mapping.Owner)
        {
          PlayerManager.Instance.AddPetToPlayer(mapping.Pet, selected.Name);
          petMappingGrid.CommitEdit();
        }
      }
    }

    private void OpenLogFile(int lastMins = -1)
    {
      try
      {
        // WPF doesn't have its own file chooser so use Win32 Version
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
          // filter to txt files
          DefaultExt = ".txt",
          Filter = "eqlog_Player_server (.txt .txt.gz)|*.txt;*.txt.gz",
        };

        // show dialog and read result
        var result = dialog.ShowDialog();
        if (result.Value)
        {
          StopProcessing();
          CastProcessor = new ActionProcessor<string>("CastProcessor", CastLineParser.Process);
          DamageProcessor = new ActionProcessor<string>("DamageProcessor", DamageLineParser.Process);
          HealingProcessor = new ActionProcessor<string>("HealProcessor", HealingLineParser.Process);
          MiscProcessor = new ActionProcessor<string>("MiscProcessor", MiscLineParser.Process);

          bytesReadLabel.Foreground = BRIGHT_TEXT_BRUSH;
          Title = APP_NAME + " " + VERSION + " -- (" + dialog.FileName + ")";
          StartLoadTime = DateTime.Now;
          FilePosition = 0;

          string name = "You";
          string server = "Uknown";
          if (dialog.FileName.Length > 0)
          {
            LOG.Info("Selected Log File: " + dialog.FileName);

            string file = Path.GetFileName(dialog.FileName);
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
            PetPlayersView.Clear();
            VerifiedPetsView.Clear();
            VerifiedPlayersProperty.Clear();
            verifiedPetsWindow.Title = string.Format(CultureInfo.CurrentCulture, PETS_LIST_TITLE, VerifiedPetsView.Count);
            verifiedPlayersWindow.Title = string.Format(CultureInfo.CurrentCulture, PLAYER_LIST_TITLE, VerifiedPlayersProperty.Count);
            PlayerManager.Instance.Save();
          }

          ConfigUtil.ServerName = server;
          ConfigUtil.PlayerName = name;

          if (changed)
          {
            PlayerManager.Instance.Clear();
          }

          DataManager.Instance.Clear();
          PlayerChatManager = new ChatManager();

          NpcDamageManager.ResetTime();
          progressWindow.IsOpen = true;
          EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback, CurrentLogOption == LogOption.MONITOR, lastMins);
          EQLogReader.Start();
          UpdateLoadingProgress();
        }
      }
      catch (InvalidCastException)
      {
        // ignore
      }
      catch (ArgumentException)
      {
        // ignore
      }
      catch (FormatException)
      {
        // ignore
      }
    }

    private void FileLoadingCallback(string line, long position)
    {
      if ((int)((DamageProcessor.Size() + HealingProcessor.Size() + MiscProcessor.Size() + CastProcessor.Size()) / 5000) is int sleep && sleep > 10)
      {
        Thread.Sleep(4 * (sleep - 10));
      }

      Interlocked.Exchange(ref FilePosition, position);

      if (PreLineParser.NeedProcessing(line))
      {
        // avoid having other things parse chat by accident
        if (ChatLineParser.Process(line) is ChatType chatType)
        {
          PlayerChatManager.Add(chatType);
        }
        else if (CurrentLogOption != LogOption.ARCHIVE)
        {
          CastProcessor.Add(line);
          DamageProcessor.Add(line);
          HealingProcessor.Add(line);
          MiscProcessor.Add(line);
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

    private void WindowIcon_Loaded(object sender, RoutedEventArgs e)
    {
      if (sender is FrameworkElement icon && IconToWindow.ContainsKey(icon.Name))
      {
        icon.Visibility = IconToWindow[icon.Name]?.IsOpen == true ? Visibility.Visible : Visibility.Hidden;
      }
    }

    private void NPCWindow_KeyDown(object sender, KeyEventArgs e)
    {
      (npcWindow?.Content as FightTable).FightSearchBoxKeyDown(sender, e);
    }

    private void RemovePetMouseDown(object sender, MouseButtonEventArgs e)
    {
      var cell = sender as DataGridCell;
      if (cell.DataContext is SortableName sortable)
      {
        PlayerManager.Instance.RemoveVerifiedPet(sortable.Name);
      }
    }

    private void RemovePlayerMouseDown(object sender, MouseButtonEventArgs e)
    {
      var cell = sender as DataGridCell;
      if (cell.DataContext is SortableName sortable)
      {
        PlayerManager.Instance.RemoveVerifiedPlayer(sortable.Name);
      }
    }

    private void DockSite_WindowUnreg(object sender, DockingWindowEventArgs e)
    {
      // This is where closing summary tables and line charts will get disposed
      (e.Window.Content as IDisposable)?.Dispose();
    }

    private void TrayIcon_MouseUp(object sender, RoutedEventArgs e)
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

    private void Window_StateChanged(object sender, EventArgs e)
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

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
          taskBarIcon?.Dispose();
          PlayerChatManager?.Dispose();
        }

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

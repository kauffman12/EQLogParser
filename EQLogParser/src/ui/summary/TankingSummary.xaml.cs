using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingSummary.xaml
  /// </summary>
  public partial class TankingSummary : IDocumentContent
  {
    // Made property since it's used outside this class
    public int DamageType { get; set; }

    private string _currentClass;
    private bool _currentPetValue;
    private int _currentGroupCount;
    private bool _ready;

    public TankingSummary()
    {
      InitializeComponent();

      // if pets are shown
      showPets.IsChecked = _currentPetValue = ConfigUtil.IfSet("TankingSummaryShowPets", null, true);

      // default damage types to display
      var damageType = ConfigUtil.GetSetting("TankingSummaryDamageType");
      if (!string.IsNullOrEmpty(damageType) && int.TryParse(damageType, out var type) && type is > -1 and < 3)
      {
        damageTypes.SelectedIndex = type;
      }
      else
      {
        damageTypes.SelectedIndex = 0;
      }

      DamageType = damageTypes.SelectedIndex;
      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowTankingBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);
      CreateClassMenuItems(menuItemShowHealingBreakdown, DataGridShowBreakdown2Click, DataGridShowBreakdown2ByClassClick);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns);
      dataGrid.GridCopyContent += DataGridCopyContent;
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var breakdown, typeof(TankingBreakdown),
              "tankingBreakdownWindow", "Tanking Breakdown"))
        {
          (breakdown.Content as TankingBreakdown)?.Init(CurrentStats, selected);
        }
      }
    }

    internal override void ShowBreakdown2(List<PlayerStats> selected)
    {
      if (selected?.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var breakdown, typeof(HealBreakdown),
          "receivedHealingWindow", "Received Healing Breakdown"))
        {
          // healing stats on the tank is stored in MoreStats property
          // it's like another player stat based around healing
          var selectedHealing = selected.Where(stats => stats.MoreStats != null).Select(stats => stats.MoreStats).ToList();
          (breakdown.Content as HealBreakdown)?.Init(CurrentStats, selectedHealing, true);
        }
      }
    }

    internal override void UpdateDataGridMenuItems()
    {
      var selectedName = "Unknown";

      Dispatcher.InvokeAsync(() =>
      {
        if (CurrentStats != null && CurrentStats.StatsList.Count > 0 && dataGrid.View != null)
        {
          menuItemShowSpellCasts.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
            menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowTankingLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
          copyTankingParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
          copyReceivedHealingParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) &&
            (dataGrid.SelectedItem as PlayerStats)?.MoreStats != null;
          menuItemShowDefensiveTimeline.IsEnabled = (dataGrid.SelectedItems.Count == 1 || dataGrid.SelectedItems.Count == 2) && _currentGroupCount == 1;

          menuItemShowDeathLog.IsEnabled = false;

          if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
          {
            menuItemSetAsPet.IsEnabled = playerStats.OrigName != Labels.Unk && playerStats.OrigName != Labels.Rs &&
            !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName) && !PlayerManager.Instance.IsMerc(playerStats.OrigName);
            selectedName = playerStats.OrigName;
            menuItemShowDeathLog.IsEnabled = !string.IsNullOrEmpty(playerStats.Special) && playerStats.Special.Contains("X");
          }

          EnableClassMenuItems(menuItemShowHealingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowTankingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
             menuItemShowTankingLog.IsEnabled = menuItemSetAsPet.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyTankingParseToEQClick.IsEnabled =
             copyOptions.IsEnabled = copyReceivedHealingParseToEQClick.IsEnabled = menuItemShowSpellCasts.IsEnabled = menuItemShowHitFreq.IsEnabled =
             menuItemShowDefensiveTimeline.IsEnabled = false;
        }

        menuItemSetAsPet.Header = $"Set {selectedName} as Pet to";
      });
    }

    private void CopyToEqClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.CopyToEqClick(Labels.TankParse);
    private void CopyReceivedHealingToEqClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.CopyToEqClick(Labels.ReceivedHealParse);
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void ClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var update = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      var needUpdate = _currentClass != update;
      _currentClass = update;

      if (needUpdate)
      {
        dataGrid.SelectedItems.Clear();
        dataGrid.View?.RefreshFilter();
      }
    }

    private void CreatePetOwnerMenu()
    {
      menuItemPetOptions.Children.Clear();
      if (CurrentStats != null)
      {
        foreach (var stats in CurrentStats.StatsList.Where(stats => PlayerManager.Instance.IsVerifiedPlayer(stats.OrigName)).OrderBy(stats => stats.OrigName))
        {
          var item = new MenuItem { IsEnabled = true, Header = stats.OrigName };
          item.Click += AssignOwnerClick;
          menuItemPetOptions.Children.Add(item);
        }
      }
    }

    private void AssignOwnerClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is PlayerStats stats && sender is MenuItem item)
      {
        PlayerManager.Instance.AddPetToPlayer(stats.OrigName, item.Header as string);
        PlayerManager.Instance.AddVerifiedPet(stats.OrigName);
      }
    }

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (MainWindow.IsMapSendToEqEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEqClick(sender, null);
      }
    }

    private void DataGridTankingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(HitLogViewer), "tankingLogWindow", "Tanking Log"))
        {
          (log.Content as HitLogViewer)?.Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups, true);
        }
      }
    }

    private void DataGridDeathLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(DeathLogViewer), "deathLogWindow", "Death Log"))
        {
          (log.Content as DeathLogViewer)?.Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First());
        }
      }
    }

    private void DataGridHitFreqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var hitFreq, typeof(HitFreqChart), "tankHitFreqChart", "Tanking Hit Frequency"))
        {
          (hitFreq.Content as HitFreqChart)?.Update(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentStats);
        }
      }
    }

    private void DataGridDefensiveTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var timeline, typeof(GanttChart), "defensiveTimeline", "Defensive Timeline"))
        {
          ((GanttChart)timeline.Content).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentGroups, 0);
        }
      }
    }

    private void EventsClearedActiveData(bool cleared) => ClearData();

    private void ClearData()
    {
      CurrentStats = null;
      dataGrid.ItemsSource = NoResultsList;
      title.Content = DefaultTableLabel;
    }

    private void EventsGenerationStatus(StatsGenerationEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (e.Type == Labels.HealParse && e.State == "COMPLETED")
        {
          if (CurrentStats != null)
          {
            HealingStatsManager.Instance.PopulateHealing(CurrentStats);
            dataGrid.SelectedItems.Clear();
            dataGrid.View?.RefreshFilter();

            if (e.Limited)
            {
              title.Content = CurrentStats.FullTitle + " (Not All Healing Opts Chosen)";
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
            }
          }
        }
        else if (e.Type == Labels.TankParse)
        {
          switch (e.State)
          {
            case "STARTED":
              title.Content = "Calculating Tanking DPS...";
              dataGrid.ItemsSource = NoResultsList;
              break;
            case "COMPLETED":
              CurrentStats = e.CombinedStats;
              CurrentGroups = e.Groups;
              _currentGroupCount = e.UniqueGroupCount;

              var isHealingLimited = false;
              if (CurrentStats == null)
              {
                title.Content = NodataTableLabel;
              }
              else
              {
                title.Content = CurrentStats.FullTitle;
                isHealingLimited = HealingStatsManager.Instance.PopulateHealing(CurrentStats);
                dataGrid.ItemsSource = CurrentStats.StatsList;
              }

              if (isHealingLimited)
              {
                title.Content += " (Not All Healing Opts Chosen)";
              }

              CreatePetOwnerMenu();
              UpdateDataGridMenuItems();
              break;
            case "NONPC":
            case "NODATA":
              CurrentStats = null;
              title.Content = e.State == "NONPC" ? DefaultTableLabel : NodataTableLabel;
              CreatePetOwnerMenu();
              UpdateDataGridMenuItems();
              break;
          }
        }
      });
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = stats =>
        {
          string className = null;
          string name = null;

          if (stats is PlayerStats playerStats)
          {
            name = playerStats.Name;
            className = playerStats.ClassName;
          }
          else if (stats is string dataPointName)
          {
            name = dataPointName;
            className = PlayerManager.Instance.GetPlayerClass(name);
          }

          var isPet = PlayerManager.Instance.IsVerifiedPet(name);
          if (isPet && _currentPetValue == false)
          {
            return false;
          }

          return string.IsNullOrEmpty(_currentClass) || (!string.IsNullOrEmpty(name) && _currentClass == className);
        };

        if (dataGrid.SelectedItems.Count > 0)
        {
          dataGrid.SelectedItems.Clear();
        }

        dataGrid.View.RefreshFilter();
      }
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (dataGrid is { ItemsSource: not null })
      {
        var needRequery = DamageType != damageTypes.SelectedIndex;
        _currentPetValue = showPets.IsChecked == true;
        DamageType = damageTypes.SelectedIndex;
        ConfigUtil.SetSetting("TankingSummaryShowPets", _currentPetValue);
        ConfigUtil.SetSetting("TankingSummaryDamageType", DamageType);

        dataGrid.SelectedItems.Clear();
        dataGrid.View.RefreshFilter();

        if (needRequery)
        {
          var tankingOptions = new GenerateStatsOptions { DamageType = DamageType };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    private void EventsChartOpened(string name)
    {
      if (name == "Tanking")
      {
        var tankingOptions = new GenerateStatsOptions { DamageType = DamageType };
        var selected = GetSelectedStats();
        TankingStatsManager.Instance.FireChartEvent(tankingOptions, "UPDATE", selected);
      }
    }

    internal override void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;
        MainActions.FireTankingSelectionChanged(selectionChanged);
      });
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        TankingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
        HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.EventsChartOpened += EventsChartOpened;
        if (TankingStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var tankingOptions = new GenerateStatsOptions { DamageType = DamageType };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
        _ready = true;
      }
    }

    public void HideContent()
    {
      TankingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      MainActions.EventsChartOpened -= EventsChartOpened;
      ClearData();
      TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions(), "UPDATE");
      _ready = false;
    }
  }
}

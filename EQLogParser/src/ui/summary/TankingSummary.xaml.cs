using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingSummary.xaml
  /// </summary>
  public partial class TankingSummary : SummaryTable, IDisposable
  {
    private int CurrentDamageType = 0;
    private string CurrentClass = null;
    private bool CurrentPetValue;
    private int CurrentGroupCount = 0;
    // Made property since it's used outside this class
    public int DamageType { get => CurrentDamageType; set => CurrentDamageType = value; }

    public TankingSummary()
    {
      InitializeComponent();

      // if pets are shown
      showPets.IsChecked = CurrentPetValue = ConfigUtil.IfSet("TankingSummaryShowPets", null, true);

      // default damage types to display
      string damageType = ConfigUtil.GetSetting("TankingSummaryDamageType");
      if (!string.IsNullOrEmpty(damageType) && int.TryParse(damageType, out int type) && type > -1 && type < 3)
      {
        damageTypes.SelectedIndex = type;
      }
      else
      {
        damageTypes.SelectedIndex = 0;
      }

      DamageType = damageTypes.SelectedIndex;

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Properties.Resources.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowTankingBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);
      CreateClassMenuItems(menuItemShowHealingBreakdown, DataGridShowBreakdown2Click, DataGridShowBreakdown2ByClassClick);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns);
      TankingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl breakdown, typeof(TankingBreakdown),
          "tankingBreakdownWindow", "Tanking Breakdown"))
        {
          (breakdown.Content as TankingBreakdown).Init(CurrentStats, selected);
        }
      }
    }

    internal override void ShowBreakdown2(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl breakdown, typeof(HealBreakdown),
          "receivedHealingWindow", "Received Healing Breakdown"))
        {
          // healing stats on the tank is stored in MoreStats property
          // it's like another player stat based around healing
          var selectedHealing = selected.Where(stats => stats.MoreStats != null).Select(stats => stats.MoreStats).ToList();
          (breakdown.Content as HealBreakdown).Init(CurrentStats, selectedHealing, true);
        }
      }
    }

    override internal void UpdateDataGridMenuItems()
    {
      string selectedName = "Unknown";

      Dispatcher.InvokeAsync(() =>
      {
        if (CurrentStats != null && CurrentStats.StatsList.Count > 0 && dataGrid.View != null)
        {
          menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.View.Records.Count;
          menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
          menuItemShowSpellCasts.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
            menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowTankingLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
          copyTankingParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
          copyReceivedHealingParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) &&
            (dataGrid.SelectedItem as PlayerStats)?.SubStats2?.Count > 0;
          menuItemShowDefensiveTimeline.IsEnabled = (dataGrid.SelectedItems.Count == 1 || dataGrid.SelectedItems.Count == 2) && CurrentGroupCount == 1;

          if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
          {
            menuItemSetAsPet.IsEnabled = !PlayerManager.Instance.IsVerifiedPet(playerStats.OrigName) &&
              !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName) && !PlayerManager.Instance.IsMerc(playerStats.OrigName);
            selectedName = playerStats.OrigName;
          }

          EnableClassMenuItems(menuItemShowHealingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowTankingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
             menuItemShowTankingLog.IsEnabled = menuItemSetAsPet.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyTankingParseToEQClick.IsEnabled =
             copyOptions.IsEnabled = copyReceivedHealingParseToEQClick.IsEnabled = menuItemShowSpellCasts.IsEnabled = menuItemShowHitFreq.IsEnabled =
             menuItemShowDefensiveTimeline.IsEnabled = false;
        }

        menuItemSetAsPet.Header = string.Format("Set {0} as Pet", selectedName);

      });
    }

    private void CopyToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.TANKPARSE);
    private void CopyReceivedHealingToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.RECEIVEDHEALPARSE);
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void ClassSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      var update = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      var needUpdate = CurrentClass != update;
      CurrentClass = update;

      if (needUpdate)
      {
        dataGrid.View?.RefreshFilter();
        dataGrid.SelectedItems.Clear();
      }
    }

    private void DataGridTankingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl log, typeof(HitLogViewer), "tankingLogWindow", "Tanking Log"))
        {
          (log.Content as HitLogViewer).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups, true);
        }
      }
    }

    private void DataGridHitFreqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl hitFreq, typeof(HitFreqChart), "tankHitFreqChart", "Tanking Hit Frequency"))
        {
          (hitFreq.Content as HitFreqChart).Update(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentStats);
        }
      }
    }

    private void DataGridDefensiveTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl timeline, typeof(GanttChart), "defensiveTimeline", "Defensive Timeline"))
        {
          ((GanttChart)timeline.Content).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentGroups, true);
        }
      }
    }

    private void EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentStats = null;
      dataGrid.ItemsSource = null;
      title.Content = DEFAULT_TABLE_LABEL;
    }

    private void EventsGenerationStatus(object sender, StatsGenerationEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (e.Type == Labels.HEALPARSE && e.State == "COMPLETED")
        {
          if (CurrentStats != null)
          {
            HealingStatsManager.Instance.PopulateHealing(CurrentStats);
            dataGrid.View.RefreshFilter();
            dataGrid.SelectedItems.Clear();

            if (!MainWindow.IsAoEHealingEnabled)
            {
              title.Content = CurrentStats.FullTitle + " (Not Including AE Healing)";
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
            }
          }
        }
        else if (e.Type == Labels.TANKPARSE)
        {
          switch (e.State)
          {
            case "STARTED":
              title.Content = "Calculating Tanking DPS...";
              dataGrid.ItemsSource = null;
              break;
            case "COMPLETED":
              CurrentStats = e.CombinedStats;
              CurrentGroups = e.Groups;
              CurrentGroupCount = e.UniqueGroupCount;

              if (CurrentStats == null)
              {
                title.Content = NODATA_TABLE_LABEL;
              }
              else
              {
                title.Content = CurrentStats.FullTitle;
                HealingStatsManager.Instance.PopulateHealing(CurrentStats);
                dataGrid.ItemsSource = CurrentStats.StatsList;
              }

              if (!MainWindow.IsAoEHealingEnabled)
              {
                title.Content += " (Not Including AE Healing)";
              }

              UpdateDataGridMenuItems();
              break;
            case "NONPC":
            case "NODATA":
              CurrentStats = null;
              title.Content = e.State == "NONPC" ? DEFAULT_TABLE_LABEL : NODATA_TABLE_LABEL;
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
        dataGrid.View.Filter = (stats) =>
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
          if (isPet && CurrentPetValue == false)
          {
            return false;
          }

          return string.IsNullOrEmpty(CurrentClass) || (!string.IsNullOrEmpty(name) && CurrentClass == className);
        };

        dataGrid.SelectedItems.Clear();
        dataGrid.View.RefreshFilter();
        TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "FILTER", null);
      }
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (dataGrid != null && dataGrid.ItemsSource != null)
      {
        var needRequery = DamageType != damageTypes.SelectedIndex;
        CurrentPetValue = showPets.IsChecked.Value;
        DamageType = damageTypes.SelectedIndex;
        ConfigUtil.SetSetting("TankingSummaryShowPets", CurrentPetValue.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("TankingSummaryDamageType", DamageType.ToString(CultureInfo.CurrentCulture));

        dataGrid.View.RefreshFilter();
        dataGrid.SelectedItems.Clear();

        if (needRequery)
        {
          var tankingOptions = new GenerateStatsOptions { RequestSummaryData = true, RequestChartData = true, DamageType = DamageType };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "UPDATE");
        TankingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
        HealingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
        CurrentStats = null;
        dataGrid.Dispose();
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

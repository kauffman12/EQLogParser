using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealSummary.xaml
  /// </summary>
  public partial class HealingSummary : SummaryTable, IDisposable
  {
    private string CurrentClass = null;

    public HealingSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid, selectedColumns);

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Properties.Resources.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);
      HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        var healTable = new HealBreakdown(CurrentStats);
        healTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "healWindow", "Healing Breakdown", healTable);
      }
    }

    internal override void UpdateDataGridMenuItems()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (CurrentStats != null && CurrentStats.StatsList.Count > 0 && dataGrid.View != null)
        {
          menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.View.Records.Count;
          menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
          menuItemShowSpellCasts.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowHealingLog.IsEnabled = dataGrid.SelectedItems.Count == 1;
          copyHealParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
          copyTopHealsParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) && (dataGrid.SelectedItem as PlayerStats)?.SubStats?.Count > 0;
          EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats.UniqueClasses);
        }
        else
        {
          menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowBreakdown.IsEnabled = copyOptions.IsEnabled =
            menuItemShowHealingLog.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyHealParseToEQClick.IsEnabled = menuItemShowSpellCasts.IsEnabled = false;
        }
      });
    }

    private void CopyToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.HEALPARSE);
    private void CopyTopHealsToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.TOPHEALSPARSE);
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void ClassSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      CurrentClass = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      dataGrid.View?.RefreshFilter();
      HealingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "FILTER", null, dataGrid.View?.Filter);
    }

    private void DataGridHealingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        var log = Helpers.OpenWindow(main.dockSite, null, typeof(HitLogViewer), "healingLogWindow", "Healing Log");
        (log.Content as HitLogViewer).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
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
        switch (e.State)
        {
          case "STARTED":
            title.Content = "Calculating HPS...";
            dataGrid.ItemsSource = null;
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats;
            CurrentGroups = e.Groups;

            if (CurrentStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
              dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(CurrentStats.StatsList);
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
      }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = (stats) =>
        {
          string className = null;
          if (stats is PlayerStats playerStats)
          {
            className = playerStats.ClassName;
          }
          else if (stats is string name)
          {
            className = PlayerManager.Instance.GetPlayerClass(name);
          }

          return string.IsNullOrEmpty(CurrentClass) || CurrentClass == className;
        };

        HealingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "FILTER", null, dataGrid.View.Filter);
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        HealingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "UPDATE");
        HealingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;

        if (disposing)
        {
          CurrentStats = null;
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

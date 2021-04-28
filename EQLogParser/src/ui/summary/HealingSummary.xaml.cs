
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
      InitSummaryTable(title, dataGrid, null, selectedColumns);

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, "All Classes");
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);

      HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
    }

    private void Instance_EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentStats = null;
      dataGrid.ItemsSource = null;
      title.Content = DEFAULT_TABLE_LABEL;
    }

    private void Instance_EventsGenerationStatus(object sender, StatsGenerationEvent e)
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
            CurrentStats = e.CombinedStats as CombinedStats;
            CurrentGroups = e.Groups;

            if (CurrentStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
              var view = CollectionViewSource.GetDefaultView(CurrentStats.StatsList);
              dataGrid.ItemsSource = SetFilter(view);
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
      });
    }

    internal void DataGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      FireSelectionChangedEvent(GetSelectedStats());
      UpdateDataGridMenuItems();
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

    private void DataGridHealingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        var log = new HitLogViewer(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
        var main = Application.Current.MainWindow as MainWindow;
        var window = Helpers.OpenNewTab(main.dockSite, "healingLog", "Healing Log", log, 400, 300);
        window.CanFloat = true;
        window.CanClose = true;
      }
    }

    private void UpdateDataGridMenuItems()
    {
      if (CurrentStats != null && CurrentStats.StatsList.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
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
    }

    private ICollectionView SetFilter(ICollectionView view)
    {
      if (view != null)
      {
        view.Filter = (stats) =>
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

        HealingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "FILTER", null, view.Filter);
      }

      return view;
    }

    private void CopyToEQClick(object sender, RoutedEventArgs e)
    {
      (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.HEALPARSE);
    }

    private void CopyTopHealsToEQClick(object sender, RoutedEventArgs e)
    {
      (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.TOPHEALSPARSE);
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      CurrentClass = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      SetFilter(dataGrid?.ItemsSource as ICollectionView);
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      HealingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "UPDATE");

      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
          CurrentStats = null;
        }

        HealingStatsManager.Instance.EventsGenerationStatus -= Instance_EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData -= Instance_EventsClearedActiveData;
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

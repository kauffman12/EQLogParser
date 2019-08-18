using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingSummary.xaml
  /// </summary>
  public partial class TankingSummary : SummaryTable, IDisposable
  {
    public TankingSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      TankingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
    }

    protected void DataGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      FireSelectionChangedEvent(GetSelectedStats());
      UpdateDataGridMenuItems();
    }

    protected override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        var tankingTable = new TankingBreakdown(CurrentStats);
        tankingTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "tankWindow", "Tanking Breakdown", tankingTable);
      }
    }

    protected override void ShowBreakdown2(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        var receivedHealingTable = new ReceivedHealingBreakdown(CurrentStats);
        receivedHealingTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "receivedHealingWindow", "Received Healing Breakdown", receivedHealingTable);
      }
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
            if (e.Type == Labels.TANKPARSE)
            {
              (Application.Current.MainWindow as MainWindow).Busy(true);
              title.Content = "Calculating Tanking DPS...";
              dataGrid.ItemsSource = null;
            }
            break;
          case "COMPLETED":
            if (e.Type == Labels.TANKPARSE)
            {
              CurrentStats = e.CombinedStats as CombinedStats;

              if (CurrentStats == null)
              {
                title.Content = NODATA_TABLE_LABEL;
              }
              else
              {
                title.Content = CurrentStats.FullTitle;
                HealingStatsManager.Instance.PopulateHealing(CurrentStats.StatsList);

                var view = CollectionViewSource.GetDefaultView(CurrentStats.StatsList);
                SetFilter(view);

                dataGrid.ItemsSource = view;
              }

            (Application.Current.MainWindow as MainWindow).Busy(false);
              UpdateDataGridMenuItems();
            }
            else if (e.Type == Labels.HEALPARSE)
            {
              (Application.Current.MainWindow as MainWindow).Busy(true);
              HealingStatsManager.Instance.PopulateHealing(CurrentStats.StatsList);
              dataGrid.Items?.Refresh();
              (Application.Current.MainWindow as MainWindow).Busy(false);
            }
            break;
          case "NONPC":
            if (e.Type == Labels.TANKPARSE)
            {
              CurrentStats = null;
              title.Content = DEFAULT_TABLE_LABEL;
              (Application.Current.MainWindow as MainWindow).Busy(false);
              UpdateDataGridMenuItems();
            }
            break;
        }
      });
    }

    private void UpdateDataGridMenuItems()
    {
      if (CurrentStats?.StatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        copyTankingParseToEQClick.IsEnabled = true;
        UpdateClassMenuItems(menuItemShowHealingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
        UpdateClassMenuItems(menuItemShowTankingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
           menuItemShowSpellCasts.IsEnabled = copyTankingParseToEQClick.IsEnabled = false;
      }
    }

    private void SetFilter(ICollectionView view)
    {
      if (view != null)
      {
        view.Filter = stats => showPets.IsChecked.Value || DataManager.Instance.CheckNameForPet(((PlayerStats)stats).Name) == false;

        // chart event
        Predicate<object> chartFilter = dataPoint => showPets.IsChecked.Value || DataManager.Instance.CheckNameForPet(((DataPoint)dataPoint).Name) == false;
        TankingStatsManager.Instance.FireFilterEvent(new TankingStatsOptions() { RequestChartData = true }, chartFilter);
      }
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      SetFilter(dataGrid?.ItemsSource as ICollectionView);
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
        }

        TankingStatsManager.Instance.EventsGenerationStatus -= Instance_EventsGenerationStatus;
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

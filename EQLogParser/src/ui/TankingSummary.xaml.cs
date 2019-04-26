using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingSummary.xaml
  /// </summary>
  public partial class TankingSummary : SummaryTable, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private CombinedTankStats CurrentTankingStats = null;

    public TankingSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      TankingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
    }

    protected void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      FireSelectionChangedEvent(GetSelectedStats());
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

        TankingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
        disposedValue = true;
      }
    }

    private void Instance_EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentTankingStats = null;
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
            (Application.Current.MainWindow as MainWindow).Busy(true);
            title.Content = "Calculating HPS...";
            dataGrid.ItemsSource = null;
            break;
          case "COMPLETED":
            CurrentTankingStats = e.CombinedStats as CombinedTankStats;

            if (CurrentTankingStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              title.Content = CurrentTankingStats.FullTitle;

              HealingStatsManager.Instance.PopulateHealing(CurrentTankingStats.StatsList);
              dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentTankingStats.StatsList);
            }

            (Application.Current.MainWindow as MainWindow).Busy(false);
            //UpdateDataGridMenuItems();
            break;
          case "NONPC":
            CurrentTankingStats = null;
            title.Content = DEFAULT_TABLE_LABEL;
            (Application.Current.MainWindow as MainWindow).Busy(false);
            //UpdateDataGridMenuItems();
            break;
        }
      });
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      // GC.SuppressFinalize(this);
    }
    #endregion
  }
}

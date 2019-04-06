
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealSummary.xaml
  /// </summary>
  public partial class HealingSummary : SummaryTable, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private CombinedHealStats CurrentHealingStats = null;
    private bool Ready = false;

    public HealingSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      // AE healing
      bool bValue;
      string value = DataManager.Instance.GetApplicationSetting("IncludeAEHealing");
      includeAEHealing.IsChecked = value == null || bool.TryParse(value, out bValue) && bValue;

      Ready = true;
      HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
    }

    internal bool IsAEHealingEnabled()
    {
      return includeAEHealing.IsChecked.Value;
    }


    private void Instance_EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentHealingStats = null;
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
            CurrentHealingStats = e.CombinedStats as CombinedHealStats;

            if (CurrentHealingStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              includeAEHealing.IsEnabled = e.IsAEHealingAvailable;
              title.Content = CurrentHealingStats.FullTitle;
              dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentHealingStats.StatsList);
            }

            (Application.Current.MainWindow as MainWindow).Busy(false);
            UpdateDataGridMenuItems();
            break;
          case "NONPC":
            CurrentHealingStats = null;
            title.Content = DEFAULT_TABLE_LABEL;
            (Application.Current.MainWindow as MainWindow).Busy(false);
            UpdateDataGridMenuItems();
            break;
        }
      });
    }

    protected void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      FireSelectionChangedEvent(GetSelectedStats());
      UpdateDataGridMenuItems();
    }

    protected override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        var healTable = new HealBreakdown(CurrentHealingStats);
        healTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "healWindow", "Healing Breakdown", healTable);
      }
    }

    protected override void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var spellTable = new SpellCountTable(CurrentHealingStats.ShortTitle);
        spellTable.ShowSpells(selected, CurrentHealingStats);
        var main = Application.Current.MainWindow as MainWindow;
        Helpers.OpenNewTab(main.dockSite, "spellCastsWindow", "Spell Counts", spellTable);
      }
    }

    private void UpdateDataGridMenuItems()
    {
      if (CurrentHealingStats != null && CurrentHealingStats.StatsList.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowBreakdown.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        copyHealParseToEQClick.IsEnabled = true;
        UpdateClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentHealingStats.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentHealingStats.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowBreakdown.IsEnabled = 
          menuItemShowSpellCasts.IsEnabled = copyHealParseToEQClick.IsEnabled = false;
      }
    }

    private void IncludeAEHealingChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        bool isAEHealingEnabled = includeAEHealing.IsChecked.Value == true;
        DataManager.Instance.SetApplicationSetting("IncludeAEHealing", isAEHealingEnabled.ToString());

        if (CurrentHealingStats != null && CurrentHealingStats.RaidStats != null)
        {
          includeAEHealing.IsEnabled = false;
          var options = new HealingStatsOptions() { IsAEHealingEanbled = isAEHealingEnabled, RequestChartData = true, RequestSummaryData = true };
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(options));
        }
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
          CurrentHealingStats = null;
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

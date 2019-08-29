using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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
    private string CurrentClass = null;
    private bool CurrentPetValue;

    public TankingSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      CurrentPetValue = showPets.IsChecked.Value;
      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, "All Classes");
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      TankingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
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
        var tankingTable = new TankingBreakdown(CurrentStats);
        tankingTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "tankWindow", "Tanking Breakdown", tankingTable);
      }
    }

    internal override void ShowBreakdown2(List<PlayerStats> selected)
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
                dataGrid.ItemsSource = SetFilter(view);
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
      string selectedName = "Unknown";

      if (CurrentStats?.StatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        copyTankingParseToEQClick.IsEnabled = true;

        if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
        {
          menuItemSetAsPet.IsEnabled = !PlayerManager.Instance.IsVerifiedPet(playerStats.OrigName) && !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName);
          selectedName = playerStats.OrigName;
        }

        UpdateClassMenuItems(menuItemShowHealingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
        UpdateClassMenuItems(menuItemShowTankingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
           menuItemSetAsPet.IsEnabled = menuItemShowSpellCasts.IsEnabled = copyTankingParseToEQClick.IsEnabled = false;
      }

      menuItemSetAsPet.Header = string.Format(CultureInfo.CurrentCulture, "Set {0} as Pet", selectedName);
    }

    private ICollectionView SetFilter(ICollectionView view)
    {
      if (view != null)
      {
        view.Filter = (stats) =>
        {
          string className = null;
          string name = null;

          if (stats is PlayerStats playerStats)
          {
            name = playerStats.Name;
            className = playerStats.ClassName;
          }
          else if (stats is DataPoint dataPoint)
          {
            name = dataPoint.Name;
            className = PlayerManager.Instance.GetPlayerClass(name);
          }

          var isPet = PlayerManager.Instance.IsVerifiedPet(name);
          if (isPet && CurrentPetValue == false)
          {
            return false;
          }

          if (!string.IsNullOrEmpty(CurrentClass) && isPet)
          {
            return false;
          }

          return string.IsNullOrEmpty(CurrentClass) || (!string.IsNullOrEmpty(name) && CurrentClass == className);
        };

        TankingStatsManager.Instance.FireFilterEvent(new GenerateStatsOptions() { RequestChartData = true }, view.Filter);
      }

      return view;
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      CurrentPetValue = showPets.IsChecked.Value;
      SetFilter(dataGrid?.ItemsSource as ICollectionView);
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

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
  /// Interaction logic for TankingSummary.xaml
  /// </summary>
  public partial class TankingSummary : SummaryTable, IDisposable
  {
    private string CurrentClass = null;
    private bool CurrentPetValue;

    public TankingSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid, selectedColumns);

      CurrentPetValue = showPets.IsChecked.Value;
      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, "All Classes");
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowTankingBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);
      CreateClassMenuItems(menuItemShowHealingBreakdown, DataGridShowBreakdown2Click, DataGridShowBreakdown2ByClassClick);

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

    private void DataGridTankingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        var log = new HitLogViewer(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups, true);
        var main = Application.Current.MainWindow as MainWindow;
        var window = Helpers.OpenNewTab(main.dockSite, "tankingLog", "Tanking Log", log, 400, 300);
        window.CanFloat = true;
        window.CanClose = true;
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
        if (e.Type == Labels.HEALPARSE && e.State == "COMPLETED")
        {
          if (CurrentStats != null)
          {
            HealingStatsManager.Instance.PopulateHealing(CurrentStats);
            dataGrid.Items?.Refresh();

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

              if (CurrentStats == null)
              {
                title.Content = NODATA_TABLE_LABEL;
              }
              else
              {
                title.Content = CurrentStats.FullTitle;
                HealingStatsManager.Instance.PopulateHealing(CurrentStats);
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
        menuItemShowSpellCasts.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
        menuItemShowTankingLog.IsEnabled = dataGrid.SelectedItems.Count == 1;
        copyTankingParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
        copyReceivedHealingParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) && (dataGrid.SelectedItem as PlayerStats)?.SubStats2?.ContainsKey("receivedHealing") == true;

        if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
        {
          menuItemSetAsPet.IsEnabled = !PlayerManager.Instance.IsVerifiedPet(playerStats.OrigName) && !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName);
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
           copyOptions.IsEnabled = copyReceivedHealingParseToEQClick.IsEnabled = menuItemShowSpellCasts.IsEnabled = false;
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

        TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "FILTER", null, view.Filter);
      }

      return view;
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      CurrentPetValue = showPets.IsChecked.Value;
      SetFilter(dataGrid?.ItemsSource as ICollectionView);
    }

    private void CopyToEQClick(object sender, RoutedEventArgs e)
    {
      (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.TANKPARSE);
    }

    private void CopyReceivedHealingToEQClick(object sender, RoutedEventArgs e)
    {
      (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.RECEIVEDHEALPARSE);
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
      TankingStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "UPDATE");

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

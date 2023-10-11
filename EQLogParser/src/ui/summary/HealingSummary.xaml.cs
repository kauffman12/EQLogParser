using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealSummary.xaml
  /// </summary>
  public partial class HealingSummary : SummaryTable, IDisposable
  {
    private string CurrentClass;

    public HealingSummary()
    {
      InitializeComponent();

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns);
      HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
      dataGrid.GridCopyContent += DataGridCopyContent;
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var breakdown, typeof(HealBreakdown),
          "healingBreakdownWindow", "Healing Breakdown"))
        {
          (breakdown.Content as HealBreakdown).Init(CurrentStats, selected);
        }
      }
    }

    internal override void UpdateDataGridMenuItems()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (CurrentStats != null && CurrentStats.StatsList.Count > 0 && dataGrid.View != null)
        {
          menuItemShowSpellCasts.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowHealingLog.IsEnabled = dataGrid.SelectedItems.Count == 1;
          copyHealParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
          copyTopHealsParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) && (dataGrid.SelectedItem as PlayerStats)?.SubStats?.Count > 0;
          menuItemShowHealingTimeline.IsEnabled = dataGrid.SelectedItems.Count == 1 || dataGrid.SelectedItems.Count == 2;

          menuItemShowDeathLog.IsEnabled = false;

          if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
          {
            menuItemShowDeathLog.IsEnabled = !string.IsNullOrEmpty(playerStats.Special) && playerStats.Special.Contains("X");
          }

          EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats.UniqueClasses);
        }
        else
        {
          menuItemShowBreakdown.IsEnabled = copyOptions.IsEnabled =
            menuItemShowHealingLog.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyHealParseToEQClick.IsEnabled =
            menuItemShowSpellCasts.IsEnabled = menuItemShowHealingTimeline.IsEnabled = false;
        }
      });
    }

    private void CopyToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.HEALPARSE);
    private void CopyTopHealsToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.TOPHEALSPARSE);
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void ClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var update = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      var needUpdate = CurrentClass != update;
      CurrentClass = update;

      if (needUpdate)
      {
        dataGrid.SelectedItems.Clear();
        dataGrid.View?.RefreshFilter();
      }
    }

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (MainWindow.IsMapSendToEQEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEQClick(sender, null);
      }
    }

    private void DataGridHealingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(HitLogViewer), "healingLogWindow", "Healing Log"))
        {
          (log.Content as HitLogViewer).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
        }
      }
    }

    private void DataGridDeathLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(DeathLogViewer), "deathLogWindow", "Death Log"))
        {
          (log.Content as DeathLogViewer).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First());
        }
      }
    }

    private void DataGridHealingTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var timeline, typeof(GanttChart), "healingTimeline", "Healing Timeline"))
        {
          ((GanttChart)timeline.Content).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentGroups, 2);
        }
      }
    }

    private void EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentStats = null;
      dataGrid.ItemsSource = NoResultsList;
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
            dataGrid.ItemsSource = NoResultsList;
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
              dataGrid.ItemsSource = CurrentStats.StatsList;
            }

            if (e.Limited)
            {
              title.Content += " (Not All Healing Opts Chosen)";
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
      }, DispatcherPriority.Render);
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = stats =>
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

        if (dataGrid.SelectedItems.Count > 0)
        {
          dataGrid.SelectedItems.Clear();
        }

        dataGrid.View.RefreshFilter();
      }
    }

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        SummaryCleanup();
        HealingStatsManager.Instance.FireChartEvent("UPDATE");
        HealingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
        dataGrid.GridCopyContent -= DataGridCopyContent;
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

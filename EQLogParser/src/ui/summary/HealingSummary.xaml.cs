using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealSummary.xaml
  /// </summary>
  public partial class HealingSummary : IDocumentContent
  {
    private string _currentClass;
    private bool _ready;

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
      dataGrid.GridCopyContent += DataGridCopyContent;
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var breakdown, typeof(HealBreakdown),
          "healingBreakdownWindow", "Healing Breakdown"))
        {
          (breakdown.Content as HealBreakdown)?.Init(CurrentStats, selected);
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
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemShowBreakdown.IsEnabled = copyOptions.IsEnabled =
            menuItemShowHealingLog.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyHealParseToEQClick.IsEnabled =
            menuItemShowSpellCasts.IsEnabled = menuItemShowHealingTimeline.IsEnabled = false;
        }
      });
    }

    private void CopyToEqClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.CopyToEqClick(Labels.HealParse);
    private void CopyTopHealsToEqClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.CopyToEqClick(Labels.TopHealParse);
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

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (MainWindow.IsMapSendToEqEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEqClick(sender, null);
      }
    }

    private void DataGridHealingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(HitLogViewer), "healingLogWindow", "Healing Log"))
        {
          (log.Content as HitLogViewer)?.Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
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

    private void DataGridHealingTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0 && Application.Current.MainWindow is MainWindow main)
      {
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var timeline, typeof(GanttChart), "healingTimeline", "Healing Timeline"))
        {
          ((GanttChart)timeline.Content).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentGroups, 2);
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
              title.Content = NodataTableLabel;
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
            title.Content = e.State == "NONPC" ? DefaultTableLabel : NodataTableLabel;
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

          return string.IsNullOrEmpty(_currentClass) || _currentClass == className;
        };

        if (dataGrid.SelectedItems.Count > 0)
        {
          dataGrid.SelectedItems.Clear();
        }

        dataGrid.View.RefreshFilter();
      }
    }

    private void EventsChartOpened(string name)
    {
      if (name == "Healing")
      {
        var selected = GetSelectedStats();
        HealingStatsManager.Instance.FireChartEvent("UPDATE", selected);
      }
    }

    internal override void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;
        MainActions.FireHealingSelectionChanged(selectionChanged);
      });
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.EventsChartOpened += EventsChartOpened;
        if (HealingStatsManager.Instance.GetGroupCount() > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats());
        }
        _ready = true;
      }
    }

    public void HideContent()
    {
      HealingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      MainActions.EventsChartOpened -= EventsChartOpened;
      ClearData();
      HealingStatsManager.Instance.FireChartEvent("UPDATE");
      _ready = false;
    }
  }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageSummary.xaml
  /// </summary>
  public partial class DamageSummary : SummaryTable, IDisposable
  {
    private static BitmapImage COLLAPSE_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Collapse_16x.png"));
    private static BitmapImage EXPAND_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Expand_16x.png"));

    // workaround for adjusting column withs of player datagrid
    private List<DataGrid> ChildGrids = new List<DataGrid>();
    private string CurrentClass = null;

    public DamageSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      PropertyDescriptor pd = DependencyPropertyDescriptor.FromProperty(DataGridColumn.ActualWidthProperty, typeof(DataGridColumn));
      foreach (var column in dataGrid.Columns)
      {
        pd.AddValueChanged(column, new EventHandler(ColumnWidthPropertyChanged));
      }

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, "All Classes");
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);

      DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
    }

    private void Instance_EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentStats = null;
      dataGrid.ItemsSource = null;
      ChildGrids.Clear();
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
            title.Content = "Calculating DPS...";
            dataGrid.ItemsSource = null;
            ChildGrids.Clear();
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

            (Application.Current.MainWindow as MainWindow).Busy(false);
            UpdateDataGridMenuItems();
            break;
          case "NONPC":
            CurrentStats = null;
            title.Content = DEFAULT_TABLE_LABEL;
            (Application.Current.MainWindow as MainWindow).Busy(false);
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
        var damageTable = new DamageBreakdown(CurrentStats);
        damageTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "damageWindow", "Damage Breakdown", damageTable);
      }
    }

    private void ColumnWidthPropertyChanged(object sender, EventArgs e)
    {
      var column = sender as DataGridColumn;
      ChildGrids.ForEach(grid => grid.Columns[column.DisplayIndex].Width = column.ActualWidth);
    }

    private void DataGridAdpsTimelineClick(object sender, RoutedEventArgs e)
    {
      var timeline = new GanttChart(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
      var main = Application.Current.MainWindow as MainWindow;
      var window = Helpers.OpenNewTab(main.dockSite, "adpsTimeline", "ADPS Timeline", timeline, 400, 300);
      window.CanFloat = true;
      window.CanClose = true;
    }

    private void DataGridDamageLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        var log = new HitLogViewer(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
        var main = Application.Current.MainWindow as MainWindow;
        var window = Helpers.OpenNewTab(main.dockSite, "damageLog", "Damage Log", log, 400, 300);
        window.CanFloat = true;
        window.CanClose = true;
      }
    }

    private void DataGridHitFreqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        var chart = new HitFreqChart();
        var results = DamageStatsManager.Instance.GetHitFreqValues(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentStats);

        var main = Application.Current.MainWindow as MainWindow;
        var hitFreqWindow = Helpers.OpenNewTab(main.dockSite, "freqChart", "Hit Frequency", chart, 400, 300);

        chart.Update(results);
        hitFreqWindow.CanFloat = true;
        hitFreqWindow.CanClose = true;
      }
    }

    private void DataGridExpanderLoaded(object sender, RoutedEventArgs e)
    {
      Image image = (sender as Image);
      var children = CurrentStats?.Children;

      if (image.DataContext is PlayerStats stats && children != null && children.ContainsKey(stats.Name))
      {
        var list = children[stats.Name];
        if (list.Count > 1 || stats.Name == Labels.UNASSIGNED || (list.Count == 1 && !list[0].Name.StartsWith(stats.Name, StringComparison.Ordinal)))
        {
          if (dataGrid.ItemContainerGenerator.ContainerFromItem(stats) is DataGridRow container)
          {
            image.Source = container.DetailsVisibility != Visibility.Visible ? EXPAND_BITMAP : COLLAPSE_BITMAP;
          }
        }
      }
    }

    private void DataGridExpanderMouseDown(object sender, MouseButtonEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;

      if (image != null && dataGrid.ItemContainerGenerator.ContainerFromItem(stats) is DataGridRow container)
      {
        if (image.Source == COLLAPSE_BITMAP)
        {
          image.Source = EXPAND_BITMAP;
          container.DetailsVisibility = Visibility.Collapsed;
        }
        else if (image.Source == EXPAND_BITMAP)
        {
          image.Source = COLLAPSE_BITMAP;
          container.DetailsVisibility = Visibility.Visible;
        }
      }
    }

    private void ChildrenDataGridPrevMouseWheel(object sender, MouseEventArgs e)
    {
      if (!e.Handled)
      {
        e.Handled = true;
        MouseWheelEventArgs wheelArgs = e as MouseWheelEventArgs;
        var newEvent = new MouseWheelEventArgs(wheelArgs.MouseDevice, wheelArgs.Timestamp, wheelArgs.Delta)
        {
          RoutedEvent = MouseWheelEvent
        };

        var container = dataGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
        container.RaiseEvent(newEvent);
      }
    }

    private void ChildrenGridRowDetailsVis(object sender, DataGridRowDetailsEventArgs e)
    {
      var children = CurrentStats?.Children;
      if (e.Row.Item is PlayerStats stats && e.DetailsElement is DataGrid childrenDataGrid && children != null && children.ContainsKey(stats.Name))
      {
        if (childrenDataGrid.ItemsSource != children[stats.Name])
        {
          childrenDataGrid.ItemsSource = children[stats.Name];
          ChildGrids.Add(childrenDataGrid);

          // show bane column if needed
          if (dataGrid.Columns[4].Visibility == Visibility.Visible)
          {
            childrenDataGrid.Columns[4].Visibility = Visibility.Visible;
          }

          // fix column widths
          foreach (var column in dataGrid.Columns)
          {
            childrenDataGrid.Columns[column.DisplayIndex].Width = column.ActualWidth;
          }
        }
      }
    }

    private void UpdateDataGridMenuItems()
    {
      string selectedName = "Unknown";

      if (CurrentStats?.StatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowBreakdown.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        menuItemShowDamageLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
        copyDamageParseToEQClick.IsEnabled = true;

        if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
        {
          menuItemSetAsPet.IsEnabled = !PlayerManager.Instance.IsVerifiedPet(playerStats.OrigName) && !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName);
          selectedName = playerStats.OrigName;
        }

        EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats?.UniqueClasses);
        EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowBreakdown.IsEnabled =
         menuItemShowDamageLog.IsEnabled = menuItemSetAsPet.IsEnabled = menuItemShowSpellCasts.IsEnabled = menuItemShowHitFreq.IsEnabled = copyDamageParseToEQClick.IsEnabled = false;
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
          if (stats is PlayerStats playerStats)
          {
            className = playerStats.ClassName;
          }
          else if (stats is DataPoint dataPoint)
          {
            className = PlayerManager.Instance.GetPlayerClass(dataPoint.Name);
          }

          return string.IsNullOrEmpty(CurrentClass) || CurrentClass == className;
        };

        DamageStatsManager.Instance.FireFilterEvent(new GenerateStatsOptions() { RequestChartData = true }, view.Filter);
      }

      return view;
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
          CurrentStats = null;
          ChildGrids = null;
        }

        DamageStatsManager.Instance.EventsGenerationStatus -= Instance_EventsGenerationStatus;
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

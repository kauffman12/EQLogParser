using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    private bool Ready = false;

    public DamageSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      PropertyDescriptor pd = DependencyPropertyDescriptor.FromProperty(DataGridColumn.ActualWidthProperty, typeof(DataGridColumn));
      foreach (var column in dataGrid.Columns)
      {
        pd.AddValueChanged(column, new EventHandler(ColumnWidthPropertyChanged));
      }

      // read bane and healing setting
      string value = DataManager.Instance.GetApplicationSetting("IncludeBaneDamage");
      includeBane.IsChecked = value != null && bool.TryParse(value, out bool bValue) && bValue;

      value = DataManager.Instance.GetApplicationSetting("IngoreInitialPullDamage");
      pullerOption.IsChecked = value != null && bool.TryParse(value, out bool bValue2) && bValue2;

      value = DataManager.Instance.GetApplicationSetting("IsDamageOverlayEnabled");
      overlayOption.IsChecked = bool.TryParse(value, out bValue) && bValue;
      if (overlayOption.IsChecked.Value)
      {
        (Application.Current.MainWindow as MainWindow)?.OpenOverlay();
      }

      Ready = true;

      DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
    }

    internal bool IsBaneEnabled()
    {
      return includeBane.IsChecked.Value;
    }

    internal bool IsPullerEnabled()
    {
      return pullerOption.IsChecked.Value;
    }

    internal bool IsOverlayEnabled()
    {
      return overlayOption.IsChecked.Value;
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
        switch(e.State)
        {
          case "STARTED":
            (Application.Current.MainWindow as MainWindow).Busy(true);
            title.Content = "Calculating DPS...";
            dataGrid.ItemsSource = null;
            ChildGrids.Clear();
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats as CombinedStats;

            if (CurrentStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              includeBane.IsEnabled = e.IsBaneAvailable;
              title.Content = CurrentStats.FullTitle;
              dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentStats.StatsList);
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

    private void DataGridHitFreq_Click(object sender, RoutedEventArgs e)
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

    private void ShowColumn(int index, bool show)
    {
      if (dataGrid.Columns[index].Visibility == Visibility.Hidden && show || dataGrid.Columns[index].Visibility == Visibility.Visible && !show)
      {
        dataGrid.Columns[index].Visibility = show ? Visibility.Visible : Visibility.Hidden;
        foreach (var grid in ChildGrids)
        {
          grid.Columns[index].Visibility = show ? Visibility.Visible : Visibility.Hidden;
        }
      }
    }

    private void UpdateDataGridMenuItems()
    {
      if (CurrentStats?.StatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowDamage.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
        copyDamageParseToEQClick.IsEnabled = true;
        UpdateClassMenuItems(menuItemShowDamage, dataGrid, CurrentStats?.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowDamage.IsEnabled =
          menuItemShowSpellCasts.IsEnabled = menuItemShowHitFreq.IsEnabled = copyDamageParseToEQClick.IsEnabled = false;
      }
    }

    private void IncludeBaneChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        bool isPullerEnabled = pullerOption.IsChecked.Value;
        bool isBaneEnabled = includeBane.IsChecked.Value;
        DataManager.Instance.SetApplicationSetting("IncludeBaneDamage", isBaneEnabled.ToString(CultureInfo.CurrentCulture));

        if (CurrentStats != null && CurrentStats.RaidStats != null)
        {
          includeBane.IsEnabled = false;
          var options = new DamageStatsOptions() { IsBaneEanbled = isBaneEnabled, IsPullerEnabled = isPullerEnabled, RequestChartData = true, RequestSummaryData = true };
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(options));
        }
      }
    }

    private void PullerOptionChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        bool isBaneEnabled = includeBane.IsChecked.Value;
        bool isPullerEnabled =  pullerOption.IsChecked.Value;
        DataManager.Instance.SetApplicationSetting("IngoreInitialPullDamage", isPullerEnabled.ToString(CultureInfo.CurrentCulture));

        if (CurrentStats != null && CurrentStats.RaidStats != null)
        {
          var options = new DamageStatsOptions() { IsBaneEanbled = isBaneEnabled, IsPullerEnabled = isPullerEnabled, RequestChartData = true, RequestSummaryData = true };
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(options));
        }
      }
    }

    private void OverlayOptionChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        if (overlayOption.IsChecked.Value)
        {
          (Application.Current.MainWindow as MainWindow)?.OpenOverlay(true, false);
        }
        else
        {
          (Application.Current.MainWindow as MainWindow)?.CloseOverlay();
        }

        DataManager.Instance.SetApplicationSetting("IsDamageOverlayEnabled", overlayOption.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
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

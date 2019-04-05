using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
  public partial class DamageSummary : SummaryTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static BitmapImage COLLAPSE_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Collapse_16x.png"));
    private static BitmapImage EXPAND_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Expand_16x.png"));

    private CombinedDamageStats CurrentDamageStats = null;

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
      bool bValue;
      string value = DataManager.Instance.GetApplicationSetting("IncludeBaneDamage");
      includeBane.IsChecked = value != null && bool.TryParse(value, out bValue) && bValue;

      value = DataManager.Instance.GetApplicationSetting("IsDamageOverlayEnabled");
      overlayOption.IsChecked = bool.TryParse(value, out bValue) && bValue;
      if (overlayOption.IsChecked.Value)
      {
        (Application.Current.MainWindow as MainWindow)?.OpenOverlay();
      }

      Ready = true;
    }

    internal bool IsBaneEnabled()
    {
      return includeBane.IsChecked.Value;
    }

    internal bool IsOverlayEnabled()
    {
      return overlayOption.IsChecked.Value;
    }

    private void Instance_EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentDamageStats = null;
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
            CurrentDamageStats = e.CombinedStats as CombinedDamageStats;

            if (CurrentDamageStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              includeBane.IsEnabled = e.IsBaneAvailable;
              title.Content = CurrentDamageStats.FullTitle;
              dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentDamageStats.StatsList);
            }

            (Application.Current.MainWindow as MainWindow).Busy(false);
            UpdateDataGridMenuItems();
            break;
          case "NONPC":
            CurrentDamageStats = null;
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
        var damageTable = new DamageBreakdown(CurrentDamageStats);
        damageTable.Show(selected);
        Helpers.OpenNewTab(main.dockSite, "damageWindow", "Damage Breakdown", damageTable);
      }
    }

    protected override void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var spellTable = new SpellCountTable(CurrentDamageStats?.ShortTitle ?? "");
        spellTable.ShowSpells(selected, CurrentDamageStats);
        var main = Application.Current.MainWindow as MainWindow;
        Helpers.OpenNewTab(main.dockSite, "spellCastsWindow", "Spell Counts", spellTable);
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
        var results = DamageStatsManager.Instance.GetHitFreqValues(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentDamageStats);

        var main = Application.Current.MainWindow as MainWindow;
        var hitFreqWindow = Helpers.OpenNewTab(main.dockSite, "freqChart", "Hit Frequency", chart, 400, 300);

        chart.Update(results);
        hitFreqWindow.CanFloat = true;
        hitFreqWindow.CanClose = true;
      }
    }

    private void DataGridExpander_Loaded(object sender, RoutedEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      var children = CurrentDamageStats?.Children;

      if (stats != null && children != null && children.ContainsKey(stats.Name))
      {
        var list = children[stats.Name];
        if (list.Count > 1 || stats.Name == Labels.UNASSIGNED || (list.Count == 1 && !list[0].Name.StartsWith(stats.Name)))
        {
          var container = dataGrid.ItemContainerGenerator.ContainerFromItem(stats) as DataGridRow;
          if (container != null)
          {
            image.Source = container.DetailsVisibility != Visibility.Visible ? EXPAND_BITMAP : COLLAPSE_BITMAP;
          }
        }
      }
    }

    private void DataGridExpander_MouseDown(object sender, MouseButtonEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      var container = dataGrid.ItemContainerGenerator.ContainerFromItem(stats) as DataGridRow;

      if (image != null && container != null)
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

    private void ChildrenDataGrid_PrevMouseWheel(object sender, MouseEventArgs e)
    {
      if (!e.Handled)
      {
        e.Handled = true;
        MouseWheelEventArgs wheelArgs = e as MouseWheelEventArgs;
        var newEvent = new MouseWheelEventArgs(wheelArgs.MouseDevice, wheelArgs.Timestamp, wheelArgs.Delta);
        newEvent.RoutedEvent = MouseWheelEvent;
        var container = dataGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
        container.RaiseEvent(newEvent);
      }
    }

    private void ChildrenGrid_RowDetailsVis(object sender, DataGridRowDetailsEventArgs e)
    {
      PlayerStats stats = e.Row.Item as PlayerStats;
      var childrenDataGrid = e.DetailsElement as DataGrid;
      var children = CurrentDamageStats?.Children;

      if (stats != null && childrenDataGrid != null && children != null && children.ContainsKey(stats.Name))
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
      if (CurrentDamageStats?.StatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowDamage.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
        copyDamageParseToEQClick.IsEnabled = true;
        UpdateClassMenuItems(menuItemShowDamage, dataGrid, CurrentDamageStats?.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentDamageStats?.UniqueClasses);
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
        bool isBaneEnabled = includeBane.IsChecked.Value;
        DataManager.Instance.SetApplicationSetting("IncludeBaneDamage", isBaneEnabled.ToString());

        if (CurrentDamageStats != null && CurrentDamageStats.RaidStats != null)
        {
          includeBane.IsEnabled = false;
          var options = new DamageStatsOptions() { IsBaneEanbled = isBaneEnabled, RequestChartData = true, RequestSummaryData = true };
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

        DataManager.Instance.SetApplicationSetting("IsDamageOverlayEnabled", overlayOption.IsChecked.Value.ToString());
      }
    }

    private void Summary_Unloaded(object sender, RoutedEventArgs e)
    {
      DamageStatsManager.Instance.EventsGenerationStatus -= Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData -= Instance_EventsClearedActiveData;
      connected = false;
    }

    protected override void Summary_Loaded(object sender, RoutedEventArgs e)
    {
      DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;
      connected = true;
    }
  }
}

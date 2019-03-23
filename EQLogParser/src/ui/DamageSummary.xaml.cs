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

    // workaround for adjusting column withs of player datagrid
    private List<DataGrid> ChildGrids = new List<DataGrid>();

    private CombinedDamageStats CurrentDamageStats = null;
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

      // Clear/Reset
      DataManager.Instance.EventsClearedActiveData += (sender, cleared) =>
      {
        CurrentDamageStats = null;
        dataGrid.ItemsSource = null;
        ChildGrids.Clear();
        title.Content = DEFAULT_TABLE_LABEL;
      };

      // read bane and healing setting
      bool bValue;
      string value = DataManager.Instance.GetApplicationSetting("IncludeBaneDamage");
      includeBane.IsChecked = value != null && bool.TryParse(value, out bValue) && bValue;

      value = DataManager.Instance.GetApplicationSetting("IsDamageOverlayEnabled");
      overlayOption.IsChecked = bool.TryParse(value, out bValue) && bValue;
      if (overlayOption.IsChecked.Value)
      {
        TheMainWindow.OpenOverlay();
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

    internal void UpdateStats(List<NonPlayer> npcList, bool rebuild = false)
    {
      if (UpdateStatsTask == null && (rebuild || (npcList != null && npcList.Count > 0)))
      {
        TheMainWindow.Busy(true);
        title.Content = "Calculating DPS...";
        ChildGrids.Clear();

        string name = npcList?.First().Name;
        bool showBane = includeBane.IsChecked.Value;
        UpdateStatsTask = new Task(() =>
        {
          try
          {
            if (rebuild)
            {
              CurrentDamageStats = DamageStatsBuilder.ComputeDamageStats(CurrentDamageStats.RaidStats, showBane);
            }
            else
            {
              CurrentDamageStats = DamageStatsBuilder.BuildTotalStats(name, npcList, showBane);
            }

            Dispatcher.InvokeAsync((() =>
            {
              if (CurrentDamageStats == null)
              {
                title.Content = NODATA_TABLE_LABEL;
                dataGrid.ItemsSource = null;
              }
              else
              {
                includeBane.IsEnabled = DamageStatsBuilder.IsBaneAvailable;
                title.Content = CurrentDamageStats.FullTitle;
                dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentDamageStats.StatsList);
              }

              UpdateDataGridMenuItems();
            }));
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
          }
          finally
          {
            Dispatcher.InvokeAsync(() =>
            {
              UpdateStatsTask = null;
              TheMainWindow.Busy(false);
            });
          }
        });
          
        UpdateStatsTask.Start();    
      }
      else if (dataGrid.ItemsSource is ObservableCollection<PlayerStats> damageList)
      {
        CurrentDamageStats = null;
        dataGrid.ItemsSource = null;
        title.Content = DEFAULT_TABLE_LABEL;
        UpdateDataGridMenuItems();
      }
    }

    protected void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var selected = GetSelectedStats();
      DamageStatsBuilder.FireSelectionEvent(selected);
      FireSelectionChangedEvent(selected);
      UpdateDataGridMenuItems();
    }

    protected override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var damageTable = new DamageBreakdown(TheMainWindow, CurrentDamageStats.ShortTitle);
        damageTable.Show(selected, CurrentDamageStats);
        Helpers.OpenNewTab(TheMainWindow.dockSite, "damageWindow", "Damage Breakdown", damageTable);
      }
    }

    protected override void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var spellTable = new SpellCountTable(TheMainWindow, CurrentDamageStats.ShortTitle);
        spellTable.ShowSpells(selected, CurrentDamageStats);
        Helpers.OpenNewTab(TheMainWindow.dockSite, "spellCastsWindow", "Spell Counts", spellTable);
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
        var results = DamageStatsBuilder.GetHitFreqValues(CurrentDamageStats, dataGrid.SelectedItems.Cast<PlayerStats>().First());
        var hitFreqWindow = Helpers.OpenNewTab(TheMainWindow.dockSite, "freqChart", "Hit Frequency", chart, 400, 300);

        chart.Update(results);
        hitFreqWindow.CanFloat = true;
        hitFreqWindow.CanClose = true;
      }
    }

    private void DataGridExpander_Loaded(object sender, RoutedEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;

      if (stats != null && CurrentDamageStats.Children.ContainsKey(stats.Name))
      {
        var list = CurrentDamageStats.Children[stats.Name];
        if (list.Count > 1 || stats.Name == Labels.UNASSIGNED_PET_OWNER || (list.Count == 1 && !list[0].Name.StartsWith(stats.Name)))
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
      if (stats != null && childrenDataGrid != null && CurrentDamageStats != null && CurrentDamageStats.Children.ContainsKey(stats.Name))
      {
        if (childrenDataGrid.ItemsSource != CurrentDamageStats.Children[stats.Name])
        {
          childrenDataGrid.ItemsSource = CurrentDamageStats.Children[stats.Name];
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
      if (CurrentDamageStats != null && CurrentDamageStats.StatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowDamage.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
        UpdateClassMenuItems(menuItemShowDamage, dataGrid, CurrentDamageStats.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentDamageStats.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowDamage.IsEnabled =
          menuItemShowSpellCasts.IsEnabled = menuItemShowHitFreq.IsEnabled = false;
      }
    }

    private void IncludeBaneChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        UpdateStats(null, true);
        DataManager.Instance.SetApplicationSetting("IncludeBaneDamage", includeBane.IsChecked.Value.ToString());
      }
    }

    private void OverlayOptionChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        if (overlayOption.IsChecked.Value)
        {
          TheMainWindow.OpenOverlay(true, false);
        }
        else
        {
          TheMainWindow.CloseOverlay();
        }

        DataManager.Instance.SetApplicationSetting("IsDamageOverlayEnabled", overlayOption.IsChecked.Value.ToString());
      }
    }
  }
}

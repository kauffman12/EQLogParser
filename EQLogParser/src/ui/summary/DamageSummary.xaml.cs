using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageSummary.xaml
  /// </summary>
  public partial class DamageSummary : SummaryTable, IDisposable
  {
    private readonly static BitmapImage COLLAPSE_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Collapse_16x.png"));
    private readonly static BitmapImage EXPAND_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Expand_16x.png"));

    // workaround for adjusting column withs of player datagrid
    private List<DataGrid> ChildGrids = new List<DataGrid>();
    private string CurrentClass = null;
    private int CurrentGroupCount = 0;
    private int CurrentPetOrPlayerOption = 0;
    private readonly DispatcherTimer SelectionTimer;

    public DamageSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid, selectedColumns);

      PropertyDescriptor widthPd = DependencyPropertyDescriptor.FromProperty(DataGridColumn.ActualWidthProperty, typeof(DataGridColumn));
      PropertyDescriptor orderPd = DependencyPropertyDescriptor.FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn));

      foreach (var column in dataGrid.Columns)
      {
        widthPd.AddValueChanged(column, new EventHandler(ColumnWidthPropertyChanged));
        orderPd.AddValueChanged(column, new EventHandler(ColumnDisplayIndexPropertyChanged));
      }

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Properties.Resources.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      petOrPlayerList.ItemsSource = new List<string> { Labels.PETPLAYEROPTION, Labels.PLAYEROPTION, Labels.PETOPTION, Labels.EVERYTHINGOPTION };
      petOrPlayerList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);

      DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1200) };
      SelectionTimer.Tick += (sender, e) =>
      {
        var damageOptions = new GenerateStatsOptions() { RequestSummaryData = true, MaxSeconds = timeChooser.Value };
        Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(damageOptions));
        SelectionTimer.Stop();
      };
    }

    internal new void SelectDataGridColumns(object sender, EventArgs e) => TheShownColumns = DataGridUtils.ShowColumns(selectedColumns, dataGrid, ChildGrids);

    private void CopyToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.DAMAGEPARSE);

    internal override bool IsPetsCombined() => CurrentPetOrPlayerOption == 0;

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
            title.Content = "Calculating DPS...";
            dataGrid.ItemsSource = null;
            ChildGrids.Clear();
            timeChooser.Value = 0;
            timeChooser.MaxValue = 0;
            timeChooser.IsEnabled = false;
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats;
            CurrentGroups = e.Groups;
            CurrentGroupCount = e.UniqueGroupCount;

            if (CurrentStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
              UpdateView();
              timeChooser.Value = Convert.ToInt64(CurrentStats.RaidStats.TotalSeconds);
              timeChooser.MaxValue = Convert.ToInt64(CurrentStats.RaidStats.MaxTime);
            }

            if (!MainWindow.IsBaneDamageEnabled)
            {
              title.Content += " (Not Including Banes)";
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

    private void ColumnDisplayIndexPropertyChanged(object sender, EventArgs e)
    {
      ChildGrids.ForEach(grid =>
      {
        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
          if (dataGrid.Columns[i].DisplayIndex != grid.Columns[i].DisplayIndex)
          {
            grid.Columns[i].DisplayIndex = dataGrid.Columns[i].DisplayIndex;
          }
        }
      });
    }

    private void DataGridAdpsTimelineClick(object sender, RoutedEventArgs e)
    {
      var timeline = new GanttChart(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentGroups);
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
        if (list.Count > 1 || (list.Count == 1 && !list[0].Name.StartsWith(stats.Name, StringComparison.Ordinal)))
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
        // initialize data one time
        if (childrenDataGrid.ItemsSource != children[stats.Name])
        {
          childrenDataGrid.ItemsSource = children[stats.Name];
          ChildGrids.Add(childrenDataGrid);

          // fix column widths and hidden values
          for (int i = 0; i < dataGrid.Columns.Count; i++)
          {
            var column = dataGrid.Columns[i];
            var childColumn = childrenDataGrid.Columns[i];

            if (childColumn.Width != column.ActualWidth)
            {
              childColumn.Width = column.ActualWidth;
            }

            if (childColumn.DisplayIndex != column.DisplayIndex)
            {
              childColumn.DisplayIndex = column.DisplayIndex;
            }

            if (TheShownColumns != null && TheShownColumns.Count > 0)
            {
              // never let users hide the first two columns
              if (i > 1)
              {
                var vis = TheShownColumns.ContainsKey(column.Header as string) ? Visibility.Visible : Visibility.Hidden;
                if (vis != childColumn.Visibility)
                {
                  childColumn.Visibility = vis;
                }
              }
            }
            else
            {
              if (childColumn.Visibility != dataGrid.Columns[i].Visibility)
              {
                childColumn.Visibility = dataGrid.Columns[i].Visibility;
              }
            }
          }
        }
      }
    }

    private void UpdateDataGridMenuItems()
    {
      string selectedName = "Unknown";

      if (CurrentStats?.ExpandedStatsList?.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowSpellCasts.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
        menuItemShowDamageLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
        menuItemShowAdpsTimeline.IsEnabled = (dataGrid.SelectedItems.Count == 1 || dataGrid.SelectedItems.Count == 2) && CurrentGroupCount == 1;
        copyDamageParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;

        if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
        {
          menuItemSetAsPet.IsEnabled = !PlayerManager.Instance.IsVerifiedPet(playerStats.OrigName) && !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName);
          selectedName = playerStats.OrigName;
        }

        EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats?.UniqueClasses);
        EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
        EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowDamageLog.IsEnabled =
          menuItemSetAsPet.IsEnabled = menuItemShowSpellCounts.IsEnabled = menuItemShowHitFreq.IsEnabled = copyDamageParseToEQClick.IsEnabled =
          copyOptions.IsEnabled = menuItemShowAdpsTimeline.IsEnabled = menuItemShowSpellCasts.IsEnabled = false;
      }

      menuItemSetAsPet.Header = string.Format(CultureInfo.CurrentCulture, "Set {0} as Pet", selectedName);
    }

    private ICollectionView SetFilter(ICollectionView view)
    {
      if (view != null)
      {
        view.Filter = (stats) =>
        {
          string name = "";
          string className = "";
          if (stats is PlayerStats playerStats)
          {
            name = playerStats.Name;
            className = playerStats.ClassName;
          }
          else if (stats is string playerName)
          {
            name = playerName;
            className = PlayerManager.Instance.GetPlayerClass(name);
          }

          bool result = false;
          if (CurrentPetOrPlayerOption == 1)
          {
            result = !PlayerManager.Instance.IsVerifiedPet(name) && (string.IsNullOrEmpty(CurrentClass) || CurrentClass == className);
          }
          else if (CurrentPetOrPlayerOption == 2)
          {
            result = PlayerManager.Instance.IsVerifiedPet(name);
          }
          else
          {
            result = string.IsNullOrEmpty(CurrentClass) || CurrentClass == className;
          }

          return result;
        };

        DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { RequestChartData = true }, "FILTER", null, view.Filter);
      }

      return view;
    }

    private void UpdateView()
    {
      if (dataGrid != null && CurrentStats?.ExpandedStatsList != null)
      {
        petOrPlayerList.IsEnabled = classesList.IsEnabled = timeChooser.IsEnabled = false;
        Task.Delay(20).ContinueWith(task =>
        {
          // until i figure out something better just re-rank everything
          var statsList = CurrentPetOrPlayerOption == 0 ? CurrentStats.StatsList : CurrentStats.ExpandedStatsList;
          for (int i = 0; i < statsList.Count; i++)
          {
            statsList[i].Rank = Convert.ToUInt16(i + 1);
          }

          Dispatcher.InvokeAsync(() =>
          {
            var view = CollectionViewSource.GetDefaultView(statsList);
            dataGrid.ItemsSource = SetFilter(view);
            petOrPlayerList.IsEnabled = true;
            timeChooser.IsEnabled = CurrentGroupCount == 1;
            classesList.IsEnabled = CurrentPetOrPlayerOption != 2;
          });
        }, TaskScheduler.Default);
      }
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      CurrentClass = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      CurrentPetOrPlayerOption = petOrPlayerList.SelectedIndex;
      UpdateView();
    }

    private void MaxTimeChanged(object sender, RoutedPropertyChangedEventArgs<long> e)
    {
      if (timeChooser.IsEnabled && e.OldValue != 0 && e.NewValue != 0)
      {
        SelectionTimer.Stop();
        SelectionTimer.Start();
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions() { MaxSeconds = long.MinValue, RequestChartData = true }, "UPDATE");

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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public class SummaryTable : UserControl, IDisposable
  {
    protected const string DEFAULT_TABLE_LABEL = "No NPCs Selected";
    protected const string NODATA_TABLE_LABEL = Labels.NO_DATA;

    internal event EventHandler<PlayerStatsSelectionChangedEvent> EventsSelectionChange;

    protected MainWindow TheMainWindow;
    protected DataGrid TheDataGrid;
    protected Label TheTitle;
    protected Task UpdateStatsTask;

    protected void InitSummaryTable(Label title, DataGrid dataGrid)
    {
      TheMainWindow = Application.Current.MainWindow as MainWindow;
      TheDataGrid = dataGrid;
      TheTitle = title;

      title.Content = DEFAULT_TABLE_LABEL;
      dataGrid.Sorting += DataGrid_Sorting; // sort numbers descending
    }

    internal void UpdateClassMenuItems(MenuItem menu, DataGrid dataGrid, Dictionary<string, byte> uniqueClasses)
    {
      foreach (var item in menu.Items)
      {
        MenuItem menuItem = item as MenuItem;
        menuItem.IsEnabled = menuItem.Header as string == "Selected" ? dataGrid.SelectedItems.Count > 0 : uniqueClasses.ContainsKey(menuItem.Header as string);
      }
    }

    internal void Clear()
    {
      TheTitle.Content = DEFAULT_TABLE_LABEL;
      TheDataGrid.ItemsSource = null;
    }

    internal List<PlayerStats> GetSelectedStats()
    {
      return TheDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
    }

    protected void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      TheMainWindow.CopyToEQ_Click();
    }

    protected void DataGridSelectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridSelectAll(sender);
    }

    protected void DataGridUnselectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridUnselectAll(sender);
    }

    protected void DataGridShowBreakdown_Click(object sender, RoutedEventArgs e)
    {
      ShowBreakdown(GetSelectedStats());
    }

    protected void DataGridShowBreakdownByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowBreakdown(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, TheDataGrid.Items));
    }

    protected void DataGridShowSpellCasts_Click(object sender, RoutedEventArgs e)
    {
      ShowSpellCasts(GetSelectedStats());
    }

    protected void DataGridSpellCastsByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, TheDataGrid.Items));
    }

    protected void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      EventsSelectionChange(this, new PlayerStatsSelectionChangedEvent() { Selected = selected });
    }

    protected virtual void ShowBreakdown(List<PlayerStats> selected)
    {
      // need to override this method
    }

    protected virtual void ShowSpellCasts(List<PlayerStats> selected)
    {
      // need to override this method
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
      if (e.Column.Header != null && e.Column.Header.ToString() != "Name")
      {
        e.Column.SortDirection = e.Column.SortDirection ?? ListSortDirection.Ascending;
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        UpdateStatsTask?.Dispose();
        UpdateStatsTask = null;
        disposedValue = true;
      }
    }

    ~SummaryTable() {
      Dispose(false);
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}

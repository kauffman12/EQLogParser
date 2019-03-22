using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public class SummaryTable : UserControl
  {
    protected const string DEFAULT_TABLE_LABEL = " No NPCs Selected";
    protected MainWindow TheMainWindow;
    protected DataGrid TheDataGrid;

    internal static void UpdateClassMenuItems(MenuItem menu, DataGrid dataGrid, Dictionary<string, byte> uniqueClasses)
    {
      foreach (var item in menu.Items)
      {
        MenuItem menuItem = item as MenuItem;
        menuItem.IsEnabled = menuItem.Header as string == "Selected" ? dataGrid.SelectedItems.Count > 0 : uniqueClasses.ContainsKey(menuItem.Header as string);
      }
    }

    internal List<PlayerStats> GetSelectedStats()
    {
      return TheDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
    }

    protected void InitSummaryTable(Label title, DataGrid dataGrid)
    {
      TheMainWindow = Application.Current.MainWindow as MainWindow;
      TheDataGrid = dataGrid;
      title.Content = DEFAULT_TABLE_LABEL;

      // fix player DPS table sorting
      dataGrid.Sorting += DataGrid_Sorting;
    }

    protected void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      TheMainWindow.CopyToEQ_Click(null, null);
    }

    protected void DataGridSelectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridSelectAll(sender);
    }

    protected void DataGridUnselectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridUnselectAll(sender);
    }

    protected void DataGridShowDamage_Click(object sender, RoutedEventArgs e)
    {
      ShowDamage(GetSelectedStats());
    }

    protected void DataGridShowDamageByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowDamage(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, TheDataGrid.Items));
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

    protected virtual void ShowDamage(List<PlayerStats> selected)
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
  }
}

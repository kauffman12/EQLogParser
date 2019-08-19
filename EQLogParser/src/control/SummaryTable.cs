using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public class SummaryTable : UserControl
  {
    protected const string DEFAULT_TABLE_LABEL = "No NPCs Selected";
    protected const string NODATA_TABLE_LABEL = Labels.NODATA;

    internal event EventHandler<PlayerStatsSelectionChangedEventArgs> EventsSelectionChange;

    protected DataGrid TheDataGrid;
    protected Label TheTitle;
    protected CombinedStats CurrentStats;

    protected void InitSummaryTable(Label title, DataGrid dataGrid)
    {
      TheDataGrid = dataGrid;
      TheTitle = title;

      title.Content = DEFAULT_TABLE_LABEL;
      dataGrid.Sorting += DataGrid_Sorting; // sort numbers descending
    }

    internal static void UpdateClassMenuItems(MenuItem menu, DataGrid dataGrid, Dictionary<string, byte> uniqueClasses)
    {
      foreach (var item in menu.Items)
      {
        MenuItem menuItem = item as MenuItem;
        menuItem.IsEnabled = menuItem.Header as string == "Selected" ? dataGrid.SelectedItems.Count > 0 : uniqueClasses != null && uniqueClasses.ContainsKey(menuItem.Header as string);
      }
    }

    internal void Clear()
    {
      TheTitle.Content = DEFAULT_TABLE_LABEL;
      TheDataGrid.ItemsSource = null;
    }

    internal Predicate<object> GetFilter()
    {
      return (TheDataGrid.ItemsSource as ICollectionView)?.Filter;
    }

    internal List<PlayerStats> GetSelectedStats()
    {
      return TheDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
    }

    protected void CopyToEQClick(object sender, RoutedEventArgs e)
    {
      (Application.Current.MainWindow as MainWindow).CopyToEQClick();
    }

    protected void DataGridSelectAllClick(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridSelectAll(sender as FrameworkElement);
    }

    protected void DataGridUnselectAllClick(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridUnselectAll(sender as FrameworkElement);
    }

    protected void DataGridShowBreakdownClick(object sender, RoutedEventArgs e)
    {
      ShowBreakdown(GetSelectedStats());
    }

    protected void DataGridShowBreakdown2Click(object sender, RoutedEventArgs e)
    {
      ShowBreakdown2(GetSelectedStats());
    }

    protected void DataGridShowBreakdownByClassClick(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowBreakdown(GetPlayerStatsByClass(menuItem.Tag as string));
    }

    protected void DataGridShowBreakdown2ByClassClick(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = sender as MenuItem;
      ShowBreakdown2(GetPlayerStatsByClass(menuItem.Tag as string));
    }

    protected void DataGridShowSpellCastsClick(object sender, RoutedEventArgs e)
    {
      ShowSpellCasts(GetSelectedStats());
    }

    protected void DataGridSpellCastsByClassClick(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(GetPlayerStatsByClass(menuItem?.Tag as string));
    }

    protected List<PlayerStats> GetPlayerStatsByClass(string classString)
    {
      SpellClass type = (SpellClass)Enum.Parse(typeof(SpellClass), classString);
      string className = DataManager.Instance.GetClassName(type);

      List<PlayerStats> selectedStats = new List<PlayerStats>();
      foreach (var item in TheDataGrid.Items)
      {
        PlayerStats stats = item as PlayerStats;
        if (stats.ClassName == className)
        {
          selectedStats.Add(stats);
        }
      }

      return selectedStats;
    }

    protected void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      EventsSelectionChange(this, new PlayerStatsSelectionChangedEventArgs() { Selected = selected });
    }

    protected virtual void ShowBreakdown(List<PlayerStats> selected)
    {
      // need to override this method
    }

    protected virtual void ShowBreakdown2(List<PlayerStats> selected)
    {
      // need to override this method
    }

    protected void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var spellTable = new SpellCountTable(CurrentStats?.ShortTitle ?? "");
        spellTable.ShowSpells(selected, CurrentStats);
        var main = Application.Current.MainWindow as MainWindow;
        Helpers.OpenNewTab(main.dockSite, "spellCastsWindow", "Spell Counts", spellTable);
      }
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

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

    internal event EventHandler<PlayerStatsSelectionChangedEvent> EventsSelectionChange;

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

    internal void UpdateClassMenuItems(MenuItem menu, DataGrid dataGrid, Dictionary<string, byte> uniqueClasses)
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

    internal List<PlayerStats> GetSelectedStats()
    {
      return TheDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
    }

    protected void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      (Application.Current.MainWindow as MainWindow).CopyToEQ_Click();
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
      ShowBreakdown(GetPlayerStatsByClass(menuItem.Tag as string));
    }

    protected void DataGridShowSpellCasts_Click(object sender, RoutedEventArgs e)
    {
      ShowSpellCasts(GetSelectedStats());
    }

    protected void DataGridSpellCastsByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(GetPlayerStatsByClass(menuItem.Tag as string));
    }

    protected List<PlayerStats> GetPlayerStatsByClass(string classString)
    {
      SpellClass type = (SpellClass) Enum.Parse(typeof(SpellClass), classString);
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
      EventsSelectionChange(this, new PlayerStatsSelectionChangedEvent() { Selected = selected });
    }

    protected virtual void ShowBreakdown(List<PlayerStats> selected)
    {
      // need to override this method
    }

    protected void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
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

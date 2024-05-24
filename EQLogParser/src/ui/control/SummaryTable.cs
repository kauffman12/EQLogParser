using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  public class SummaryTable : UserControl
  {
    internal const string DefaultTableLabel = "No NPCs Selected";
    internal const string NodataTableLabel = Labels.NoData;
    internal readonly List<PlayerStats> NoResultsList = [];

    internal dynamic TheDataGrid;
    internal ComboBox TheColumnsCombo;
    internal Label TheTitle;
    internal CombinedStats CurrentStats;
    internal List<List<ActionGroup>> CurrentGroups;
    private static readonly string[] Item = ["Rank", "Rank"];

    internal void InitSummaryTable(Label title, SfGridBase gridBase, ComboBox columnsCombo)
    {
      TheDataGrid = gridBase;
      TheColumnsCombo = columnsCombo;
      TheDataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
      TheTitle = title;
      TheTitle.Content = DefaultTableLabel;

      // default these columns to descending
      var desc = new[] { "PercentOfRaid", "Total", "Extra", "Potential", "DPS", "SDPS", "TotalSeconds", "Hits", "Max",
        "Avg", "AvgCrit", "AvgLucky", "ExtraRate", "CritRate", "LuckRate", "MeleeHitRate", "MeleeAccRate", "RampageRate", "Special"};

      if (TheDataGrid is SfTreeGrid treeGrid)
      {
        treeGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
        treeGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      }
      else if (TheDataGrid is SfDataGrid dataGrid)
      {
        dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
        dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      }

      DataGridUtil.LoadColumns(TheColumnsCombo, TheDataGrid);
      DataGridUtil.UpdateTableMargin(TheDataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      // workaround to avoid drag/drop failing when grid has no data
      TheDataGrid.ItemsSource = NoResultsList;
    }

    internal virtual bool IsPetsCombined() => false;
    internal virtual void ShowBreakdown(List<PlayerStats> selected) => new object(); // need to override this method
    internal virtual void ShowBreakdown2(List<PlayerStats> selected) => new object(); // need to override this method
    internal virtual void UpdateDataGridMenuItems() => new object(); // need to override this method
    internal virtual void FireSelectionChangedEvent(List<PlayerStats> stats) => new object(); // need to override this method
    internal string GetTargetTitle() => CurrentStats?.TargetTitle ?? GetTitle();
    internal string GetTitle() => TheTitle.Content as string;
    internal void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(TheDataGrid, TheTitle.Content.ToString());
    internal void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitle);
    internal void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitle, true);
    internal void DataGridShowBreakdownClick(object sender, RoutedEventArgs e) => ShowBreakdown(GetSelectedStats());
    internal void DataGridShowBreakdown2Click(object sender, RoutedEventArgs e) => ShowBreakdown2(GetSelectedStats());
    internal void DataGridShowBreakdownByClassClick(object sender, RoutedEventArgs e) => ShowBreakdown(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void DataGridShowBreakdown2ByClassClick(object sender, RoutedEventArgs e) => ShowBreakdown2(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void DataGridShowSpellCountsClick(object sender, RoutedEventArgs e) => ShowSpellCounts(GetSelectedStats());
    internal void DataGridSpellCountsByClassClick(object sender, RoutedEventArgs e) => ShowSpellCounts(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void DataGridShowSpellCastsClick(object sender, RoutedEventArgs e) => ShowSpellCasts(GetSelectedStats());
    internal void DataGridSpellCastsByClassClick(object sender, RoutedEventArgs e) => ShowSpellCasts(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(TheColumnsCombo, TheDataGrid);
    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);

    internal static void CreateClassMenuItems(MenuItem parent, Action<object, RoutedEventArgs> selectedHandler, Action<object, RoutedEventArgs> classHandler)
    {
      var selected = new MenuItem { IsEnabled = false, Header = "Selected" };
      selected.Click += new RoutedEventHandler(selectedHandler);
      parent.Items.Add(selected);

      PlayerManager.Instance.GetClassList().ForEach(name =>
      {
        var item = new MenuItem { IsEnabled = false, Header = name };
        item.Click += new RoutedEventHandler(classHandler);
        parent.Items.Add(item);
      });
    }

    internal static void CreateSpellCountMenuItems(MenuItem parent, Action<object, RoutedEventArgs> selectedHandler, Action<object, RoutedEventArgs> classHandler)
    {
      CreateClassMenuItems(parent, selectedHandler, classHandler);
      parent.Items.Add(new Separator());
      var item = new MenuItem { IsEnabled = true, Header = "All Players" };
      item.Click += new RoutedEventHandler(classHandler);
      parent.Items.Add(item);
    }

    internal void Clear()
    {
      TheTitle.Content = DefaultTableLabel;
      TheDataGrid.ItemsSource = null;
    }

    internal static void EnableClassMenuItems(MenuItem menu, SfGridBase gridBase, Dictionary<string, byte> uniqueClasses)
    {
      foreach (var item in menu.Items)
      {
        if (item is MenuItem { Header: string headerValue } menuItem)
        {
          if (headerValue == "Selected")
          {
            menuItem.IsEnabled = gridBase.SelectedItems.Count > 0;
          }
          else if (headerValue != "All Players")
          {
            menuItem.IsEnabled = uniqueClasses != null && uniqueClasses.ContainsKey(headerValue);
          }
        }
      }
    }

    internal List<string[]> GetHeaders()
    {
      var headers = new List<string[]> { Item };

      if (TheDataGrid is SfTreeGrid treeGrid)
      {
        foreach (var column in treeGrid.Columns)
        {
          var binding = ((Binding)column.ValueBinding).Path.Path;
          var title = column.HeaderText;
          headers.Add([binding, title]);
        }
      }
      else if (TheDataGrid is SfDataGrid dataGrid)
      {
        foreach (var column in dataGrid.Columns)
        {
          var binding = ((Binding)column.ValueBinding).Path.Path;
          var title = column.HeaderText;
          headers.Add([binding, title]);
        }
      }

      return headers;
    }

    internal List<PlayerStats> GetSelectedStats()
    {
      if (TheDataGrid is SfTreeGrid treeGrid)
      {
        return treeGrid.SelectedItems.Cast<PlayerStats>().ToList();
      }

      if (TheDataGrid is SfDataGrid dataGrid)
      {
        return dataGrid.SelectedItems.Cast<PlayerStats>().ToList();
      }

      return [];
    }

    internal List<PlayerStats> GetPlayerStats()
    {
      if (TheDataGrid is SfDataGrid { View.Records: not null } dataGrid)
      {
        return dataGrid.View.Records.Select(record => record.Data).Cast<PlayerStats>().ToList();
      }

      if (TheDataGrid is SfTreeGrid { View.Nodes: not null } treeGrid)
      {
        var results = new List<PlayerStats>();
        foreach (var stats in treeGrid.View.Nodes.Where(node => node.Level == 0).Select(node => node.Item).Cast<PlayerStats>())
        {
          results.Add(stats);
          if (CurrentStats.Children.TryGetValue(stats.Name, out var child))
          {
            results.AddRange(child);
          }
        }
        return results;
      }

      return [];
    }

    internal List<PlayerStats> GetStatsByClass(string className)
    {
      if (className == "All Players")
      {
        return null;
      }

      return GetPlayerStats().Where(stats => stats.IsTopLevel && stats.ClassName == className).ToList();
    }

    internal void DataGridSelectionChanged()
    {
      FireSelectionChangedEvent(GetSelectedStats());
      UpdateDataGridMenuItems();
    }

    internal void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (SyncFusionUtil.OpenWindow(out var spellTable, typeof(SpellCastTable), "spellCastsWindow", "Spell Cast Order"))
      {
        (spellTable.Content as SpellCastTable)?.Init(selected, CurrentStats);
      }
    }

    internal void ShowSpellCounts(List<PlayerStats> selected)
    {
      if (SyncFusionUtil.OpenWindow(out var spellTable, typeof(SpellCountTable), "spellCountsWindow", "Spell Counts"))
      {
        (spellTable.Content as SpellCountTable)?.Init(selected, CurrentStats);
      }
    }

    private void EventsThemeChanged(string _)
    {
      DataGridUtil.RefreshTableColumns(TheDataGrid);
    }
  }
}

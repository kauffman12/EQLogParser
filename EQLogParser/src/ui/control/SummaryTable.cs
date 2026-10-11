using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  public class SummaryTable : UserControl
  {
    internal readonly List<PlayerStats> NoResultsList = [];

    internal dynamic TheDataGrid;
    internal ComboBox TheClassesCombo;
    internal ComboBox TheColumnsCombo;
    internal Label TheTitle;
    internal CombinedStats CurrentStats;
    internal List<List<ActionGroup>> CurrentGroups;
    internal readonly List<string> SelectedClasses = [];
    private static readonly string[] Item = ["Rank", "Rank"];

    internal void InitSummaryTable(Label title, SfGridBase gridBase, ComboBox columnsCombo, ComboBox classesCombo)
    {
      TheDataGrid = gridBase;
      TheColumnsCombo = columnsCombo;
      TheClassesCombo = classesCombo;
      TheDataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
      TheTitle = title;
      TheTitle.Content = Labels.NoNpcs;

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

      // classes combo
      SharedControls.PopulateClassesList(TheClassesCombo, SelectedClasses);

      DataGridUtil.RefreshTableColumns(TheDataGrid);
      DataGridUtil.LoadColumns(TheColumnsCombo, TheDataGrid);
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;

      // workaround to avoid drag/drop failing when grid has no data
      TheDataGrid.ItemsSource = NoResultsList;
    }

    internal virtual bool IsPetsCombined() => false;
    internal virtual void ShowBreakdown(List<PlayerStats> selected) => new object(); // need to override this method
    internal virtual void ShowBreakdown2(List<PlayerStats> selected) => new object(); // need to override this method
    internal virtual void UpdateDataGridMenuItems() => new object(); // need to override this method
    internal virtual void FireSelectionChangedEvent(List<PlayerStats> stats) => new object(); // need to override this method
    internal string GetTargetTitle() => CurrentStats?.TargetTitle ?? GetTitle();
    internal string GetTitle() => TheTitle.Content as string ?? "";
    internal void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(TheDataGrid, TheTitle.Content.ToString());
    internal void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImageAsync(TheDataGrid, TheTitle);
    internal void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImageAsync(TheDataGrid, TheTitle, true);
    internal void DataGridShowBreakdownClick(object sender, RoutedEventArgs e) => ShowBreakdown(GetSelectedStats());
    internal void DataGridShowBreakdown2Click(object sender, RoutedEventArgs e) => ShowBreakdown2(GetSelectedStats());
    internal void DataGridShowBreakdownByClassClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Header: string className })
      {
        ShowBreakdown(GetStatsByClass(className));
      }
    }
    internal void DataGridShowBreakdown2ByClassClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Header: string className2 })
      {
        ShowBreakdown2(GetStatsByClass(className2));
      }
    }
    internal void DataGridShowSpellCountsClick(object sender, RoutedEventArgs e) => ShowSpellCounts(GetSelectedStats());
    internal void DataGridSpellCountsByClassClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Header: string className3 })
      {
        ShowSpellCounts(GetStatsByClass(className3));
      }
    }
    internal async void DataGridShowSpellCastsClick(object sender, RoutedEventArgs e) => await ShowSpellCasts(GetSelectedStats());
    internal async void DataGridSpellCastsByClassClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Header: string className4 })
      {
        await ShowSpellCasts(GetStatsByClass(className4));
      }
    }
    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(TheColumnsCombo, TheDataGrid);
    internal void ClassPreviewMouseDown(object sender, EventArgs e) => SharedControls.ClassPreviewMouseDown(TheClassesCombo, sender);

    internal static void CreateClassMenuItems(MenuItem parent, Action<object, RoutedEventArgs> classHandler, bool enabled, Action<object, RoutedEventArgs> selectedHandler = null)
    {
      if (selectedHandler != null)
      {
        var selected = new MenuItem { IsEnabled = enabled, Header = "Selected" };
        selected.Click += new RoutedEventHandler(selectedHandler);
        parent.Items.Add(selected);
      }

      EQDataStore.Instance.GetClassList().ForEach(name =>
      {
        var item = new MenuItem { IsEnabled = enabled, Header = name };
        item.Click += new RoutedEventHandler(classHandler);
        parent.Items.Add(item);
      });
    }

    internal static void CreateSpellCountMenuItems(MenuItem parent, Action<object, RoutedEventArgs> classHandler, Action<object, RoutedEventArgs> selectedHandler)
    {
      CreateClassMenuItems(parent, classHandler, false, selectedHandler);
      parent.Items.Add(new Separator());
      var item = new MenuItem { IsEnabled = true, Header = "All Players" };
      item.Click += new RoutedEventHandler(classHandler);
      parent.Items.Add(item);
    }

    /*
     * "No rows to show" is THIS empty collection, never null (2026-11, returning to the pattern master ships). SfDataGrid/SfTreeGrid have
     * two words for an empty table and only one of them is quiet: null means "the data has not arrived" and the control answers it with a
     * spinner in the middle of the grid — an animation claiming work is under way, whether or not anything is coming — while an empty list
     * draws an empty table. Every pane therefore hands back NoResultsList from ClearData, from STARTED (its title already says what it is
     * waiting for; the grid does not need to invent a second indicator nobody controls), and from a build that found no NPCs / no data.
     *
     * The one null that stays is DamageSummary's group re-slice, where assigning null BEFORE the grouped list exists to make the tree grid
     * see a change; that is a control workaround, not a state. And no pane asks its grid whether an answer exists — those guards read
     * CurrentStats, which is what makes an empty placeholder safe to display (docs/DesignNotes.md → "A busy state may not compete with its own answer").
     */

    internal void ClassSelectionChanged(object sender, EventArgs e)
    {
      if (SharedControls.ClassesListSelectedChanged(TheClassesCombo, SelectedClasses))
      {
        TheDataGrid.SelectedItems.Clear();
        DataGridUtil.RefreshTableView(TheDataGrid);
      }
    }

    internal void DataGridSetPlayerClassClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Header: string className })
      {
        var selected = GetSelectedStats().FirstOrDefault();
        if (selected is { } s && !string.IsNullOrEmpty(s.OrigName))
        {
          PlayerRegistry.Instance.SetDefaultPlayerClass(s.OrigName, className);
          s.ClassName = className;
          DataGridUtil.RefreshTable(TheDataGrid);
        }
      }
    }

    internal static void EnableClassMenuItems(MenuItem menu, SfGridBase gridBase, List<string> uniqueClasses)
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
            menuItem.IsEnabled = uniqueClasses != null && uniqueClasses.Contains(headerValue);
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

    internal virtual List<PlayerStats> GetSelectedStats()
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
          else if (stats is GroupEntry groupEntry)
          {
            results.AddRange(groupEntry.Children);
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

    internal async Task ShowSpellCasts(List<PlayerStats> selected)
    {
      if (SyncFusionUtil.OpenWindow(out var spellTable, typeof(SpellCastTable), "spellCastsWindow", "Spell Cast Order")
        && spellTable.Content is SpellCastTable table)
      {
        await table?.InitAsync(selected, CurrentStats);
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

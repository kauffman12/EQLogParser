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
using System.Windows.Input;

namespace EQLogParser
{
  public class SummaryTable : UserControl
  {
    internal const string DEFAULT_TABLE_LABEL = "No NPCs Selected";
    internal const string NODATA_TABLE_LABEL = Labels.NODATA;

    internal event EventHandler<PlayerStatsSelectionChangedEventArgs> EventsSelectionChange;

    internal SfTreeGrid TheDataGrid;
    internal ComboBox TheColumnsCombo;
    internal Label TheTitle;
    internal CombinedStats CurrentStats;
    internal List<List<ActionBlock>> CurrentGroups;

    internal void InitSummaryTable(Label title, SfTreeGrid dataGrid, ComboBox columnsCombo)
    {
      TheDataGrid = dataGrid;
      TheColumnsCombo = columnsCombo;
      TheDataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
      TheTitle = title;
      TheTitle.Content = DEFAULT_TABLE_LABEL;

      // default these columns to descending
      string[] desc = new string[] { "PercentOfRaid", "Total", "Extra", "DPS", "SDPS", "TotalSeconds", "Hits", "Max", "Avg", "AvgCrit", "AvgLucky",
      "ExtraRate", "CritRate", "LuckRate", "MeleeHitRate", "MeleeAccRate", "RampageRate"};
      TheDataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      TheDataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.LoadColumns(TheColumnsCombo, TheDataGrid);

      // workaround to avoid drag/drop failing when grid has no data
      TheDataGrid.ItemsSource = new List<PlayerStats>();
    }

    internal virtual bool IsPetsCombined() => false;
    internal virtual void ShowBreakdown(List<PlayerStats> selected) => new object(); // need to override this method
    internal virtual void ShowBreakdown2(List<PlayerStats> selected) => new object(); // need to override this method
    internal virtual void UpdateDataGridMenuItems() => new object(); // need to override this method
    internal string GetTargetTitle() => CurrentStats?.TargetTitle ?? GetTitle();
    internal string GetTitle() => TheTitle.Content as string;
    internal List<PlayerStats> GetSelectedStats() => TheDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
    internal void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(TheDataGrid, TheTitle.Content.ToString());
    internal void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitle);
    internal void DataGridUnselectAllClick(object sender, RoutedEventArgs e) => DataGridUtil.UnselectAll(sender as FrameworkElement);
    internal void DataGridShowBreakdownClick(object sender, RoutedEventArgs e) => ShowBreakdown(GetSelectedStats());
    internal void DataGridShowBreakdown2Click(object sender, RoutedEventArgs e) => ShowBreakdown2(GetSelectedStats());
    internal void DataGridShowBreakdownByClassClick(object sender, RoutedEventArgs e) => ShowBreakdown(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void DataGridShowBreakdown2ByClassClick(object sender, RoutedEventArgs e) => ShowBreakdown2(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void DataGridShowSpellCountsClick(object sender, RoutedEventArgs e) => ShowSpellCounts(GetSelectedStats());
    internal void DataGridSpellCountsByClassClick(object sender, RoutedEventArgs e) => ShowSpellCounts(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal void DataGridShowSpellCastsClick(object sender, RoutedEventArgs e) => ShowSpellCasts(GetSelectedStats());
    internal void DataGridSpellCastsByClassClick(object sender, RoutedEventArgs e) => ShowSpellCasts(GetStatsByClass((sender as MenuItem)?.Header as string));
    internal Predicate<object> GetFilter() => (TheDataGrid.ItemsSource as ICollectionView)?.Filter;
    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(TheColumnsCombo, TheDataGrid);
    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);

    internal static void CreateClassMenuItems(MenuItem parent, Action<object, RoutedEventArgs> selectedHandler, Action<object, RoutedEventArgs> classHandler)
    {
      MenuItem selected = new MenuItem { IsEnabled = false, Header = "Selected" };
      selected.Click += new RoutedEventHandler(selectedHandler);
      parent.Items.Add(selected);

      PlayerManager.Instance.GetClassList().ForEach(name =>
      {
        MenuItem item = new MenuItem { IsEnabled = false, Header = name };
        item.Click += new RoutedEventHandler(classHandler);
        parent.Items.Add(item);
      });
    }

    internal void Clear()
    {
      TheTitle.Content = DEFAULT_TABLE_LABEL;
      TheDataGrid.ItemsSource = null;
    }

    internal static void EnableClassMenuItems(MenuItem menu, SfGridBase gridBase, Dictionary<string, byte> uniqueClasses)
    {
      foreach (var item in menu.Items)
      {
        MenuItem menuItem = item as MenuItem;
        menuItem.IsEnabled = menuItem.Header as string == "Selected" ? gridBase.SelectedItems.Count > 0 : uniqueClasses != null &&
          uniqueClasses.ContainsKey(menuItem.Header as string);
      }
    }

    internal List<string[]> GetHeaders()
    {
      return TheDataGrid.Columns.Select(column =>
      {
        string binding = (column.ValueBinding as Binding).Path.Path;
        string title = column.HeaderText;
        return new string[] { binding, title };
      }).ToList();
    }

    internal List<PlayerStats> GetPlayerStats()
    {
      var results = new List<PlayerStats>();
      if (TheDataGrid.ItemsSource != null)
      {
        foreach (var item in TheDataGrid.ItemsSource as ICollectionView)
        {
          if (item is PlayerStats stats)
          {
            results.Add(stats);
            if (CurrentStats.Children.ContainsKey(stats.Name))
            {
              results.AddRange(CurrentStats.Children[stats.Name]);
            }
          }
        }
      }

      return results;
    }

    internal List<PlayerStats> GetStatsByClass(string className)
    {
      List<PlayerStats> selectedStats = new List<PlayerStats>();
      foreach (var record in TheDataGrid.View.Nodes)
      {
        PlayerStats stats = record.Item as PlayerStats;
        if (stats.ClassName == className)
        {
          selectedStats.Add(stats);
        }
      }

      return selectedStats;
    }

    internal void DataGridSelectAllClick(object sender, RoutedEventArgs e)
    {
      DataGridUtil.SelectAll(sender as FrameworkElement);
      Dispatcher.InvokeAsync(() => DataGridSelectionChanged());
    }

    internal void DataGridSelectionChanged()
    {
      FireSelectionChangedEvent(GetSelectedStats());
      UpdateDataGridMenuItems();
    }

    internal void SetPetClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement)?.Parent as ContextMenu;
      SfDataGrid callingDataGrid = menu?.PlacementTarget as SfDataGrid;
      if (callingDataGrid.SelectedItem is PlayerStats stats)
      {
        Task.Delay(100).ContinueWith(_ =>
        {
          PlayerManager.Instance.AddVerifiedPet(stats.OrigName);
          PlayerManager.Instance.AddPetToPlayer(stats.OrigName, Labels.UNASSIGNED);
        }, TaskScheduler.Default);
      }
    }

    internal void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;
        EventsSelectionChange(this, selectionChanged);
      });
    }

    internal void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl spellTable, typeof(SpellCastTable), "spellCastsWindow", "Spell Cast Timeline"))
        {
          (spellTable.Content as SpellCastTable).Init(selected, CurrentStats);
        }
      }
    }

    internal void ShowSpellCounts(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (Helpers.OpenWindow(main.dockSite, null, out ContentControl spellTable, typeof(SpellCountTable), "spellCountsWindow", "Spell Counts"))
        {
          (spellTable.Content as SpellCountTable).Init(selected, CurrentStats);
        }
      }
    }
  }
}

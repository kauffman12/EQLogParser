using ActiproSoftware.Windows.Themes;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  public abstract class BreakdownTable : UserControl
  {
    private protected string CurrentSortKey = "Total";
    private protected ListSortDirection CurrentSortDirection = ListSortDirection.Descending;
    private protected DataGridTextColumn CurrentColumn = null;
    private SfDataGrid TheDataGrid;
    private ComboBox TheSelectedColumns;

    internal void InitBreakdownTable(SfDataGrid dataGrid, ComboBox columns)
    {
      TheDataGrid = dataGrid;
      TheSelectedColumns = columns;

      // default these columns to descending
      string[] desc = new string[] { "Percent", "Total", "Extra", "DPS", "SDPS", "TotalSeconds", "Hits", "Max", "Avg", "AvgCrit", "AvgLucky",
      "ExtraRate", "CritRate", "LuckRate", "TwincastRate"};
      TheDataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      TheDataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.LoadColumns(TheSelectedColumns, TheDataGrid);
    }

    internal abstract void Display(List<PlayerStats> selectedStats = null);

    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.ShowColumns(TheSelectedColumns, TheDataGrid);

    internal object GetSortValue(PlayerSubStats sub) => sub?.GetType().GetProperty(CurrentSortKey).GetValue(sub, null);

    internal void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      if (e?.Row.DataContext is PlayerStats)
      {
        e.Row.Style = Application.Current.FindResource(DataGridResourceKeys.DataGridRowStyleKey) as Style;
      }
      else
      {
        e.Row.Style = Application.Current.Resources["DetailsDataGridRowSyle"] as Style;
      }
    }

    internal List<PlayerSubStats> SortSubStats(List<PlayerSubStats> subStats)
    {
      OrderedParallelQuery<PlayerSubStats> query;
      if (CurrentSortDirection == ListSortDirection.Ascending)
      {
        query = subStats.AsParallel().OrderBy(subStat => GetSortValue(subStat));
      }
      else
      {
        query = subStats.AsParallel().OrderByDescending(subStat => GetSortValue(subStat));
      }

      return query.ToList();
    }
  }
}

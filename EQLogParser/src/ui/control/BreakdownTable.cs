using ActiproSoftware.Windows.Themes;
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
    private DataGrid TheDataGrid;
    private ComboBox TheSelectedColumns;

    internal void InitBreakdownTable(DataGrid dataGrid, ComboBox columns)
    {
      TheDataGrid = dataGrid;
      TheSelectedColumns = columns;

      if (TheDataGrid != null)
      {
        TheDataGrid.Sorting += DataGrid_Sorting; // sort numbers descending

        PropertyDescriptor orderPd = DependencyPropertyDescriptor.FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn));
        foreach (var column in dataGrid.Columns)
        {
          orderPd.AddValueChanged(column, new EventHandler(ColumnDisplayIndexPropertyChanged));
        }

        if (TheSelectedColumns != null)
        {
          DataGridUtil.LoadColumns(TheSelectedColumns, TheDataGrid);
        }
      }
    }

    internal abstract void Display(List<PlayerStats> selectedStats = null);

    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.ShowColumns(TheSelectedColumns, TheDataGrid);

    internal void ColumnDisplayIndexPropertyChanged(object sender, EventArgs e) => DataGridUtil.SaveColumnIndexes(TheSelectedColumns, TheDataGrid);

    internal void CustomSorting(object sender, DataGridSortingEventArgs e)
    {
      if (e?.Column is DataGridTextColumn column)
      {
        // prevent the built-in sort from sorting
        e.Handled = true;

        if (column.Binding is Binding binding && binding.Path != null) // dont sort on percent total, its not useful
        {
          CurrentSortKey = binding.Path.Path;
          CurrentColumn = column;

          if (column.Header.ToString() != "Name" && column.SortDirection == null)
          {
            CurrentSortDirection = ListSortDirection.Descending;
          }
          else
          {
            CurrentSortDirection = (column.SortDirection != ListSortDirection.Ascending) ? ListSortDirection.Ascending : ListSortDirection.Descending;
          }

          Display();
        }
      }
    }

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

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
      if (e.Column.Header != null && e.Column.Header.ToString() != "Name")
      {
        e.Column.SortDirection = e.Column.SortDirection ?? ListSortDirection.Ascending;
      }
    }
  }
}

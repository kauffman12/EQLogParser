using ActiproSoftware.Windows.Themes;
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
    protected string CurrentSortKey = "Total";
    protected ListSortDirection CurrentSortDirection = ListSortDirection.Descending;
    protected DataGridTextColumn CurrentColumn = null;

    protected void CustomSorting(object sender, DataGridSortingEventArgs e)
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

    protected virtual void Display(List<PlayerStats> selectedStats = null)
    {
      // need to override this method
    }

    protected object GetSortValue(PlayerSubStats sub)
    {
      return sub?.GetType().GetProperty(CurrentSortKey).GetValue(sub, null);
    }

    protected void LoadingRow(object sender, DataGridRowEventArgs e)
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

    protected List<PlayerSubStats> SortSubStats(List<PlayerSubStats> subStats)
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

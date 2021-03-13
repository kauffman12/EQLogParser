
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  class DataGridUtils
  {
    internal static Tuple<List<string>, List<List<object>>> BuildExportData(DataGrid dataGrid)
    {
      var headers = new List<string>();
      var headerKeys = new List<string>();
      var data = new List<List<object>>();

      for (int i = 0; i < dataGrid.Columns.Count; i++)
      {
        var bound = dataGrid.Columns[i] as DataGridBoundColumn;
        if (bound != null)
        {
          headers.Add(bound.Header as string);
          headerKeys.Add(((System.Windows.Data.Binding)bound.Binding).Path.Path);
        }
      }

      foreach (var item in dataGrid.Items)
      {
        if (item is IDictionary<string, object> dict)
        {
          var row = new List<object>();
          foreach (var key in headerKeys)
          {
            if (dict.ContainsKey(key))
            {
              row.Add(dict[key]);
            }
          }

          data.Add(row);
        }
      }

      return new Tuple<List<string>, List<List<object>>>(headers, data);
    }

    internal static void SelectAll(FrameworkElement sender)
    {
      if (sender?.Parent is ContextMenu menu)
      {
        (menu.PlacementTarget as DataGrid)?.SelectAll();
      }
    }

    internal static void UnselectAll(FrameworkElement sender)
    {
      if (sender?.Parent is ContextMenu menu)
      {
        (menu.PlacementTarget as DataGrid)?.UnselectAll();
      }
    }

    internal static Tuple<double, int> GetRowDetails(DataGrid dataGrid)
    {
      double height = 0;
      int count = 0;
      for (int i = 0; i < dataGrid.Items.Count; i++)
      {
        var row = dataGrid.ItemContainerGenerator.ContainerFromIndex(i);
        if (row != null && row is DataGridRow)
        {
          height = (row as DataGridRow).ActualHeight;
          count++;
        }
        else if (count > 0)
        {
          break;
        }
      }

      return new Tuple<double, int>(height, count);
    }
  }
}

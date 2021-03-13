
using System;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  class DataGridUtils
  {
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


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
  }
}

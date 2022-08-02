using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using Syncfusion.UI.Xaml.Utility;
using System.Windows.Controls;

namespace EQLogParser
{
  public static class ContextMenuCommands
  {
    static BaseCommand copy;
    static BaseCommand selectAll;
    static BaseCommand unselectAll;

    public static BaseCommand Copy
    {
      get
      {
        if (copy == null)
        {
          copy = new BaseCommand(OnCopyClicked);
        }

        return copy;
      }
    }

    private static void OnCopyClicked(object obj)
    {
      if (obj is SfDataGrid dataGrid)
      {
        dataGrid.GridCopyPaste.Copy();
      }
      else if (obj is SfTreeGrid treeGrid)
      {
        treeGrid.TreeGridCopyPaste.Copy();
      }
    }

    public static BaseCommand SelectAll
    {
      get
      {
        if (selectAll == null)
        {
          selectAll = new BaseCommand(OnSelectAllClicked, CanSelectAll);
        }

        return selectAll;
      }
    }

    private static bool CanSelectAll(object obj)
    {
      if (obj is SfDataGrid dataGrid && dataGrid.View?.Records.Count > 0)
      {
        return dataGrid.SelectedItems.Count < dataGrid.View.Records.Count;
      }
      else if (obj is SfTreeGrid treeGrid && treeGrid.View?.Nodes.Count > 0)
      {
        return treeGrid.SelectedItems.Count < treeGrid.View.Nodes.Count;
      }

      return false;
    }

    private static void OnSelectAllClicked(object obj)
    {
      // SelectAll does not throw an event so need to call the predefined method in the window
      if (obj is SfDataGrid dataGrid)
      {
        dataGrid.SelectAll();
        dataGrid.Dispatcher.InvokeAsync(() => DataGridUtil.CallSelectionChanged(dataGrid.Parent));
      }
      else if (obj is SfTreeGrid treeGrid)
      {
        treeGrid.SelectAll();
        treeGrid.Dispatcher.InvokeAsync(() => DataGridUtil.CallSelectionChanged(treeGrid.Parent));
      }
    }

    public static BaseCommand UnselectAll
    {
      get
      {
        if (unselectAll == null)
        {
          unselectAll = new BaseCommand(OnUnselectAllClicked, CanUnselectAll);
        }

        return unselectAll;
      }
    }

    private static void OnUnselectAllClicked(object obj)
    {
      if (obj is SfDataGrid dataGrid)
      {
        dataGrid.SelectedItems.Clear();
      }
      else if (obj is SfTreeGrid treeGrid)
      {
        treeGrid.SelectedItems.Clear();
      }
    }

    private static bool CanUnselectAll(object obj)
    {
      if (obj is SfDataGrid dataGrid)
      {
        return dataGrid.SelectedItems.Count > 0;
      }
      else if (obj is SfTreeGrid treeGrid)
      {
        return treeGrid.SelectedItems.Count > 0;
      }

      return false;
    }
  }
}

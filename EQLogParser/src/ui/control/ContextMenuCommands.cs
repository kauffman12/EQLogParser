using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using Syncfusion.UI.Xaml.Utility;

namespace EQLogParser
{
  public static class ContextMenuCommands
  {
    private static BaseCommand _theCopyCommand;
    private static BaseCommand _theSelectAllCommand;
    private static BaseCommand _theUnselectAllCommand;

    public static BaseCommand Copy
    {
      get
      {
        _theCopyCommand ??= new BaseCommand(OnCopyClicked);
        return _theCopyCommand;
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
        _theSelectAllCommand ??= new BaseCommand(OnSelectAllClicked, CanSelectAll);
        return _theSelectAllCommand;
      }
    }

    private static bool CanSelectAll(object obj)
    {
      if (obj is SfDataGrid dataGrid && dataGrid.View?.Records.Count > 0)
      {
        return dataGrid.SelectedItems.Count < dataGrid.View.Records.Count;
      }

      if (obj is SfTreeGrid treeGrid && treeGrid.View?.Nodes.Count > 0)
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
        _theUnselectAllCommand ??= new BaseCommand(OnUnselectAllClicked, CanUnselectAll);
        return _theUnselectAllCommand;
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

      if (obj is SfTreeGrid treeGrid)
      {
        return treeGrid.SelectedItems.Count > 0;
      }

      return false;
    }
  }
}

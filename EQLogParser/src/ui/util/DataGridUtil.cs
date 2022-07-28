using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  class DataGridUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static int StartRow = 0;

    internal static Style CreateHighlightForegroundStyle(string name, IValueConverter converter = null)
    {
      var style = new Style(typeof(GridCell));
      style.Setters.Add(new Setter(GridCell.ForegroundProperty, new Binding(name) { Converter = converter }));
      style.BasedOn = Application.Current.Resources["SyncfusionGridCellStyle"] as Style;
      return style;
    }

    internal static void SortColumnsChanged(object sender, GridSortColumnsChangedEventArgs e, IReadOnlyCollection<string> descending)
    {
      // Here, we have updated the column's items in view based on SortDescriptions. 
      if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
      {
        if (sender is SfDataGrid)
        {
          var sortcolumn = ((SfDataGrid)sender).View.SortDescriptions.FirstOrDefault(x => x.PropertyName == e.AddedItems[0].ColumnName);
          ((SfDataGrid)sender).View.SortDescriptions.Remove(sortcolumn);

          SortDescription sortDescription;
          if (descending != null && descending.Contains(e.AddedItems[0].ColumnName))
          {
            sortDescription = new SortDescription(sortcolumn.PropertyName, ListSortDirection.Descending);
          }
          else
          {
            sortDescription = new SortDescription(sortcolumn.PropertyName, ListSortDirection.Ascending);
          }

          ((SfDataGrid)sender).View.SortDescriptions.Add(sortDescription);
        }
        else if (sender is SfTreeGrid)
        {
          var sortcolumn = ((SfTreeGrid)sender).View.SortDescriptions.FirstOrDefault(x => x.ColumnName == e.AddedItems[0].ColumnName);
          ((SfTreeGrid)sender).View.SortDescriptions.Remove(sortcolumn);

          SortColumnDescription sortDescription;
          if (descending != null && descending.Contains(e.AddedItems[0].ColumnName))
          {
            sortDescription = new SortColumnDescription { ColumnName = sortcolumn.ColumnName, SortDirection = ListSortDirection.Descending };
          }
          else
          {
            sortDescription = new SortColumnDescription { ColumnName = sortcolumn.ColumnName, SortDirection = ListSortDirection.Ascending };
          }

          ((SfTreeGrid)sender).View.SortDescriptions.Add(sortDescription);
        }
      }
    }

    internal static void SortColumnsChanging(object sender, GridSortColumnsChangingEventArgs e, IReadOnlyCollection<string> descending)
    {
      // Initially, we can change the SortDirection of particular column based on columnchanged action. 
      if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
      {
        if (descending != null && descending.Contains(e.AddedItems[0].ColumnName))
        {
          e.AddedItems[0].SortDirection = ListSortDirection.Descending;
        }
        else
        {
          e.AddedItems[0].SortDirection = ListSortDirection.Ascending;
        }
      }
    }

    internal static void CopyCsvFromTable(SfGridBase gridBase, string title)
    {
      try
      {
        var export = BuildExportData(gridBase);
        string result = TextFormatUtils.BuildCsv(export.Item1, export.Item2, title);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create CSV\r\n");
        LOG.Error(ane);
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    internal static Tuple<List<string>, List<List<object>>> BuildExportData(SfGridBase gridBase)
    {
      var headers = new List<string>();
      var headerKeys = new List<string>();
      var data = new List<List<object>>();
      IPropertyAccessProvider props = null;
      List<object> records = null;

      if (gridBase is SfDataGrid)
      {
        var dataGrid = gridBase as SfDataGrid;
        props = dataGrid.View.GetPropertyAccessProvider();
        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
          if (!dataGrid.Columns[i].IsHidden && dataGrid.Columns[i].ValueBinding is Binding binding)
          {
            headers.Add(dataGrid.Columns[i].HeaderText);
            headerKeys.Add(binding.Path.Path);
          }
        }

        records = dataGrid.View.Records.Select(record => record.Data).ToList();
      }
      else if (gridBase is SfTreeGrid)
      {
        var treeGrid = gridBase as SfTreeGrid;
        props = treeGrid.View.GetPropertyAccessProvider();
        for (int i = 0; i < treeGrid.Columns.Count; i++)
        {
          if (!treeGrid.Columns[i].IsHidden && treeGrid.Columns[i].ValueBinding is Binding binding)
          {
            headers.Add(treeGrid.Columns[i].HeaderText);
            headerKeys.Add(binding.Path.Path);
          }
        }

        records = treeGrid.View.Nodes.Select(node => node.Item).ToList();
      }

      // Rank data is in the row header column not a regular column
      if (records.Count > 0 && records[0] is PlayerStats)
      {
        headers.Insert(0, "Rank");
        headerKeys.Insert(0, "Rank");
      }

      foreach (ref var record in records.ToArray().AsSpan())
      {
        var row = new List<object>();
        foreach (var key in headerKeys)
        {
          // regular object with properties
          row.Add(props.GetFormattedValue(record, key) ?? "");
        }

        data.Add(row);
      }

      return new Tuple<List<string>, List<List<object>>>(headers, data);
    }

    internal static void CreateImage(SfGridBase gridBase, Label titleLabel)
    {
      gridBase.SelectedItems.Clear();
      gridBase.IsHitTestVisible = false;
      Task.Delay(100).ContinueWith((t) => gridBase.Dispatcher.InvokeAsync(() =>
      {
        try
        {
          var realTableHeight = gridBase.ActualHeight + gridBase.HeaderRowHeight + 1;
          var realColumnWidth = gridBase.ActualWidth;
          var titlePadding = titleLabel.Padding.Top + titleLabel.Padding.Bottom;
          var titleHeight = titleLabel.ActualHeight - titlePadding - 4;
          var titleWidth = titleLabel.DesiredSize.Width;

          var dpiScale = VisualTreeHelper.GetDpi(gridBase);
          RenderTargetBitmap rtb = new RenderTargetBitmap((int)realColumnWidth, (int)(realTableHeight + titleHeight),
            dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

          DrawingVisual dv = new DrawingVisual();
          using (DrawingContext ctx = dv.RenderOpen())
          {
            var background = Application.Current.Resources["ContentBackground"] as SolidColorBrush;
            ctx.DrawRectangle(background, null, new Rect(new Point(0, 0), new Size(realColumnWidth, titleHeight + titlePadding)));

            var brush = new VisualBrush(titleLabel);
            ctx.DrawRectangle(brush, null, new Rect(new Point(4, titlePadding / 2), new Size(titleWidth, titleHeight)));

            brush = new VisualBrush(gridBase);
            ctx.DrawRectangle(brush, null, new Rect(new Point(0, titleHeight + titlePadding), new Size(realColumnWidth, gridBase.ActualHeight +
              SystemParameters.HorizontalScrollBarHeight)));
          }

          rtb.Render(dv);
          Clipboard.SetImage(rtb);
        }
        catch (Exception ex)
        {
          LOG.Error("Could not Copy Image", ex);
        }
        finally
        {
          gridBase.IsHitTestVisible = true;
        }
      }));
    }

    internal static void SelectAll(FrameworkElement sender)
    {
      if (sender?.Parent is ContextMenu menu)
      {
        (menu.PlacementTarget as SfDataGrid)?.SelectAll();
      }
    }

    internal static void UnselectAll(FrameworkElement sender)
    {
      if (sender?.Parent is ContextMenu menu)
      {
        (menu.PlacementTarget as SfDataGrid)?.SelectedItems.Clear();
      }
    }

    internal static void EnableMouseSelection(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      dynamic elem = e.OriginalSource;
      if (sender is SfTreeGrid treeGrid && elem?.DataContext is PlayerSubStats stats)
      {
        StartRow = treeGrid.ResolveToRowIndex(stats);
        // Left click happened, current item is selected, now listen for mouse movement and release of left button
        treeGrid.PreviewMouseLeftButtonUp += PreviewMouseLeftButtonUp;
        treeGrid.PreviewMouseMove += MouseMove;
      }
    }

    private static void PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (sender is SfTreeGrid treeGrid)
      {
        // remove listeners if left button released
        treeGrid.PreviewMouseLeftButtonUp -= PreviewMouseLeftButtonUp;
        treeGrid.PreviewMouseMove -= MouseMove;
      }
    }

    private static void MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
      dynamic elem = e.OriginalSource;
      if (sender is SfTreeGrid treeGrid)
      {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Released)
        {
          // remove listeners if left button released
          treeGrid.PreviewMouseLeftButtonUp -= PreviewMouseLeftButtonUp;
          treeGrid.PreviewMouseMove -= MouseMove;
        }
        else if (elem?.DataContext is PlayerSubStats stats)
        {
          int row = treeGrid.ResolveToRowIndex(stats);
          if (treeGrid.CurrentItem != stats)
          {
            if (!treeGrid.SelectionController.SelectedRows.Contains(row))
            {
              treeGrid.SelectRows(StartRow, row);
            }
            else
            {
              treeGrid.SelectionController.ClearSelections(false);
              int direction = 0;
              if (StartRow < row)
              {
                direction = -1;
              }
              else if (StartRow > row)
              {
                direction = 1;
              }

              treeGrid.SelectRows(StartRow, row + direction);
            }

            treeGrid.CurrentItem = stats;
          }
        }
      }
    }

    internal static void RestoreAllTableColumns()
    {
      ConfigUtil.RemoveSetting("DamageSummaryColumns");
      ConfigUtil.RemoveSetting("HealingSummaryColumns");
      ConfigUtil.RemoveSetting("TankingSummaryColumns");
      ConfigUtil.RemoveSetting("DamageSummaryColumnsDisplayIndex");
      ConfigUtil.RemoveSetting("HealingSummaryColumnsDisplayIndex");
      ConfigUtil.RemoveSetting("TankingSummaryColumnsDisplayIndex");
      ConfigUtil.RemoveSetting("DamageBreakdownColumns");
      ConfigUtil.RemoveSetting("HealingBreakdownColumns");
      ConfigUtil.RemoveSetting("ReceivedHealingBreakdownColumns");
      ConfigUtil.RemoveSetting("TankingBreakdownColumns");
      ConfigUtil.RemoveSetting("DamageBreakdownColumnsDisplayIndex");
      ConfigUtil.RemoveSetting("HealingBreakdownColumnsDisplayIndex");
      ConfigUtil.RemoveSetting("ReceivedHealingBreakdownColumnsDisplayIndex");
      ConfigUtil.RemoveSetting("TankingBreakdownColumnsDisplayIndex");
      ConfigUtil.Save();
      _ = MessageBox.Show("Column Settings Restored. Close and Re-Open any Summary or Breakdown table to see the change take effect.",
        EQLogParser.Resource.RESTORE_TABLE_COLUMNS, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal static void LoadColumns(ComboBox columnCombo, dynamic gridBase)
    {
      HashSet<string> visible = null;
      string visibleSetting = ConfigUtil.GetSetting(columnCombo.Tag.ToString());

      if (!string.IsNullOrEmpty(visibleSetting))
      {
        visible = new HashSet<string>(visibleSetting.Split(','));
      }

      dynamic columns = null;
      dynamic updated = null;
      if (gridBase is SfDataGrid)
      {
        columns = ((SfDataGrid)gridBase).Columns;
        updated = new Columns();
      }
      else if (gridBase is SfTreeGrid)
      {
        columns = ((SfTreeGrid)gridBase).Columns;
        updated = new TreeGridColumns();
      }

      var oldFormat = false;
      var found = new Dictionary<string, bool>();
      var displayOrder = ConfigUtil.GetSetting((columnCombo.Tag as string) + "DisplayIndex");

      if (displayOrder != null)
      {
        foreach (var item in displayOrder.Split(',').ToList())
        {
          var name = item;

          // Eventually (remove this)
          oldFormat = oldFormat || name.Contains(" ");

          // changed column names
          if (name == "% Luck")
          {
            name = "% Lucky";
          }

          for (int i = 0; i < columns.Count; i++)
          {
            // handle old version that saved column display names
            // Eventually (remove the HeaderText check)
            if (columns[i].MappingName == name || columns[i].HeaderText == name)
            {
              found[columns[i].MappingName] = true;
              updated.Add(columns[i]);
              columns[i].IsHidden = !IsColumnVisible(visible, columns, i);
              break;
            }
          }
        }
      }

      // check for new columns that didn't exist when preferences were saved
      for (int i = 0; i < columns.Count; i++)
      {
        if (!found.ContainsKey(columns[i].MappingName))
        {
          updated.Add(columns[i]);
          columns[i].IsHidden = !IsColumnVisible(visible, columns, i);
        }

        // if old format make sure Name is visible
        // Eventually (remove this)
        if (oldFormat && columns[i].MappingName == "Name")
        {
          columns[i].IsHidden = false;
        }
      }

      columns = SetColumns(columnCombo, gridBase, updated);

      int selectedCount = 0;
      var list = new List<ComboBoxItemDetails>();
      for (int i = 0; i < columns.Count; i++)
      {
        list.Add(new ComboBoxItemDetails
        {
          Text = columns[i].HeaderText,
          IsChecked = !columns[i].IsHidden,
          Value = columns[i].MappingName
        });
        selectedCount += columns[i].IsHidden ? 0 : 1;
      }

      columnCombo.ItemsSource = list;
      UIElementUtil.SetComboBoxTitle(columnCombo, selectedCount, EQLogParser.Resource.COLUMNS_SELECTED);
    }

    private static dynamic SetColumns(ComboBox columnCombo, SfDataGrid dataGrid, dynamic updated)
    {
      dataGrid.Columns = updated;

      // save column order if it changes
      dataGrid.QueryColumnDragging += (object sender, QueryColumnDraggingEventArgs e) =>
      {
        if (e.Reason == QueryColumnDraggingReason.Dropped && sender is SfDataGrid dataGrid)
        {
          var columns = dataGrid.Columns.ToList().Select(column => column.MappingName).ToList();
          ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
        }
      };

      return dataGrid.Columns;
    }

    private static dynamic SetColumns(ComboBox columnCombo, SfTreeGrid treeGrid, dynamic updated)
    {
      treeGrid.Columns = updated;

      // save column order if it changes
      treeGrid.ColumnDragging += (object sender, TreeGridColumnDraggingEventArgs e) =>
      {
        if (e.Reason == QueryColumnDraggingReason.Dropped && sender is SfTreeGrid treeGrid)
        {
          var columns = treeGrid.Columns.ToList().Select(column => column.MappingName).ToList();
          ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
        }
      };

      return treeGrid.Columns;
    }

    private static bool IsColumnVisible(HashSet<string> visible, dynamic columns, int i)
    {
      var show = true;
      if (visible != null)
      {
        show = visible.Contains(columns[i].MappingName) || visible.Contains(columns[i].HeaderText);
      }
      return show;
    }

    internal static void SetHiddenColumns(ComboBox columnCombo, dynamic gridBase)
    {
      var visible = new HashSet<string>();

      if (columnCombo.Items.Count > 0)
      {
        for (int i = 0; i < columnCombo.Items.Count; i++)
        {
          var checkedItem = columnCombo.Items[i] as ComboBoxItemDetails;
          if (checkedItem.IsChecked)
          {
            visible.Add(checkedItem.Value);
          }
        }

        UIElementUtil.SetComboBoxTitle(columnCombo, visible.Count, EQLogParser.Resource.COLUMNS_SELECTED);

        dynamic columns = null;
        if (gridBase is SfDataGrid)
        {
          columns = ((SfDataGrid)gridBase).Columns;
        }
        else if (gridBase is SfTreeGrid)
        {
          columns = ((SfTreeGrid)gridBase).Columns;
        }

        for (int i = 0; i < columns.Count; i++)
        {
          columns[i].IsHidden = !IsColumnVisible(visible, columns, i);
        }

        if (!string.IsNullOrEmpty(columnCombo.Tag.ToString()))
        {
          ConfigUtil.SetSetting(columnCombo.Tag.ToString(), string.Join(",", visible));
        }
      }
    }
  }
}

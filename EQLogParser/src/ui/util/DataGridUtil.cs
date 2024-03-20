using log4net;
using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  internal static class DataGridUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static int _startRow;

    internal static Style CreateHighlightForegroundStyle(string name, IValueConverter converter = null)
    {
      var style = new Style(typeof(GridCell));
      style.Setters.Add(new Setter(Control.ForegroundProperty, new Binding(name) { Converter = converter }));
      style.BasedOn = Application.Current.Resources["SyncfusionGridCellStyle"] as Style;
      return style;
    }

    internal static void SortColumnsChanged(object sender, GridSortColumnsChangedEventArgs e, IReadOnlyCollection<string> descending)
    {
      // Here, we have updated the column's items in view based on SortDescriptions. 
      if (e.Action == NotifyCollectionChangedAction.Add)
      {
        if (sender is SfDataGrid grid)
        {
          var sortColumn = grid.View.SortDescriptions.FirstOrDefault(x => x.PropertyName == e.AddedItems[0].ColumnName);
          grid.View.SortDescriptions.Remove(sortColumn);

          SortDescription sortDescription;
          if (descending != null && descending.Contains(e.AddedItems[0].ColumnName))
          {
            sortDescription = new SortDescription(sortColumn.PropertyName, ListSortDirection.Descending);
          }
          else
          {
            sortDescription = new SortDescription(sortColumn.PropertyName, ListSortDirection.Ascending);
          }

          grid.View.SortDescriptions.Add(sortDescription);
        }
        else if (sender is SfTreeGrid treeGrid)
        {
          var sortColumns = treeGrid.View.SortDescriptions.FirstOrDefault(x => x.ColumnName == e.AddedItems[0].ColumnName);

          if (sortColumns != null)
          {
            treeGrid.View.SortDescriptions.Remove(sortColumns);

            SortColumnDescription sortDescription;
            if (descending != null && descending.Contains(e.AddedItems[0].ColumnName))
            {
              sortDescription = new SortColumnDescription { ColumnName = sortColumns.ColumnName, SortDirection = ListSortDirection.Descending };
            }
            else
            {
              sortDescription = new SortColumnDescription { ColumnName = sortColumns.ColumnName, SortDirection = ListSortDirection.Ascending };
            }

            treeGrid.View.SortDescriptions.Add(sortDescription);
          }
        }
      }
    }

    internal static void SortColumnsChanging(object sender, GridSortColumnsChangingEventArgs e, IReadOnlyCollection<string> descending)
    {
      // Initially, we can change the SortDirection of particular column based on column changed action. 
      if (e.Action == NotifyCollectionChangedAction.Add)
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
        var result = TextUtils.BuildCsv(export.Item1, export.Item2, title);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create CSV\r\n");
        Log.Error(ane);
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
      }
    }

    internal static (List<string>, List<List<object>>) BuildExportData(SfGridBase gridBase)
    {
      var headers = new List<string>();
      var headerKeys = new List<string>();
      var data = new List<List<object>>();
      IPropertyAccessProvider props = null;
      List<object> records = null;

      if (gridBase is SfDataGrid dataGrid)
      {
        props = dataGrid.View.GetPropertyAccessProvider();
        foreach (var col in dataGrid.Columns)
        {
          if (!col.IsHidden && col.ValueBinding is Binding binding)
          {
            headers.Add(col.HeaderText);
            headerKeys.Add(binding.Path.Path);
          }
        }

        records = dataGrid.View.Records.Select(record => record.Data).ToList();
      }
      else if (gridBase is SfTreeGrid treeGrid)
      {
        props = treeGrid.View.GetPropertyAccessProvider();
        foreach (var col in treeGrid.Columns)
        {
          if (!col.IsHidden && col.ValueBinding is Binding binding)
          {
            headers.Add(col.HeaderText);
            headerKeys.Add(binding.Path.Path);
          }
        }

        records = treeGrid.View.Nodes.Select(node => node.Item).ToList();
      }

      if (records != null)
      {
        // Rank data is in the row header column not a regular column
        if (records.Count > 0 && records[0] is PlayerStats)
        {
          headers.Insert(0, "Rank");
          headerKeys.Insert(0, "Rank");
        }

        foreach (var record in CollectionsMarshal.AsSpan(records))
        {
          var row = new List<object>();
          foreach (var key in CollectionsMarshal.AsSpan(headerKeys))
          {
            // regular object with properties
            row.Add(props.GetFormattedValue(record, key) ?? "");
          }

          data.Add(row);
        }
      }

      return (headers, data);
    }

    internal static void CreateImage(SfGridBase gridBase, Label titleLabel, bool allData = false)
    {
      Task.Delay(250).ContinueWith(_ =>
      {
        gridBase.Dispatcher.InvokeAsync(() =>
        {
          MessageWindow dialog = null;
          var tableHeight = GetTableHeight(gridBase, allData);
          var tableWidth = GetTableWidth(gridBase, allData);
          var needHeightChange = tableHeight > gridBase.ActualHeight;
          var needWidthChange = tableWidth > gridBase.ActualWidth;

          gridBase.SelectedItems.Clear();
          gridBase.IsHitTestVisible = false;

          var parent = gridBase.Parent as Panel;
          if (needHeightChange || needWidthChange)
          {
            dialog = new MessageWindow("Please Wait while Image is Processed.", Resource.COPY_LARGE_IMAGE);
            dialog.Show();

            if (parent != null)
            {
              gridBase.Dispatcher.InvokeAsync(() =>
              {
                if (needHeightChange)
                {
                  parent.Height = tableHeight + 200; // be safe and make sure it has extra room to work with
                }

                if (needWidthChange)
                {
                  parent.Width = tableWidth + 200;
                }
              }, DispatcherPriority.Background);
            }
          }

          gridBase.Dispatcher.InvokeAsync(() =>
          {
            try
            {
              titleLabel.Measure(titleLabel.RenderSize);
              gridBase.Measure(gridBase.RenderSize);

              // if table needed resize then recalculate values
              if (parent != null && (!double.IsNaN(parent.Height) || !double.IsNaN(parent.Width)))
              {
                tableHeight = GetTableHeight(gridBase, allData);
                tableWidth = GetTableWidth(gridBase, allData);
              }

              var titleHeight = titleLabel.ActualHeight;
              var dpiScale = UiElementUtil.GetDpi();

              // create title image
              var rtb = new RenderTargetBitmap((int)tableWidth, (int)titleHeight, dpiScale, dpiScale, PixelFormats.Default);
              rtb.Render(titleLabel);
              var titleImage = BitmapFrame.Create(rtb);

              // create table image
              rtb = new RenderTargetBitmap((int)tableWidth, (int)(tableHeight + titleHeight), dpiScale, dpiScale, PixelFormats.Default);
              rtb.Render(gridBase);
              var tableImage = BitmapFrame.Create(rtb);

              // add images together and fix missing background
              rtb = new RenderTargetBitmap((int)tableWidth, (int)(tableHeight + titleHeight),
                dpiScale, dpiScale, PixelFormats.Default);

              var dv = new DrawingVisual();
              using (var ctx = dv.RenderOpen())
              {
                var background = Application.Current.Resources["ContentBackground"] as SolidColorBrush;
                ctx.DrawRectangle(background, null, new Rect(new Point(0, 0), new Size(titleImage.Width, titleImage.Height)));
                ctx.DrawImage(titleImage, new Rect(new Point(0, 0), new Size(titleImage.Width, titleImage.Height)));
                ctx.DrawImage(tableImage, new Rect(new Point(0, 0), new Size(tableImage.Width, tableImage.Height)));
              }

              rtb.Render(dv);
              Clipboard.SetImage(BitmapFrame.Create(rtb));

              if (parent != null)
              {
                if (!double.IsNaN(parent.Height))
                {
                  parent.Height = double.NaN;
                }

                if (!double.IsNaN(parent.Width))
                {
                  parent.Width = double.NaN;
                }
              }
            }
            catch (Exception ex)
            {
              Log.Error("Could not Copy Image", ex);
            }
            finally
            {
              gridBase.IsHitTestVisible = true;
              dialog?.Close();
            }
          }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
      });
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

    internal static void CallSelectionChanged(dynamic obj)
    {
      while (obj != null)
      {
        var type = obj.GetType();

        if (type == typeof(ContentControl))
        {
          break;
        }

        if (type.GetDeclaredMethod("DataGridSelectionChanged") != null)
        {
          obj.DataGridSelectionChanged();
          break;
        }

        obj = obj.Parent;
      }
    }

    internal static void EnableMouseSelection(object sender, MouseButtonEventArgs e)
    {
      dynamic elem = e.OriginalSource;
      if (sender is SfTreeGrid treeGrid && elem?.DataContext is object stats && treeGrid.ResolveToRowIndex(stats) is var row and > -1)
      {
        _startRow = row;
        // Left click happened, current item is selected, now listen for mouse movement and release of left button
        treeGrid.PreviewMouseLeftButtonUp += PreviewMouseLeftButtonUp;
        treeGrid.PreviewMouseMove += MouseMove;
      }
    }

    internal static void UpdateTableMargin(SfGridBase gridBase)
    {
      var size = 2;
      switch (MainWindow.CurrentFontSize)
      {
        case 11:
          size = 4;
          break;
        case 12:
          size = 8;
          break;
        case 13:
          size = 12;
          break;
        case 14:
          size = 16;
          break;
        case 15:
          size = 20;
          break;
        case 16:
          size = 24;
          break;
      }

      if (gridBase is SfDataGrid dataGrid)
      {
        dataGrid.GridColumnSizer.Margin = new Thickness(size, 0, size, 0);
      }
      else if (gridBase is SfTreeGrid treeGrid)
      {
        treeGrid.TreeGridColumnSizer.Margin = new Thickness(size, 0, size, 0);
      }
    }

    internal static void RefreshTableColumns(SfGridBase gridBase)
    {
      UpdateTableMargin(gridBase);

      try
      {
        if (gridBase is SfDataGrid { ItemsSource: not null } dataGrid)
        {
          dataGrid.GridColumnSizer?.ResetAutoCalculationforAllColumns();
          dataGrid.GridColumnSizer?.Refresh();
        }
        else if (gridBase is SfTreeGrid { ItemsSource: not null } treeGrid)
        {
          treeGrid.TreeGridColumnSizer?.ResetAutoCalculationforAllColumns();
          treeGrid.TreeGridColumnSizer?.Refresh();
        }
      }
      catch (Exception ex)
      {
        Log.Debug(ex);
      }
    }

    private static double GetTableHeight(SfGridBase gridBase, bool allData)
    {
      var height = gridBase.HeaderRowHeight + 1;

      if (gridBase is SfDataGrid dataGrid)
      {
        var count = Math.Min(1000, dataGrid.View.Records.Count);
        height += count * dataGrid.RowHeight;
      }
      else if (gridBase is SfTreeGrid treeGrid)
      {
        var count = Math.Min(1000, treeGrid.View.Nodes.Count);
        height += count * treeGrid.RowHeight;
      }

      return allData ? height : Math.Min(height, gridBase.ActualHeight);
    }

    private static double GetTableWidth(SfGridBase gridBase, bool allData)
    {
      var width = 0.0;
      var rowHeaderWidth = gridBase.ShowRowHeader ? gridBase.RowHeaderWidth : 0.0;

      if (gridBase is SfDataGrid dataGrid)
      {
        width += rowHeaderWidth + dataGrid.Columns.Where(col => !col.IsHidden).Select(col => col.ActualWidth).Sum();
      }
      else if (gridBase is SfTreeGrid treeGrid)
      {
        width += rowHeaderWidth + treeGrid.Columns.Where(col => !col.IsHidden).Select(col => col.ActualWidth).Sum();
      }

      return allData ? width : Math.Min(width, gridBase.ActualWidth);
    }

    private static void PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is SfTreeGrid treeGrid)
      {
        // remove listeners if left button released
        treeGrid.PreviewMouseLeftButtonUp -= PreviewMouseLeftButtonUp;
        treeGrid.PreviewMouseMove -= MouseMove;
      }
    }

    private static void MouseMove(object sender, MouseEventArgs e)
    {
      dynamic elem = e.OriginalSource;
      if (sender is SfTreeGrid treeGrid)
      {
        if (e.LeftButton == MouseButtonState.Released)
        {
          // remove listeners if left button released
          treeGrid.PreviewMouseLeftButtonUp -= PreviewMouseLeftButtonUp;
          treeGrid.PreviewMouseMove -= MouseMove;
        }
        else if (elem?.DataContext is object stats && treeGrid.ResolveToRowIndex(stats) is var row and > -1)
        {
          if (treeGrid.CurrentItem != stats)
          {
            if (!treeGrid.SelectionController.SelectedRows.Contains(row))
            {
              treeGrid.SelectRows(_startRow, row);
            }
            else
            {
              treeGrid.SelectionController.ClearSelections(false);
              var direction = 0;
              if (_startRow < row)
              {
                direction = -1;
              }
              else if (_startRow > row)
              {
                direction = 1;
              }

              treeGrid.SelectRows(_startRow, row + direction);
            }

            treeGrid.CurrentItem = stats;
            CallSelectionChanged(treeGrid.Parent);
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
      new MessageWindow("Need to Re-Open Summary/Breakdown Windows.", Resource.RESTORE_TABLE_COLUMNS).ShowDialog();
    }

    internal static void LoadColumns(ComboBox columnCombo, dynamic gridBase)
    {
      HashSet<string> visible = null;
      var visibleSetting = ConfigUtil.GetSetting(columnCombo.Tag.ToString());

      if (!string.IsNullOrEmpty(visibleSetting))
      {
        visible = [.. visibleSetting.Split(',')];
      }

      dynamic columns = null;
      dynamic updated = null;
      if (gridBase is SfDataGrid grid)
      {
        columns = grid.Columns;
        updated = new Columns();
      }
      else if (gridBase is SfTreeGrid treeGrid)
      {
        columns = treeGrid.Columns;
        updated = new TreeGridColumns();
      }

      var oldFormat = false;
      var found = new Dictionary<string, bool>();
      var displayOrder = ConfigUtil.GetSetting((columnCombo.Tag as string) + "DisplayIndex");

      if (displayOrder != null)
      {
        foreach (var item in displayOrder.Split(',').ToArray())
        {
          var name = item;

          // Eventually (remove this)
          oldFormat = oldFormat || name.Contains(' ');

          // changed column names
          if (name == "% Luck")
          {
            name = "% Lucky";
          }

          for (var i = 0; columns != null && i < columns.Count; i++)
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
      for (var i = 0; columns != null && i < columns.Count; i++)
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

      var selectedCount = 0;
      var list = new List<ComboBoxItemDetails>();
      for (var i = 0; i < columns.Count; i++)
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
      UiElementUtil.SetComboBoxTitle(columnCombo, selectedCount, Resource.COLUMNS_SELECTED);
    }

    private static dynamic SetColumns(FrameworkElement columnCombo, SfDataGrid dataGrid, dynamic updated)
    {
      dataGrid.Columns = updated;

      // save column order if it changes
      dataGrid.QueryColumnDragging += (sender, e) =>
      {
        if (e.Reason == QueryColumnDraggingReason.Dropped && sender is SfDataGrid grid)
        {
          var columns = grid.Columns.ToList().Select(column => column.MappingName).ToList();
          ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
        }
      };

      return dataGrid.Columns;
    }

    private static dynamic SetColumns(FrameworkElement columnCombo, SfTreeGrid treeGrid, dynamic updated)
    {
      SetTreeExpander(treeGrid, updated);
      treeGrid.Columns = updated;

      // save column order if it changes
      treeGrid.ColumnDragging += (sender, e) =>
      {
        if (e.Reason == QueryColumnDraggingReason.Dropped && sender is SfTreeGrid grid)
        {
          SetTreeExpander(grid, grid.Columns);
          var columns = grid.Columns.ToList().Select(column => column.MappingName).ToList();
          ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
        }
      };

      return treeGrid.Columns;
    }

    private static bool IsColumnVisible(IReadOnlySet<string> visible, dynamic columns, int i)
    {
      var show = true;
      if (visible != null)
      {
        show = visible.Contains(columns[i].MappingName) || visible.Contains(columns[i].HeaderText);
      }
      return show;
    }

    internal static void SetTreeExpander(SfTreeGrid treeGrid, dynamic columns)
    {
      for (var i = 0; i < columns.Count; i++)
      {
        if (!columns[i].IsHidden)
        {
          treeGrid.ExpanderColumn = columns[i].MappingName;
          break;
        }
      }
    }

    internal static void SetHiddenColumns(ComboBox columnCombo, dynamic gridBase)
    {
      var visible = new HashSet<string>();

      if (columnCombo?.Items.Count > 0)
      {
        foreach (var col in columnCombo.Items)
        {
          var checkedItem = col as ComboBoxItemDetails;
          if (checkedItem?.IsChecked == true)
          {
            visible.Add(checkedItem.Value);
          }
        }

        UiElementUtil.SetComboBoxTitle(columnCombo, visible.Count, Resource.COLUMNS_SELECTED);

        if (gridBase is SfDataGrid grid)
        {
          var columns = grid.Columns;
          for (var i = 0; i < columns.Count; i++)
          {
            columns[i].IsHidden = !IsColumnVisible(visible, columns, i);
          }
        }
        else if (gridBase is SfTreeGrid treeGrid)
        {
          var expanderSet = false;
          var columns = treeGrid.Columns;
          for (var i = 0; i < columns.Count; i++)
          {
            columns[i].IsHidden = !IsColumnVisible(visible, columns, i);
            if (!expanderSet && !columns[i].IsHidden)
            {
              expanderSet = true;
              treeGrid.ExpanderColumn = columns[i].MappingName;
            }
          }
        }

        if (!string.IsNullOrEmpty(columnCombo.Tag.ToString()))
        {
          ConfigUtil.SetSetting(columnCombo.Tag.ToString(), string.Join(",", visible));
        }
      }
    }
  }
}

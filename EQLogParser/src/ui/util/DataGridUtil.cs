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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  static class DataGridUtil
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
        var result = TextUtils.BuildCsv(export.Item1, export.Item2, title);
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
        for (var i = 0; i < dataGrid.Columns.Count; i++)
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
        for (var i = 0; i < treeGrid.Columns.Count; i++)
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

    internal static void CreateImage(SfGridBase gridBase, Label titleLabel, bool allData = false)
    {
      Task.Delay(250).ContinueWith(t =>
      {
        gridBase.Dispatcher.InvokeAsync(() =>
        {
          MessageWindow dialog = null;
          var parent = gridBase.Parent as Panel;
          var tableHeight = GetTableHeight(gridBase, allData);
          var tableWidth = GetTableWidth(gridBase, allData);
          var needHeightChange = tableHeight > gridBase.ActualHeight;
          var needWidthChange = tableWidth > gridBase.ActualWidth;

          gridBase.SelectedItems.Clear();
          gridBase.IsHitTestVisible = false;

          if (needHeightChange || needWidthChange)
          {
            dialog = new MessageWindow("Please Wait while Image is Processed.", EQLogParser.Resource.COPY_LARGE_IMAGE);
            dialog.Show();

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
            }, System.Windows.Threading.DispatcherPriority.Background);
          }

          gridBase.Dispatcher.InvokeAsync(() =>
          {
            try
            {
              titleLabel.Measure(titleLabel.RenderSize);
              gridBase.Measure(gridBase.RenderSize);

              // if table needed resize then recalculate values
              if (!double.IsNaN(parent.Height) || !double.IsNaN(parent.Width))
              {
                tableHeight = GetTableHeight(gridBase, allData);
                tableWidth = GetTableWidth(gridBase, allData);
              }

              var titleHeight = titleLabel.ActualHeight;
              var titleWidth = titleLabel.DesiredSize.Width;
              var dpiScale = UIElementUtil.GetDpi();

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

              if (!double.IsNaN(parent.Height))
              {
                parent.Height = double.NaN;
              }

              if (!double.IsNaN(parent.Width))
              {
                parent.Width = double.NaN;
              }
            }
            catch (Exception ex)
            {
              LOG.Error("Could not Copy Image", ex);
            }
            finally
            {
              gridBase.IsHitTestVisible = true;

              if (dialog != null)
              {
                dialog.Close();
              }
            }
          }, System.Windows.Threading.DispatcherPriority.Background);
        }, System.Windows.Threading.DispatcherPriority.Background);
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

    internal static void EnableMouseSelection(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      dynamic elem = e.OriginalSource;
      if (sender is SfTreeGrid treeGrid && elem?.DataContext is object stats && treeGrid.ResolveToRowIndex(stats) is int row && row > -1)
      {
        StartRow = row;
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
      DataGridUtil.UpdateTableMargin(gridBase);

      try
      {
        if (gridBase is SfDataGrid dataGrid && dataGrid.ItemsSource != null)
        {
          dataGrid.GridColumnSizer?.ResetAutoCalculationforAllColumns();
          dataGrid.GridColumnSizer?.Refresh();
        }
        else if (gridBase is SfTreeGrid treeGrid && treeGrid.ItemsSource != null)
        {
          treeGrid.TreeGridColumnSizer?.ResetAutoCalculationforAllColumns();
          treeGrid.TreeGridColumnSizer?.Refresh();
        }
      }
      catch (Exception ex)
      {
        LOG.Debug(ex);
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
        else if (elem?.DataContext is object stats && treeGrid.ResolveToRowIndex(stats) is int row && row > -1)
        {
          if (treeGrid.CurrentItem != stats)
          {
            if (!treeGrid.SelectionController.SelectedRows.Contains(row))
            {
              treeGrid.SelectRows(StartRow, row);
            }
            else
            {
              treeGrid.SelectionController.ClearSelections(false);
              var direction = 0;
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
      new MessageWindow("Need to Re-Open Summary/Breakdown Windows.", EQLogParser.Resource.RESTORE_TABLE_COLUMNS).ShowDialog();
    }

    internal static void LoadColumns(ComboBox columnCombo, dynamic gridBase)
    {
      HashSet<string> visible = null;
      var visibleSetting = ConfigUtil.GetSetting(columnCombo.Tag.ToString());

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

          for (var i = 0; i < columns.Count; i++)
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
      for (var i = 0; i < columns.Count; i++)
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
      SetTreeExpander(treeGrid, updated);
      treeGrid.Columns = updated;

      // save column order if it changes
      treeGrid.ColumnDragging += (object sender, TreeGridColumnDraggingEventArgs e) =>
      {
        if (e.Reason == QueryColumnDraggingReason.Dropped && sender is SfTreeGrid treeGrid)
        {
          SetTreeExpander(treeGrid, treeGrid.Columns);
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

      if (columnCombo.Items.Count > 0)
      {
        for (var i = 0; i < columnCombo.Items.Count; i++)
        {
          var checkedItem = columnCombo.Items[i] as ComboBoxItemDetails;
          if (checkedItem.IsChecked)
          {
            visible.Add(checkedItem.Value);
          }
        }

        UIElementUtil.SetComboBoxTitle(columnCombo, visible.Count, EQLogParser.Resource.COLUMNS_SELECTED);

        if (gridBase is SfDataGrid)
        {
          var columns = ((SfDataGrid)gridBase).Columns;
          for (var i = 0; i < columns.Count; i++)
          {
            columns[i].IsHidden = !IsColumnVisible(visible, columns, i);
          }
        }
        else if (gridBase is SfTreeGrid treeGrid)
        {
          var expanderSet = false;
          var columns = ((SfTreeGrid)gridBase).Columns;
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

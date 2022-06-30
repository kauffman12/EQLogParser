using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  class DataGridUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static void SortColumnsChanged(object sender, GridSortColumnsChangedEventArgs e, string[] descending)
    {
      var dataGrid = sender as SfDataGrid;
      // Here, we have updated the column's items in view based on SortDescriptions. 
      if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
      {
        var sortcolumn = dataGrid.View.SortDescriptions.FirstOrDefault(x => x.PropertyName == e.AddedItems[0].ColumnName);
        SortDescription sortDescription;
        if (descending != null && descending.Contains(e.AddedItems[0].ColumnName))
        {
          sortDescription = new SortDescription(sortcolumn.PropertyName, ListSortDirection.Descending);
        }
        else
        {
          sortDescription = new SortDescription(sortcolumn.PropertyName, ListSortDirection.Ascending);
        }

        dataGrid.View.SortDescriptions.Remove(sortcolumn);
        dataGrid.View.SortDescriptions.Add(sortDescription);
      }
    }

    internal static void SortColumnsChanging(object sender, GridSortColumnsChangingEventArgs e, string[] descending)
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

    internal static void CopyCsvFromTable(DataGrid dataGrid, string title)
    {
      try
      {
        var export = BuildExportData(dataGrid);
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

    internal static Tuple<List<string>, List<List<object>>> BuildExportData(DataGrid dataGrid)
    {
      var headers = new List<string>();
      var headerKeys = new List<string>();
      var data = new List<List<object>>();

      for (int i = 0; i < dataGrid.Columns.Count; i++)
      {
        if (dataGrid.Columns[i] is DataGridBoundColumn bound && bound.Visibility == Visibility.Visible)
        {
          headers.Add(bound.Header as string);
          headerKeys.Add(((System.Windows.Data.Binding)bound.Binding).Path.Path);
        }
      }

      foreach (var item in dataGrid.Items)
      {
        var row = new List<object>();
        foreach (var key in headerKeys)
        {
          // spell casts and counts use dictionaries
          if (item is IDictionary<string, object> dict)
          {
            if (dict.ContainsKey(key))
            {
              row.Add(dict[key]);
            }
          }
          // regular object with properties
          else
          {
            var property = item.GetType().GetProperty(key);
            if (property != null)
            {
              var value = property.GetValue(item, null);
              row.Add(value ?? "");
            }
          }
        }

        data.Add(row);
      }

      return new Tuple<List<string>, List<List<object>>>(headers, data);
    }

    internal static void CreateImage(DataGrid dataGrid, Label titleLabel)
    {
      try
      {
        const int margin = 2;

        var details = GetRowDetails(dataGrid);
        var totalRowHeight = details.Item1 * details.Item2 + details.Item1 + 2; // add extra for header row and a little for the bottom border
        var totalColumnWidth = dataGrid.Columns.ToList().Sum(column => column.ActualWidth) + dataGrid.RowHeaderActualWidth;
        var realTableHeight = dataGrid.ActualHeight < totalRowHeight ? dataGrid.ActualHeight : totalRowHeight;
        var realColumnWidth = dataGrid.ActualWidth < totalColumnWidth ? dataGrid.ActualWidth : totalColumnWidth;

        var dpiScale = VisualTreeHelper.GetDpi(dataGrid);
        RenderTargetBitmap rtb = new RenderTargetBitmap((int)realColumnWidth, (int)(realTableHeight + titleLabel.ActualHeight + margin), dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext ctx = dv.RenderOpen())
        {
          var brush = new VisualBrush(titleLabel);
          ctx.DrawRectangle(brush, null, new Rect(new Point(4, margin / 2), new Size(titleLabel.ActualWidth, titleLabel.ActualHeight)));

          brush = new VisualBrush(dataGrid);
          ctx.DrawRectangle(brush, null, new Rect(new Point(0, titleLabel.ActualHeight + margin), new Size(dataGrid.ActualWidth, dataGrid.ActualHeight + SystemParameters.HorizontalScrollBarHeight)));
        }

        rtb.Render(dv);
        Clipboard.SetImage(rtb);
        dataGrid.Items.Refresh();
      }
      catch (Exception ex)
      {
        LOG.Error("Could not Copy Image", ex);
      }
      finally
      {
        dataGrid.IsEnabled = true;
      }
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
        Properties.Resources.RESTORE_TABLE_COLUMNS, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal static void LoadColumns(ComboBox columnCombo, SfDataGrid dataGrid, int start = 0)
    {
      string visibleSetting = ConfigUtil.GetSetting(columnCombo.Tag as string);
      Dictionary<string, bool> visible = new Dictionary<string, bool>();
      visible["Name"] = true;

      if (!string.IsNullOrEmpty(visibleSetting))
      {
        visibleSetting.Split(',').ToList().ForEach(key => visible[key] = true);
      }

      Columns updatedColumns = new Columns();
      dataGrid.Columns.Take(start).ToList().ForEach(column => updatedColumns.Add(column));

      var indexString = ConfigUtil.GetSetting((columnCombo.Tag as string) + "DisplayIndex");
      if (!string.IsNullOrEmpty(indexString))
      {
        var foundColumns = new Dictionary<string, bool>();
        foreach (var name in indexString.Split(','))
        {
          for (int i = start; i < dataGrid.Columns.Count; i++)
          {
            if (dataGrid.Columns[i].HeaderText == name)
            {
              foundColumns[name] = true;
              updatedColumns.Add(dataGrid.Columns[i]);
              dataGrid.Columns[i].IsHidden = !visible.ContainsKey(name);
              break;
            }
          }
        }

        for (int i = start; i < dataGrid.Columns.Count; i++)
        {
          if (!foundColumns.ContainsKey(dataGrid.Columns[i].HeaderText))
          {
            updatedColumns.Add(dataGrid.Columns[i]);
            dataGrid.Columns[i].IsHidden = !visible.ContainsKey(dataGrid.Columns[i].HeaderText);
          }
        }

        dataGrid.Columns = updatedColumns;

        // save column order if it changes
        dataGrid.QueryColumnDragging  += (object sender, QueryColumnDraggingEventArgs e) =>
        {
          if (e.Reason == QueryColumnDraggingReason.Dropped)
          {
            var dataGrid = sender as SfDataGrid;
            var columns = dataGrid.Columns.ToList().Skip(start).Select(column => column.HeaderText).ToList();
            ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
          }
        };
      }

      int selectedCount = 0;
      List<ComboBoxItemDetails> list = new List<ComboBoxItemDetails>();
      for (int i = start; i < dataGrid.Columns.Count; i++)
      {
        // dont let them hide Name
        if (dataGrid.Columns[i].HeaderText != "Name")
        {
          list.Add(new ComboBoxItemDetails { Text = dataGrid.Columns[i].HeaderText, IsChecked = !dataGrid.Columns[i].IsHidden });
          selectedCount += dataGrid.Columns[i].IsHidden ? 0 : 1;
        }
      }

      columnCombo.ItemsSource = list;
      SetSelectedColumnsTitle(columnCombo, selectedCount);
    }

    internal static Dictionary<string, bool> ShowColumns(ComboBox columns, SfDataGrid dataGrid, List<SfDataGrid> children = null)
    {
      Dictionary<string, bool> visible = new Dictionary<string, bool>();
      if (columns.Items.Count > 0)
      {
        for (int i = 0; i < columns.Items.Count; i++)
        {
          var checkedItem = columns.Items[i] as ComboBoxItemDetails;
          if (checkedItem.IsChecked)
          {
            visible[checkedItem.Text] = true;
          }
        }

        SetSelectedColumnsTitle(columns, visible.Count);

        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
          var header = dataGrid.Columns[i].HeaderText;
          if (!string.IsNullOrEmpty(header))
          {
            if (dataGrid.Columns[i].IsHidden == visible.ContainsKey(header))
            {
              dataGrid.Columns[i].IsHidden = !visible.ContainsKey(header);
            }

            if (children != null)
            {
              children.ForEach(child =>
              {
                if (child.Columns[i].IsHidden == visible.ContainsKey(header))
                {
                  child.Columns[i].IsHidden = !visible.ContainsKey(header);
                }
              });
            }
          }
        }

        if (!string.IsNullOrEmpty(columns.Tag as string))
        {
          ConfigUtil.SetSetting(columns.Tag as string, string.Join(",", visible.Keys));
        }
      }

      return visible;
    }

    private static void SetSelectedColumnsTitle(ComboBox columns, int count)
    {
      if (!(columns.SelectedItem is ComboBoxItemDetails selected))
      {
        selected = columns.Items[0] as ComboBoxItemDetails;
      }

      string countString = columns.Items.Count == count ? "All" : count.ToString(CultureInfo.CurrentCulture);
      selected.SelectedText = countString + " " + Properties.Resources.COLUMNS_SELECTED;
      columns.SelectedIndex = -1;
      columns.SelectedItem = selected;
    }

    internal static Tuple<double, int> GetRowDetails(DataGrid dataGrid)
    {
      double height = 0;
      int count = 0;
      for (int i = 0; i < dataGrid.Items.Count; i++)
      {
        var row = dataGrid.ItemContainerGenerator.ContainerFromIndex(i);
        if (row != null && row is DataGridRow gRow)
        {
          height = gRow.ActualHeight;
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

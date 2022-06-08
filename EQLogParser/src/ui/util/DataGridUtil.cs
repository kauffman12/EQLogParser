﻿using System;
using System.Collections.Generic;
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

    internal static Dictionary<string, bool> LoadColumns(ComboBox columns, DataGrid dataGrid, int start = 0)
    {
      var columnNames = dataGrid.Columns.Select(col => col.Header as string).ToHashSet();
      var indexesCache = new Dictionary<string, int>();
      var indexString = ConfigUtil.GetSetting((columns.Tag as string) + "DisplayIndex");
      if (!string.IsNullOrEmpty(indexString))
      {
        int index = start;
        foreach (var col in indexString.Split(','))
        {
          if (!string.IsNullOrEmpty(col))
          {
            var split = col.Split('|');
            if (split != null && split.Length >= 1 && !string.IsNullOrEmpty(split[0]))
            {
              // ignore the saved index
              if (columnNames.Contains(split[0]))
              {
                indexesCache[split[0]] = index++;
              }
            }
          }
        }
      }

      var cache = new Dictionary<string, bool>();
      string columnSetting = ConfigUtil.GetSetting(columns.Tag as string);
      if (!string.IsNullOrEmpty(columnSetting))
      {
        foreach (var selected in columnSetting.Split(','))
        {
          cache[selected] = true;
        }
      }

      int selectedCount = 0;
      List<ComboBoxItemDetails> list = new List<ComboBoxItemDetails>();
      for (int i = 0; i < dataGrid.Columns.Count; i++)
      {
        var column = dataGrid.Columns[i];
        var header = column.Header as string;
        if (!string.IsNullOrEmpty(header))
        {
          if (header != "Name")
          {
            var visible = (cache.Count == 0 && column.Visibility == Visibility.Visible) || cache.ContainsKey(header);
            column.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            selectedCount += visible ? 1 : 0;
            list.Add(new ComboBoxItemDetails { Text = column.Header as string, IsChecked = visible });

            if (indexesCache.ContainsKey(header) && column.DisplayIndex != indexesCache[header])
            {
              column.DisplayIndex = indexesCache[header];
            }
          }
        }
      }

      columns.ItemsSource = list;
      SetSelectedColumnsTitle(columns, selectedCount);
      return cache;
    }

    internal static Dictionary<string, bool> ShowColumns(ComboBox columns, DataGrid dataGrid, List<DataGrid> children = null)
    {
      Dictionary<string, bool> cache = new Dictionary<string, bool>();
      if (columns.Items.Count > 0)
      {
        for (int i = 0; i < columns.Items.Count; i++)
        {
          var checkedItem = columns.Items[i] as ComboBoxItemDetails;
          if (checkedItem.IsChecked)
          {
            cache[checkedItem.Text] = true;
          }
        }

        SetSelectedColumnsTitle(columns, cache.Count);

        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
          string header = dataGrid.Columns[i].Header as string;
          if (!string.IsNullOrEmpty(header) && header != "Name")
          {
            if (cache.ContainsKey(header))
            {
              if (dataGrid.Columns[i].Visibility != Visibility.Visible)
              {
                dataGrid.Columns[i].Visibility = Visibility.Visible;
              }

              if (children != null)
              {
                children.ForEach(child =>
                {
                  if (child.Columns[i].Visibility != Visibility.Visible)
                  {
                    child.Columns[i].Visibility = Visibility.Visible;
                  }
                });
              }
            }
            else
            {
              if (dataGrid.Columns[i].Visibility != Visibility.Hidden)
              {
                dataGrid.Columns[i].Visibility = Visibility.Hidden;
              }

              if (children != null)
              {
                children.ForEach(child =>
                {
                  if (child.Columns[i].Visibility != Visibility.Hidden)
                  {
                    child.Columns[i].Visibility = Visibility.Hidden;
                  }
                });
              }
            }
          }
        }

        if (!string.IsNullOrEmpty(columns.Tag as string))
        {
          ConfigUtil.SetSetting(columns.Tag as string, string.Join(",", cache.Keys));
        }
      }

      return cache;
    }

    internal static void SaveColumnIndexes(ComboBox columns, DataGrid dataGrid)
    {
      var columnIndexes = new List<string>();
      foreach (var column in dataGrid.Columns.OrderBy(column => column.DisplayIndex))
      {
        string header = column.Header as string;
        if (!string.IsNullOrEmpty(header))
        {
          columnIndexes.Add(header);
        }
      }

      ConfigUtil.SetSetting(columns.Tag + "DisplayIndex", string.Join(",", columnIndexes));
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

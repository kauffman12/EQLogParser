
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  class DataGridUtils
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
        var bound = dataGrid.Columns[i] as DataGridBoundColumn;
        if (bound != null)
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
              row.Add(value == null ? "" : value);
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
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        if (ex is ExternalException || ex is ThreadStateException || ex is ArgumentNullException || ex is NullReferenceException)
        {
          LOG.Error("Could not Copy Image", ex);
        }
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

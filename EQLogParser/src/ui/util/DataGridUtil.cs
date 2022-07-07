using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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
  class DataGridUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static void CheckHideTitlePanel(Panel titlePanel, Panel optionsPanel)
    {
      var settingsLoc = optionsPanel.PointToScreen(new Point(0, 0));
      var titleLoc = titlePanel.PointToScreen(new Point(0, 0));

      if ((titleLoc.X + titlePanel.ActualWidth) > (settingsLoc.X + 10))
      {
        titlePanel.Visibility = Visibility.Hidden;
      }
      else
      {
        titlePanel.Visibility = Visibility.Visible;
      }
    }

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
      Task.Delay(50).ContinueWith((t) => gridBase.Dispatcher.InvokeAsync(() =>
      {
        try
        {
          gridBase.SelectedItems.Clear();

          double totalColumnWidth = 0;
          if (gridBase is SfTreeGrid)
          {
            totalColumnWidth = ((SfTreeGrid)gridBase).Columns.ToList().Sum(column => column.ActualWidth);
          }
          else if (gridBase is SfDataGrid)
          {
            totalColumnWidth = ((SfDataGrid)gridBase).Columns.ToList().Sum(column => column.ActualWidth);
          }
  
          var realTableHeight = gridBase.ActualHeight + gridBase.HeaderRowHeight + 1;
          var realColumnWidth = gridBase.ActualWidth < totalColumnWidth ? gridBase.ActualWidth : totalColumnWidth;
          var titleHeight = titleLabel.DesiredSize.Height - (titleLabel.Padding.Top + titleLabel.Padding.Bottom);
          var titleWidth = titleLabel.DesiredSize.Width;

          var dpiScale = VisualTreeHelper.GetDpi(gridBase);
          RenderTargetBitmap rtb = new RenderTargetBitmap((int)realColumnWidth, (int)(realTableHeight + titleHeight),
            dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

          DrawingVisual dv = new DrawingVisual();
          using (DrawingContext ctx = dv.RenderOpen())
          {
            var brush = new VisualBrush(titleLabel);
            ctx.DrawRectangle(brush, null, new Rect(new Point(4, 0), new Size(titleWidth, titleHeight)));

            brush = new VisualBrush(gridBase);
            ctx.DrawRectangle(brush, null, new Rect(new Point(0, titleHeight), new Size(gridBase.ActualWidth, gridBase.ActualHeight +
              SystemParameters.HorizontalScrollBarHeight)));
          }

          rtb.Render(dv);
          Clipboard.SetImage(rtb);
        }
        catch (Exception ex)
        {
          LOG.Error("Could not Copy Image", ex);
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

    internal static void LoadColumns(ComboBox columnCombo, SfGridBase gridBase)
    {
      Dictionary<string, bool> visible = new Dictionary<string, bool>() { { "Name", true } };
      string visibleSetting = ConfigUtil.GetSetting(columnCombo.Tag as string);

      if (!string.IsNullOrEmpty(visibleSetting))
      {
        visibleSetting.Split(',').ToList().ForEach(key => visible[key] = true);
      }

      if (gridBase is SfDataGrid)
      {
        LoadGridColumns(columnCombo, (SfDataGrid)gridBase, visible);
      }
      else if (gridBase is SfTreeGrid)
      {
        LoadTreeColumns(columnCombo, (SfTreeGrid)gridBase, visible);
      }
    }

    private static void LoadGridColumns(ComboBox columnCombo, SfDataGrid dataGrid, Dictionary<string, bool> visible)
    {
      Columns updatedColumns = new Columns();
      dataGrid.Columns.Take(1).ToList().ForEach(column => updatedColumns.Add(column));

      var indexString = ConfigUtil.GetSetting((columnCombo.Tag as string) + "DisplayIndex");
      if (!string.IsNullOrEmpty(indexString))
      {
        var foundColumns = new Dictionary<string, bool>();
        foreach (var name in indexString.Split(','))
        {
          for (int i = 1; i < dataGrid.Columns.Count; i++)
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

        for (int i = 1; i < dataGrid.Columns.Count; i++)
        {
          if (!foundColumns.ContainsKey(dataGrid.Columns[i].HeaderText))
          {
            updatedColumns.Add(dataGrid.Columns[i]);
            dataGrid.Columns[i].IsHidden = !visible.ContainsKey(dataGrid.Columns[i].HeaderText);
          }
        }

        dataGrid.Columns = updatedColumns;

        // save column order if it changes
        dataGrid.QueryColumnDragging += (object sender, QueryColumnDraggingEventArgs e) =>
        {
          if (e.Reason == QueryColumnDraggingReason.Dropped)
          {
            var dataGrid = sender as SfDataGrid;
            var columns = dataGrid.Columns.ToList().Skip(1).Select(column => column.HeaderText).ToList();
            ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
          }
        };
      }

      int selectedCount = 0;
      List<ComboBoxItemDetails> list = new List<ComboBoxItemDetails>();
      for (int i = 1; i < dataGrid.Columns.Count; i++)
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

    private static void LoadTreeColumns(ComboBox columnCombo, SfTreeGrid treeGrid, Dictionary<string, bool> visible)
    {
      TreeGridColumns updatedColumns = new TreeGridColumns();
      var indexString = ConfigUtil.GetSetting((columnCombo.Tag as string) + "DisplayIndex");
      if (!string.IsNullOrEmpty(indexString))
      {
        var foundColumns = new Dictionary<string, bool>();
        foreach (var name in indexString.Split(','))
        {
          for (int i = 0; i < treeGrid.Columns.Count; i++)
          {
            if (treeGrid.Columns[i].HeaderText == name)
            {
              foundColumns[name] = true;
              updatedColumns.Add(treeGrid.Columns[i]);
              treeGrid.Columns[i].IsHidden = !visible.ContainsKey(name);
              break;
            }
          }
        }

        for (int i = 0; i < treeGrid.Columns.Count; i++)
        {
          if (!foundColumns.ContainsKey(treeGrid.Columns[i].HeaderText))
          {
            updatedColumns.Add(treeGrid.Columns[i]);
            treeGrid.Columns[i].IsHidden = !visible.ContainsKey(treeGrid.Columns[i].HeaderText);
          }
        }

        treeGrid.Columns = updatedColumns;

        // save column order if it changes
        treeGrid.ColumnDragging += (object sender,TreeGridColumnDraggingEventArgs e) =>
        {
          if (e.Reason == QueryColumnDraggingReason.Dropped)
          {
            var treeGrid = sender as SfTreeGrid;
            var columns = treeGrid.Columns.ToList().Select(column => column.HeaderText).ToList();
            ConfigUtil.SetSetting(columnCombo.Tag + "DisplayIndex", string.Join(",", columns));
          }
        };
      }

      int selectedCount = 0;
      List<ComboBoxItemDetails> list = new List<ComboBoxItemDetails>();
      for (int i = 0; i < treeGrid.Columns.Count; i++)
      {
        // dont let them hide Name
        if (treeGrid.Columns[i].HeaderText != "Name")
        {
          list.Add(new ComboBoxItemDetails { Text = treeGrid.Columns[i].HeaderText, IsChecked = !treeGrid.Columns[i].IsHidden });
          selectedCount += treeGrid.Columns[i].IsHidden ? 0 : 1;
        }
      }

      columnCombo.ItemsSource = list;
      SetSelectedColumnsTitle(columnCombo, selectedCount);
    }

    internal static void ShowColumns(ComboBox columns, SfDataGrid dataGrid, List<SfDataGrid> children = null)
    {
      Dictionary<string, bool> visible = new Dictionary<string, bool>() { { "Name", true } };

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
    }

    internal static void ShowColumns(ComboBox columns, SfTreeGrid treeGrid)
    {
      Dictionary<string, bool> visible = new Dictionary<string, bool>() { { "Name", true } };

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

        for (int i = 0; i < treeGrid.Columns.Count; i++)
        {
          var header = treeGrid.Columns[i].HeaderText;
          if (!string.IsNullOrEmpty(header))
          {
            if (treeGrid.Columns[i].IsHidden == visible.ContainsKey(header))
            {
              treeGrid.Columns[i].IsHidden = !visible.ContainsKey(header);
            }
          }
        }

        if (!string.IsNullOrEmpty(columns.Tag as string))
        {
          ConfigUtil.SetSetting(columns.Tag as string, string.Join(",", visible.Keys));
        }
      }
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
  }
}

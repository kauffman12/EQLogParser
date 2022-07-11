using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  public abstract class BreakdownTable : UserControl
  {
    private protected string CurrentSortKey = "Total";
    private protected ListSortDirection CurrentSortDirection = ListSortDirection.Descending;
    private protected DataGridTextColumn CurrentColumn = null;
    private SfTreeGrid TheDataGrid;
    private ComboBox TheColumnsCombo;
    internal Label TheTitle;

    internal void InitBreakdownTable(Label title, SfTreeGrid dataGrid, ComboBox columnsCombo)
    {
      TheDataGrid = dataGrid;
      TheColumnsCombo = columnsCombo;
      TheDataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
      TheTitle = title;

      // default these columns to descending
      string[] desc = new string[] { "Percent", "Total", "Extra", "DPS", "SDPS", "TotalSeconds", "Hits", "Max", "Avg", "AvgCrit", "AvgLucky",
      "ExtraRate", "CritRate", "LuckRate", "TwincastRate", "MeleeAccRate", "MeleeHitRate", "Absorbs", "Blocks", "Dodges", "Invulnerable",
      "Misses", "Parries", "StrikethroughHits", "RiposteHits", "RampageRate"};
      TheDataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      TheDataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.LoadColumns(TheColumnsCombo, TheDataGrid);

      // workaround to avoid drag/drop failing when grid has no data
      TheDataGrid.ItemsSource = new List<PlayerStats>();
    }

    internal void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(TheDataGrid, TheTitle.Content.ToString());
    internal void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitle);
    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(TheColumnsCombo, TheDataGrid);
  }
}

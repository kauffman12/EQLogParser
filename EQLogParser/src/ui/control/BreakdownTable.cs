using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.ComponentModel;
using System.Windows.Controls;

namespace EQLogParser
{
  public abstract class BreakdownTable : UserControl
  {
    private protected string CurrentSortKey = "Total";
    private protected ListSortDirection CurrentSortDirection = ListSortDirection.Descending;
    private protected DataGridTextColumn CurrentColumn = null;
    private SfTreeGrid TheDataGrid;
    private ComboBox TheColumnsCombo;

    internal void InitBreakdownTable(SfTreeGrid dataGrid, ComboBox columnsCombo)
    {
      TheDataGrid = dataGrid;
      TheColumnsCombo = columnsCombo;
      TheDataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });

      // default these columns to descending
      string[] desc = new string[] { "Percent", "Total", "Extra", "DPS", "SDPS", "TotalSeconds", "Hits", "Max", "Avg", "AvgCrit", "AvgLucky",
      "ExtraRate", "CritRate", "LuckRate", "TwincastRate", "MeleeAccRate", "MeleeHitRate", "Absorbs", "Blocks", "Dodges", "Invulnerable",
      "Misses", "Parries", "StrikethroughHits", "RiposteHits", "RampageRate"};
      TheDataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      TheDataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.LoadColumns(TheColumnsCombo, TheDataGrid);
    }

    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(TheColumnsCombo, TheDataGrid);
  }
}

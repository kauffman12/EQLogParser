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
  public abstract class BreakdownTable : UserControl, IDisposable
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
      var desc = new string[] { "Percent", "Total", "Extra", "Potentia", "DPS", "SDPS", "TotalSeconds", "Hits", "Max", "Avg",
        "AvgCrit", "AvgLucky", "ExtraRate", "CritRate", "LuckRate", "TwincastRate", "MeleeAccRate", "MeleeHitRate", "Absorbs",
        "Blocks", "Dodges", "Invulnerable", "Misses", "Parries", "StrikethroughHits", "RiposteHits", "RampageRate", "TotalAss",
        "TotalFinishing", "TotalHead", "TotalRiposte", "TotalSlay", "AvgNonTwincast", "AvgNonTwincastCrit", "AvgNonTwincastLucky",
        "TwincastHits", "Resists", "DoubleBowRate", "FlurryRate", "ResistRate", "MeleeAttempts", "MeleeUndefended"};

      TheDataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      TheDataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.LoadColumns(TheColumnsCombo, TheDataGrid);

      // workaround to avoid drag/drop failing when grid has no data
      TheDataGrid.ItemsSource = new List<PlayerStats>();
    }

    internal void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(TheDataGrid, TheTitle.Content.ToString());
    internal void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitle);
    internal void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitle, true);
    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(TheColumnsCombo, TheDataGrid);

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        TheDataGrid.Dispose();
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}

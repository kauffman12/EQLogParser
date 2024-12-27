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
    private protected DataGridTextColumn CurrentColumn = null;
    private SfTreeGrid _theDataGrid;
    private ComboBox _theColumnsCombo;
    internal Label TheTitle;

    internal void InitBreakdownTable(Label title, SfTreeGrid dataGrid, ComboBox columnsCombo)
    {
      _theDataGrid = dataGrid;
      _theColumnsCombo = columnsCombo;
      _theDataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
      TheTitle = title;

      // default these columns to descending
      var desc = new[] { "Percent", "Total", "Extra", "Potential", "DPS", "SDPS", "TotalSeconds", "Hits", "Max", "Avg",
        "AvgCrit", "AvgLucky", "ExtraRate", "CritRate", "LuckRate", "TwincastRate", "MeleeAccRate", "MeleeHitRate", "Absorbs",
        "Blocks", "Dodges", "Invulnerable", "Misses", "Parries", "StrikethroughHits", "RiposteHits", "RampageRate", "TotalAss",
        "TotalFinishing", "TotalHead", "TotalRiposte", "TotalSlay", "AvgNonTwincast", "AvgNonTwincastCrit", "AvgNonTwincastLucky",
        "TwincastHits", "Resists", "DoubleBowRate", "FlurryRate", "ResistRate", "MeleeAttempts", "MeleeUndefended"};

      _theDataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      _theDataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.RefreshTableColumns(_theDataGrid);
      DataGridUtil.LoadColumns(_theColumnsCombo, _theDataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      // workaround to avoid drag/drop failing when grid has no data
      _theDataGrid.ItemsSource = new List<PlayerStats>();
    }

    internal void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(_theDataGrid, TheTitle.Content.ToString());
    internal async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(_theDataGrid, TheTitle);
    internal async void CreateLargeImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(_theDataGrid, TheTitle, true);
    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    internal void SelectDataGridColumns(object sender, EventArgs e) => DataGridUtil.SetHiddenColumns(_theColumnsCombo, _theDataGrid);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(_theDataGrid);

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        _theDataGrid.Dispose();
        _disposedValue = true;
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

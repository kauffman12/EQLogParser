using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TauntStatsViewer.xaml
  /// </summary>
  public partial class TauntStatsViewer : UserControl, IDisposable
  {
    public TauntStatsViewer()
    {
      InitializeComponent();

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += LogLoadingComplete;
      (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange += SelectionChange;
      dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Taunt", SortDirection = ListSortDirection.Descending });

      // default these columns to descending
      var desc = new string[] { "Taunt", "Failed", "Improved", "SuccessRate" };
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataGridUtil.UpdateTableMargin(dataGrid);
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
      Load();
    }

    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel, true);
    private void LogLoadingComplete(string _) => Load();
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void SelectionChange(object sender, System.Collections.IList e)
    {
      if (fightOption.SelectedIndex != 0)
      {
        Load();
      }
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid?.View != null)
      {
        Load();
      }
    }

    private void Load()
    {
      var totals = new Dictionary<string, dynamic>();
      var childTotals = new Dictionary<string, dynamic>();
      var fights = fightOption.SelectedIndex == 0 ? (Application.Current.MainWindow as MainWindow).GetFightTable()?.GetFights() :
  (Application.Current.MainWindow as MainWindow).GetFightTable()?.GetSelectedFights();

      foreach (var fight in fights)
      {
        foreach (var block in fight.TauntBlocks)
        {
          foreach (var record in block.Actions.Cast<TauntRecord>())
          {
            var parentKey = record.Player;
            if (totals.TryGetValue(parentKey, out var value))
            {
              UpdateRow(record, value);
            }
            else
            {
              totals[parentKey] = CreateRow(record, record.Player, true);
            }

            var childKey = record.Player + "-" + record.Npc;
            if (childTotals.TryGetValue(childKey, out var child))
            {
              UpdateRow(record, child);
            }
            else
            {
              childTotals[childKey] = CreateRow(record, record.Npc, false);
              totals[parentKey].Children.Add(childTotals[childKey]);
            }
          }
        }
      }

      dataGrid.ItemsSource = totals.Values;
      titleLabel.Content = totals.Values.Count > 0 ? "Taunt Usage By Player" : "No Taunt Data Found";
    }

    private static void UpdateRow(TauntRecord record, dynamic row)
    {
      row.Taunt += record.IsImproved ? 0 : record.Success ? 1 : 0;
      row.Failed += record.IsImproved ? 0 : record.Success ? 0 : 1;
      row.Improved += record.IsImproved ? 1 : 0;

      var count = row.Failed + row.Taunt;
      if (count > 0)
      {
        row.SuccessRate = (double)Math.Round((float)row.Taunt / count * 100, 2);
      }
    }

    private ExpandoObject CreateRow(TauntRecord record, string name, bool parent)
    {
      dynamic row = new ExpandoObject();
      row.Name = name;
      row.Taunt = record.IsImproved ? 0 : record.Success ? 1 : 0;
      row.Failed = record.IsImproved ? 0 : record.Success ? 0 : 1;
      row.Improved = record.IsImproved ? 1 : 0;
      row.SuccessRate = record.IsImproved ? 0 : record.Success ? 100.0 : 0.0;

      if (parent)
      {
        row.Children = new List<dynamic>();
      }

      return row;
    }

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= LogLoadingComplete;
        (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange -= SelectionChange;
        dataGrid.Dispose();
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

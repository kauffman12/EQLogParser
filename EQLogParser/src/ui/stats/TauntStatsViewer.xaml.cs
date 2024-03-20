using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TauntStatsViewer.xaml
  /// </summary>
  public partial class TauntStatsViewer : IDocumentContent
  {
    private bool _ready;

    public TauntStatsViewer()
    {
      InitializeComponent();
      dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Taunt", SortDirection = ListSortDirection.Descending });
      // default these columns to descending
      var desc = new[] { "Taunt", "Failed", "Improved", "SuccessRate" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel, true);
    private void LogLoadingComplete(string _) => Load();
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void SelectionChange(List<Fight> _)
    {
      if (_ready)
      {
        Load();
      }
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (_ready)
      {
        Load();
      }
    }

    private void Load()
    {
      var totals = new Dictionary<string, dynamic>();
      var childTotals = new Dictionary<string, dynamic>();
      var fights = fightOption.SelectedIndex == 0 ? MainActions.GetFights() : MainActions.GetSelectedFights();

      foreach (var fight in CollectionsMarshal.AsSpan(fights))
      {
        foreach (var block in CollectionsMarshal.AsSpan(fight.TauntBlocks))
        {
          foreach (var record in block.Actions.ToArray().Cast<TauntRecord>())
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

      if ((row.Failed + row.Taunt) is var count && count > 0)
      {
        row.SuccessRate = (double)Math.Round((float)row.Taunt / count * 100, 2);
      }
    }

    private static ExpandoObject CreateRow(TauntRecord record, string name, bool parent)
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

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        MainActions.EventsLogLoadingComplete += LogLoadingComplete;
        MainActions.EventsFightSelectionChanged += SelectionChange;
        Load();
        _ready = true;
      }
    }

    public void HideContent()
    {
      MainActions.EventsLogLoadingComplete -= LogLoadingComplete;
      MainActions.EventsFightSelectionChanged -= SelectionChange;
      dataGrid.ItemsSource = null;
      _ready = false;
    }
  }
}

using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TauntStatsViewer.xaml
  /// </summary>
  public partial class TauntStatsViewer : IDocumentContent
  {
    public ObservableCollection<TauntRow> TauntData { get; set; }
    private bool _ready;

    public TauntStatsViewer()
    {
      InitializeComponent();
      TauntData = [];
      DataContext = this;

      // default these columns to descending
      var desc = new[] { "Taunt", "Failed", "Improved", "SuccessRate" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private async void CreateLargeImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel, true);
    private void LogLoadingComplete(string file, bool open) => Load();
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void SelectionChange(List<Fight> _)
    {
      if (_ready && fightOption.SelectedIndex == 1)
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
      var totals = new Dictionary<string, TauntRow>();
      var childTotals = new Dictionary<string, TauntRow>();
      var fights = MainActions.GetFights(fightOption.SelectedIndex != 0);

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

      UiUtil.UpdateObservable(totals.Values.AsEnumerable(), TauntData);
      titleLabel.Content = totals.Values.Count > 0 ? "Taunt Usage By Player" : "No Taunt Data Found";
    }

    private static void UpdateRow(TauntRecord record, TauntRow row)
    {
      row.Taunt += record.IsImproved ? 0 : record.Success ? 1 : 0;
      row.Failed += record.IsImproved ? 0 : record.Success ? 0 : 1;
      row.Improved += record.IsImproved ? 1 : 0;

      if ((row.Failed + row.Taunt) is var count and > 0)
      {
        row.SuccessRate = Math.Round((float)row.Taunt / count * 100, 2);
      }
    }

    private static TauntRow CreateRow(TauntRecord record, string name, bool parent)
    {
      var row = new TauntRow
      {
        Name = name,
        Taunt = record.IsImproved ? 0 : record.Success ? 1 : 0,
        Failed = record.IsImproved ? 0 : record.Success ? 0 : 1,
        Improved = record.IsImproved ? 1 : 0,
        SuccessRate = record.IsImproved ? 0 : record.Success ? 100.0 : 0.0
      };


      if (parent)
      {
        row.Children = [];
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
      TauntData.Clear();
      _ready = false;
    }

    private void AutoGeneratingColumn(object sender, TreeGridAutoGeneratingColumnEventArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping == "Name")
      {
        e.Column.Width = MainActions.CurrentNameWidth;
      }
      else if (mapping is "Taunt" or "Failed" or "Improved")
      {
        var column = new TreeGridNumericColumn
        {
          MappingName = mapping,
          NumberDecimalDigits = 0,
          NumberGroupSizes = [3],
          TextAlignment = TextAlignment.Right,
          HeaderText = "# " + mapping
        };

        if (mapping == "Failed")
        {
          column.ShowHeaderToolTip = true;
          column.HeaderToolTipTemplate = (DataTemplate)Application.Current.Resources["HeaderFailedTauntToolTip"];
        }
        else if (mapping == "Improved")
        {
          column.ShowHeaderToolTip = true;
          column.HeaderToolTipTemplate = (DataTemplate)Application.Current.Resources["HeaderImprovedTauntToolTip"];
        }

        column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
        e.Column = column;
      }
      else if (mapping is "SuccessRate")
      {
        e.Column.TextAlignment = TextAlignment.Right;
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath(mapping),
          Converter = new ZeroConverter()
        };
        e.Column.HeaderText = "Success %";
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
      else if (mapping is "Children")
      {
        e.Cancel = true;
      }
    }
  }

  public class TauntRow : INotifyPropertyChanged
  {
    private string _name;
    private int _taunt;
    private int _failed;
    private double _successRate;
    private int _improved;

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string Name
    {
      get => _name;
      set
      {
        if (_name != value)
        {
          _name = value;
          OnPropertyChanged(nameof(Name));
        }
      }
    }

    public int Taunt
    {
      get => _taunt;
      set
      {
        if (_taunt != value)
        {
          _taunt = value;
          OnPropertyChanged(nameof(Taunt));
        }
      }
    }

    public int Failed
    {
      get => _failed;
      set
      {
        if (_failed != value)
        {
          _failed = value;
          OnPropertyChanged(nameof(Failed));
        }
      }
    }

    public double SuccessRate
    {
      get => _successRate;
      set
      {
        if (!_successRate.Equals(value))
        {
          _successRate = value;
          OnPropertyChanged(nameof(SuccessRate));
        }
      }
    }

    public int Improved
    {
      get => _improved;
      set
      {
        if (_improved != value)
        {
          _improved = value;
          OnPropertyChanged(nameof(Improved));
        }
      }
    }

    // Other properties that do not require notification
    public ObservableCollection<TauntRow> Children { get; set; }
  }
}

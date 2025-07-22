using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  public partial class TriggersLogView : IDocumentContent
  {
    private List<Tuple<string, ObservableCollection<AlertEntry>>> _alertLogs;
    private readonly DispatcherTimer _updateTimer;

    public TriggersLogView()
    {
      InitializeComponent();

      // default these columns to descending
      var desc = new[] { "Eval", "BeginTime", "LogTime" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
      _updateTimer.Tick += (_, _) =>
      {
        _updateTimer.Stop();
        var colDescriptions = dataGrid.SortColumnDescriptions;
        if (colDescriptions.Count != 1 || colDescriptions[0].ColumnName != "BeginTime" ||
            colDescriptions[0].SortDirection != ListSortDirection.Descending)
        {
          dataGrid.SortColumnDescriptions.Clear();
          dataGrid.SortColumnDescriptions.Add(new SortColumnDescription
          { ColumnName = "BeginTime", SortDirection = ListSortDirection.Descending });
          dataGrid?.View?.Refresh();
        }
      };

      TriggerManager.Instance.EventsProcessorsUpdated += EventsProcessorsUpdated;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private async void EventsProcessorsUpdated(bool _)
    {
      _alertLogs = [.. await TriggerManager.Instance.GetAlertLogs()];
      if (logList != null)
      {
        var selected = logList.SelectedItem as string;
        var list = _alertLogs.Select(log => log.Item1).ToList();

        logList.ItemsSource = list;
        // not sure why
        logList.SelectedIndex = -1;

        if (_alertLogs.Count > 0)
        {
          if (selected != null && list.IndexOf(selected) is var found and > -1)
          {
            logList.SelectedIndex = found;
          }
          else
          {
            logList.SelectedIndex = 0;
          }
        }
      }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox combo && dataGrid != null)
      {
        var sorting = dataGrid.SortColumnDescriptions.ToList();
        dataGrid.SortColumnDescriptions.Clear();
        var collection = combo.SelectedIndex >= 0 ? _alertLogs[combo.SelectedIndex].Item2 : null;
        dataGrid.ItemsSource = collection;
        sorting.ForEach(item => dataGrid.SortColumnDescriptions.Add(item));

        if (collection != null)
        {
          collection.CollectionChanged -= TheCollectionChanged;
          collection.CollectionChanged += TheCollectionChanged;
        }
      }
    }

    private void TheCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
      if (!_updateTimer.IsEnabled)
      {
        _updateTimer.Start();
      }
    }

    private new void PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
      {
        return;
      }

      // case where click happened but selection event doesn't fire
      if (e.OriginalSource is FrameworkElement { DataContext: AlertEntry entry })
      {
        if (dataGrid.SelectedItem != entry)
        {
          dataGrid.SelectedItem = entry;
        }

        TriggerManager.Instance.Select(entry);
      }
    }

    public void HideContent()
    {
      // do nothing
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping is "BeginTime" or "LogTime")
      {
        e.Column.SortMode = DataReflectionMode.Value;
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath(mapping),
          Converter = new DateTimeConverter()
        };
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.HeaderText = mapping == "BeginTime" ? "Event Time" : "Log Time";
        e.Column.Width = MainActions.CurrentDateTimeWidth;
      }
      else if (mapping == "Name")
      {
        e.Column.Width = MainActions.CurrentNameWidth;
      }
      else if (mapping == "Eval")
      {
        e.Column = new GridNumericColumn
        {
          MappingName = mapping,
          SortMode = DataReflectionMode.Value,
          HeaderText = "Eval (μs)",
          NumberDecimalDigits = 0,
          NumberGroupSizes = [3],
          Width = MainActions.CurrentMediumWidth
        };
      }
      else if (mapping == "Priority")
      {
        e.Column = new GridNumericColumn
        {
          MappingName = mapping,
          SortMode = DataReflectionMode.Value,
          HeaderText = mapping,
          NumberDecimalDigits = 0,
          NumberGroupSizes = [3],
          Width = MainActions.CurrentMediumWidth
        };
      }
      else if (mapping == "Line")
      {
        e.Column.HeaderText = "Line Matched";
        e.Column.ColumnSizer = GridLengthUnitType.AutoLastColumnFill;
      }
      else if (mapping == "Type")
      {
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(mapping);
      }
      else
      {
        e.Cancel = true;
      }
    }
  }

  public class AlertEntry
  {
    public double BeginTime { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public long Priority { get; set; }
    public long Eval { get; set; }
    public double LogTime { get; set; }
    public string Line { get; set; }
    public string NodeId { get; set; }
    public string CharacterId { get; set; }
  }
}

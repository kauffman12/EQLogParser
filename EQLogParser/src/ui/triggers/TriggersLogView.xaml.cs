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

namespace EQLogParser
{
  public partial class TriggersLogView : IDocumentContent
  {
    private readonly DelayedAction _batchRefresh;
    private bool _ready;
    private ObservableCollection<TriggerLogEntry> _currentCollection;

    public TriggersLogView()
    {
      _batchRefresh = new DelayedAction(TimeSpan.FromSeconds(1), RefreshGrid);
      InitializeComponent();

      // default these columns to descending
      var desc = new[] { "Eval", "BeginTime", "LogTime" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      Loaded += ContentLoaded;
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        TriggerManager.Instance.EventsProcessorsUpdated += EventsProcessorsUpdated;
        ThemeConfig.EventsThemeChanged += EventsThemeChanged;
        _ready = true;
        EventsProcessorsUpdated();
      }
    }

    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void EventsProcessorsUpdated()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (logList != null)
        {
          var logs = TriggerLogManager.Instance.GetLogs(out var activeProcessors);
          var list = new List<string>(activeProcessors);

          logList.ItemsSource = list;
          // not sure why
          logList.SelectedIndex = -1;

          if (list.Count > 0)
          {
            if (logList.SelectedItem is string selected && list.IndexOf(selected) is var found and > -1)
            {
              logList.SelectedIndex = found;
            }
            else
            {
              logList.SelectedIndex = 0;
            }
          }
          // Note: SelectionChanged handler above subscribes to CollectionChanged for the selected item.
          // Do NOT subscribe here again — that was causing a double subscription.
        }
      });
    }

    private void SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      if (sender is ComboBox combo && dataGrid != null)
      {
        // Unsubscribe from previous collection
        if (_currentCollection != null)
        {
          _currentCollection.CollectionChanged -= TheCollectionChanged;
          _currentCollection = null;
        }

        var sorting = dataGrid.SortColumnDescriptions.ToList();
        dataGrid.SortColumnDescriptions.Clear();

        BulkObservableCollection<TriggerLogEntry> collection = null;
        if (combo.SelectedIndex >= 0 && combo.SelectedItem is string selectedName)
        {
          var logs = TriggerLogManager.Instance.GetLogs(out _);
          collection = logs.TryGetValue(selectedName, out var log) ? log : new BulkObservableCollection<TriggerLogEntry>();
        }

        dataGrid.ItemsSource = collection;
        sorting.ForEach(item => dataGrid.SortColumnDescriptions.Add(item));

        // Subscribe to new collection
        if (collection != null)
        {
          _currentCollection = collection;
          collection.CollectionChanged += TheCollectionChanged;
        }
      }
    }

    private void TheCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
      _batchRefresh.Invoke();
    }

    private void RefreshGrid()
    {
      Dispatcher.InvokeAsync(() =>
      {
        var colDescriptions = dataGrid.SortColumnDescriptions;
        if (colDescriptions.Count != 1 || colDescriptions[0].ColumnName != "BeginTime" ||
            colDescriptions[0].SortDirection != ListSortDirection.Descending)
        {
          dataGrid.SortColumnDescriptions.Clear();
          dataGrid.SortColumnDescriptions.Add(new SortColumnDescription
          { ColumnName = "BeginTime", SortDirection = ListSortDirection.Descending });
          dataGrid?.View?.Refresh();
        }
      });
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
      TriggerLogManager.Instance.ClearAll();
    }

    private new void PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
      {
        return;
      }

      // case where click happened but selection event doesn't fire
      if (e.OriginalSource is FrameworkElement { DataContext: TriggerLogEntry entry })
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
      TriggerManager.Instance.EventsProcessorsUpdated -= EventsProcessorsUpdated;
      ThemeConfig.EventsThemeChanged -= EventsThemeChanged;

      // Unsubscribe from current collection to prevent memory leaks
      if (_currentCollection != null)
      {
        _currentCollection.CollectionChanged -= TheCollectionChanged;
        _currentCollection = null;
      }

      _ready = false;
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
        e.Column.Width = ThemeConfig.CurrentDateTimeWidth;
      }
      else if (mapping == "Name")
      {
        e.Column.Width = ThemeConfig.CurrentNameWidth;
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
          Width = ThemeConfig.CurrentMediumWidth
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
          Width = ThemeConfig.CurrentMediumWidth
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
}

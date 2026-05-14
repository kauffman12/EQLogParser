using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
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

    public TriggersLogView()
    {
      _batchRefresh = new DelayedAction(TimeSpan.FromSeconds(1), RefreshGrid);
      InitializeComponent();

      // default these columns to descending
      var desc = new[] { "Eval", "BeginTime", "LogTime" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        TriggerManager.Instance.EventsProcessorsUpdated += EventsProcessorsUpdated;
        ThemeManager.EventsThemeChanged += EventsThemeChanged;
        _ready = true;

        // Trigger EventsProcessorsUpdated to set up selection and subscriptions
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
          var logs = TriggerLogManager.Instance.GetLogs();
          var list = new List<string>(logs.Keys);

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

          // Subscribe to CollectionChanged for currently selected character
          if (logList.SelectedItem is string selectedCharacter && logs.TryGetValue(selectedCharacter, out var collection))
          {
            collection.CollectionChanged -= TheCollectionChanged;
            collection.CollectionChanged += TheCollectionChanged;
          }
        }
      });
    }

    private void SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      if (sender is ComboBox combo && dataGrid != null)
      {
        var sorting = dataGrid.SortColumnDescriptions.ToList();
        dataGrid.SortColumnDescriptions.Clear();
        var logs = TriggerLogManager.Instance.GetLogs();
        var collection = combo.SelectedIndex >= 0 && logs.TryGetValue(combo.SelectedItem?.ToString() ?? "", out var log) ? log : null;
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
      ThemeManager.EventsThemeChanged -= EventsThemeChanged;
      _batchRefresh?.Dispose();
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
        e.Column.Width = ThemeManager.CurrentDateTimeWidth;
      }
      else if (mapping == "Name")
      {
        e.Column.Width = ThemeManager.CurrentNameWidth;
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
          Width = ThemeManager.CurrentMediumWidth
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
          Width = ThemeManager.CurrentMediumWidth
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

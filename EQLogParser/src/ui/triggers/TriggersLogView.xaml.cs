using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
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
    private readonly DispatcherTimer _filterTimer;
    private string _currentFilterText = string.Empty;
    private bool _ready;
    private ObservableCollection<TriggerLogEntry> _currentCollection;

    public TriggersLogView()
    {
      _batchRefresh = new DelayedAction(TimeSpan.FromSeconds(1), RefreshGrid);

      InitializeComponent();
      SyncFusionUtil.ConfigureFrozenCellAnimations(dataGrid);
      _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
      _filterTimer.Tick += (_, _) =>
      {
        _filterTimer.Stop();
        ApplyFilter();
      };
      UiElementUtil.SetWatermark(filterBox, Resource.TRIGGER_LOG_FILTER_TEXT);

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
        ApplyFilter();
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
          // Capture current selection before resetting ItemsSource
          var currentSelection = logList.SelectedItem as string;

          var logs = TriggerLogManager.Instance.GetLogs(out var activeProcessors);
          // Sort alphabetically for consistent ordering
          var list = activeProcessors.OrderBy(x => x).ToList();

          logList.ItemsSource = list;
          logList.SelectedIndex = -1;

          if (list.Count > 0)
          {
            // Try to preserve user's previous selection if it still exists
            if (!string.IsNullOrEmpty(currentSelection) && list.Contains(currentSelection))
            {
              logList.SelectedIndex = list.IndexOf(currentSelection);
            }
            else
            {
              logList.SelectedIndex = 0;
            }
          }
        }
      });
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid?.View == null) return;
      dataGrid.View.Filter = item =>
      {
        var entry = (TriggerLogEntry)item;
        return string.IsNullOrEmpty(_currentFilterText)
          || (entry.Name?.Contains(_currentFilterText, StringComparison.OrdinalIgnoreCase) == true)
          || (entry.Line?.Contains(_currentFilterText, StringComparison.OrdinalIgnoreCase) == true);
      };
      dataGrid.View.RefreshFilter();
    }

    private void ApplyFilter()
    {
      if (dataGrid == null) return;
      var query = filterBox.Text == Resource.TRIGGER_LOG_FILTER_TEXT ? string.Empty : filterBox.Text;
      if (_currentFilterText == query) return;
      _currentFilterText = query;
      dataGrid.SelectedItems.Clear();
      dataGrid.View?.RefreshFilter();
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs e)
    {
      // XAML can raise TextChanged before the constructor has wired the timer.
      _filterTimer?.Stop();
      _filterTimer?.Start();
    }

    private void FilterKeyDown(object sender, KeyEventArgs e)
    {
      if (dataGrid != null && e.Key == Key.Escape)
      {
        e.Handled = true;
        UiElementUtil.RestoreWatermark(filterBox, Resource.TRIGGER_LOG_FILTER_TEXT);
        _filterTimer.Stop();
        ApplyFilter();
        dataGrid.Focus();
      }
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
      if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
      {
      }

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
      _filterTimer.Stop();
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

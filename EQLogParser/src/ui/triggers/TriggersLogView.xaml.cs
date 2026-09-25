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
    /*
     * What this document reports to the heartbeat (PerfCounters, UiBeatMonitor). The grid is bound to a live collection that the trigger
     * thread appends to in batches, and each batch arrives as a whole-collection Reset — which tells WPF nothing it can do incrementally: a
     * bound grid throws its view away and rebuilds it, re-sorting up to 5000 rows on the UI thread. trig.logGrid prices the refresh this
     * window asks for; trig.logReset counts the invalidations arriving whether or not anybody asks, so a stall naming "triglog" with no span
     * running points at the grid's own reload rather than at code of ours. Both are new because a player debugging triggers opens exactly
     * this window during exactly the fight that made them debug it, and until now nothing in the log could see it.
     */
    private static readonly int LogGridId = PerfCounters.Register("trig.logGrid");
    private static readonly int LogResetId = PerfCounters.Register("trig.logReset", uiThread: false);

    private readonly DelayedAction _batchRefresh;
    private bool _ready;
    private ObservableCollection<TriggerLogEntry> _currentCollection;

    public TriggersLogView()
    {
      _batchRefresh = new DelayedAction(TimeSpan.FromSeconds(1), RefreshGrid);

      /* Named once, not per change: the beat line has to say this window was up while a beat went unanswered. */
      IsVisibleChanged += (_, e) => UiBeatMonitor.NoteSurface("triglog", (bool)e.NewValue);

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
        PerfCounters.Note(LogResetId);
      }

      _batchRefresh.Invoke();
    }

    private void RefreshGrid()
    {
      Dispatcher.InvokeAsync(() => PerfCounters.Run(LogGridId, () =>
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
      }));
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

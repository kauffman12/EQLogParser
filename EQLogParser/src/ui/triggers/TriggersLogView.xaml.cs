using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersLogView.xaml
  /// </summary>
  public partial class TriggersLogView : UserControl, IDisposable
  {
    private List<Tuple<string, ObservableCollection<AlertEntry>>> AlertLogs;

    public TriggersLogView()
    {
      InitializeComponent();
      TriggerManager.Instance.EventsProcessorsUpdated += EventsProcessorsUpdated;
    }

    private void EventsProcessorsUpdated(bool _)
    {
      AlertLogs = TriggerManager.Instance.GetAlertLogs().ToList();
      if (AlertLogs != null)
      {
        var selected = logList?.SelectedItem as string;
        var list = AlertLogs.Select(log => log.Item1).ToList();
        logList.ItemsSource = list;
        logList.SelectedIndex = -1;

        if (AlertLogs?.Count > 0)
        {
          if (selected != null && list.IndexOf(selected) is int found and > -1)
          {
            logList.SelectedIndex = found;
          }
          else
          {
            logList.SelectedIndex = 0;
          }
        }
      }
      else
      {
        logList.ItemsSource = null;
      }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox combo && dataGrid != null)
      {
        if (combo.SelectedIndex >= 0)
        {
          dataGrid.ItemsSource = AlertLogs[combo.SelectedIndex].Item2;
        }
        else
        {
          dataGrid.ItemsSource = null;
        }
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

        TriggerManager.Instance.Select(entry.NodeId);
      }
    }

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        TriggerManager.Instance.EventsProcessorsUpdated -= EventsProcessorsUpdated;
        AlertLogs?.Clear();
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

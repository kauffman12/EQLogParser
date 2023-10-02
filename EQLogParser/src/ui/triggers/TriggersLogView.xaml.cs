using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
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
    private List<Tuple<string, ObservableCollection<AlertEntry>, bool>> AlertLogs = null;

    public TriggersLogView()
    {
      InitializeComponent();
      TriggerManager.Instance.EventsProcessorsUpdated += EventsProcessorsUpdated;
    }

    private void EventsProcessorsUpdated(bool obj)
    {
      AlertLogs = TriggerManager.Instance.GetAlertLogs().OrderBy(logs => logs.Item1).ToList();
      logList.ItemsSource = AlertLogs.Select(log => log.Item3 ? "Trigger Tester" : log.Item1).ToList();
      logList.SelectedIndex = -1;
      logList.SelectedIndex = 0;
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
      if (e.OriginalSource is FrameworkElement element && element.DataContext is ExpandoObject data)
      {
        if (dataGrid.SelectedItem != data)
        {
          dataGrid.SelectedItem = data;
        }

        TriggerManager.Instance.Select((data as dynamic).Trigger);
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

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

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
  public partial class TriggersLogView : IDocumentContent
  {
    private List<Tuple<string, ObservableCollection<AlertEntry>>> _alertLogs;

    public TriggersLogView()
    {
      InitializeComponent();
      TriggerManager.Instance.EventsProcessorsUpdated += EventsProcessorsUpdated;
    }

    private void EventsProcessorsUpdated(bool _)
    {
      _alertLogs = TriggerManager.Instance.GetAlertLogs().ToList();
      if (_alertLogs != null)
      {
        var selected = logList?.SelectedItem as string;
        var list = _alertLogs.Select(log => log.Item1).ToList();
        if (logList != null)
        {
          logList.ItemsSource = list;
          logList.SelectedIndex = -1;

          if (_alertLogs?.Count > 0)
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
          dataGrid.ItemsSource = _alertLogs[combo.SelectedIndex].Item2;
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

    public void HideContent()
    {
      // do nothing
    }
  }
}

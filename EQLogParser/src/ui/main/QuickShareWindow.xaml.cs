using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using Syncfusion.UI.Xaml.ScrollAxis;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  public partial class QuickShareWindow
  {
    public ObservableCollection<QuickShareRecord> QuickShareData { get; set; }
    private readonly ObservableCollection<TrustedPlayer> _items = [];
    private bool _addInProgress;

    public QuickShareWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      QuickShareData = RecordManager.Instance.AllQuickShareRecords;
      DataContext = this;

      TriggerStateManager.Instance.GetTrustedPlayers().ContinueWith(task =>
      {
        if (task.Result != null)
        {
          Dispatcher.Invoke(() =>
          {
            foreach (var item in task.Result)
            {
              _items.Add(item);
            }
          });
        }
      });

      trustGrid.ItemsSource = _items;
      MainActions.EventsThemeChanged += EventsThemeChanged;
      watchQuickShare.IsChecked = ConfigUtil.IfSet("TriggersWatchForQuickShare");
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);
    private void TrustGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => CleanupTable();

    private void SendToEqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.SelectedItem is QuickShareRecord record)
      {
        Clipboard.SetText(record.Key);
      }
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.SelectedItem is QuickShareRecord record)
      {
        if (record.Type == "GINA")
        {
          GinaUtil.ImportQuickShare(record.Key, record.From);
        }
        else if (record.Type == TriggerUtil.ShareTrigger || record.Type == TriggerUtil.ShareOverlay)
        {
          TriggerUtil.ImportQuickShare(record.Key, record.From);
        }
        else
        {
          new MessageWindow("Quick Share Key is Invalid", Resource.RECEIVED_SHARE).ShowDialog();
        }
      }
    }

    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      sendToEQ.IsEnabled = dataGrid is { SelectedItem: QuickShareRecord };
      download.IsEnabled = dataGrid is { SelectedItem: QuickShareRecord };
    }

    private async void QuickShareWindowOnClosing(object sender, CancelEventArgs e)
    {
      CleanupTable();
      await TriggerStateManager.Instance.SaveTrustedPlayers([.. _items]);
      MainActions.EventsThemeChanged -= EventsThemeChanged;

      try
      {
        dataGrid?.Dispose();
        trustGrid?.Dispose();
      }
      catch (Exception)
      {
        // ignore
      }
    }

    private void EnableCheckBoxOnChecked(object sender, RoutedEventArgs e)
    {
      titleLabel.Content = "Quick Shares Enabled";
      titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
      ConfigUtil.SetSetting("TriggersWatchForQuickShare", true);
      trustGrid.IsEnabled = true;
    }

    private void EnableCheckBoxOnUnchecked(object sender, RoutedEventArgs e)
    {
      titleLabel.Content = "Enable Quick Shares";
      titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
      ConfigUtil.SetSetting("TriggersWatchForQuickShare", false);
      trustGrid.IsEnabled = false;
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping == "BeginTime")
      {
        e.Column.SortMode = DataReflectionMode.Value;
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath(mapping),
          Converter = new DateTimeConverter()
        };
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.HeaderText = "Share Time";
        e.Column.Width = MainActions.CurrentDateTimeWidth;
      }
      else if (mapping == "Type")
      {
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
      else if (mapping == "Key")
      {
        e.Column.HeaderText = "Share Key";
        e.Column.Width = MainActions.CurrentSpellWidth;
      }
      else if (mapping == "IsMine")
      {
        e.Cancel = true;
      }
    }

    private void CleanupTable()
    {
      for (var i = _items.Count - 1; i >= 0; i--)
      {
        if (string.IsNullOrEmpty(_items[i].Name))
        {
          _items.RemoveAt(i);
        }
      }
    }

    private void TrustGridLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
      var cellManager = trustGrid.SelectionController.CurrentCellManager;
      if (trustGrid.View.IsAddingNew && cellManager != null && cellManager.HasCurrentCell && cellManager.CurrentCell.IsEditing &&
        cellManager.CurrentCell.Element.DataContext is TrustedPlayer tp && !string.IsNullOrEmpty(tp.Name))
      {
        if (trustGrid.View.IsEditingItem)
        {
          trustGrid.View.CommitEdit();
        }

        if (!_addInProgress)
        {
          _addInProgress = true;

          trustGrid.MoveCurrentCell(new RowColumnIndex(trustGrid.View.Records.Count + 1, 1));
          trustGrid.GetAddNewRowController().CommitAddNew();
          trustGrid.SelectedIndex = trustGrid.View.Records.Count - 1;
        }
        else
        {
          _addInProgress = false;
        }
      }
    }
  }

  public class QuickShareRecord
  {
    public double BeginTime { get; set; }
    public string Type { get; set; }
    public string To { get; set; }
    public string From { get; set; }
    public string Key { get; set; }
    public bool IsMine { get; set; }
  }
}

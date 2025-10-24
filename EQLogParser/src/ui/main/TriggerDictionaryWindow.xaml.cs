using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using Syncfusion.UI.Xaml.ScrollAxis;
using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace EQLogParser
{
  public partial class TriggerDictionaryWindow
  {
    private readonly ObservableCollection<LexiconItem> _items = [];
    private TriggersTreeView _treeView;
    private bool _addInProgress;

    public TriggerDictionaryWindow(TriggersTreeView view)
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      TriggerStateManager.Instance.GetLexicon().ContinueWith(task =>
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

        foreach (var item in _items)
        {
          item.PropertyChanged += (s, e) => EnableSave();
        }

        _items.CollectionChanged += (s, e) => EnableSave();
      });

      dataGrid.ItemsSource = _items;
      _treeView = view;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void EnableSave()
    {
      saveButton.IsEnabled = true;
      closeButton.Content = "Cancel";
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
      CleanupTable();
      await TriggerStateManager.Instance.SaveLexicon([.. _items]);
      _treeView = null;
      Close();
    }

    private void CleanupTable()
    {
      for (var i = _items.Count - 1; i >= 0; i--)
      {
        if (string.IsNullOrEmpty(_items[i].Replace) && string.IsNullOrEmpty(_items[i].With))
        {
          _items.RemoveAt(i);
        }
      }
    }

    private void TestClicked(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is LexiconItem item)
      {
        _treeView?.PlayTts(item.Replace + " will be spoken as " + item.With);
      }
    }

    private void DataGridCurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
    {
      if (e.OriginalSender is SfDataGrid { CurrentItem: LexiconItem item })
      {
        UpdateTestButton(item);
      }
      else
      {
        UpdateTestButton(null);
      }
    }

    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      UpdateTestButton(dataGrid.SelectedItem as LexiconItem);
      CleanupTable();
    }

    private void DataGridLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
      // Guard against null View during window disposal
      if (dataGrid?.View == null) return;
      
      var cellManager = dataGrid.SelectionController?.CurrentCellManager;
      if (dataGrid.View.IsAddingNew && cellManager != null && cellManager.HasCurrentCell && cellManager.CurrentCell.IsEditing &&
        cellManager.CurrentCell.Element != null && cellManager.CurrentCell.Element.DataContext is LexiconItem item && 
        !string.IsNullOrEmpty(item.Replace) && !string.IsNullOrEmpty(item.With))
      {
        if (dataGrid.View.IsEditingItem)
        {
          dataGrid.View.CommitEdit();
        }

        if (!_addInProgress)
        {
          _addInProgress = true;

          dataGrid.MoveCurrentCell(new RowColumnIndex(dataGrid.View.Records.Count + 1, 1));
          dataGrid.GetAddNewRowController().CommitAddNew();
          dataGrid.SelectedIndex = dataGrid.View.Records.Count - 1;
        }
        else
        {
          _addInProgress = false;
        }
      }
    }

    private void UpdateTestButton(LexiconItem item)
    {
      if (item != null && !string.IsNullOrEmpty(item.Replace) && !string.IsNullOrEmpty(item.With))
      {
        testButton.IsEnabled = true;
      }
      else
      {
        testButton.IsEnabled = false;
      }
    }

    private void TheWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      MainActions.EventsThemeChanged -= EventsThemeChanged;

      try
      {
        dataGrid?.Dispose();
      }
      catch (Exception)
      {
        // ignore
      }
    }
  }
}

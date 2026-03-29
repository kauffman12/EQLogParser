using log4net;
using Microsoft.Win32;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using Syncfusion.UI.Xaml.ScrollAxis;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace EQLogParser
{
  public partial class TriggerDictionaryWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
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
    private void ImportClicked(object sender, RoutedEventArgs e) => Import();
    private void ExportClicked(object sender, RoutedEventArgs e) => Export();

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
      if (dataGrid?.View == null) return;
      var cellManager = dataGrid?.SelectionController?.CurrentCellManager;

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

    private void Import()
    {
      var openFileDialog = new OpenFileDialog
      {
        Filter = "CSV Files (*.csv)|*.csv|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
        Title = "Import Phonetic Dictionary"
      };

      if (openFileDialog.ShowDialog() == true)
      {
        try
        {
          var lines = File.ReadAllLines(openFileDialog.FileName);
          var initialCount = _items.Count;

          for (var i = 0; i < lines.Length; i++)
          {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || (i == 0 && line.Trim().StartsWith("Word to Replace", StringComparison.OrdinalIgnoreCase)))
            {
              continue; // Skip header and empty lines
            }

            var parts = line.Split(',');
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            {
              var replace = parts[0].Trim();
              var with = parts[1].Trim();

              // Check for duplicates
              if (!_items.Any(item => item.Replace == replace && item.With == with))
              {
                _items.Add(new LexiconItem { Replace = replace, With = with });
              }
            }
            else if (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]))
            {
              // Handle single column entries (word to replace without replacement)
              var replace = parts[0].Trim();
              if (!_items.Any(item => item.Replace == replace))
              {
                _items.Add(new LexiconItem { Replace = replace, With = replace });
              }
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow($"Error Importing: {ex.Message}", "Import Error").ShowDialog();
          Log.Error("Error Importing", ex);
        }
      }
    }

    private void Export()
    {
      var saveFileDialog = new SaveFileDialog
      {
        Filter = "CSV Files (*.csv)|*.csv|Text Files (*.txt)|*.txt",
        Title = "Export Phonetic Dictionary",
        FileName = "eqlp-phonetic-dictionary.csv"
      };

      if (saveFileDialog.ShowDialog() == true)
      {
        try
        {
          using (var writer = new StreamWriter(saveFileDialog.FileName))
          {
            // Write header
            writer.WriteLine("Word to Replace,Say As");

            // Write data
            foreach (var item in _items)
            {
              if (!string.IsNullOrEmpty(item.Replace) && !string.IsNullOrEmpty(item.With))
              {
                writer.WriteLine($"{item.Replace},{item.With}");
              }
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow($"Error Exporting: {ex.Message}", "Export Error").ShowDialog();
          Log.Error("Error Exporting", ex);
        }
      }
    }
  }
}

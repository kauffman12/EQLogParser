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

      TriggerStateDB.Instance.GetLexicon().ContinueWith(task =>
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
      await TriggerStateDB.Instance.SaveLexicon([.. _items]);
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
      if (dataGrid.SelectedItem is LexiconItem item)
      {
        UpdateTestButton(item);
      }
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
        Filter = "TSV Files (*.tsv)|*.tsv|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
        Title = "Import Phonetic Dictionary"
      };

      if (openFileDialog.ShowDialog() == true)
      {
        try
        {
          var data = File.ReadAllText(openFileDialog.FileName);
          var rows = TextUtils.ReadTsv(data);

          var updated = false;

          for (var i = 0; i < rows.Count; i++)
          {
            var parts = rows[i];

            if (parts == null || parts.Length == 0 || parts.All(string.IsNullOrWhiteSpace))
            {
              continue;
            }

            if (i == 0 && parts.Length > 0 &&
                parts[0].Trim().StartsWith("Word to Replace", StringComparison.OrdinalIgnoreCase))
            {
              continue;
            }

            if (parts.Length >= 2 &&
                !string.IsNullOrWhiteSpace(parts[0]) &&
                !string.IsNullOrWhiteSpace(parts[1]))
            {
              var replace = parts[0].Trim();
              var with = parts[1].Trim();

              if (!_items.Any(item => item.Replace == replace && item.With == with))
              {
                _items.Add(new LexiconItem { Replace = replace, With = with });
                updated = true;
              }
            }
            else if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
            {
              var replace = parts[0].Trim();

              if (!_items.Any(item => item.Replace == replace))
              {
                _items.Add(new LexiconItem { Replace = replace, With = replace });
                updated = true;
              }
            }
          }

          if (updated)
          {
            EnableSave();
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
        Filter = "TSV Files (*.tsv)|*.tsv|Text Files (*.txt)|*.txt",
        Title = "Export Phonetic Dictionary",
        FileName = "eqlp-phonetic-dictionary.tsv"
      };

      if (saveFileDialog.ShowDialog() == true)
      {
        try
        {
          var export = DataGridUtil.BuildExportData(dataGrid);
          var result = TextUtils.BuildTsv(export.Item1, export.Item2);
          using (var writer = new StreamWriter(saveFileDialog.FileName))
          {
            writer.Write(result);
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

using Syncfusion.UI.Xaml.Grid;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggerDictionaryWindow.xaml
  /// </summary>
  public partial class TriggerDictionaryWindow
  {
    private readonly ObservableCollection<LexiconItem> _items = [];
    private LexiconItem _previous;

    public TriggerDictionaryWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      foreach (var item in TriggerStateManager.Instance.GetLexicon())
      {
        _items.Add(item);
      }

      dataGrid.ItemsSource = _items;
      _items.CollectionChanged += ItemsChanged;
    }

    private void ItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
      if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?[0] is LexiconItem oldItem &&
        (!string.IsNullOrEmpty(oldItem.Replace) || !string.IsNullOrEmpty(oldItem.With)))
      {
        saveButton.IsEnabled = true;
      }

      if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?[0] is LexiconItem newItem &&
        (!string.IsNullOrEmpty(newItem.Replace) || !string.IsNullOrEmpty(newItem.With)))
      {
        saveButton.IsEnabled = true;
      }
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
      CleanupTable();
      TriggerStateManager.Instance.SaveLexicon([.. _items]);
      saveButton.IsEnabled = false;
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

    private async void TestClicked(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is LexiconItem item)
      {
        var testSynth = AudioManager.CreateSpeechSynthesizer();
        if (testSynth == null)
        {
          return;
        }

        AudioManager.Instance.SpeakAsync(testSynth, item.With ?? "");
        testSynth.Dispose();
      }
    }

    private void TriggerDictionaryWindowOnClosing(object sender, CancelEventArgs e)
    {
      dataGrid?.Dispose();
    }

    private void DataGridCurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
    {
      if (e.OriginalSender is SfDataGrid { CurrentItem: LexiconItem item })
      {
        if (item.Replace != null && !WordCheck().IsMatch(item.Replace))
        {
          item.Replace = _previous.Replace;
        }

        if (_previous != null && (_previous.Replace != item.Replace || _previous.With != item.With))
        {
          saveButton.IsEnabled = true;
        }

        UpdateTestButton(item);
      }
      else
      {
        UpdateTestButton(null);
      }
    }

    private void DataGridCurrentCellBeginEdit(object sender, CurrentCellBeginEditEventArgs e)
    {
      if (e.OriginalSender is SfDataGrid { CurrentItem: LexiconItem item })
      {
        _previous = new LexiconItem { Replace = item.Replace, With = item.With };
      }
    }

    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      UpdateTestButton(dataGrid.SelectedItem as LexiconItem);
      CleanupTable();
    }

    private void UpdateTestButton(LexiconItem item)
    {
      if (item != null && !string.IsNullOrEmpty(item.Replace))
      {
        testButton.Content = "Test " + item.Replace;
        testButton.IsEnabled = true;
      }
      else
      {
        testButton.Content = "Test Selected";
        testButton.IsEnabled = false;
      }
    }

    [GeneratedRegex(@"^\w+$")]
    private static partial Regex WordCheck();
  }
}

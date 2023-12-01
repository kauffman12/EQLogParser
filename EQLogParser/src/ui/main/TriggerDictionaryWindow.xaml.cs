using Syncfusion.UI.Xaml.Grid;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggerDictionaryWindow.xaml
  /// </summary>
  public partial class TriggerDictionaryWindow
  {
    private readonly SpeechSynthesizer TestSynth;
    private readonly ObservableCollection<LexiconItem> Items = new();
    private LexiconItem Previous;

    public TriggerDictionaryWindow()
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();
      Owner = Application.Current.MainWindow;
      TestSynth = TriggerUtil.GetSpeechSynthesizer();

      foreach (var item in TriggerStateManager.Instance.GetLexicon())
      {
        Items.Add(item);
      }

      dataGrid.ItemsSource = Items;
      Items.CollectionChanged += ItemsChanged;
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
      TriggerStateManager.Instance.SaveLexicon(Items.ToList());
      saveButton.IsEnabled = false;
    }

    private void CleanupTable()
    {
      for (var i = Items.Count - 1; i >= 0; i--)
      {
        if (string.IsNullOrEmpty(Items[i].Replace) && string.IsNullOrEmpty(Items[i].With))
        {
          Items.RemoveAt(i);
        }
      }
    }

    private void TestClicked(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is LexiconItem item)
      {
        TestSynth.SpeakAsync(item.With ?? "");
      }
    }

    private void TriggerDictionaryWindowOnClosing(object sender, CancelEventArgs e)
    {
      TestSynth?.Dispose();
      dataGrid?.Dispose();
    }

    private void DataGridCurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
    {
      if (e.OriginalSender is SfDataGrid { CurrentItem: LexiconItem item })
      {
        if (item.Replace != null && !Regex.IsMatch(item.Replace, @"^\w+$"))
        {
          item.Replace = Previous.Replace;
        }

        if (Previous != null && (Previous.Replace != item.Replace || Previous.With != item.With))
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
        Previous = new LexiconItem { Replace = item.Replace, With = item.With };
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
  }
}

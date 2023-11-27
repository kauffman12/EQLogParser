using Syncfusion.UI.Xaml.Grid;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for QuickShareLogView.xaml
  /// </summary>
  public partial class QuickShareLogView : IDocumentContent
  {
    public QuickShareLogView()
    {
      InitializeComponent();
      dataGrid.ItemsSource = RecordManager.Instance.AllQuickShareRecords;
    }

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
        else if (record.Type == TriggerUtil.ShareTrigger)
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

    public void HideContent()
    {
      // nothing to do
    }
  }
}

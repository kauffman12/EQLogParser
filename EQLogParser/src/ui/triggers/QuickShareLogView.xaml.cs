using Syncfusion.UI.Xaml.Grid;
using System;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for QuickShareLogView.xaml
  /// </summary>
  public partial class QuickShareLogView : IDisposable
  {
    public QuickShareLogView()
    {
      InitializeComponent();
      dataGrid.ItemsSource = DataManager.Instance.GetQuickShareRecords();
    }

    private void SendToEQClick(object sender, RoutedEventArgs e)
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
        else if (record.Type == TriggerUtil.SHARE_TRIGGER)
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

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
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

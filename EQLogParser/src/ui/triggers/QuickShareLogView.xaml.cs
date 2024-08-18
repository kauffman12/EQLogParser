using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for QuickShareLogView.xaml
  /// </summary>
  public partial class QuickShareLogView : IDocumentContent
  {
    public ObservableCollection<QuickShareRecord> QuickShareData { get; set; }
    public QuickShareLogView()
    {
      InitializeComponent();
      QuickShareData = RecordManager.Instance.AllQuickShareRecords;
      DataContext = this;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

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
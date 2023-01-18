using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersLogView.xaml
  /// </summary>
  public partial class TriggersLogView : UserControl
  {
    public TriggersLogView()
    {
      InitializeComponent();

      dataGrid.ItemsSource = TriggerManager.Instance.GetAlertLog();
    }
  }
}

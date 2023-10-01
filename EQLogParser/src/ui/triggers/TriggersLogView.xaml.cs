using System.Dynamic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
      // dataGrid.ItemsSource = TriggerManager.Instance.GetAlertLog();
    }

    private new void PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
      {
        return;
      }
      // case where click happened but selection event doesn't fire
      if (e.OriginalSource is FrameworkElement element && element.DataContext is ExpandoObject data)
      {
        if (dataGrid.SelectedItem != data)
        {
          dataGrid.SelectedItem = data;
        }

        TriggerManager.Instance.Select((data as dynamic).Trigger);
      }
    }
  }
}

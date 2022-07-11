using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  public class ComboBoxItemTemplateSelector : DataTemplateSelector
  {
    private readonly DataTemplate SelectedItemTemplate;
    private readonly DataTemplate DropDownItemTemplate;

    public ComboBoxItemTemplateSelector()
    {
      var textBoxFactory = new FrameworkElementFactory(typeof(TextBox));
      textBoxFactory.SetBinding(TextBox.TextProperty, new Binding("SelectedText"));
      textBoxFactory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
      SelectedItemTemplate = new DataTemplate(typeof(ComboBoxItemDetails)) { VisualTree = textBoxFactory };

      var stackPanelFactory = new FrameworkElementFactory(typeof(StackPanel));
      var checkBoxFactory = new FrameworkElementFactory(typeof(CheckBox));
      var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
      stackPanelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
      checkBoxFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding("IsChecked"));
      checkBoxFactory.SetValue(CheckBox.WidthProperty, 20.0);
      textBlockFactory.SetBinding(TextBlock.TextProperty, new Binding("Text"));
      textBlockFactory.SetValue(TextBlock.IsHitTestVisibleProperty, false);
      stackPanelFactory.AppendChild(checkBoxFactory);
      stackPanelFactory.AppendChild(textBlockFactory);
      DropDownItemTemplate = new DataTemplate(typeof(ComboBoxItemDetails)) { VisualTree = stackPanelFactory };
    }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      return GetVisualParent<ComboBoxItem>(container) == null ? SelectedItemTemplate : DropDownItemTemplate;
    }

    private static T GetVisualParent<T>(DependencyObject child) where T : Visual
    {
      T result = null;
      while (child != null)
      {
        if (child is T found)
        {
          result = found;
          break;
        }
        child = VisualTreeHelper.GetParent(child);
      }
      return result;
    }
  }
}

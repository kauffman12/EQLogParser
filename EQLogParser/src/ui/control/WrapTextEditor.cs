using Syncfusion.DocIO;
using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  internal class WrapTextEditor : ITypeEditor
  {
    TextBox textBox;

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      if (info.CanWrite)
      {
        var binding = new Binding("Value")
        {
          Mode = BindingMode.TwoWay,
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };

        BindingOperations.SetBinding(textBox, TextBox.TextProperty, binding);
      }
      else
      {
        textBox.IsEnabled = false;
        var binding = new Binding("Value")
        {
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };

        BindingOperations.SetBinding(textBox, TextBox.TextProperty, binding);
      }
    }

    public object Create(PropertyInfo propertyInfo)
    {
      textBox = new TextBox()
      {
        TextWrapping = System.Windows.TextWrapping.Wrap,
        Padding = new System.Windows.Thickness(2)
      };
    
      return textBox;
    }

    public void Detach(PropertyViewItem property)
    {

    }
  }
}

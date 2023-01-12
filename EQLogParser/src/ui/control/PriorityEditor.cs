
using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.Reflection;
using System.Windows.Data;

namespace EQLogParser
{
  public class PriorityEditor : ITypeEditor
  {
    IntegerTextBox integerTextBox;

    public PriorityEditor()
    {

    }

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
        BindingOperations.SetBinding(integerTextBox, IntegerTextBox.ValueProperty, binding);
      }
      else
      {
        integerTextBox.IsEnabled = false;
        var binding = new Binding("Value")
        {
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };
        BindingOperations.SetBinding(integerTextBox, IntegerTextBox.ValueProperty, binding);
      }
    }
    public object Create(PropertyInfo propertyInfo)
    {
      integerTextBox = new IntegerTextBox()
      {
        ApplyZeroColor = false,
        MinValue = 1,
        MaxValue = 5,
        ShowSpinButton = true,
      };
      return integerTextBox;
    }
    public void Detach(PropertyViewItem property)
    {

    }
  }
}

using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.Reflection;
using System.Windows.Data;

namespace EQLogParser
{
  public class RangeEditor : ITypeEditor
  {
    IntegerTextBox integerTextBox;
    readonly private long Min;
    readonly private long Max;

    public RangeEditor(long min, long max)
    {
      Min = min;
      Max = max;
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
        MinValue = Min,
        MaxValue = Max,
        ShowSpinButton = true
      };

      return integerTextBox;
    }

    public void Detach(PropertyViewItem property)
    {

    }
  }
}

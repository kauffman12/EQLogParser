using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.Reflection;
using System.Windows.Data;

namespace EQLogParser
{
  public class RangeEditor : ITypeEditor
  {
    private IntegerTextBox TheTextBox;
    private readonly long Min;
    private readonly long Max;

    public RangeEditor(long min, long max)
    {
      Min = min;
      Max = max;
    }

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      Binding binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(TheTextBox, IntegerTextBox.ValueProperty, binding);
    }

    public object Create(PropertyInfo propertyInfo)
    {
      var textBox = new IntegerTextBox()
      {
        ApplyZeroColor = false,
        MinValue = Min,
        MaxValue = Max,
        ShowSpinButton = true
      };

      textBox.SetResourceReference(IntegerTextBox.PositiveForegroundProperty, "ContentForeground");

      TheTextBox = textBox;
      return textBox;
    }

    public void Detach(PropertyViewItem property)
    {
      if (TheTextBox != null)
      {
        BindingOperations.ClearAllBindings(TheTextBox);
        TheTextBox?.Dispose();
        TheTextBox = null;
      }
    }
  }
}

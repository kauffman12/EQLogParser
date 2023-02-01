using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Data;

namespace EQLogParser
{
  public class RangeEditor : ITypeEditor
  {
    private readonly List<IntegerTextBox> TheTextBoxes = new List<IntegerTextBox>();
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

      BindingOperations.SetBinding(TheTextBoxes.Last(), IntegerTextBox.ValueProperty, binding);
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

      TheTextBoxes.Add(textBox);
      return textBox;
    }

    public void Detach(PropertyViewItem property)
    {
      TheTextBoxes.ForEach(textBox =>
      {
        BindingOperations.ClearAllBindings(textBox);
        textBox?.Dispose();
      });
      TheTextBoxes.Clear();
    }
  }
}

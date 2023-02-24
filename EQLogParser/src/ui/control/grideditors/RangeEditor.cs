using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  public class RangeEditor : BaseTypeEditor
  {
    private IntegerTextBox TheTextBox;
    private readonly long Min;
    private readonly long Max;

    public RangeEditor(long min, long max)
    {
      Min = min;
      Max = max;
    }

    public void Update(long value)
    {
      TheTextBox.Value = value;
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      Binding binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      };

      BindingOperations.SetBinding(TheTextBox, IntegerTextBox.ValueProperty, binding);
    }

    public override object Create(PropertyDescriptor PropertyDescriptor) => Create();
    public override object Create(PropertyInfo propertyInfo) => Create();

    public object Create()
    {
      var textBox = new IntegerTextBox()
      {
        ApplyZeroColor = false,
        ShowSpinButton = true
      };

      if (Min != Max)
      {
        textBox.MinValue = Min;
        textBox.MaxValue = Max;
      }

      textBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");

      TheTextBox = textBox;
      return textBox;
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
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

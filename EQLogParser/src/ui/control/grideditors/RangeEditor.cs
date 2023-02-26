using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  public class RangeEditor : BaseTypeEditor
  {
    private IntegerTextBox TheIntTextBox;
    private DoubleTextBox TheDoubleTextBox;
    private readonly double Min;
    private readonly double Max;
    private readonly Type Type;

    public RangeEditor(Type type, double min, double max)
    {
      Type = type;
      Min = min;
      Max = max;
    }

    public void Update(long value)
    {
      if (TheIntTextBox != null)
      {
        TheIntTextBox.Value = value;
      }
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

      if (Type == typeof(long))
      {
        BindingOperations.SetBinding(TheIntTextBox, IntegerTextBox.ValueProperty, binding);
      }
      else
      {
        BindingOperations.SetBinding(TheDoubleTextBox, DoubleTextBox.ValueProperty, binding);
      }
    }

    public override object Create(PropertyDescriptor PropertyDescriptor) => Create();
    public override object Create(PropertyInfo propertyInfo) => Create();

    public object Create()
    {
      object result;
      if (Type == typeof(long))
      {
        var intTextBox = new IntegerTextBox() { ApplyZeroColor = false, ShowSpinButton = true };

        if (Min != Max)
        {
          intTextBox.MinValue = (long)Min;
          intTextBox.MaxValue = (long)Max;
        }

        intTextBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");
        TheIntTextBox = intTextBox;
        result = intTextBox;
      }
      else
      {
        var doubleTextBox = new DoubleTextBox() { ApplyZeroColor = false, ShowSpinButton = true, ScrollInterval = 0.1 };

        if (Min != Max)
        {
          doubleTextBox.MinValue = (double)Min;
          doubleTextBox.MaxValue = (double)Max;
        }

        doubleTextBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");
        TheDoubleTextBox = doubleTextBox;
        result = doubleTextBox;
      }

      return result;
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (TheIntTextBox != null)
      {
        BindingOperations.ClearAllBindings(TheIntTextBox);
        TheIntTextBox?.Dispose();
        TheIntTextBox = null;
      }

      if (TheDoubleTextBox != null)
      {
        BindingOperations.ClearAllBindings(TheDoubleTextBox);
        TheDoubleTextBox?.Dispose();
        TheDoubleTextBox = null;
      }
    }
  }
}

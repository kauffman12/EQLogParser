﻿using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.Reflection;
using System.Windows.Data;

namespace EQLogParser
{
  public class RangeEditor : ITypeEditor
  {
    private IntegerTextBox TheTextBox;
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

        BindingOperations.SetBinding(TheTextBox, IntegerTextBox.ValueProperty, binding);
      }
      else
      {
        TheTextBox.IsEnabled = false;
        var binding = new Binding("Value")
        {
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };

        BindingOperations.SetBinding(TheTextBox, IntegerTextBox.ValueProperty, binding);
      }
    }

    public object Create(PropertyInfo propertyInfo)
    {
      TheTextBox = new IntegerTextBox()
      {
        ApplyZeroColor = false,
        MinValue = Min,
        MaxValue = Max,
        ShowSpinButton = true
      };

      return TheTextBox;
    }

    public void Detach(PropertyViewItem property)
    {
      if (TheTextBox != null)
      {
        BindingOperations.ClearAllBindings(TheTextBox);
      }

      TheTextBox = null;
    }
  }
}

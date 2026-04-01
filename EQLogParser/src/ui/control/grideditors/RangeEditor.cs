using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  public class RangeEditor : BaseTypeEditor
  {
    private IntegerTextBox _theIntTextBox;
    private DoubleTextBox _theDoubleTextBox;
    private readonly double _min;
    private readonly double _max;
    private readonly Type _type;

    public RangeEditor(Type type, double min, double max)
    {
      _type = type;
      _min = min;
      _max = max;
    }

    public void Update(long value)
    {
      if (_theIntTextBox != null && _theIntTextBox.Value != value)
      {
        _theIntTextBox.Value = value;
      }
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      };

      if (_type == typeof(long))
      {
        BindingOperations.SetBinding(_theIntTextBox, IntegerTextBox.ValueProperty, binding);
      }
      else
      {
        BindingOperations.SetBinding(_theDoubleTextBox, DoubleTextBox.ValueProperty, binding);
      }
    }

    public override object Create(PropertyDescriptor _) => Create();
    public override object Create(PropertyInfo _) => Create();

    public object Create()
    {
      if (_theIntTextBox != null)
        return _theIntTextBox;

      if (_theDoubleTextBox != null)
        return _theDoubleTextBox;

      if (_type == typeof(long))
      {
        _theIntTextBox = new IntegerTextBox
        {
          ApplyZeroColor = false,
          ShowSpinButton = true,
          BorderThickness = new Thickness(0)
        };

        if (!_min.Equals(_max))
        {
          _theIntTextBox.MinValue = (long)_min;
          _theIntTextBox.MaxValue = (long)_max;
        }

        _theIntTextBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");
        return _theIntTextBox;
      }
      else
      {
        _theDoubleTextBox = new DoubleTextBox
        {
          ApplyZeroColor = false,
          ShowSpinButton = true,
          ScrollInterval = 0.1,
          BorderThickness = new Thickness(0)
        };

        if (!_min.Equals(_max))
        {
          _theDoubleTextBox.MinValue = _min;
          _theDoubleTextBox.MaxValue = _max;
        }

        _theDoubleTextBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");
        return _theDoubleTextBox;
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theIntTextBox != null)
      {
        BindingOperations.ClearAllBindings(_theIntTextBox);
        _theIntTextBox?.Dispose();
        _theIntTextBox = null;
      }

      if (_theDoubleTextBox != null)
      {
        BindingOperations.ClearAllBindings(_theDoubleTextBox);
        _theDoubleTextBox?.Dispose();
        _theDoubleTextBox = null;
      }
    }
  }
}

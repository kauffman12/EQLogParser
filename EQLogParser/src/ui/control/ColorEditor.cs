using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;

namespace EQLogParser
{
  internal class ColorEditor : ITypeEditor
  {
    private ColorPicker TheColorPicker;

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      Binding binding;
      if (info.CanWrite)
      {
        binding = new Binding("Value")
        {
          Mode = BindingMode.TwoWay,
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };       
      }
      else
      {
        TheColorPicker.IsEnabled = false;
        binding = new Binding("Value")
        {
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };
      }

      BindingOperations.SetBinding(TheColorPicker, ColorPicker.BrushProperty, binding);
    }

    public object Create(PropertyInfo propertyInfo)
    {
      TheColorPicker = new ColorPicker
      {
        EnableSolidToGradientSwitch = false
      };

      return TheColorPicker;
    }

    public object Create(PropertyDescriptor descriotor)
    {
      TheColorPicker = new ColorPicker
      {
        EnableSolidToGradientSwitch = false
      };

      return TheColorPicker;
    }

    public void Detach(PropertyViewItem property)
    {
      if (TheColorPicker != null)
      {
        BindingOperations.ClearAllBindings(TheColorPicker);
      }

      TheColorPicker = null;
    }
  }
}

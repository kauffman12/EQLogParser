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
      Binding binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(TheColorPicker, ColorPicker.BrushProperty, binding);
    }

    public object Create(PropertyInfo propertyInfo) => Create();
    public object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var colorPicker = new ColorPicker { EnableSolidToGradientSwitch = false };
      TheColorPicker = colorPicker;
      return colorPicker;
    }

    public void Detach(PropertyViewItem property)
    {
      if (TheColorPicker!= null)
      {
        BindingOperations.ClearAllBindings(TheColorPicker);
        TheColorPicker.Dispose();
        TheColorPicker = null;
      }
    }
  }
}

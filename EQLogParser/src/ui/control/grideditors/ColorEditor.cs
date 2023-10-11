using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class ColorEditor : BaseTypeEditor
  {
    private ColorPicker TheColorPicker;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(TheColorPicker, ColorPicker.BrushProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var colorPicker = new ColorPicker { EnableSolidToGradientSwitch = false, BorderThickness = new Thickness(0, 0, 0, 0) };
      TheColorPicker = colorPicker;
      return colorPicker;
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (TheColorPicker != null)
      {
        BindingOperations.ClearAllBindings(TheColorPicker);
        TheColorPicker.Dispose();
        TheColorPicker = null;
      }
    }
  }
}

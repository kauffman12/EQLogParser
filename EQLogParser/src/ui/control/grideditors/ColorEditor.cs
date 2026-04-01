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
    private ColorPicker _theColorPicker;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(_theColorPicker, ColorPicker.BrushProperty, binding);
    }

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      if (_theColorPicker != null)
        return _theColorPicker;

      _theColorPicker = new ColorPicker
      {
        EnableSolidToGradientSwitch = false,
        Margin = new Thickness(0, 0, 2, 0),
        BorderThickness = new Thickness(0, 0, 0, 0)
      };

      return _theColorPicker;
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theColorPicker != null)
      {
        BindingOperations.ClearAllBindings(_theColorPicker);
        _theColorPicker.Dispose();
        _theColorPicker = null;
      }
    }
  }
}

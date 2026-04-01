using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class WrapTextEditor : TextBoxEditor
  {
    private TextBox _theTextBox;

    public override object Create(PropertyDescriptor descriptor)
    {
      if (_theTextBox != null)
        return _theTextBox;

      _theTextBox = base.Create(descriptor) as TextBox;
      if (_theTextBox != null)
      {
        _theTextBox.TextWrapping = TextWrapping.Wrap;
        _theTextBox.Padding = new Thickness(2);
      }

      return _theTextBox;
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theTextBox != null)
      {
        BindingOperations.ClearAllBindings(_theTextBox);
        _theTextBox = null;
      }
    }
  }
}

using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class WrapTextEditor : TextBoxEditor
  {
    private TextBox _theTextBox;

    public override object Create(PropertyInfo propertyInfo)
    {
      var textBox = base.Create(propertyInfo) as TextBox;
      if (textBox != null)
      {
        textBox.TextWrapping = TextWrapping.Wrap;
        textBox.Padding = new Thickness(0, 2, 0, 2);
        _theTextBox = textBox;
      }
      return textBox;
    }

    public override object Create(PropertyDescriptor descriptor)
    {
      var textBox = base.Create(descriptor) as TextBox;
      if (textBox != null)
      {
        textBox.TextWrapping = TextWrapping.Wrap;
        textBox.Padding = new Thickness(2);
        _theTextBox = textBox;
      }
      return textBox;
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

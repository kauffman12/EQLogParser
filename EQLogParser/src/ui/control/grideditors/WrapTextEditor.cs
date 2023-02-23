using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class WrapTextEditor : TextBoxEditor
  {
    private TextBox TheTextBox;

    public void SetForeground(string foreground)
    {
      // this only works if there's one reference to this editor...
      // TODO figure out better way
      if (TheTextBox != null)
      {
        TheTextBox.SetResourceReference(Control.ForegroundProperty, foreground);
      }
    }

    public override object Create(PropertyInfo propertyInfo)
    {
      var textBox = base.Create(propertyInfo) as TextBox;
      textBox.TextWrapping = System.Windows.TextWrapping.Wrap;
      textBox.Padding = new System.Windows.Thickness(0, 2, 0, 2);
      TheTextBox = textBox;
      return textBox;
    }

    public override object Create(PropertyDescriptor descriptor)
    {
      var textBox = base.Create(descriptor) as TextBox;
      textBox.TextWrapping = System.Windows.TextWrapping.Wrap;
      textBox.Padding = new System.Windows.Thickness(2);
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
        TheTextBox = null;
      }
    }
  }
}

using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  internal class WrapTextEditor : TextBoxEditor
  {
    private TextBox TextBox;

    public void SetForeground(string foreground)
    {
      TextBox.SetResourceReference(TextBox.ForegroundProperty, foreground);
    }

    public override object Create(PropertyInfo propertyInfo)
    {
      TextBox = base.Create(propertyInfo) as TextBox;
      TextBox.TextWrapping = System.Windows.TextWrapping.Wrap;
      TextBox.Padding = new System.Windows.Thickness(2);
      return TextBox;
    }

    public override object Create(PropertyDescriptor descriotor)
    {
      TextBox = base.Create(descriotor) as TextBox;
      TextBox.TextWrapping = System.Windows.TextWrapping.Wrap;
      TextBox.Padding = new System.Windows.Thickness(2);
      return TextBox;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (TextBox != null)
      {
        BindingOperations.ClearAllBindings(TextBox);
      }

      TextBox = null;
    }
  }
}

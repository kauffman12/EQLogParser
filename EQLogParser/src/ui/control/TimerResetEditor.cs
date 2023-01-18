using Syncfusion.Windows.PropertyGrid;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TimerResetEditor : BaseTypeEditor
  {
    private ComboBox ComboBox;
    private static List<string> Options = new List<string>() { "Start Additional Timer", "Restart Timer", "Do Nothing" };

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = BindingMode.TwoWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };
      BindingOperations.SetBinding(ComboBox, ComboBox.SelectedIndexProperty, binding);
    }

    // Create a custom editor for a normal property
    public override object Create(PropertyInfo PropertyInfo)
    {
      ComboBox = new ComboBox();
      foreach (var items in Options)
      {
        ComboBox.Items.Add(items);
      }

      ComboBox.SelectedIndex = 0;
      return ComboBox;
    }

    // Create a custom editor for a dynamic property
    public override object Create(PropertyDescriptor PropertyDescriptor)
    {
      ComboBox = new ComboBox();
      foreach (var items in Options)
      {
        ComboBox.Items.Add(items);
      }

      ComboBox.SelectedIndex = 0;
      return ComboBox;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (ComboBox != null)
      {
        BindingOperations.ClearAllBindings(ComboBox);
      }

      ComboBox = null;
    }
  }
}

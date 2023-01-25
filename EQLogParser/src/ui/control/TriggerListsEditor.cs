using Syncfusion.Windows.PropertyGrid;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerListsEditor : ITypeEditor
  {
    private ComboBox ComboBox;
    
    private static Dictionary<string, List<string>> Options = new Dictionary<string, List<string>>()
    {
      { "TriggerAgainOption", new  List<string>() { "Start Additional Timer", "Restart Timer", "Do Nothing" } },
      { "FontSize", new  List<string>() { "10pt", "11pt", "12pt", "13pt", "14pt", "15pt", "16pt", "17pt", "18pt" } },
      { "SortBy", new List<string>() { "Time Trigger", "Time Remaining" } }
    };

    private static Dictionary<string, DependencyProperty> Props = new Dictionary<string, DependencyProperty>()
    {
      { "TriggerAgainOption", ComboBox.SelectedIndexProperty },
      { "FontSize", ComboBox.SelectedValueProperty },
      { "SortBy", ComboBox.SelectedIndexProperty }
    };


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
        ComboBox.IsEnabled = false;
        binding = new Binding("Value")
        {
          Source = info,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };
      }


      BindingOperations.SetBinding(ComboBox, Props[info.Name], binding);
    }

    // Create a custom editor for a normal property
    public object Create(PropertyInfo info)
    {
      ComboBox = new ComboBox();
      foreach (var items in Options[info.Name])
      {
        ComboBox.Items.Add(items);
      }

      ComboBox.SelectedIndex = 0;
      return ComboBox;
    }

    // Create a custom editor for a dynamic property
    public object Create(PropertyDescriptor desc)
    {
      ComboBox = new ComboBox();
      foreach (var items in Options[desc.Name])
      {
        ComboBox.Items.Add(items);
      }

      ComboBox.SelectedIndex = 0;
      return ComboBox;
    }

    public void Detach(PropertyViewItem property)
    {
      if (ComboBox != null)
      {
        BindingOperations.ClearAllBindings(ComboBox);
      }

      ComboBox = null;
    }
  }
}

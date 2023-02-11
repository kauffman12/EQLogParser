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
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private ComboBox TheComboBox;

    private static Dictionary<string, List<string>> Options = new Dictionary<string, List<string>>()
    {
      { "TriggerAgainOption", new  List<string>() { "Start Additional Timer", "Restart Timer", "Do Nothing" } },
      { "FontSize", new  List<string>() { "10pt", "11pt", "12pt", "13pt", "14pt", "15pt", "16pt", "17pt",
          "18pt", "20pt", "22pt", "24pt", "26pt", "28pt", "30pt", "34pt", "38pt", "42pt", "46pt", "50pt" } },
      { "SortBy", new List<string>() { "Trigger Time", "Remaining Time" } },
      { "TimerMode", new List<string>() { "Standard", "Cooldown" } }
    };

    private static Dictionary<string, DependencyProperty> Props = new Dictionary<string, DependencyProperty>()
    {
      { "TriggerAgainOption", ComboBox.SelectedIndexProperty },
      { "FontSize", ComboBox.SelectedValueProperty },
      { "SortBy", ComboBox.SelectedIndexProperty },
      { "TimerMode", ComboBox.SelectedIndexProperty }
    };


    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      TheComboBox.IsEnabled = info.CanWrite;
      BindingOperations.SetBinding(TheComboBox, Props[info.Name], binding);
    }

    // Create a custom editor for a normal property
    public object Create(PropertyInfo info) => Create(info.Name);

    // Create a custom editor for a dynamic property
    public object Create(PropertyDescriptor desc) => Create(desc.Name);

    private object Create(string name)
    {
      var comboBox = new ComboBox();

      if (Options.ContainsKey(name))
      {
        Options[name].ForEach(item => comboBox.Items.Add(item));
      }

      comboBox.SelectedIndex = 0;
      TheComboBox = comboBox;
      return comboBox;
    }

    public void Detach(PropertyViewItem property)
    {
      if (TheComboBox != null)
      {
        BindingOperations.ClearAllBindings(TheComboBox);
        TheComboBox = null;
      }
    }
  }
}

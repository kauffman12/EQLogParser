using Syncfusion.Windows.PropertyGrid;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class TriggerListsEditor : BaseTypeEditor
  {
    private ComboBox _theComboBox;

    private static readonly Dictionary<string, List<string>> Options = new()
    {
      { "TriggerAgainOption",
        [
          "Start Additional Timer", "Restart Timer", "Restart Timer If Same Name", "Do Nothing",
          "Do Nothing If Same Name"
        ]
      },
      { "FontSize", [
          "10pt", "11pt", "12pt", "13pt", "14pt", "15pt", "16pt", "17pt",  "18pt", "20pt",
          "22pt", "24pt", "26pt", "28pt", "30pt", "34pt", "38pt", "42pt", "46pt", "50pt",
          "58pt", "66pt", "74pt", "82pt", "90pt", "98pt", "106pt", "114pt", "122pt", "130pt",
          "138pt", "146pt", "154pt", "162pt", "170pt", "178pt", "186pt", "194pt", "202pt"
        ]
      },
      { "SortBy", ["Trigger Time", "Remaining Time", "Timer Name"] },
      { "TimerMode", ["Standard", "Cooldown"] },
      { "TimerType", ["No Timer", "Countdown", "Fast Countdown", "Progress", "Looping"] },
      { "FontFamily", UiElementUtil.GetSystemFontFamilies().Select(font => font.Source).ToList() },
      { "FontWeight", UiElementUtil.GetFontWeights() },
      { "HorizontalAlignment", ["Left", "Center", "Right"]},
      { "VerticalAlignment", ["Top", "Center", "Bottom"]},
      { "Volume", [
        "Increase by 80%", "Increase by 60%", "Increase by 40%", "Increase by 20%", "Default",
        "Decrease by 20%", "Decrease by 40%", "Decrease by 60%", "Decrease by 80%"
        ]
      }
    };

    private static readonly Dictionary<string, DependencyProperty> Props = new()
    {
      { "TriggerAgainOption", Selector.SelectedIndexProperty },
      { "FontSize", Selector.SelectedValueProperty },
      { "SortBy", Selector.SelectedIndexProperty },
      { "TimerMode", Selector.SelectedIndexProperty },
      { "TimerType", Selector.SelectedIndexProperty },
      { "FontFamily", Selector.SelectedValueProperty },
      { "FontWeight", Selector.SelectedValueProperty },
      { "HorizontalAlignment", Selector.SelectedIndexProperty },
      { "VerticalAlignment", Selector.SelectedIndexProperty },
      { "Volume", Selector.SelectedIndexProperty }
    };

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      _theComboBox.IsEnabled = info.CanWrite;
      BindingOperations.SetBinding(_theComboBox, Props[info.Name], binding);
    }

    // Create a custom editor for a normal property
    public override object Create(PropertyInfo info) => Create(info.Name);

    // Create a custom editor for a dynamic property
    public override object Create(PropertyDescriptor desc) => Create(desc.Name);

    private object Create(string name)
    {
      var comboBox = new ComboBox { Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };

      if (Options.TryGetValue(name, out var option))
      {
        option.ForEach(item => comboBox.Items.Add(item));
      }

      comboBox.SelectedIndex = 0;
      _theComboBox = comboBox;
      return comboBox;
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theComboBox != null)
      {
        BindingOperations.ClearAllBindings(_theComboBox);
        _theComboBox = null;
      }
    }
  }
}

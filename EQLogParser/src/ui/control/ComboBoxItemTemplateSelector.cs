using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EQLogParser
{
  public class ComboBoxItemTemplateSelector : DataTemplateSelector
  {
    public List<DataTemplate> SelectedItemTemplates { get; } = new List<DataTemplate>();
    public List<DataTemplate> DropDownItemTemplates { get; } = new List<DataTemplate>();

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      return GetVisualParent<ComboBoxItem>(container) == null ? ChooseFrom(SelectedItemTemplates, item) : ChooseFrom(DropDownItemTemplates, item);
    }

    private static DataTemplate ChooseFrom(IEnumerable<DataTemplate> templates, object item)
    {
      DataTemplate result = null;

      if (item != null)
      {
        var targetType = item.GetType();
        result = templates.FirstOrDefault(t => (t.DataType as Type) == targetType);
      }

      return result;
    }

    private static T GetVisualParent<T>(DependencyObject child) where T : Visual
    {
      T result = null;
      while (child != null)
      {
        if (child is T found)
        {
          result = found;
          break;
        }
        child = VisualTreeHelper.GetParent(child);
      }
      return result;
    }
  }
}

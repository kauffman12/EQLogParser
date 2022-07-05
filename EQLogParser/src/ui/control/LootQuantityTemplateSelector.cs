using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public class LootQuantityTemplateSelector : DataTemplateSelector
  {
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      DataTemplate template = null;

      if (item is LootRow row)
      {
        if (row.IsCurrency)
        {
          template = Application.Current.Resources["currencyTemplate"] as DataTemplate;
        }
        else
        {
          template = Application.Current.Resources["quantityTemplate"] as DataTemplate;
        }
      }

      return template;
    }
  }
}

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
        if (row.Record.IsCurrency)
        {
          template = Application.Current.Resources["CurrencyTemplate"] as DataTemplate;
        }
        else
        {
          template = Application.Current.Resources["QuantityTemplate"] as DataTemplate;
        }
      }

      return template;
    }
  }
}

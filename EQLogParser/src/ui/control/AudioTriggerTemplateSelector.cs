using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  internal class AudioTriggerTemplateSelector : DataTemplateSelector
  {
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      if (item is AudioTriggerTreeViewNode node)
      {
        if (node.IsTrigger)
        {
          return Application.Current.Resources["AudioTriggerFileTemplate"] as DataTemplate;
        }
        else
        {
          return Application.Current.Resources["AudioTriggerNodeTemplate"] as DataTemplate;
        }
      }

      return null;
    }
  }
}

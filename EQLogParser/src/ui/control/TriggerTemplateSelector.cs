using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  internal class TriggerTemplateSelector : DataTemplateSelector
  {
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      if (item is TriggerTreeViewNode node)
      {
        if (node.IsTrigger)
        {
          return Application.Current.Resources["TriggerFileTemplate"] as DataTemplate;
        }
        else if (node.IsOverlay)
        {
          if (node.SerializedData?.OverlayData?.IsTimerOverlay == true)
          {
            return Application.Current.Resources["TimerOverlayFileTemplate"] as DataTemplate;
          }
          else
          {
            return Application.Current.Resources["TextOverlayFileTemplate"] as DataTemplate;
          }
        }
        else
        {
          return Application.Current.Resources["TriggerNodeTemplate"] as DataTemplate;
        }
      }

      return null;
    }
  }
}

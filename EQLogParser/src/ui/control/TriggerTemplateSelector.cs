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
        if (node.IsTrigger())
        {
          return Application.Current.Resources["TriggerFileTemplate"] as DataTemplate;
        }

        if (node.IsOverlay())
        {
          if (node.SerializedData?.OverlayData?.IsTimerOverlay == true)
          {
            return Application.Current.Resources["TimerOverlayFileTemplate"] as DataTemplate;
          }

          return Application.Current.Resources["TextOverlayFileTemplate"] as DataTemplate;
        }

        if (TriggerStateManager.Overlays.Equals(node.Content?.ToString()))
        {
          return Application.Current.Resources["OverlayNodeTemplate"] as DataTemplate;
        }

        return Application.Current.Resources["TriggerNodeTemplate"] as DataTemplate;
      }

      return null;
    }
  }
}

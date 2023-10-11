using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  internal class TriggerTemplateSelector : DataTemplateSelector
  {
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      switch (item)
      {
        case TriggerTreeViewNode node when node.IsTrigger():
          {
            return Application.Current.Resources["TriggerFileTemplate"] as DataTemplate;
          }
        case TriggerTreeViewNode node when node.IsOverlay():
          {
            if (node.SerializedData?.OverlayData?.IsTimerOverlay == true)
            {
              return Application.Current.Resources["TimerOverlayFileTemplate"] as DataTemplate;
            }

            return Application.Current.Resources["TextOverlayFileTemplate"] as DataTemplate;
          }
        case TriggerTreeViewNode node when TriggerStateManager.OVERLAYS.Equals(node.Content?.ToString()):
          {
            return Application.Current.Resources["OverlayNodeTemplate"] as DataTemplate;
          }
      }
      return null;
    }
  }
}

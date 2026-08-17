using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /* Picks folder vs character DataTemplate for the Manage Characters SfTreeView. */
  internal class CharacterTemplateSelector : DataTemplateSelector
  {
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      if (item is CharacterTreeViewNode node)
      {
        if (node.IsFolder())
        {
          return Application.Current.Resources["CharacterFolderTemplate"] as DataTemplate;
        }

        return Application.Current.Resources["CharacterNodeTemplate"] as DataTemplate;
      }

      return null;
    }
  }
}

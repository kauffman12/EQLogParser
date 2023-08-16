using Syncfusion.Windows.PropertyGrid;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  internal static class PropertyGridUtil
  {
    internal static void EnableCategories(PropertyGrid propertyGrid, dynamic[] settings)
    {
      foreach (var item in propertyGrid.Items)
      {
        var found = settings.ToList().Find(setting => setting.Name == item.CategoryName);
        if (found != null)
        {
          item.Visibility = found.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
      }

      propertyGrid.RefreshPropertygrid();
    }

    internal static PropertyItem FindProperty(List<object> list, string name)
    {
      foreach (var prop in list)
      {
        if (prop is PropertyItem item)
        {
          if (item.Name == name)
          {
            return item;
          }
        }
        else if (prop is PropertyCategoryViewItemCollection sub && FindProperty(sub.Properties.ToList(), name) is PropertyItem found)
        {
          return found;
        }
      }

      return null;
    }
  }
}

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
        var categoryName = item.CategoryName as string;
        if (string.IsNullOrEmpty(categoryName))
          continue;

        var found = settings.FirstOrDefault(setting => setting.Name == categoryName);
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
        if (prop is PropertyItem item && item.Name == name)
        {
          return item;
        }

        if (prop is PropertyCategoryViewItemCollection sub && FindProperty(sub.Properties.ToList(), name) is { } found)
        {
          return found;
        }
      }

      return null;
    }
  }
}

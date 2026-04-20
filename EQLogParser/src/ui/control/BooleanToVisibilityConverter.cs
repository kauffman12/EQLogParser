using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  public class BooleanToVisibilityConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      var flag = value is bool b && b;

      if (parameter is string s &&
          s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
      {
        flag = !flag;
      }

      return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      var isVisible = value is Visibility visibility && visibility == Visibility.Visible;

      if (parameter is string s &&
          s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
      {
        isVisible = !isVisible;
      }

      return isVisible;
    }
  }
}
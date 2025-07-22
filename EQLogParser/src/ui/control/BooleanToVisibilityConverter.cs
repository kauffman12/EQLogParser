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
      var flag = (bool)value;
      return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      return (Visibility)value == Visibility.Visible;
    }
  }
}

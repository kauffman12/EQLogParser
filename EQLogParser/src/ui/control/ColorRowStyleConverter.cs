using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  internal class ColorRowStyleConverter : IValueConverter
  {
    object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is Fight { BeginTimeString: Fight.BREAKTIME })
      {
        return Application.Current.Resources["EQWarnBackgroundBrush"] as SolidColorBrush;
      }
      return null;
    }

    object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
  }
}

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  internal class FightRowStyleConverter : IValueConverter
  {
    private static readonly SolidColorBrush BREAK_TIME_BRUSH = Application.Current.Resources["warnBackgroundBrush"] as SolidColorBrush;

    object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is Fight npc && npc.BeginTimeString == Fight.BREAKTIME)
      {
        return BREAK_TIME_BRUSH;
      }
      return null;
    }

    object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
  }
}

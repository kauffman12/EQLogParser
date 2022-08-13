using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  public class ReceivedSpellColorConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is string spellName && spellName.StartsWith("Received ", StringComparison.Ordinal))
      {
        return Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
      }
      else
      {
        return Application.Current.Resources["ContentForeground"] as SolidColorBrush;
      }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      throw new NotSupportedException();
    }
  }
}

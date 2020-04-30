using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  public class ReceivedSpellColorConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      object result;

      if (value is string spellName && spellName.StartsWith("Received ", StringComparison.Ordinal))
      {
        result = Brushes.LightGreen;
      }
      else
      {
        result = Brushes.White;
      }
      return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      throw new NotSupportedException();
    }
  }
}

using System;
using System.Globalization;
using System.Windows.Data;

namespace EQLogParser
{
  public class TotalFormatConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value?.GetType() == typeof(long) && (long)value > 0)
      {
        return StatsUtil.FormatTotals((long)value, 0);
      }
      return "-";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is string)
      {
        if (!long.TryParse((string)value, out long decValue))
        {
          decValue = 0;
        }
        return decValue;
      }
      return 0;
    }
  }
}

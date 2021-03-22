using System;
using System.Globalization;
using System.Windows.Data;

namespace EQLogParser
{
  public class NumberFormatConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      return $"{value:n0}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is string)
      {
        if (!double.TryParse((string)value, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double decValue))
        {
          decValue = 0;
        }
        return decValue;
      }
      return 0;
    }
  }
}

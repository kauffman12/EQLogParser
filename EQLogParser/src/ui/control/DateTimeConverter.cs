using System;
using System.Globalization;
using System.Windows.Data;

namespace EQLogParser
{
  public class DateTimeConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value?.GetType() == typeof(double))
      {
        return DateUtil.FormatSimpleDate(System.Convert.ToDouble(value, CultureInfo.CurrentCulture));
      }
      return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      double result = 0;
      if (value is string s)
      {
        result = DateUtil.ParseSimpleDate(s);
      }
      return result;
    }
  }
}

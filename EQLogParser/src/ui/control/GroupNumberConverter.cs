using System;
using System.Globalization;
using System.Windows.Data;

namespace EQLogParser
{
 public class GroupNumberConverter : IValueConverter
   {
     public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
     {
       if (value is int i)
       {
         return i == 0 ? string.Empty : i.ToString();
       }
       return string.Empty;
     }

     public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
     {
       if (value is string s)
       {
         if (string.IsNullOrEmpty(s))
           return 0;
         if (int.TryParse(s, out var result))
           return result;
       }
       return 0;
     }
  }
}

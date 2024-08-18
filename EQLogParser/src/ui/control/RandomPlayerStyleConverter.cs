using System;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  internal class RandomPlayerStyleConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
      if (value is RandomRow row)
      {
        try
        {
          if (!string.IsNullOrEmpty(row.Highest) && row.Winners.Contains(ConfigUtil.PlayerName))
          {
            return Application.Current.Resources["EQSearchBackgroundBrush"];
          }
        }
        catch (Exception)
        {
          // do nothing
        }
      }

      return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
      throw new NotImplementedException();
    }
  }
}

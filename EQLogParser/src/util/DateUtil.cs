using System;
using System.Globalization;

namespace EQLogParser
{
  internal class DateUtil
  {
    // counting this thing is really slow
    private string LastDateTimeString = null;
    private double LastDateTime;
    private double increment = 0.0;

    internal static string FormatDate(double seconds)
    {
      var dateTime = new DateTime().AddSeconds(seconds);
      return dateTime.ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    internal static double ToDouble(DateTime dateTime)
    {
      return dateTime.Ticks / TimeSpan.FromSeconds(1).Ticks;
    }

    internal bool HasTimeInRange(double now, string line, int lastMins)
    {
      bool found = false;
      if (line.Length > 24)
      {
        double dateTime = ParseDate(line.Substring(1, 24), out double precise);
        if (!double.IsNaN(dateTime))
        {
          TimeSpan diff = TimeSpan.FromSeconds(now - dateTime);
          found = (diff.TotalMinutes < lastMins);
        }
      }
      return found;
    }

    internal double ParseDate(string timeString, out double precise)
    {
      double result = double.NaN;

      if (LastDateTimeString == timeString)
      {
        increment += 0.001;
        precise = LastDateTime + increment;
        return LastDateTime;
      }

      increment = 0.0;
      DateTime.TryParseExact(timeString, "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime dateTime);
      if (dateTime == DateTime.MinValue)
      {
        DateTime.TryParseExact(timeString, "ddd MMM  d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
      }

      if (dateTime == DateTime.MinValue)
      {
        LastDateTime = double.NaN;
      }
      else
      {
        result = LastDateTime = ToDouble(dateTime);
      }

      LastDateTimeString = timeString;
      precise = result;
      return result;
    }
  }
}

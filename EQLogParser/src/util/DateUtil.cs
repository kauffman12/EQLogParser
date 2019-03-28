using System;
using System.Globalization;

namespace EQLogParser
{
  internal class DateUtil
  {
    // counting this thing is really slow
    private string LastDateTimeString = "";
    private double LastDateTime;

    internal bool HasTimeInRange(double now, string line, int lastMins)
    {
      bool found = false;
      if (line.Length > 24)
      {
        double dateTime = ParseDate(line.Substring(1, 24));
        TimeSpan diff = TimeSpan.FromSeconds(now - dateTime);
        found = (diff.TotalMinutes < lastMins);
      }
      return found;
    }

    internal double ParseDate(string timeString)
    {
      double result = double.NaN;

      if (LastDateTimeString == timeString)
      {
        return LastDateTime;
      }

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
        result = LastDateTime = dateTime.Ticks / TimeSpan.FromSeconds(1).Ticks;
      }

      LastDateTimeString = timeString;
      return result;
    }
  }
}

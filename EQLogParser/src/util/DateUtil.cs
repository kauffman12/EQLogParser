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

    internal static string GetCurrentDate(string format) => DateTime.Now.ToString(format, CultureInfo.InvariantCulture);

    internal static string FormatSimpleDate(double seconds) => new DateTime().AddSeconds(seconds).ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string FormatSimpleTime(double seconds) => new DateTime().AddSeconds(seconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string FormatGeneralTime(double seconds)
    {
      TimeSpan diff = TimeSpan.FromSeconds(seconds);
      string result = "";

      if (diff.Days >= 1)
      {
        switch (diff.Days)
        {
          case 1:
            result += diff.Days + " day";
            break;
          default:
            result += diff.Days + " days";
            break;
        }
      }
      else if (diff.Hours >= 1)
      {
        switch (diff.Hours)
        {
          case 1:
            result += diff.Hours + " hour";
            break;
          default:
            result += diff.Hours + " hours";
            break;
        }
      }
      else if (diff.Minutes >= 1)
      {
        switch (diff.Minutes)
        {
          case 1:
            result += diff.Minutes + " minute";
            break;
          default:
            result += diff.Minutes + " minutes";
            break;
        }
      }

      return result;
    }

    internal static double ParseSimpleDate(string timeString)
    {
      double result = 0;
      if (!string.IsNullOrEmpty(timeString) && DateTime.TryParseExact(timeString, "MMM dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime dateTime))
      {
        result = ToDouble(dateTime);
      }

      return result;
    }

    internal static double ToDouble(DateTime dateTime) => dateTime.Ticks / TimeSpan.FromSeconds(1).Ticks;

    internal bool HasTimeInRange(double now, string line, int lastMins)
    {
      bool found = false;
      if (line.Length > 24)
      {
        double dateTime = ParseDate(line.Substring(1, 24));
        if (!double.IsNaN(dateTime))
        {
          TimeSpan diff = TimeSpan.FromSeconds(now - dateTime);
          found = (diff.TotalMinutes < lastMins);
        }
      }
      return found;
    }

    internal double ParseLogDate(string line, out string timeString)
    {
      timeString = line.Substring(1, 24);
      return ParseDate(timeString);
    }

    internal double ParseDate(string timeString) => ParseDate(timeString, out double _);

    internal double ParsePreciseDate(string timeString)
    {
      ParseDate(timeString, out double precise);
      return precise;
    }

    private double ParseDate(string timeString, out double precise)
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

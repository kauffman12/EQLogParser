using log4net;
using System;
using System.Globalization;
using System.Reflection;

namespace EQLogParser
{
  internal class DateUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    // counting this thing is really slow
    private string LastDateTimeString;
    private double LastDateTime;
    private double increment;

    internal static double ToDouble(DateTime dateTime) => dateTime.Ticks / TimeSpan.FromSeconds(1).Ticks;
    internal static DateTime FromDouble(double value) => new((long)value * TimeSpan.FromSeconds(1).Ticks);
    internal static string GetCurrentDate(string format) => DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
    internal static string FormatDate(double seconds) => new DateTime().AddSeconds(seconds).ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);
    internal static string FormatSimpleDate(double seconds) => new DateTime().AddSeconds(seconds).ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);
    internal static string FormatSimpleHMS(double seconds) => new DateTime().AddSeconds(seconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string FormatSimpleMS(long ticks)
    {
      return new DateTime(ticks < 0 ? 0 : ticks).ToString("mm:ss", CultureInfo.InvariantCulture);
    }

    internal static string FormatSimpleMillis(long ticks)
    {
      return new DateTime(ticks < 0 ? 0 : ticks).ToString("s.fff", CultureInfo.InvariantCulture);
    }

    internal static string FormatGeneralTime(double seconds, bool showSeconds = false)
    {
      var diff = TimeSpan.FromSeconds(seconds);
      var result = "";

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
      else if (showSeconds && diff.Seconds >= 1)
      {
        switch (diff.Seconds)
        {
          case 1:
            result += diff.Seconds + " second";
            break;
          default:
            result += diff.Seconds + " seconds";
            break;
        }
      }

      return result;
    }

    internal static uint SimpleTimeToSeconds(string source)
    {
      if (string.IsNullOrEmpty(source))
      {
        return 0;
      }

      uint h = 0, m = 0, s = 0;

      var split = source.Split(':');

      if (split.Length == 0 || split.Length > 3)
      {
        return 0;
      }

      if (split.Length == 1)
      {
        s = StatsUtil.ParseUInt(split[0], 0);
      }
      else if (split.Length == 2)
      {
        m = StatsUtil.ParseUInt(split[0], 0);
        s = StatsUtil.ParseUInt(split[1], 0);

        if (s > 59 || m > 59)
        {
          return 0;
        }
      }
      else if (split.Length == 3)
      {
        h = StatsUtil.ParseUInt(split[0], 0);
        m = StatsUtil.ParseUInt(split[1], 0);
        s = StatsUtil.ParseUInt(split[2], 0);

        if (s > 59 || m > 59 || h > 23)
        {
          return 0;
        }
      }

      // Convert to total seconds
      return s + (m * 60) + (h * 60 * 60);
    }

    // This doesn't currently get called so test if ever needed
    internal static double ParseSimpleDate(string timeString)
    {
      double result = 0;
      if (!string.IsNullOrEmpty(timeString))
      {
        var dateTime = CustomDateTimeParser("MMM dd HH:mm:ss", timeString);
        if (dateTime != DateTime.MinValue)
        {
          result = ToDouble(dateTime);
        }
      }

      return result;
    }

    internal double ParseDate(string timeString) => ParseDateTime(timeString, out var _);

    internal double ParsePreciseDate(string timeString)
    {
      ParseDateTime(timeString, out var precise);
      return precise;
    }

    private double ParseDateTime(string timeString, out double precise)
    {
      var result = double.NaN;

      if (LastDateTimeString == timeString)
      {
        increment += 0.001;
        precise = LastDateTime + increment;
        return LastDateTime;
      }

      increment = 0.0;
      var dateTime = ParseStandardDate(timeString);

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

      if (double.IsNaN(result))
      {
        Log.Debug("Invalid Date: " + timeString);
      }

      return result;
    }

    internal static double StandardDateToDouble(string source) => ToDouble(ParseStandardDate(source));
    internal static DateTime ParseStandardDate(string source) => CustomDateTimeParser("MMM dd HH:mm:ss yyyy", source, 5);

    internal static DateTime CustomDateTimeParser(string dateFormat, string source, int offset = 0)
    {
      var year = 0;
      var month = 0;
      var day = 0;
      var hour = 0;
      var minute = 0;
      var second = 0;
      var letterMonth = 0;

      if (source.Length - offset >= dateFormat.Length)
      {
        for (var i = 0; i < dateFormat.Length; i++)
        {
          var c = source[offset + i];
          switch (dateFormat[i])
          {
            case 'y':
              year = (year * 10) + (c - '0');
              break;
            case 'M':
              if (char.IsLetter(c))
              {
                if (++letterMonth == 3)
                {
                  var cur = offset + i;
                  switch (source[cur - 2])
                  {
                    case 'J':
                    case 'j':
                      month = (source[cur - 1] == 'a') ? 1 : (source[cur] == 'n') ? 6 : 7;
                      break;
                    case 'F':
                    case 'f':
                      month = 2;
                      break;
                    case 'M':
                    case 'm':
                      month = (source[cur] == 'r') ? 3 : 5;
                      break;
                    case 'A':
                    case 'a':
                      month = (source[cur - 1] == 'p') ? 4 : 8;
                      break;
                    case 'S':
                    case 's':
                      month = 9;
                      break;
                    case 'O':
                    case 'o':
                      month = 10;
                      break;
                    case 'N':
                    case 'n':
                      month = 11;
                      break;
                    case 'D':
                    case 'd':
                      month = 12;
                      break;
                  }
                }
              }
              else
              {
                month = (month * 10) + (c - '0');
              }
              break;
            case 'd':
              if (char.IsDigit(c))
              {
                day = (day * 10) + (c - '0');
              }
              break;
            case 'H':
              hour = (hour * 10) + (c - '0');
              break;
            case 'm':
              minute = (minute * 10) + (c - '0');
              break;
            case 's':
              second = (second * 10) + (c - '0');
              break;
          }
        }
      }

      var result = DateTime.MinValue;

      if (year > 0 && month is > 0 and < 13 && day is > 0 and < 32 && hour is >= 0 and < 24 && minute is >= 0 and < 60 && second is >= 0 and < 60)
      {
        try
        {
          result = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        }
        catch (ArgumentException)
        {
          // do nothing
        }
      }

      return result;
    }
  }
}

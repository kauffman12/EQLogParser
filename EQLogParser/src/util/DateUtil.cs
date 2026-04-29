using log4net;
using System;
using System.Globalization;
using System.Reflection;

namespace EQLogParser
{
  internal class DateUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // counting this thing is really slow
    private string _lastDateTimeString;
    private double _lastDateTime;
    private double _increment;

    // ReSharper disable once PossibleLossOfFraction
    internal static double ToDotNetSeconds(DateTime dateTime) => dateTime.Ticks / TimeSpan.TicksPerSecond;
    internal static double TicksToDotNetSeconds(long ticks) => ticks / TimeSpan.TicksPerSecond;
    internal static DateTime FromDotNetSeconds(double value) => new((long)value * TimeSpan.TicksPerSecond);
    internal static string GetCurrentDate(string format) => DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
    internal static string FormatDotNetDateSeconds(double seconds) => new DateTime().AddSeconds(seconds).ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);
    internal static string FormatDotNetTimeSeconds(double seconds) => new DateTime().AddSeconds(seconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    internal static double StandardDateToDotNetSeconds(string source) => ToDotNetSeconds(ParseStandardDate(source));
    internal static DateTime ParseStandardDate(string source) => CustomDateTimeParser("MMM dd HH:mm:ss yyyy", source, 5);

    internal enum TimeFormat
    {
      SecondsMs,     // ss.mmm (seconds and milliseconds)
      HMSCompact,    // mm:ss or hh:mm:ss (omits hours if zero)
      HMSMsCompact  // mm:ss.mmm or hh:mm:ss.mmm (omits hours if zero)
    }

    internal static string FormatTicks(long ticks, TimeFormat format)
    {
      if (ticks < 0) ticks = 0;

      switch (format)
      {
        case TimeFormat.SecondsMs:
          var totalMillisecondsPrecise = (long)Math.Round((double)ticks / TimeSpan.TicksPerMillisecond);
          var secondsPrecise = totalMillisecondsPrecise / 1000 % 60;
          var millisecondsPrecise = totalMillisecondsPrecise % 1000;
          return $"{secondsPrecise:D2}.{millisecondsPrecise:D3}";

        case TimeFormat.HMSCompact:
          var totalSecondsElapsed = (long)Math.Round((double)ticks / TimeSpan.TicksPerSecond);
          var hoursElapsed = totalSecondsElapsed / 3600;
          var minutesElapsed = totalSecondsElapsed % 3600 / 60;
          var secondsElapsed = totalSecondsElapsed % 60;

          if (hoursElapsed > 0)
            return $"{hoursElapsed:D2}:{minutesElapsed:D2}:{secondsElapsed:D2}";
          else
            return $"{minutesElapsed:D2}:{secondsElapsed:D2}";

        case TimeFormat.HMSMsCompact:
          var totalMillisecondsComplete = (long)Math.Round((double)ticks / TimeSpan.TicksPerMillisecond);
          var hoursComplete = totalMillisecondsComplete / (3600 * 1000);
          var minutesComplete = totalMillisecondsComplete % (3600 * 1000) / (60 * 1000);
          var secondsComplete = totalMillisecondsComplete % (60 * 1000) / 1000;
          var millisecondsComplete = totalMillisecondsComplete % 1000;

          if (hoursComplete > 0)
            return $"{hoursComplete:D2}:{minutesComplete:D2}:{secondsComplete:D2}.{millisecondsComplete:D3}";
          else
            return $"{minutesComplete:D2}:{secondsComplete:D2}.{millisecondsComplete:D3}";
      }

      return string.Empty; // Unreachable due to exhaustive enum cases
    }

    internal static string FormatGeneralTime(double seconds, bool showSeconds = false)
    {
      var diff = TimeSpan.FromSeconds(seconds);
      var result = "";

      if (diff.Days >= 1)
      {
        result += diff.Days switch
        {
          1 => diff.Days + " day",
          _ => diff.Days + " days",
        };
      }
      else if (diff.Hours >= 1)
      {
        result += diff.Hours switch
        {
          1 => diff.Hours + " hour",
          _ => diff.Hours + " hours",
        };
      }
      else if (diff.Minutes >= 1)
      {
        result += diff.Minutes switch
        {
          1 => diff.Minutes + " minute",
          _ => diff.Minutes + " minutes",
        };
      }
      else if (showSeconds && diff.Seconds >= 1)
      {
        result += diff.Seconds switch
        {
          1 => diff.Seconds + " second",
          _ => diff.Seconds + " seconds"
        };
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

      if (split.Length is 0 or > 3)
      {
        return 0;
      }

      if (split.Length == 1)
      {
        s = TextUtils.ParseUInt(split[0], 0);
      }
      else if (split.Length == 2)
      {
        m = TextUtils.ParseUInt(split[0], 0);
        s = TextUtils.ParseUInt(split[1], 0);

        if (s > 59 || m > 59)
        {
          return 0;
        }
      }
      else if (split.Length == 3)
      {
        h = TextUtils.ParseUInt(split[0], 0);
        m = TextUtils.ParseUInt(split[1], 0);
        s = TextUtils.ParseUInt(split[2], 0);

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
          result = ToDotNetSeconds(dateTime);
        }
      }

      return result;
    }

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

    internal double ParseDate(string timeString) => ParseDateTime(timeString, out _);

    internal double ParsePreciseDate(string timeString)
    {
      ParseDateTime(timeString, out var precise);
      return precise;
    }

    private double ParseDateTime(string timeString, out double precise)
    {
      var result = double.NaN;

      if (_lastDateTimeString == timeString)
      {
        _increment += 0.001;
        precise = _lastDateTime + _increment;
        return _lastDateTime;
      }

      _increment = 0.0;
      var dateTime = ParseStandardDate(timeString);

      if (dateTime == DateTime.MinValue)
      {
        _lastDateTime = double.NaN;
      }
      else
      {
        result = _lastDateTime = ToDotNetSeconds(dateTime);
      }

      _lastDateTimeString = timeString;
      precise = result;

      if (double.IsNaN(result))
      {
        Log.Debug("Invalid Date: " + timeString);
      }

      return result;
    }
  }
}

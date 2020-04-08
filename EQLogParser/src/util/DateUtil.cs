using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  internal class DateUtil
  {
    // counting this thing is really slow
    private string LastDateTimeString = null;
    private double LastDateTime;
    private double increment = 0.0;

    internal static string GetCurrentDate(string format)
    {
      return DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
    }

    internal static string FormatSimpleDate(double seconds)
    {
      var dateTime = new DateTime().AddSeconds(seconds);
      return dateTime.ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    internal static string FormatSimpleTime(double seconds)
    {
      var dateTime = new DateTime().AddSeconds(seconds);
      return dateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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

    internal static double ToDouble(DateTime dateTime)
    {
      return dateTime.Ticks / TimeSpan.FromSeconds(1).Ticks;
    }

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

    internal double ParseLogDate(string line)
    {
      return ParseDate(line.Substring(1, 24));
    }

    internal double ParseDate(string timeString)
    {
      return ParseDate(timeString, out double _);
    }

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

  public class TimeRange
  {
    private List<TimeSegment> TimeSegments = new List<TimeSegment>();

    public double GetTotal() => TimeSegments.Sum(segment => segment.End - segment.Begin + 1);

    public void Add(TimeSegment segment)
    {
      if (TimeSegments.Count == 0)
      {
        TimeSegments.Add(segment);
      }
      else
      {
        int i, action = -1, last = -1, exists = -1;
        for (i = 0; i < TimeSegments.Count; i++)
        {
          if (TimeSegments[i].Equals(segment))
          {
            exists = i;
          }
          else
          {
            action = TimeSegments[i].CompareTo(segment);
            if (action == 0 || (action == 1 && i + i == TimeSegments.Count) || (action == -1 && last == 1))
            {
              break;
            }
            else if (action == 1)
            {
              last = 1;
            }
          }
        }

        if (action == 0 && !(TimeSegments[i].Begin <= segment.Begin && TimeSegments[i].End >= segment.End))
        {
          TimeSegments[i].Begin = Math.Min(TimeSegments[i].Begin, segment.Begin);
          TimeSegments[i].End = Math.Max(TimeSegments[i].End, segment.End);

          if (exists > -1)
          {
            TimeSegments.RemoveAt(exists);
          }
          else
          {
            Add(TimeSegments[i]);
          }
        }
        else if (action == 1 && exists == -1)
        {
          if (i - 1 is int previous && previous >= 0)
          {
            if (TimeSegments[previous].End + 1 == segment.Begin)
            {
              TimeSegments[previous].End = segment.End;
            }
            else
            {
              TimeSegments.Insert(i, segment);
            }
          }
          else if (TimeSegments[i].End + 1 == segment.Begin)
          {
            TimeSegments[i].End = segment.End;
          }
          else
          {
            TimeSegments.Add(segment);
          }
        }
        else if (action == -1 && last == 1)
        {
          if (i < TimeSegments.Count)
          {
            if (TimeSegments[i].Begin - 1 == segment.End)
            {
              TimeSegments[i].Begin = segment.Begin;
            }
            else
            {
              TimeSegments.Insert(i, segment);
            }
          }
          else if (segment.End + 1 == TimeSegments[i].Begin)
          {
            TimeSegments[i].Begin = segment.Begin;
          }
          else
          {
            TimeSegments.Insert(i, segment);
          }
        }
        else if (action == -1 && exists == -1)
        {
          TimeSegments.Insert(0, segment);
        }
      }
    }
  }

  public class TimeSegment
  {
    public double Begin { get; set; }
    public double End { get; set;  }

    public TimeSegment(double begin, double end)
    {
      Begin = begin;
      End = end;
    }

    public double GetTotal() => End - Begin + 1;

    public int CompareTo(TimeSegment value) => (value.End < Begin) ? -1 : (value.Begin > End) ? 1 : 0;

    public bool Equals(TimeSegment value) => value.Begin == Begin && value.End == End;
  }
}

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
    private const int OFFSET = 6;
    public List<TimeSegment> TimeSegments { get; } = new List<TimeSegment>();

    public double GetTotal()
    {
      var additional = new List<TimeSegment>();

      for (int i=0; i<TimeSegments.Count; i++)
      {
        if (i + 1 is int next && next < TimeSegments.Count)
        {
          if (TimeSegments[i].EndTime + OFFSET >= TimeSegments[next].BeginTime)
          {
            additional.Add(new TimeSegment(TimeSegments[i].EndTime, TimeSegments[next].BeginTime));
          }
        }
      }

      additional.ForEach(segment => Add(segment));
      return TimeSegments.Sum(segment => segment.GetTotal());
    }

    public void Add(List<TimeSegment> list) => list?.ForEach(segment => Add(segment));

    public TimeRange() { }

    public TimeRange(TimeSegment segment)
    {
      TimeSegments.Add(segment);
    }

    public void Add(TimeSegment segment)
    {
      if (segment?.BeginTime <= segment?.EndTime)
      {
        if (TimeSegments.Count == 0)
        {
          TimeSegments.Add(segment);
        }
        else
        {
          int leftIndex = -1;
          int rightIndex = -1;
          bool handled = false;

          for (int i = 0; i < TimeSegments.Count; i++)
          {
            if (TimeSegments[i].Equals(segment))
            {
              handled = true;
              break;
            }

            if (IsSurrounding(TimeSegments[i], segment))
            {
              TimeSegments[i].BeginTime = segment.BeginTime;
              TimeSegments[i].EndTime = segment.EndTime;
              CollapseLeft(i);
              CollapseRight(i);
              handled = true;
              break;
            }

            if (IsWithin(TimeSegments[i], segment.BeginTime))
            {
              if (segment.EndTime > TimeSegments[i].EndTime)
              {
                TimeSegments[i].EndTime = segment.EndTime;
                CollapseRight(i);
              }

              handled = true;
              break;
            }

            if (IsWithin(TimeSegments[i], segment.EndTime))
            {
              if (segment.BeginTime < TimeSegments[i].BeginTime)
              {
                TimeSegments[i].BeginTime = segment.BeginTime;
                CollapseLeft(i);
              }

              handled = true;
              break;
            }

            if (IsLeftOf(segment, TimeSegments[i].BeginTime))
            {
              leftIndex = i;
              break;
            }
            else if (IsRightOf(segment, TimeSegments[i].EndTime))
            {
              rightIndex = i;
            }
          }

          if (!handled)
          {
            if (rightIndex + 1 is int newIndex && newIndex >= 1)
            {
              if (TimeSegments.Count > newIndex)
              {
                TimeSegments.Insert(newIndex, segment);
              }
              else
              {
                TimeSegments.Add(segment);
              }
            }
            else if (leftIndex >= 0)
            {
              TimeSegments.Insert(leftIndex, segment);
            }
          }
        }
      }
    }
    private void CollapseLeft(int index)
    {
      if (index - 1 is int prev && prev >= 0)
      {
        if (TimeSegments[index].BeginTime <= TimeSegments[prev].EndTime)
        {
          TimeSegments[index].BeginTime = TimeSegments[prev].BeginTime;
          TimeSegments.RemoveAt(prev);
          CollapseLeft(prev);
        }
      }
    }

    private void CollapseRight(int index)
    {
      if (index + 1 is int next && next < TimeSegments.Count)
      {
        if (TimeSegments[index].EndTime >= TimeSegments[next].BeginTime)
        {
          TimeSegments[index].EndTime = TimeSegments[next].EndTime;
          TimeSegments.RemoveAt(next);
          CollapseRight(index);
        }
      }
    }

    private static bool IsLeftOf(TimeSegment one, double value) => value > one.BeginTime && value > one.EndTime;
    private static bool IsRightOf(TimeSegment one, double value) => value < one.BeginTime && value < one.EndTime;
    private static bool IsSurrounding(TimeSegment one, TimeSegment two) => two.BeginTime <= one.BeginTime && two.EndTime >= one.EndTime;
    private static bool IsWithin(TimeSegment one, double value) => value >= one.BeginTime && value <= one.EndTime;
  }

  public class TimeSegment
  {
    public double BeginTime { get; set; }
    public double EndTime { get; set; }

    public TimeSegment(double begin, double end)
    {
      BeginTime = begin;
      EndTime = end;
    }

    public bool Equals(TimeSegment check) => check?.BeginTime == BeginTime && check?.EndTime == EndTime; 
    public double GetTotal() => EndTime - BeginTime + 1;
  }
}

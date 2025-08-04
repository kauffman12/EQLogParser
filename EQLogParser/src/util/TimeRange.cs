using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  public class TimeRange
  {
    private const int Offset = 6;
    public List<TimeSegment> TimeSegments { get; } = [];

    public double GetTotal()
    {
      var additional = new List<TimeSegment>();

      for (var i = 0; i < TimeSegments.Count; i++)
      {
        if (i + 1 is var next && next < TimeSegments.Count)
        {
          if (TimeSegments[i].EndTime + Offset >= TimeSegments[next].BeginTime)
          {
            additional.Add(new TimeSegment(TimeSegments[i].EndTime, TimeSegments[next].BeginTime));
          }
        }
      }

      foreach (var segment in CollectionsMarshal.AsSpan(additional))
      {
        Add(segment);
      }

      return TimeSegments.Sum(segment => segment.Total);
    }

    public void Add(List<TimeSegment> collection)
    {
      if (collection != null)
      {
        foreach (var segment in CollectionsMarshal.AsSpan(collection))
        {
          Add(segment);
        }
      }
    }

    public TimeRange() { }

    public TimeRange(TimeSegment segment)
    {
      TimeSegments.Add(segment);
    }

    public TimeRange(List<TimeSegment> segments)
    {
      segments.ForEach(segment => Add(new TimeSegment(segment.BeginTime, segment.EndTime)));
    }

    public void Add(TimeSegment segment)
    {
      if (segment != null && segment.BeginTime <= segment.EndTime)
      {
        if (TimeSegments.Count == 0)
        {
          TimeSegments.Add(segment);
        }
        else
        {
          var leftIndex = -1;
          var rightIndex = -1;
          var handled = false;
          for (var i = 0; i < TimeSegments.Count; i++)
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

            if (IsRightOf(segment, TimeSegments[i].EndTime))
            {
              rightIndex = i;
            }
          }

          if (!handled)
          {
            if (rightIndex + 1 is var newIndex and >= 1)
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

    internal static bool TimeCheck(string line, double start, double end = -1)
    {
      var pass = false;
      if (!string.IsNullOrEmpty(line) && line.Length > 24)
      {
        var logTime = DateUtil.StandardDateToDouble(line);
        if (!double.IsNaN(logTime))
        {
          if (end > -1)
          {
            pass = logTime >= start && logTime <= end;
          }
          else
          {
            pass = start > 0 && logTime >= start;
          }
        }
      }

      return pass;
    }

    internal static bool TimeCheck(string line, double start, TimeRange range, out bool exceeds)
    {
      exceeds = false;

      // this is any time?
      if (start == 0)
      {
        return true;
      }

      var pass = false;
      if (!string.IsNullOrEmpty(line) && line.Length > 24)
      {
        var logTime = DateUtil.StandardDateToDouble(line);
        if (!double.IsNaN(logTime))
        {
          if (range == null)
          {
            pass = start > -1 && logTime >= start;
          }
          else
          {
            if (logTime > range.TimeSegments.Last().EndTime)
            {
              exceeds = true;
            }
            else
            {
              foreach (var segment in range.TimeSegments)
              {
                if (logTime >= segment.BeginTime && logTime <= segment.EndTime)
                {
                  pass = true;
                  break;
                }
              }
            }
          }
        }
      }

      return pass;
    }

    private void CollapseLeft(int index)
    {
      if (index - 1 is var prev and >= 0)
      {
        if (TimeSegments[index].BeginTime <= TimeSegments[prev].EndTime)
        {
          TimeSegments[index].BeginTime = Math.Min(TimeSegments[index].BeginTime, TimeSegments[prev].BeginTime);
          TimeSegments.RemoveAt(prev);
          CollapseLeft(prev);
        }
      }
    }

    private void CollapseRight(int index)
    {
      if (index + 1 is var next && next < TimeSegments.Count)
      {
        if (TimeSegments[index].EndTime >= TimeSegments[next].BeginTime)
        {
          TimeSegments[index].EndTime = Math.Max(TimeSegments[index].EndTime, TimeSegments[next].EndTime);
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

    public bool Equals(TimeSegment check) => check != null && check.BeginTime.Equals(BeginTime) && check.EndTime.Equals(EndTime);
    public double Total => EndTime - BeginTime + 1;
  }
}

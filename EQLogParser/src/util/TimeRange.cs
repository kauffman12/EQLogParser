using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  public class TimeRange
  {
    private const int OFFSET = 6;
    public List<TimeSegment> TimeSegments { get; } = new List<TimeSegment>();

    public TimeRange() { }

    public void Add(IReadOnlyCollection<TimeSegment> collection) => collection?.ToList().ForEach(segment => Add(segment));
    public TimeRange(TimeSegment segment) => TimeSegments.Add(segment);
    public TimeRange(List<TimeSegment> segments) => segments.ForEach(segment => Add(new TimeSegment(segment.BeginTime, segment.EndTime)));

    public double GetTotal()
    {
      var additional = new List<TimeSegment>();

      for (var i = 0; i < TimeSegments.Count; i++)
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
      return TimeSegments.Sum(segment => segment.Total);
    }

    public void Add(TimeSegment segment)
    {
      if (segment != null && segment.BeginTime <= segment.EndTime)
      {
        TimeSegments.Add(segment);
        TimeSegments.Sort((s1, s2) => s1.BeginTime.CompareTo(s2.BeginTime)); // Sort in-place.

        var mergedSegments = new List<TimeSegment>();
        var current = TimeSegments[0];

        for (var i = 1; i < TimeSegments.Count; i++)
        {
          if (current.EndTime >= TimeSegments[i].BeginTime)
          {
            // Merge segments
            current.EndTime = Math.Max(current.EndTime, TimeSegments[i].EndTime);
          }
          else
          {
            mergedSegments.Add(current);
            current = TimeSegments[i];
          }
        }

        mergedSegments.Add(current);

        TimeSegments.Clear(); // Clear the existing list.
        TimeSegments.AddRange(mergedSegments); // Add the merged segments back.
      }
    }
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
    public double Total => EndTime - BeginTime + 1;
  }
}

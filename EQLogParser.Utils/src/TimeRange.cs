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

    /*
     * The seconds covered, short silences included. Reads only: the rule that fills a silence lives in Add, so asking for a number no longer rewrites the
     * list it is asked of. See docs/DesignNotes.md -> "TimeRange: the tick rule lives in Add."
     */
    public double GetTotal()
    {
      var total = 0d;

      foreach (var segment in CollectionsMarshal.AsSpan(TimeSegments))
      {
        total += segment.Total;
      }

      return total;
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

    /*
     * Merges another range's seconds in without taking ownership of them. The List overload adopts whatever it is handed - those segment objects become
     * part of this list and get welded where something overlaps them - so summing one range rewrites the spans another caller is still holding.
     * Copying costs one small allocation per segment. See docs/DesignNotes.md -> "TimeRange: merging copies, adding adopts."
     */
    public void Add(TimeRange range)
    {
      if (range != null)
      {
        foreach (var segment in CollectionsMarshal.AsSpan(range.TimeSegments))
        {
          Add(new TimeSegment(segment.BeginTime, segment.EndTime));
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

    /*
     * Lays this span into the set, merging whatever it touches. The list is kept sorted and disjoint, which means there are only ever two facts to
     * establish - where the new span belongs, and how far right it now reaches - and both come from a halving search instead of testing every span.
     *
     * The loop this replaces asked six questions per span in a fixed order (identical, surrounds us, our begin inside, our end inside, strictly left,
     * strictly right). Reduced to the two above they agree on every input, including the awkward ones: an exact duplicate widens nothing and adds
     * nothing; a span contained in another changes no bound; a null or an inverted span is dropped before anything is read. How close counts as touching is
     * the tick rule at the bottom of this comment: runs merge across up to 5 seconds of dead air, and 6 seconds of daylight keeps them apart. The equality
     * check is gone rather than moved - merging with a span that has the same bounds is the same nothing, done by the arithmetic instead of by name.
     *
     * Nothing collapses to the left, which is worth stating because the old code called CollapseLeft: the span before the found index ends more than Offset
     * before our begin (that is why the search skipped it), and runs are disjoint, so it cannot touch a span we are about to widen rightward.
     *
     * The search rewrite changed behaviour by none of that, which was checked rather than read: the implementation before it is frozen in
     * local/timerange-lab/OldTimeRange.cs and both ran side by side over 1.9M random adds - in order, out of order, duplicates, touching endpoints,
     * points, inverted and null - with the whole segment list compared after every single add. Zero differences at that point. The tick rule below is the one
     * change since, made deliberately, and it moves lists and no numbers: -- equivA in that lab is red by design now (310k shape disagreements, 0 totals),
     * and -- equivB is the comparison to trust. Meaning is pinned in EQLogParser.Test's TimeRangeSpecTest.
     *
     * What the search changed is the cost. Walking from span zero made building "this player's activity over every fight" grow with the square of the fight
     * count: 40 names x 800 fight spans took 166 ms, and now takes 2.6 ms.
     *
     * This is also where the tick rule lives: a span landing within Offset of an existing run joins it, so the list is kept in the shape GetTotal() used to
     * reach only by inserting bridge spans of its own. Every span reaches this method - the List overload, the copy constructor and Add(TimeRange) all come
     * through here - which is what makes "runs are always more than Offset apart" safe to rely on. Building a list by touching TimeSegments directly skips
     * that, and would leave silences nobody counted.
     */
    public void Add(TimeSegment segment)
    {
      if (segment is not null && segment.BeginTime <= segment.EndTime)
      {
        var index = FirstSpanReaching(segment.BeginTime - Offset);

        if (index < TimeSegments.Count && segment.EndTime + Offset >= TimeSegments[index].BeginTime)
        {
          var target = TimeSegments[index];
          target.BeginTime = Math.Min(target.BeginTime, segment.BeginTime);
          target.EndTime = Math.Max(target.EndTime, segment.EndTime);

          /* Widening this span may now reach the next one, and the next. Swallow them in one pass and drop them in one RemoveRange. */
          var last = index;

          while (last + 1 < TimeSegments.Count && TimeSegments[last + 1].BeginTime <= target.EndTime + Offset)
          {
            target.EndTime = Math.Max(target.EndTime, TimeSegments[last + 1].EndTime);
            last++;
          }

          if (last > index)
          {
            TimeSegments.RemoveRange(index + 1, last - index);
          }

          return;
        }

        /* Open ground: everything to the left ends before this span starts, and this span ends before the span at `index`.
           Insert(count) is an append, so "past the end of the list" needs no branch of its own. */
        TimeSegments.Insert(index, segment);
      }
    }

    internal static bool TimeCheck(string line, double start, double end = -1)
    {
      var pass = false;
      if (!string.IsNullOrEmpty(line) && line.Length > 24)
      {
        var logTime = DateUtil.StandardDateToDotNetSeconds(line);
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
        var logTime = DateUtil.StandardDateToDotNetSeconds(line);
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

    /*
     * The first span whose EndTime reaches `value`, or Count when none does. Sorted and disjoint is what makes halving legal here: spans rise in
     * BeginTime and EndTime together, so "ends before this" is a fence you can binary search for.
     */
    private int FirstSpanReaching(double value)
    {
      var low = 0;
      var high = TimeSegments.Count - 1;

      while (low <= high)
      {
        var middle = (low + high) >>> 1;

        if (TimeSegments[middle].EndTime >= value)
        {
          high = middle - 1;
        }
        else
        {
          low = middle + 1;
        }
      }

      return low;
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

    public bool Equals(TimeSegment check) => check != null && check.BeginTime.Equals(BeginTime) && check.EndTime.Equals(EndTime);
    public double Total => EndTime - BeginTime + 1;
  }
}

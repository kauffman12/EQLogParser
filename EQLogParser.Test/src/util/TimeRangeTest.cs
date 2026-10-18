using EQLogParser;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class TimeRangeTest
  {
    [TestMethod]
    public void EmptyAndRefusedAddsLeaveNoActivity()
    {
      var range = new TimeRange();
      Assert.HasCount(0, range.TimeSegments);
      Assert.AreEqual(0.0, range.GetTotal());
      range.Add((TimeSegment?)null);
      range.Add(new TimeSegment(30, 20));
      range.Add((List<TimeSegment>?)null);
      Assert.HasCount(0, range.TimeSegments);
    }

    [TestMethod]
    public void ConstructorsKeepTheSuppliedBounds()
    {
      var single = new TimeRange(new TimeSegment(10, 20));
      Assert.HasCount(1, single.TimeSegments);
      Assert.AreEqual(10.0, single.TimeSegments[0].BeginTime);
      Assert.AreEqual(20.0, single.TimeSegments[0].EndTime);
      var multiple = new TimeRange(new List<TimeSegment> { new(10, 20), new(30, 40) });
      Assert.HasCount(2, multiple.TimeSegments);
    }

    [TestMethod]
    [DataRow(10.0, 20.0, 15.0, 25.0, 10.0, 25.0)]
    [DataRow(10.0, 40.0, 15.0, 25.0, 10.0, 40.0)]
    [DataRow(10.0, 20.0, 5.0, 25.0, 5.0, 25.0)]
    [DataRow(10.0, 20.0, 10.0, 20.0, 10.0, 20.0)]
    [DataRow(20.0, 30.0, 10.0, 25.0, 10.0, 30.0)]
    [DataRow(10.0, 20.0, 20.0, 30.0, 10.0, 30.0)]
    public void OverlapContainmentAndDuplicatesMergeWithoutLosingBounds(
      double begin, double end, double addedBegin, double addedEnd, double expectedBegin, double expectedEnd)
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(begin, end));
      Assert.HasCount(1, range.TimeSegments);
      range.Add(new TimeSegment(addedBegin, addedEnd));
      Assert.HasCount(1, range.TimeSegments);
      Assert.AreEqual(expectedBegin, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(expectedEnd, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void AddingAListKeepsDisjointActivityAndTotalsBothInclusiveSpans()
    {
      var range = new TimeRange();
      range.Add(new List<TimeSegment> { new(10, 20), new(50, 60) });
      Assert.HasCount(2, range.TimeSegments);
      Assert.AreEqual(10.0, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(20.0, range.TimeSegments[0].EndTime);
      Assert.AreEqual(50.0, range.TimeSegments[1].BeginTime);
      Assert.AreEqual(60.0, range.TimeSegments[1].EndTime);
      Assert.AreEqual(22.0, range.GetTotal());
    }

    [TestMethod]
    public void SequentialOverlapsCollapseTransitively()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(15, 25));
      range.Add(new TimeSegment(20, 30));
      Assert.HasCount(1, range.TimeSegments);
      Assert.AreEqual(10.0, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(30.0, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    [DataRow(10.0, 20.0, 11.0)]
    [DataRow(10.0, 10.0, 1.0)]
    public void AStoredSpanAndItsRangeCountSecondsInclusively(double begin, double end, double expected)
    {
      var segment = new TimeSegment(begin, end);
      Assert.AreEqual(begin, segment.BeginTime);
      Assert.AreEqual(end, segment.EndTime);
      Assert.AreEqual(expected, segment.Total);
      Assert.AreEqual(expected, new TimeRange(segment).GetTotal());
    }
  }

  [TestClass]
  public class TimeSegmentTest
  {
    [TestMethod]
    [DataRow(10.0, 20.0, true)]
    [DataRow(11.0, 20.0, false)]
    [DataRow(10.0, 21.0, false)]
    public void EqualityUsesBothBounds(double begin, double end, bool expected)
    {
      var segment = new TimeSegment(10, 20);
      Assert.AreEqual(expected, segment.Equals(new TimeSegment(begin, end)));
      Assert.IsFalse(segment.Equals(null));
    }

    [TestMethod]
    public void Contains_MatchesTheLinearQuestionOverRandomlyBuiltRanges()
    {
      var random = new Random(20261112);
      for (var trial = 0; trial < 30; trial++)
      {
        var range = new TimeRange();
        for (var i = 0; i < random.Next(0, 12); i++)
        {
          var begin = random.Next(500, 1_000) + random.Next(0, 3) * 400;
          range.Add(new TimeSegment(begin, begin + random.Next(0, 30)));
        }
        var probes = new List<double> { -1_000, 0, 100_000 };
        foreach (var segment in range.TimeSegments)
        {
          probes.AddRange([segment.BeginTime - 1, segment.BeginTime, (segment.BeginTime + segment.EndTime) / 2,
            segment.EndTime, segment.EndTime + 1]);
        }
        foreach (var probe in probes)
        {
          var expected = range.TimeSegments.Any(s => probe >= s.BeginTime && probe <= s.EndTime);
          Assert.AreEqual(expected, range.Contains(probe), $"trial {trial}, probe {probe}");
        }
      }
    }

    [TestMethod]
    public void Contains_EmptyRangeIsFalseAndReadsNothing()
    {
      Assert.IsFalse(new TimeRange().Contains(100));
      var range = new TimeRange(new TimeSegment(10, 20));
      Assert.IsTrue(range.Contains(10));
      Assert.IsTrue(range.Contains(20));
      Assert.IsFalse(range.Contains(9));
      Assert.IsFalse(range.Contains(21));
      Assert.HasCount(1, range.TimeSegments);
      Assert.AreEqual(10.0, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(20.0, range.TimeSegments[0].EndTime);
    }
  }
}

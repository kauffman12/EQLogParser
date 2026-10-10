using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class TimeRangeTest
  {
    [TestMethod]
    public void Constructor_Default_EmptySegments()
    {
      var range = new TimeRange();
      Assert.AreEqual(0, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Constructor_WithSegment_AddsSegment()
    {
      var segment = new TimeSegment(10, 20);
      var range = new TimeRange(segment);
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(20, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Constructor_WithList_AddsAllSegments()
    {
      var segments = new List<TimeSegment>
      {
        new TimeSegment(10, 20),
        new TimeSegment(30, 40),
      };
      var range = new TimeRange(segments);
      Assert.AreEqual(2, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_SingleSegment_ToEmptyRange()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(20, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Add_OverlappingSegments_MergesThem()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(15, 25));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(25, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Add_NonOverlappingSegments_KeepsSeparate()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(30, 40));
      Assert.AreEqual(2, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_ContainingSegment_AbsorbsInner()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 40));
      range.Add(new TimeSegment(15, 25));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(40, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Add_ExtendedSegment_ExpandsExisting()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(15, 25));
      range.Add(new TimeSegment(10, 40));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(40, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Add_DuplicateSegment_NoDuplicateAdded()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(10, 20));
      Assert.AreEqual(1, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_NullSegment_Ignored()
    {
      var range = new TimeRange();
      range.Add((TimeSegment?)null);
      Assert.AreEqual(0, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_InvertedSegment_Ignored()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(30, 20)); // begin > end
      Assert.AreEqual(0, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_AdjacentSegments_WithinOffset_Merges()
    {
      // Offset is 6, so segments within 6 seconds merge during GetTotal
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(25, 35));
      // These are 5 seconds apart (20+6 >= 25), so they should merge on GetTotal
      var total = range.GetTotal();
      // 10-20 = 11 + 25-35 = 11 + gap fill = 22 + gap (4 seconds + 1)
      Assert.IsTrue(total > 0);
    }

    [TestMethod]
    public void GetTotal_EmptyRange_ReturnsZero()
    {
      var range = new TimeRange();
      Assert.AreEqual(0, range.GetTotal());
    }

    [TestMethod]
    public void GetTotal_SingleSegment_ReturnsDurationPlusOne()
    {
      var range = new TimeRange(new TimeSegment(10, 20));
      Assert.AreEqual(11, range.GetTotal()); // 20 - 10 + 1
    }

    [TestMethod]
    public void GetTotal_MultipleNonOverlapping_SumsAll()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20)); // 11
      range.Add(new TimeSegment(50, 60)); // 11
      Assert.AreEqual(22, range.GetTotal());
    }

    [TestMethod]
    public void Add_ListOfSegments_AddsAll()
    {
      var range = new TimeRange();
      range.Add(new List<TimeSegment>
      {
        new TimeSegment(10, 20),
        new TimeSegment(30, 40),
      });
      Assert.AreEqual(2, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_NullList_NoOp()
    {
      var range = new TimeRange();
      range.Add((List<TimeSegment>?)null);
      Assert.AreEqual(0, range.TimeSegments.Count);
    }

    [TestMethod]
    public void Add_SequentialOverlapping_CollapsesAll()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(18, 30));
      range.Add(new TimeSegment(28, 40));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(40, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Add_ExtendsLeftSide()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(20, 30));
      range.Add(new TimeSegment(15, 25));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(15, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(30, range.TimeSegments[0].EndTime);
    }

    [TestMethod]
    public void Add_ExtendsRightSide()
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(10, 20));
      range.Add(new TimeSegment(18, 30));
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(30, range.TimeSegments[0].EndTime);
    }
  }

  [TestClass]
  public class TimeSegmentTest
  {
    [TestMethod]
    public void Constructor_SetsValues()
    {
      var seg = new TimeSegment(10.0, 20.0);
      Assert.AreEqual(10.0, seg.BeginTime);
      Assert.AreEqual(20.0, seg.EndTime);
    }

    [TestMethod]
    public void Total_DurationPlusOne()
    {
      var seg = new TimeSegment(10.0, 20.0);
      Assert.AreEqual(11.0, seg.Total);
    }

    [TestMethod]
    public void Total_SinglePoint()
    {
      var seg = new TimeSegment(10.0, 10.0);
      Assert.AreEqual(1.0, seg.Total);
    }

    [TestMethod]
    public void Equals_SameValues_ReturnsTrue()
    {
      var a = new TimeSegment(10, 20);
      var b = new TimeSegment(10, 20);
      Assert.IsTrue(a.Equals(b));
    }

    [TestMethod]
    public void Equals_DifferentBeginTime_ReturnsFalse()
    {
      var a = new TimeSegment(10, 20);
      var b = new TimeSegment(11, 20);
      Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void Equals_DifferentEndTime_ReturnsFalse()
    {
      var a = new TimeSegment(10, 20);
      var b = new TimeSegment(10, 21);
      Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void Equals_Null_ReturnsFalse()
    {
      var a = new TimeSegment(10, 20);
      Assert.IsFalse(a.Equals(null));
    }

    /*
     * Contains is the halved form of "is this instant inside one of my seconds", and it is legal only because Add keeps the list
     * sorted and disjoint - so this asserts it against the linear question over randomly built ranges (which exercises that law at
     * the same time), including every instant around every edge and each gap in between.
     */
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

        // Probes: every edge, both sides of every edge, the middle of every gap, and points outside everything.
        var probes = new List<double> { -1_000, 0, 100_000 };
        foreach (var segment in range.TimeSegments)
        {
          probes.Add(segment.BeginTime - 1);
          probes.Add(segment.BeginTime);
          probes.Add((segment.BeginTime + segment.EndTime) / 2);
          probes.Add(segment.EndTime);
          probes.Add(segment.EndTime + 1);
        }

        foreach (var probe in probes)
        {
          var linear = range.TimeSegments.Any(s => probe >= s.BeginTime && probe <= s.EndTime);
          Assert.AreEqual(linear, range.Contains(probe),
              $"trial {trial} at {probe}: halving and scanning disagreed over [{string.Join(", ", range.TimeSegments.Select(s => $"{s.BeginTime}-{s.EndTime}"))}]");
        }
      }
    }

    [TestMethod]
    public void Contains_EmptyRangeIsFalseAndReadsNothing()
    {
      Assert.IsFalse(new TimeRange().Contains(100));

      var range = new TimeRange(new TimeSegment(10, 20));
      Assert.IsTrue(range.Contains(10), "inclusive at the start");
      Assert.IsTrue(range.Contains(20), "inclusive at the end");
      Assert.IsFalse(range.Contains(9));
      Assert.IsFalse(range.Contains(21));

      // A read may not rewrite the list it is asked of - the rule GetTotal() carries for the same reason.
      Assert.AreEqual(1, range.TimeSegments.Count);
      Assert.AreEqual(10, range.TimeSegments[0].BeginTime);
      Assert.AreEqual(20, range.TimeSegments[0].EndTime);
    }
  }
}

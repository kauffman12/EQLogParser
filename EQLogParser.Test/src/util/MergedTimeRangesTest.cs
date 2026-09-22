using EQLogParser;

namespace EQLogParserTest
{
  /*
   * The group view needs one number from a handful of players: the seconds during which ANY of them was active, recomputed from `Members` on every
   * pass so that adding or removing a member cannot leave anything behind. DamageSummary used to keep a cached List<TimeSegment> per group instead and
   * patch it by hand when a player changed groups, which required (a) collecting segment REFERENCES out of the players' own ranges and (b) removing
   * them again later by `Contains` - a reference test, since TimeSegment overrides neither Equals nor GetHashCode. Both halves are gone; these tests
   * cover the replacement and the rule it depends on: merging must copy.
   *
   * Two things make copying non-negotiable rather than tidy. Add(List<TimeSegment>) ADOPTS the segments handed to it, so a merge performed on the
   * callers' objects welds them in place: the player whose span got swallowed stays holding an object that now belongs to somebody else's run. And
   * because group ranges are rebuilt on every pass, any such leak is applied repeatedly, by whoever happens to ask for a number.
   */
  [TestClass]
  public class MergedTimeRangesTest
  {
    private static PlayerStats Member(string name, params double[] beginEnd)
    {
      var stats = new PlayerStats { OrigName = name, Name = name };

      for (var i = 0; i < beginEnd.Length; i += 2)
      {
        stats.Ranges.Add(new TimeSegment(beginEnd[i], beginEnd[i + 1]));
      }

      return stats;
    }

    /* The (begin, end) pairs of a range, as plain values - so a comparison says "the same seconds", not "the same objects". */
    private static List<(double Begin, double End)> Pairs(TimeRange range) =>
      range.TimeSegments.Select(s => (s.BeginTime, s.EndTime)).ToList();

    private static void AssertUnchanged(IEnumerable<PlayerStats> members, IReadOnlyDictionary<string, List<(double, double)>> before, string because)
    {
      foreach (var member in members)
      {
        CollectionAssert.AreEqual(before[member.OrigName], Pairs(member.Ranges),
          $"{member.OrigName}'s own spans must survive the merge - {because}");
      }
    }

    private static IReadOnlyDictionary<string, List<(double, double)>> Snapshot(IEnumerable<PlayerStats> members) =>
      members.ToDictionary(m => m.OrigName, m => Pairs(m.Ranges));

    /* ---- the rule: merging copies ---- */

    [TestMethod]
    public void MergingARangeInLeavesTheSourceAlone()
    {
      var source = new TimeRange();
      source.Add(new TimeSegment(10, 20));

      var dest = new TimeRange();
      dest.Add(source);

      // Someone else's activity lands on top of the merged copy. This is where an adopted segment gets widened.
      dest.Add(new TimeSegment(15, 40));

      CollectionAssert.AreEqual(new List<(double, double)> { (10d, 20d) }, Pairs(source), "the source still describes its own eleven seconds");
      Assert.AreEqual(31d, dest.GetTotal(), "the destination got the union");
    }

    [TestMethod]
    public void AddRangeToleratesNullAndEmpty()
    {
      var range = new TimeRange();

      range.Add((TimeRange?)null!);
      Assert.AreEqual(0, range.TimeSegments.Count, "a null range adds nothing");

      range.Add(new TimeRange());
      Assert.AreEqual(0, range.TimeSegments.Count, "an empty range adds nothing");
    }

    /* ---- the group number: union across members ---- */

    [TestMethod]
    public void GroupSecondsCoverEveryMember()
    {
      var members = new List<PlayerStats> { Member("A", 0, 10), Member("B", 40, 50) };

      Assert.AreEqual(22d, StatsUtil.MergeMemberRanges(members).GetTotal(), "two disjoint eleven second members");
    }

    [TestMethod]
    public void MembersActiveAtTheSameTimeAreCountedOnce()
    {
      var members = new List<PlayerStats> { Member("A", 0, 10), Member("B", 0, 10) };

      // This is why the group cannot just sum its members' TotalSeconds.
      Assert.AreEqual(11d, StatsUtil.MergeMemberRanges(members).GetTotal(), "two players fighting together are one period of activity");
    }

    [TestMethod]
    public void ASilenceBetweenTwoMembersShorterThanTheTickCountsAsOneRun()
    {
      var members = new List<PlayerStats> { Member("A", 0, 10), Member("B", 14, 20) };

      // Same rule GetTotal applies inside one player's spans, applied across the group - unchanged from the old cache.
      Assert.AreEqual(21d, StatsUtil.MergeMemberRanges(members).GetTotal());
    }

    [TestMethod]
    public void GroupSecondsDoNotDependOnMemberOrder()
    {
      var forward = new List<PlayerStats> { Member("A", 0, 10), Member("B", 9, 30), Member("C", 100, 110) };
      var backward = new List<PlayerStats> { Member("C", 100, 110), Member("B", 9, 30), Member("A", 0, 10) };

      Assert.AreEqual(
        StatsUtil.MergeMemberRanges(forward).GetTotal(),
        StatsUtil.MergeMemberRanges(backward).GetTotal(),
        "the grid sorts members by damage, so order must not reach the number");
    }

    [TestMethod]
    public void RebuildingTheGroupTwiceChangesNothing()
    {
      var members = new List<PlayerStats> { Member("A", 0, 10), Member("B", 14, 20) };
      var before = Snapshot(members);

      var first = StatsUtil.MergeMemberRanges(members).GetTotal();
      var second = StatsUtil.MergeMemberRanges(members).GetTotal();
      var third = StatsUtil.MergeMemberRanges(members).GetTotal();

      Assert.AreEqual(first, second, "asking again gives the same answer");
      Assert.AreEqual(second, third, "and keeps giving it: the group is rebuilt on every pass over the grid");
      AssertUnchanged(members, before, "the group is rebuilt twice");
    }

    [TestMethod]
    public void MergingTheGroupNeverEditsAMembersOwnSpans()
    {
      var members = new List<PlayerStats>
      {
        Member("A", 10, 11, 14, 15, 18, 19),   // three spans with short silences inside: the ones a merge would weld
        Member("B", 20, 23, 60, 70)            // sits right on A's weld point
      };
      var before = Snapshot(members);

      _ = StatsUtil.MergeMemberRanges(members).GetTotal();
      _ = StatsUtil.MergeMemberRanges(members).GetTotal();

      AssertUnchanged(members, before, "the group summary was computed twice");
    }

    /* ---- what the hand-patched cache used to get wrong ---- */

    [TestMethod]
    public void RemovingAMemberLeavesExactlyTheRemainingMembers()
    {
      var a = Member("A", 10, 20);
      var b = Member("B", 40, 55);
      var c = Member("C", 90, 95);
      var members = new List<PlayerStats> { a, b, c };

      var withAllThree = StatsUtil.MergeMemberRanges(members).GetTotal();

      // The old code removed the departing player's segments from a cached list by reference; anything it missed kept counting.
      members.Remove(b);
      var withoutB = StatsUtil.MergeMemberRanges(members).GetTotal();

      Assert.AreEqual(16d, withAllThree - withoutB, "B contributed its sixteen seconds and nothing else");
      Assert.AreEqual(withoutB, StatsUtil.MergeMemberRanges(new List<PlayerStats> { a, c }).GetTotal(),
        "the group equals the same two players built from scratch");
    }

    [TestMethod]
    public void AGroupWithNoMembersHasNoSeconds()
    {
      Assert.AreEqual(0d, StatsUtil.MergeMemberRanges(new List<PlayerStats>()).GetTotal());
    }

    /* ---- today's API, documented as it behaves: this is the leak the copy-in merge exists to stop ---- */

    [TestMethod]
    public void SharpEdgeAddCollectionAdoptsAndRewritesTheCallersSegments()
    {
      var source = new TimeRange();
      source.Add(new TimeSegment(10, 20));

      var merged = new TimeRange();
      merged.Add(source.TimeSegments);            // what DamageSummary did per group, and builders still do in a few places
      merged.Add(new TimeSegment(15, 40));

      Assert.AreEqual(31d, merged.GetTotal());
      Assert.AreEqual(31d, source.GetTotal(), "the SOURCE reports the merged total - it handed over its own objects");
    }

    /* ---- StatsUtil.FilterTimeRange: the other place a range gets built out of somebody else's spans, and the one that no longer does ---- */

    [TestMethod]
    public void FilteringARangeNeverEditsTheSource()
    {
      var source = new TimeRange();
      source.Add(new TimeSegment(100, 110));
      source.Add(new TimeSegment(140, 150));

      var before = Pairs(source);
      var beforeTotal = source.GetTotal();

      // a window that keeps the first run whole and cuts the second one in half
      var filtered = StatsUtil.FilterTimeRange(source, 0, 145);

      Assert.AreEqual(17d, filtered.GetTotal(), "11 seconds whole, plus the 6 kept of the second run");
      CollectionAssert.AreEqual(before, Pairs(source), "filtering handed the source's own TimeSegment objects to the new range, and Add welds what it is given");
      Assert.AreEqual(beforeTotal, source.GetTotal(), "the unfiltered range still reports its own seconds after somebody filtered a copy of it");
    }
  }
}

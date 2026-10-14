using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;

namespace EQLogParser;

/*
 * The chat archive's date range, in the one place that decides it (EQLogParser.Core/src/util/ChatDateRange.cs).
 *
 * A field report: leave EQLogParser open past midnight and the Chat pane shows nothing newer than yesterday. Not the
 * archive — every line is filed under its own date (that was checked by replaying the writer's bucketing across day,
 * month and year rollovers) — but the query that reads it back. The pane stamped its end dial with `DateTime.Now` once,
 * in its constructor, and the pruning test asked `_endDate >= dayBegin`; that holds for the day the dial was stamped and
 * fails for the next one, so at 00:00:01 today left the answer set. Refresh rebuilt the filter from the same instant, so
 * there was no button for what had just happened.
 *
 * THE DISCIPLINE OF THESE TESTS: every case names a DAY and says whether it is in. A test written as "the bound got
 * bigger" passes on an off-by-one that still hides today, which is the bug being fixed; the interesting boundary is the
 * midnight between two whole days, so it is crossed on purpose in each direction. The `During*` calls the filter makes
 * are spelled here as ChatDateRange.Overlaps over a day/month/year span — if ChatFilter ever stops calling it, these
 * tests keep passing and the pane regresses, which is why the wiring itself is stated below.
 */
[TestClass]
public class ChatDateRangeTest
{
  private static readonly DateTime PaneBuilt = new(2026, 11, 7, 21, 42, 5); // the app opened on the evening of Nov 7
  private static readonly DateTime BeforeMidnight = new(2026, 11, 7, 23, 59, 59);
  private static readonly DateTime AfterMidnight = new(2026, 11, 8, 0, 0, 1);

  private static bool CoversDay(double begin, double end, int year, int month, int day)
  {
    var dayStart = new DateTime(year, month, day);
    return ChatDateRange.Overlaps(begin, end, DateUtil.ToDotNetSeconds(dayStart),
      DateUtil.ToDotNetSeconds(dayStart.AddDays(1)));
  }

  private static bool CoversMonth(double begin, double end, int year, int month)
  {
    var monthStart = new DateTime(year, month, 1);
    return ChatDateRange.Overlaps(begin, end, DateUtil.ToDotNetSeconds(monthStart),
      DateUtil.ToDotNetSeconds(monthStart.AddMonths(1)));
  }

  // The shipped default range: a start dial nobody has moved either, and an end dial riding the clock.
  private static (double Begin, double End) UntouchedRangeAt(DateTime now) =>
    (ChatDateRange.ResolveStart(new DateTime(1999, 3, 16)), ChatDateRange.ResolveEnd(PaneBuilt, false, now));

  [TestMethod]
  public void AnUntouchedEndDialCoversTodayBeforeMidnight()
  {
    var (begin, end) = UntouchedRangeAt(BeforeMidnight);

    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 7), "the evening the pane was built has to be in the range");
    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 6), "an earlier day of the same month has to be in the range");
  }

  /*
   * THE bug. Yesterday this is the whole report: the clock crosses midnight and today silently leaves the answer set
   * because the end bound is still an instant on the previous evening.
   */
  [TestMethod]
  public void AnUntouchedEndDialStillCoversTodayAfterMidnight()
  {
    var (begin, end) = UntouchedRangeAt(AfterMidnight);

    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 8),
                  "the day that just started has to be in the range — this is the assertion the shipped dial failed");
    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 7), "yesterday stays in: nobody asked to lose it");
  }

  [TestMethod]
  public void AnUntouchedEndDialRollsWithTheClockForDaysOnEnd()
  {
    for (var offset = 0; offset < 5; offset++)
    {
      var today = AfterMidnight.AddDays(offset);
      var (begin, end) = UntouchedRangeAt(today);

      Assert.IsTrue(CoversDay(begin, end, today.Year, today.Month, today.Day),
                    $"day +{offset} is 'today' and must be covered");
      Assert.IsFalse(CoversDay(begin, end, today.AddDays(1).Year, today.AddDays(1).Month, today.AddDays(1).Day),
                     $"day +{offset} +1 is the future and must not be covered");
    }
  }

  [TestMethod]
  public void TheEndBoundIsExclusiveSoItCoversItsWholeDayAndNoMore()
  {
    var picked = new DateTime(2026, 11, 7, 8, 0, 0);
    var end = ChatDateRange.ResolveEnd(picked, true, AfterMidnight);

    // The clock time off the pick is not part of the question: "through Nov 7" means the last second of Nov 7 too.
    var begin = ChatDateRange.ResolveStart(new DateTime(1999, 3, 16));
    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 7), "the picked day is covered to its end");
    Assert.IsFalse(CoversDay(begin, end, 2026, 11, 8), "the day after the picked one is not, even though the clock moved on");

    Assert.AreEqual(DateUtil.ToDotNetSeconds(new DateTime(2026, 11, 8)), end,
                    "the bound is midnight ending the picked day, not an instant inside it");
  }

  [TestMethod]
  public void AnOperatorPickedEndDialStopsFollowingTheClock()
  {
    // Someone asking for "up to Nov 7" means exactly that a week later; that is the one case where a stale answer is right.
    var (begin, end) = (ChatDateRange.ResolveStart(new DateTime(1999, 3, 16)),
                        ChatDateRange.ResolveEnd(PaneBuilt, true, PaneBuilt.AddDays(9)));

    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 7));
    Assert.IsFalse(CoversDay(begin, end, 2026, 11, 8), "a picked end date is the operator's, not ours to widen");
  }

  [TestMethod]
  public void TheStartBoundIsInclusiveOfThePickedDay()
  {
    var begin = ChatDateRange.ResolveStart(new DateTime(2026, 11, 7, 18, 30, 0));
    var end = ChatDateRange.NoBound;

    Assert.IsTrue(CoversDay(begin, end, 2026, 11, 7), "the picked start day is in");
    Assert.IsFalse(CoversDay(begin, end, 2026, 11, 6), "the day before it is out");
  }

  [TestMethod]
  public void AnEmptyDialMeansUnbounded()
  {
    Assert.AreEqual(ChatDateRange.NoBound, ChatDateRange.ResolveEnd(null, false, AfterMidnight));
    Assert.AreEqual(ChatDateRange.NoBound, ChatDateRange.ResolveStart(null));

    // Unbounded on both sides covers anything, including a capture years out.
    Assert.IsTrue(CoversDay(ChatDateRange.NoBound, ChatDateRange.NoBound, 2035, 1, 1));
  }

  /*
   * The same arithmetic hides a whole MONTH on the 1st and a whole YEAR folder on Jan 1, because DuringMonth and
   * DuringYear compare the same pair of numbers against the folder's own span. These are the shapes those calls make.
   */
  [TestMethod]
  public void AMonthRolloverDoesNotCutOffTheNewMonth()
  {
    var stale = new DateTime(2026, 11, 30, 22, 0, 0);
    var now = new DateTime(2026, 12, 1, 0, 0, 3);
    var (begin, end) = (ChatDateRange.ResolveStart(new DateTime(1999, 3, 16)), ChatDateRange.ResolveEnd(stale, false, now));

    Assert.IsTrue(CoversMonth(begin, end, 2026, 12), "December's archive has to be opened on December 1st");
    Assert.IsTrue(CoversMonth(begin, end, 2026, 11), "November stays in — the rollover is not a cut to one day");

    // And a picked November end must not drag December in behind it.
    var frozen = ChatDateRange.ResolveEnd(stale, true, now);
    Assert.IsFalse(CoversMonth(begin, frozen, 2026, 12), "a picked end date still owns the boundary");
  }

  [TestMethod]
  public void AYearRolloverDoesNotCutOffTheNewYearFolder()
  {
    var stale = new DateTime(2026, 12, 31, 23, 50, 0);
    var now = new DateTime(2027, 1, 1, 0, 5, 0);
    var begin = ChatDateRange.ResolveStart(new DateTime(1999, 3, 16));
    var end = ChatDateRange.ResolveEnd(stale, false, now);

    Assert.IsTrue(CoversDay(begin, end, 2027, 1, 1), "the new year folder has to be reachable on January 1st");
    Assert.IsTrue(CoversDay(begin, end, 2026, 12, 31));

    // Half-open against a year span: touching edges do not overlap, so 2028 is out while 2027 is in.
    Assert.IsTrue(ChatDateRange.Overlaps(begin, end, DateUtil.ToDotNetSeconds(new DateTime(2027, 1, 1)),
                                        DateUtil.ToDotNetSeconds(new DateTime(2028, 1, 1))));
    Assert.IsFalse(ChatDateRange.Overlaps(begin, end, DateUtil.ToDotNetSeconds(new DateTime(2028, 1, 1)),
                                         DateUtil.ToDotNetSeconds(new DateTime(2029, 1, 1))));
  }

  /*
   * Belt and braces for the pane: even if a caller forgets to re-stamp an untouched dial, the bound is the LATER of the
   * stamped day and today, so the frozen-instant answer cannot come back through the side door.
   */
  [TestMethod]
  public void AnUntouchedDialStampedInThePastNeverLosesToday()
  {
    var end = ChatDateRange.ResolveEnd(new DateTime(2026, 11, 7), false, new DateTime(2026, 11, 30));
    Assert.AreEqual(DateUtil.ToDotNetSeconds(new DateTime(2026, 12, 1)), end);

    // ...while a touched dial stamped in the past keeps its own answer.
    Assert.AreEqual(DateUtil.ToDotNetSeconds(new DateTime(2026, 11, 8)),
                    ChatDateRange.ResolveEnd(new DateTime(2026, 11, 7), true, new DateTime(2026, 11, 30)));
  }

  /*
   * The wiring this all hangs off, stated so it cannot rot quietly: ChatFilter's DuringYear/DuringMonth/DuringDay are
   * each one call to Overlaps with the span's own half-open edges. If that changes, the tests above stop describing the
   * pane and only this one notices.
   */
  [TestMethod]
  public void ChatFilterPrunesThroughTheSharedPredicate()
  {
    var source = File.ReadAllText(FindWpfSource("src", "dao", "util", "ChatFilter.cs"));

    foreach (var method in new[] { "DuringYear", "DuringMonth", "DuringDay" })
    {
      var at = source.IndexOf("bool " + method, StringComparison.Ordinal);
      Assert.IsTrue(at > -1, $"{method} disappeared from ChatFilter");
      var body = source.Substring(at, Math.Min(400, source.Length - at));
      StringAssert.Contains(body, "ChatDateRange.Overlaps",
                            $"{method} must prune through ChatDateRange.Overlaps, not its own comparison");
      Assert.IsFalse(body.Contains("_endDate >="), $"{method} went back to the inclusive end bound that hid today");
    }
  }

  private static string FindWpfSource(params string[] parts)
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
      var candidate = Path.Combine(dir.FullName, "EQLogParser", Path.Combine(parts));
      if (File.Exists(candidate))
      {
        return candidate;
      }

      dir = dir.Parent;
    }

    throw new AssertInconclusiveException("EQLogParser (the WPF project) was not found above the test binaries.");
  }
}

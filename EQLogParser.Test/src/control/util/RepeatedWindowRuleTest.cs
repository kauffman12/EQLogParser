using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;

namespace EQLogParser;

/*
 * The anchor policy of a repeat window, in the one place that decides it (EQLogParser.Core/src/control/util/RepeatedWindowRule.cs).
 *
 * Two policies, one assignment apart: FIXED leaves the deadline where the epoch started, so an Exp trigger
 * with a 3600 s window restarts once per hour no matter how much it fires; SLIDING moves the deadline on every fire - GINA's
 * `LastMatched = DateTime.Now`, unconditionally - so a grind counts upward forever and only quiet LONGER than the window ends the epoch.
 * A field report from a GINA user called the fixed one "unexpected", which is fair: it is not what the same number meant in the program
 * they came from. The option is per trigger, default fixed, so nothing that already exists changes meaning (see NagUtilTriggerImportTest
 * for the imported half, where the flag comes along with the number).
 *
 * THE DISCIPLINE OF THESE TESTS: assert WHICH fire starts epoch N+1. Both policies increment per match, so any test written as
 * "the count went up" passes on either one - including a rule that never resets anything. Every case below therefore names the exact
 * fire index that must (or must not) read 1 again, and the sliding/fixed pair is replayed over the SAME stream wherever the two differ,
 * because a difference nobody can point at on a timeline is a difference that will be "simplified" back.
 */
[TestClass]
public class RepeatedWindowRuleTest
{
  private const double Hour = 3600;

  private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

  // Replays a stream of fire times through the rule and hands back the count each fire produced.
  private static List<long> Replay(double[] fires, double windowSeconds, bool slides)
  {
    var counts = new List<long>(fires.Length);
    long anchor = Ticks(fires[0]);
    long count = 1;
    counts.Add(count); // the caller mints the first sighting; the rule only answers what a LATER fire does to it

    for (var i = 1; i < fires.Length; i++)
    {
      var step = RepeatedWindowRule.Next(count, anchor, Ticks(fires[i]), windowSeconds, slides);
      count = step.Count;
      anchor = step.AnchorTicks;
      counts.Add(count);
    }

    return counts;
  }

  [TestMethod]
  public void AGrindCountsUpwardWhileTheWindowSlides()
  {
    // The reported scenario: Exp every five minutes for three hours, window 3600 s. GINA kept counting; that is the expectation to meet.
    var fires = new double[36];
    for (var i = 0; i < fires.Length; i++)
      fires[i] = i * 300;

    var counts = Replay(fires, Hour, slides: true);

    Assert.AreEqual(36, counts[^1], "a grind that never pauses must reach the last fire still counting");

    var restarted = new List<int>();
    for (var i = 1; i < counts.Count; i++)
      if (counts[i] == 1)
        restarted.Add(i);

    Assert.AreEqual(0, restarted.Count, $"no fire mid-grind may restart at 1 while the window rides on the last match; fire(s) {string.Join(", ", restarted)} did");
  }

  [TestMethod]
  public void TheSameGrindRestartsOncePerHourWhenTheWindowIsFixed()
  {
    var fires = new double[36];
    for (var i = 0; i < fires.Length; i++)
      fires[i] = i * 300;

    var counts = Replay(fires, Hour, slides: false);

    // Fixed anchor at t=0: the first fire PAST 3600 s is index 13 (t=3900; index 12 sits exactly on the deadline and does not count, which
    // is what the strict comparison buys). Its own epoch then ends at 7500, so the next restart is index 26 (t=7800) - and nothing after it
    // fits a third hour inside the three hours of grinding.
    var resets = new List<int>();
    for (var i = 1; i < counts.Count; i++)
      if (counts[i] == 1)
        resets.Add(i);

    CollectionAssert.AreEqual(new[] { 13, 26 }, resets,
      "fixed pins restarts to the epoch's own clock: exactly two in three hours of grinding, and neither one is the fire on the boundary");
  }

  [TestMethod]
  public void AQuietShorterThanTheWindowContinuesTheCountOnlyWhileSliding()
  {
    // Two hours of grinding, then 40 minutes of nothing. Sliding measures the quiet from the LAST match (2400 s < 3600 s) so the count
    // continues; fixed measures it from the epoch's start (7200 s + 2400 s > 3600 s) so it restarts. Same stream, opposite answers, and
    // this is the difference a long-time GINA user feels without being able to name it.
    var fires = new double[25];
    for (var i = 0; i < 24; i++)
      fires[i] = i * 300;
    fires[24] = 23 * 300 + 2400;

    Assert.AreEqual(25, Replay(fires, Hour, slides: true)[^1], "40 quiet minutes is inside a sliding hour");
    Assert.AreEqual(1, Replay(fires, Hour, slides: false)[^1], "the same 40 minutes sits past a fixed hour");
  }

  [TestMethod]
  public void AQuietLongerThanTheWindowEndsTheEpochEitherWay()
  {
    var fires = new[] { 0.0, 300.0, 300.0 + Hour + 1 };

    Assert.AreEqual(1, Replay(fires, Hour, slides: true)[^1], "sliding: a gap longer than the window starts epoch 2");
    Assert.AreEqual(1, Replay(fires, Hour, slides: false)[^1], "fixed: same answer - the reset itself is not what the option changes");
  }

  [TestMethod]
  public void TheBoundaryIsStrictAndBothModesAgreeAboutIt()
  {
    // GINA compared with `>`, and so does this: a fire landing exactly on the deadline is still the same epoch. One second later is not.
    var onTheLine = RepeatedWindowRule.Next(7, 0, Ticks(Hour), Hour, slides: false);
    Assert.AreEqual(8, onTheLine.Count, "elapsed == window does not reset; the shipped comparison was '>' and stays that way");

    var oneSecondPast = RepeatedWindowRule.Next(7, 0, Ticks(Hour) + TimeSpan.TicksPerSecond, Hour, slides: false);
    Assert.AreEqual(1, oneSecondPast.Count, "one second past the deadline reads 1 again");
    Assert.AreEqual(Ticks(Hour) + TimeSpan.TicksPerSecond, oneSecondPast.AnchorTicks, "the new epoch is anchored at the fire that started it");
  }

  [TestMethod]
  public void SlidingMovesTheAnchorEveryFireAndFixedNeverDoes()
  {
    // The one assignment the whole option is made of, asserted as a fact about the anchor rather than inferred from a count.
    var sliding = RepeatedWindowRule.Next(4, Ticks(1000), Ticks(1200), Hour, slides: true);
    Assert.AreEqual(Ticks(1200), sliding.AnchorTicks, "sliding hands this fire's timestamp to the next comparison");

    var fixedWindow = RepeatedWindowRule.Next(4, Ticks(1000), Ticks(1200), Hour, slides: false);
    Assert.AreEqual(Ticks(1000), fixedWindow.AnchorTicks, "fixed leaves the epoch's own timestamp in place");

    Assert.AreEqual(5, sliding.Count);
    Assert.AreEqual(5, fixedWindow.Count, "both increment; the disagreement is only ever about where the deadline sits");
  }

  [TestMethod]
  public void AZeroSecondWindowMakesEveryFireItsOwnEpochExceptInsideOneSecond()
  {
    // RepeatedResetTime can be set to 0 (the grid allows it), and both modes then mean "no accumulation across seconds" - pinned so a future
    // rewrite of the comparison does not quietly turn a 0 into "never reset".
    var later = RepeatedWindowRule.Next(9, Ticks(500), Ticks(501), 0, slides: true);
    Assert.AreEqual(1, later.Count, "one second has passed a zero window");

    var sameSecond = RepeatedWindowRule.Next(9, Ticks(500), Ticks(500), 0, slides: true);
    Assert.AreEqual(10, sameSecond.Count, "fires inside the same truncated second still accumulate");
  }
}


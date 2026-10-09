using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * A `healer|healed` KEY STRING is built once per PAIR, never once per heal.
 *
 * The log writes one heal line per event — 2,647,774 of them over a night's select-all — and this key is a CONCATENATED string,
 * so a build that asked for it per record would mint millions of strings to look up a few thousand pairs. `HealerHealedKey` has
 * memoized on `_healerHealedKeys` for a while; this test is what stops that being an accident somebody can undo from either end
 * (the call site or the helper), because the failure mode is invisible: the board looks identical and the log file grows.
 *
 * The measured context, over eqlog_Kizant_xegony-09-03-26.txt with `EQLP_BOARD_WALK=… dotnet test --filter BoardWalkCost` (the
 * same instrument production prints from): the healing build went **238.8 MB → 145.5 MB** traced, its `window` stage
 * **225.8 MB → 132.4 MB**, and the wall time stayed **flat** (701 → 708 ms). The bytes came from two things, neither of them a
 * string: pass 1 used to copy every accepted heal into a `List<(double, HealRecord)>` (24 bytes each, plus the arrays its growth
 * copies through) where it now walks the source range when nothing was dropped, and the six per-segment time-segment maps are
 * reused across segments instead of allocated 4,452 times. Writing this test down is also what caught that the key memo already
 * existed: a "fix" for a per-record concat measured against a helper that already memoized was redundant, and the honest A/B said
 * so. Flat time is the standing lesson of this line of work — at these volumes allocation costs memory, not milliseconds, because
 * hashing and probing dominate either way. Time is bought by NOT walking (B13), not by walking lighter.
 *
 * The assertion is an exact equality rather than a threshold because the law is exact: this fixture contains 60 pairs and no
 * others, so 60 builds is "memoized" and anything near 30,000 is "concatenating per record again". A threshold would let a
 * half-restoration through and would need re-tuning whenever the builder's internals move.
 *
 * Behaviour is not this test's job: the healing golden (`HealingBoardGoldenTest`) freezes the board byte-for-byte over a real
 * fixture through both branches pass 1 can take — everything accepted (the zero-copy run) and rejections present, which is
 * where the index list gets seeded — including the no-swarm-pets setting that drops pet heals.
 */
[TestClass]
public class HealingWindowKeyTest
{
  private const int Healers = 6;
  private const int HealedPerHealer = 10;
  private const int Rounds = 500;

  [TestMethod]
  public void AHealerHealedKeyIsBuiltOncePerPairRatherThanOncePerHeal()
  {
    // CountedAsOurs asks the registry first, and the harness clears it for exactly this reason: a name remembered by an
    // earlier test must not decide whether this fixture's heals count at all.
    PlayerRegistry.Instance.Clear();

    var heals = new List<(double Time, HealRecord Record)>(Healers * HealedPerHealer * Rounds);
    var time = 1_000d;
    for (var round = 0; round < Rounds; round++)
    {
      for (var h = 0; h < Healers; h++)
      {
        for (var p = 0; p < HealedPerHealer; p++)
        {
          heals.Add((time, new HealRecord
          {
            Healer = NameOf(h),
            Healed = NameOf(Healers + p),
            Type = Labels.Heal,
            SubType = "Superior Healing",
            Total = 100 + (uint)p,
          }));

          // Ten heals to the second: pairs stay hot inside a segment rather than being re-missed every second.
          time += 0.1;
        }
      }
    }

    var expectedPairs = Healers * HealedPerHealer;
    var lastTime = time;

    // A builder of our own: the singleton carries a night's state between tests, and this law is about one build.
    var builder = new HealingStatsBuilder();
    var actionsSeen = 0;
    long boardTotal = -1;

    void OnStatus(StatsGenerationEvent e)
    {
      if (e.State != "COMPLETED") return;
      actionsSeen = 0;
      foreach (var segment in e.Groups)
      {
        foreach (var block in segment) actionsSeen += block.Actions.Count;
      }

      boardTotal = e.CombinedStats?.RaidStats.Total ?? -1;
    }

    builder.EventsGenerationStatus += OnStatus;
    try
    {
      builder.BuildTotalStats(new GenerateStatsOptions
      {
        AllRanges = BoardRange(lastTime),
        Heals = heals,
        Source = "healing window key test",
      });
    }
    finally
    {
      builder.EventsGenerationStatus -= OnStatus;
    }

    // The pass has to have RUN for the key count to mean anything: every heal on the board, and the board's total equal to
    // what was fed in (100..109 per round across 60 pairs). A build that silently dropped the list would answer 0 keys here.
    Assert.AreEqual(heals.Count, actionsSeen,
      $"the healing build put {actionsSeen:N0} of {heals.Count:N0} heals on the board (keys built: {builder.PairKeyBuilds:N0}): " +
      "either a name was refused or the completion event does not carry every segment");
    const long ExpectedTotal = Rounds * (long)(HealedPerHealer * 100 + (HealedPerHealer - 1) * HealedPerHealer / 2) * Healers;
    Assert.AreEqual(ExpectedTotal, boardTotal, "the board's total is not what was fed in: the build did not scan this list");

    Assert.AreEqual(expectedPairs, builder.PairKeyBuilds,
      $"the composite healer/healed key was built {builder.PairKeyBuilds:N0} times for {heals.Count:N0} heals across " +
      $"{expectedPairs} pairs: that is a string per heal line again (2.65 M of them over a night's select-all)");
  }

    /*
     * Letters only, every one distinct: `CountedAsOurs` asks `PlayerRegistry.IsPossiblePlayerName`, and a digit is not something
     * this game writes inside a character name -- a name it refuses takes that heal off the board, which is a different question
     * from the one this test asks.
     */
    private static string NameOf(int index) => $"Zyl{(char)('a' + index / 26)}{(char)('a' + index % 26)}";

    /*
     * A range WITH a segment, not `new TimeRange()`: the healing build walks the selection's time segments (pass 1 iterates
     * `_raidTotals.Ranges.TimeSegments`, filled from AllRanges), so an empty range means "no windows to fill" and every heal is
     * quietly uncounted -- the way this test first failed, at zero. Production passes the selection's own AllRanges, which is why
     * the derived board never sees this; a synthetic build has to say it out loud.
     */
    private static TimeRange BoardRange(double lastTime)
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(1_000d, lastTime));
      return range;
    }
}

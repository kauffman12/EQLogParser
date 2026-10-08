using EQLogParser;

namespace EQLogParserTest
{
  /*
   * The first thing an incremental refresh needs from the counters: publishing a report may not drain the accumulator the next batch is still
   * writing into. `BestSecTemp` holds the CURRENT second's running total, and finalizing folded it into `BestSec` and zeroed it — so a board
   * published mid-second, followed by more damage in that same second, hands back the second half as that name's best second (60 where one
   * uninterrupted pass says 160). Nothing publishes twice per build today, which is why only a test can see it — and it is exactly why a
   * resumed count would be wrong rather than merely unimplemented, so this is the law the resume is built on, not a note about the future.
   *
   * Written against the uninterrupted pass rather than a hand-computed number, which is the actual requirement: publish → count → publish equals
   * count → publish on every field the report reads. See docs/incremental-summary-refresh.md §3.3.
   */
  [TestClass]
  public sealed class StatResumeTest
  {
    private static DamageRecord Hit(uint total) =>
      new() { Attacker = "Zomk", Defender = "Fen Claw", Type = Labels.Melee, SubType = "Hits", Total = total };

    private static PlayerStats Raid() => new() { Name = "Raid", Total = 10_000, TotalSeconds = 10 };

    [TestMethod]
    public void APublishedReportLeavesTheCurrentSecondAbleToGrow()
    {
      var published = new PlayerSubStats { Name = "Zomk" };
      var uninterrupted = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(published, Hit(100), newFrame: true);
      StatsUtil.UpdateCalculations(published, Raid());   // a board goes out while this second is still running
      StatsUtil.UpdateDamageStats(published, Hit(60));   // the same second keeps arriving

      StatsUtil.UpdateDamageStats(uninterrupted, Hit(100), newFrame: true);
      StatsUtil.UpdateDamageStats(uninterrupted, Hit(60));

      // Both sides are read at the same point in their own story: one board for each 160 that landed inside one second.
      StatsUtil.UpdateCalculations(uninterrupted, Raid());
      StatsUtil.UpdateCalculations(published, Raid());

      Assert.AreEqual(uninterrupted.Total, published.Total, "totals were always additive");
      Assert.AreEqual(160L, uninterrupted.BestSec, "setup: one second of damage is 160");
      Assert.AreEqual(uninterrupted.BestSec, published.BestSec,
        "the resumed count says the same thing; draining the running second during a publish reported 100 here");

      StatsUtil.UpdateCalculations(published, Raid());
      Assert.AreEqual(uninterrupted.BestSec, published.BestSec, "finalizing twice must not invent a second second of damage");
      Assert.AreEqual(uninterrupted.Total, published.Total);
      Assert.AreEqual(160L, published.BestSecTemp, "the accumulator is still whole after two publications — raw state outlives the report");
    }

    /*
     * A new second after a publish still starts its own running total, which is the half that must NOT change: leaving the accumulator intact
     * is not the same as letting one second's total carry into the next.
     */
    [TestMethod]
    public void ANewSecondAfterAPublishStartsFresh()
    {
      var stats = new PlayerSubStats { Name = "Zomk" };

      StatsUtil.UpdateDamageStats(stats, Hit(100), newFrame: true);
      StatsUtil.UpdateCalculations(stats, Raid());
      StatsUtil.UpdateDamageStats(stats, Hit(20), newFrame: true);   // the caller's clock says a new second began
      StatsUtil.UpdateCalculations(stats, Raid());

      Assert.AreEqual(120L, stats.Total);
      Assert.AreEqual(100L, stats.BestSec, "the best second is the 100-second, not 120 and not 20");
    }
  }
}

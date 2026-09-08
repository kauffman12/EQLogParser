using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The category switches — my damage, damage on me, healing either way (FctIngest.ShowDealt/ShowTaken/ShowHeals).
   * These answer "what kind of story does this overlay tell", which is a different question from the threshold's
   * "which numbers are too small to matter", and the tests below keep them that way apart: separate counts, gate
   * before folding, labels following their side, and the configure demo obeying whatever the row says. The use case
   * that shaped the set is a real one: outgoing damage left, healing right, incoming damage off entirely.
   */
  [TestClass]
  public sealed class FctFilterTest
  {
    private const double Width = 1200;
    private const double Height = 700;

    private static FctIngest Open() => new(new Random(11));

    private static FctHitState Take(FctIngest ingest, List<FctHitState> hits, FctLane lane, double value, string source = "Bite",
      bool crit = false, string? text = null, double now = 0) =>
      ingest.Accept(hits, lane, value, source, crit, false, false, text, Width, Height, now);

    [TestMethod]
    public void EverythingShowsAndNothingIsCountedUntilSomebodyAsks()
    {
      var ingest = Open();
      var hits = new List<FctHitState>();

      Assert.IsNotNull(Take(ingest, hits, FctLane.DamageDealt, 500));
      Assert.IsNotNull(Take(ingest, hits, FctLane.DamageTaken, 400));
      Assert.IsNotNull(Take(ingest, hits, FctLane.HealingDealt, 300));
      Assert.IsNotNull(Take(ingest, hits, FctLane.HealingReceived, 200));

      Assert.AreEqual(0, ingest.FilteredCount, "a default overlay filters nothing");
      Assert.AreEqual(4, hits.Count);
    }

    [TestMethod]
    public void EachSwitchHidesItsOwnCategoryAndCountsTheRest()
    {
      var cases = new (Action<FctIngest> mute, FctLane gone, FctLane stays)[]
      {
        (i => i.ShowTaken = false, FctLane.DamageTaken, FctLane.DamageDealt),
        (i => i.ShowDealt = false, FctLane.DamageDealt, FctLane.DamageTaken),
      };

      foreach (var (mute, gone, stays) in cases)
      {
        var ingest = Open();
        mute(ingest);
        var hits = new List<FctHitState>();

        Assert.IsNull(Take(ingest, hits, gone, 900), "the switched-off category never spawns");
        Assert.IsNull(Take(ingest, hits, gone, 9001, crit: true), "crits belong to their category too — identity beats size");
        Assert.IsNotNull(Take(ingest, hits, stays, 900), "the other side keeps drawing");

        Assert.AreEqual(2, ingest.FilteredCount, $"{gone} filtered while off");
        Assert.AreEqual(1, hits.Count);
      }
    }

    [TestMethod]
    public void TheHealingSwitchCoversBothDirections()
    {
      var ingest = Open();
      ingest.ShowHeals = false;
      var hits = new List<FctHitState>();

      Assert.IsNull(Take(ingest, hits, FctLane.HealingDealt, 3000));
      Assert.IsNull(Take(ingest, hits, FctLane.HealingReceived, 800));
      Assert.IsNotNull(Take(ingest, hits, FctLane.DamageDealt, 500), "heals off says nothing about damage");

      Assert.AreEqual(2, ingest.FilteredCount);
    }

    /* A word is not a number and is exempt from the threshold — but it is somebody's word: "Miss" belongs to the
     * miss-er, a block on you belongs to you. The gate reads that off the lane, which is where the routing already put it. */
    [TestMethod]
    public void WordsFollowTheirSideNotTheNumberRule()
    {
      var ingest = Open();
      ingest.ShowDealt = false;
      var hits = new List<FctHitState>();

      Assert.IsNull(Take(ingest, hits, FctLane.DamageDealt, 0, text: "Miss"), "my miss is part of my damage story");

      ingest.ShowDealt = true;
      Assert.IsNotNull(Take(ingest, hits, FctLane.DamageDealt, 0, text: "Miss"), "and it comes back with the switch");

      // Defensive routes as incoming (FctLayout.IsIncoming): a resisted spell is an event happening TO its target's owner.
      ingest.ShowDealt = false;
      Assert.IsNotNull(Take(ingest, hits, FctLane.Defensive, 0, text: "Resist"), "words of defense belong to the taken story, not the dealt one");

      ingest.ShowTaken = false;
      Assert.IsNull(Take(ingest, hits, FctLane.DamageTaken, 0, text: "Dodge"), "their miss is their hit attempt on me");
      Assert.IsNull(Take(ingest, hits, FctLane.Defensive, 0, text: "Resist"), "and defense words follow the taken story off with it");

      Assert.AreEqual(3, ingest.FilteredCount, "words count like anything else — nothing goes quietly");
    }

    /* Two filters stacked on one number still tell the truth about which one took it: identity is decided first, so a
     * filtered hit never spends the threshold's budget. */
    [TestMethod]
    public void CategoriesDecideBeforeThresholds()
    {
      var ingest = Open();
      ingest.Threshold = 1000;
      ingest.ShowTaken = false;
      var hits = new List<FctHitState>();

      Assert.IsNull(Take(ingest, hits, FctLane.DamageTaken, 500), "under both filters");
      Assert.IsNull(Take(ingest, hits, FctLane.DamageTaken, 5000), "over the threshold but still the wrong category");

      Assert.AreEqual(2, ingest.FilteredCount, "the category claimed them");
      Assert.AreEqual(0, ingest.HiddenCount, "and the threshold never got to decide");
    }

    /* Folding an invisible number into a visible one would inflate an ×N nobody could account for; because the gate sits
     * above the fold like the threshold does, a category that was off simply has no history when it comes back. */
    [TestMethod]
    public void HiddenNumbersNeverFoldIntoVisibleOnes()
    {
      var ingest = Open();
      var hits = new List<FctHitState>();

      ingest.ShowDealt = false;
      for (var i = 0; i < 3; i++)
      {
        Assert.IsNull(Take(ingest, hits, FctLane.DamageDealt, 412, "Flurry", now: i * 900));
      }

      ingest.ShowDealt = true;
      var visible = Take(ingest, hits, FctLane.DamageDealt, 412, "Flurry", now: 3600);

      Assert.IsNotNull(visible);
      Assert.IsFalse(visible.DisplayText.Contains("×"), $"the invisible ticks must not fold into the first visible one (got {visible.DisplayText})");
    }

    /* The configure demo must be the overlay, not a contradicting diagram: cues of a switched-off category never spawn
     * while the switch says otherwise (FctDemo.Advance copies the gates every frame). */
    [TestMethod]
    public void TheConfigureDemoObeysTheSwitches()
    {
      var gates = Open();
      gates.ShowTaken = false;

      var demo = new FctDemo();
      var spawned = new List<FctHitState>();
      demo.Start(0);
      for (var now = 0.0; now <= FctDemo.CycleMs + 100; now += 50)
      {
        demo.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Shipped, spawned.Add, null, gates);
      }

      Assert.IsTrue(spawned.Count > 0, "the loop still runs — filtering is not pausing");
      Assert.IsFalse(spawned.Any(h => h.Incoming && !h.Heal), "and it never spawns the category that was switched off");
    }
  }
}

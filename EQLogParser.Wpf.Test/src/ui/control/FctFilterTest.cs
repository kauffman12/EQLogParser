using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * The category switches — my damage, damage to me, healing either way, procs (FctIngest.ShowDealt/ShowTaken/
   * ShowHeals/ShowProcs).
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

    /* Procs asked for their own switch: a proc line can read as double-counting the swing that triggered it, so the
       player who wants one often does not want the other. It is an extra opt-out on top of categories — every gate
       still counts what it removes, and turning procs back on never resurrects what was hidden. */
    [TestMethod]
    public void ProcsHaveTheirOwnSwitchOnTopOfTheirCategory()
    {
      var ingest = Open();
      var hits = new List<FctHitState>();

      bool Proc(double value, double now) =>
        ingest.Accept(hits, FctLane.DamageDealt, value, "Flurry", false, false, false, null, Width, Height, now, proc: true) != null;

      Assert.IsTrue(Proc(100, 0), "by default procs are part of the show");
      Assert.AreEqual(1, hits.Count);

      ingest.ShowProcs = false;

      // Off, they do not render — and they are counted, never silently dropped.
      Assert.IsFalse(Proc(200, 1));
      Assert.AreEqual(1, hits.Count, "a proc nobody asked for stays out of the hit list");
      Assert.AreEqual(1, ingest.FilteredCount, "filtered beside the other kinds somebody switched off");

      // Plain damage is none of this switch's business...
      Assert.IsNotNull(Take(ingest, hits, FctLane.DamageDealt, 300, now: 2));

      // ...and nothing hidden comes back when procs return.
      ingest.ShowProcs = true;
      Assert.IsTrue(Proc(400, 3));
      Assert.AreEqual(3, hits.Count);
    }

    /*
     * The word switches: every event word the parser can produce (FctManager's IsDefensiveLabel set plus Resist) stands
     * on its own, mutes only itself, lands in `filtered` and never in `hidden` — the threshold's count is about numbers,
     * and these are not numbers. Each case uses the word's natural lane: outgoing failures (Miss, Resist) travel the
     * Missed lane, defended events travel Defensive.
     */
    [TestMethod]
    public void EveryWordHasItsOwnSwitchAndNobodyElses()
    {
      var words = new (FctLane Lane, string Word, Action<FctIngest> Mute)[]
      {
        (FctLane.Missed, Labels.Miss, i => i.ShowMiss = false),
        (FctLane.Defensive, Labels.Parry, i => i.ShowParry = false),
        (FctLane.Defensive, Labels.Dodge, i => i.ShowDodge = false),
        (FctLane.Defensive, Labels.Block, i => i.ShowBlock = false),
        (FctLane.Defensive, Labels.Riposte, i => i.ShowRiposte = false),
        (FctLane.Missed, Labels.Resist, i => i.ShowResist = false),
        (FctLane.Defensive, Labels.Absorb, i => i.ShowAbsorb = false),
        (FctLane.Defensive, Labels.Invulnerable, i => i.ShowInvulnerable = false),
      };

      foreach (var (lane, word, mute) in words)
      {
        // default: every word shows and nothing is counted
        var open = Open();
        Assert.IsNotNull(Take(open, new List<FctHitState>(), lane, 0, text: word), $"{word} shows by default");
        Assert.AreEqual(0, open.FilteredCount);

        // muted: it never spawns, it is counted, and its neighbours are untouched
        var ingest = Open();
        mute(ingest);
        var hits = new List<FctHitState>();
        Assert.IsNull(Take(ingest, hits, lane, 0, text: word), $"{word} is switched off");
        Assert.AreEqual(1, ingest.FilteredCount, $"{word} lands in the filtered count");
        Assert.AreEqual(0, ingest.HiddenCount, "words are not numbers — they never visit the threshold's count");

        foreach (var (_, other, _) in words)
        {
          if (other != word)
          {
            Assert.IsNotNull(Take(ingest, hits, lane, 0, text: other), $"{other} survived {word}'s switch");
          }
        }
      }
    }

    /* Words these switches have never heard of draw: an unknown text is not silently somebody's opt-out side effect,
     * and a future parser word cannot arrive already muted. */
    [TestMethod]
    public void UnknownWordsAreNobodyToSwitchOff()
    {
      var ingest = Open();
      ingest.ShowMiss = false;
      ingest.ShowParry = false;
      ingest.ShowDodge = false;
      ingest.ShowBlock = false;
      ingest.ShowRiposte = false;
      ingest.ShowResist = false;
      ingest.ShowAbsorb = false;
      ingest.ShowInvulnerable = false;

      var hits = new List<FctHitState>();
      Assert.IsNotNull(Take(ingest, hits, FctLane.Missed, 0, text: "Flub"));
      Assert.AreEqual(0, ingest.FilteredCount, "a word outside the set passes every switch clean");
    }

    /* The demo has to carry the vocabulary it teaches: all eight words appear in one cycle, and switches — procs'
     * included, which once silently failed to reach the demo's own ingest — mute them there exactly as live. */
    [TestMethod]
    public void TheConfigureDemoCarriesAndObeysTheWords()
    {
      var all = new[] { Labels.Miss, Labels.Parry, Labels.Dodge, Labels.Block, Labels.Riposte, Labels.Resist, Labels.Absorb, Labels.Invulnerable };

      var demo = new FctDemo();
      var spawned = new List<FctHitState>();
      demo.Start(0);
      for (var now = 0.0; now <= FctDemo.CycleMs + 100; now += 50)
      {
        demo.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Shipped, spawned.Add, null);
      }

      foreach (var word in all)
      {
        Assert.IsTrue(spawned.Any(h => h.FixedText == word), $"the cycle shows {word} once — a switch nobody can preview is a switch nobody finds");
      }

      var gates = Open();
      gates.ShowDodge = false;
      gates.ShowProcs = false;
      var muted = new List<FctHitState>();
      var demo2 = new FctDemo();
      demo2.Start(0);
      for (var now = 0.0; now <= FctDemo.CycleMs + 100; now += 50)
      {
        demo2.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Shipped, muted.Add, null, gates);
      }

      Assert.IsFalse(muted.Any(h => h.FixedText == Labels.Dodge), "dodge obeys its switch inside the demo");
      Assert.IsTrue(muted.Any(h => h.FixedText == Labels.Miss), "and only that word goes quiet");
      Assert.IsFalse(muted.Any(h => h.Proc), "the proc switch reaches the demo's ingest too");
    }
  }
}
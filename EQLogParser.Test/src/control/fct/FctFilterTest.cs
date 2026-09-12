using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * What the overlay is allowed to draw: the seventeen switches of the show list (FctShowList) — nine rows of numbers and eight
   * event words — plus the three side switches that hide a whole half of the fight at once (FctIngest.ShowDealt/ShowTaken/ShowHeals,
   * which the lanes now feed).
   *
   * These answer "what kind of story does this overlay tell", which is a different question from the threshold's "which numbers are
   * too small to matter", and the tests below keep them that way apart: separate counts, gate before folding, labels following their
   * side, and the configure demo obeying whatever the row says. The use case that shaped the set is a real one: outgoing damage left,
   * healing right, incoming damage off entirely.
   *
   * "Before folding" is load-bearing rather than pedantic. A switch that stops new numbers from spawning but still lets values merge
   * into a live cell would look like it worked while quietly growing a number the player can see out of hits they had hidden — the
   * worst kind of filter, one that is wrong in the direction nothing shows. So the tests below hide a row whose value matches a cell
   * already on screen and assert the visible fold never moved.
   */
  [TestClass]
  public sealed class FctFilterTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
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
        demo.Advance(now, 800, 560, FctMotionStyle.Freeze, FctLayoutChoice.Bands, spawned.Add, null, gates);
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
        demo.Advance(now, 800, 560, FctMotionStyle.Freeze, FctLayoutChoice.Bands, spawned.Add, null);
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
        demo2.Advance(now, 800, 560, FctMotionStyle.Freeze, FctLayoutChoice.Bands, muted.Add, null, gates);
      }

      Assert.IsFalse(muted.Any(h => h.FixedText == Labels.Dodge), "dodge obeys its switch inside the demo");
      Assert.IsTrue(muted.Any(h => h.FixedText == Labels.Miss), "and only that word goes quiet");
      Assert.IsFalse(muted.Any(h => h.Proc), "the proc switch reaches the demo's ingest too");
    }

    /*
     * A row switched off: no spawn, no fold, still counted. The value below is deliberately identical to the one already on screen,
     * same source and amount, which is exactly what folds — so this asserts the hidden hit could have joined a visible number and did
     * not. FilteredCount rising is the other half of the promise: "hidden, not lost", the same accounting as the threshold's, because a
     * player who mutes something deserves to know it kept happening.
     */
    [TestMethod]
    public void AHiddenRowSpawnsNothingAndFoldsNothing()
    {
      var ingest = Open();
      ingest.Accumulate = true; // the promise is that a hidden row folds nothing EVEN with accumulation on; door itself: FctAccumulationTest
      var hits = new List<FctHitState>();

      Assert.IsNotNull(TakeRow(ingest, hits, FctLane.DamageDealt, 500, "Slash", row: FctRow.MeleeHits), "every row ships visible");
      Assert.AreEqual(1, hits[0].MergeCount);

      ingest.SetRowShown(FctRow.MeleeHits, shown: false);

      Assert.IsNull(TakeRow(ingest, hits, FctLane.DamageDealt, 500, "Slash", row: FctRow.MeleeHits, now: 200), "a hidden row must not spawn");
      Assert.AreEqual(1, hits.Count, "and must not spawn a second copy either");
      Assert.AreEqual(1, hits[0].MergeCount, "or fold itself into the number that is still showing");
      Assert.AreEqual(1, ingest.FilteredCount, "hidden hits are counted, never silently dropped");

      // and the row beside it is untouched: muting melee must not be a way of muting spells
      Assert.IsNotNull(TakeRow(ingest, hits, FctLane.DamageDealt, 500, "Harmonious Strike", row: FctRow.SpellHits, now: 300));
    }

    /*
     * Rows are independent in both directions. Each pair — hits/crits, mine/the pet's, healing/healing crits — exists because somebody
     * wanted one without the other, so a test that only checks "off hides it" would pass while off also hid its neighbour, which is the
     * failure mode of any implementation that resolves several rows onto one switch.
     */
    [TestMethod]
    public void EachRowAnswersForItselfAlone()
    {
      AssertOffLeavesOthers(FctRow.MeleeHits, (FctLane.DamageDealt, "Slash", FctRow.MeleeCrits));
      AssertOffLeavesOthers(FctRow.MeleeCrits, (FctLane.DamageDealt, "Slash", FctRow.MeleeHits));
      AssertOffLeavesOthers(FctRow.SpellHits, (FctLane.DamageDealt, "Harmonious Strike", FctRow.SpellCrits));
      AssertOffLeavesOthers(FctRow.SpellCrits, (FctLane.DamageDealt, "Harmonious Strike", FctRow.SpellHits));
      AssertOffLeavesOthers(FctRow.PetMelee, (FctLane.DamageDealt, "Claw", FctRow.MeleeHits));
      AssertOffLeavesOthers(FctRow.PetSpells, (FctLane.DamageDealt, "Sonic Shock", FctRow.PetMelee));
      AssertOffLeavesOthers(FctRow.Healing, (FctLane.HealingReceived, "Complete Heal", FctRow.HealingCrits));
      AssertOffLeavesOthers(FctRow.HealingCrits, (FctLane.HealingReceived, "Complete Heal", FctRow.Healing));
      AssertOffLeavesOthers(FctRow.Procs, (FctLane.DamageDealt, "Flash of Brilliance", FctRow.SpellHits));
    }

    private static void AssertOffLeavesOthers(FctRow muted, (FctLane lane, string source, FctRow other) keeps)
    {
      var ingest = Open();
      var hits = new List<FctHitState>();
      ingest.SetRowShown(muted, shown: false);

      Assert.IsNull(TakeRow(ingest, hits, muted == FctRow.Healing || muted == FctRow.HealingCrits ? FctLane.HealingReceived : FctLane.DamageDealt,
        500, muted == FctRow.Procs ? "Flash of Brilliance" : "Slash", row: muted, now: 0), $"{muted} must hide its own numbers");
      Assert.IsNotNull(TakeRow(ingest, hits, keeps.lane, 700, keeps.source, row: keeps.other, now: 100),
        $"muting {muted} must not mute {keeps.other} beside it");
    }

    /*
     * Words are not rows (FctRow.Word): their switch is the word itself, chosen by FctIngest.WordShown from the text the parser put on
     * the record. Pinning that keeps a future edit from routing words through the row table, which would either silently ungated them or
     * needed a row per word that the panel does not offer. It also pins the one behaviour players argue about: a word is an attack, so it
     * follows its side (FctIngest.ShowDealt/ShowTaken) and disappears with a lane set to none — the block you caused, not the block you ate.
     */
    [TestMethod]
    public void WordsAnswerToTheirTextAndToTheirSide()
    {
      var ingest = Open();
      var hits = new List<FctHitState>();

      // a word carries no row and is unaffected by every row switch
      ingest.SetRowShown(FctRow.MeleeHits, shown: false);
      ingest.SetRowShown(FctRow.HealingCrits, shown: false);
      Assert.IsNotNull(Take(ingest, hits, FctLane.Missed, 0, text: Labels.Miss), "a word has no row to hide behind");

      // and it does obey its word, and only its word
      var side = Open();
      var missed = new List<FctHitState>();
      Assert.IsNotNull(Take(side, missed, FctLane.Missed, 0, text: Labels.Miss));
      Assert.IsNotNull(Take(side, missed, FctLane.Defensive, 0, text: Labels.Block, now: 10));

      side.ShowDealt = false; // the dealt lane set to none (FctConfigState.OutgoingShown)
      Assert.IsNull(Take(side, missed, FctLane.Missed, 0, text: Labels.Dodge, now: 20), "my own failed attack goes with my hidden side");
      Assert.IsNotNull(Take(side, missed, FctLane.Defensive, 0, text: Labels.Riposte, now: 30), "the words about attacks on me stay");
    }

    /* Accept with a row named, which is all the row tests add to the shared Take helper. */
    private static FctHitState TakeRow(FctIngest ingest, List<FctHitState> hits, FctLane lane, double value, string source, FctRow row,
      bool crit = false, double now = 0) =>
      ingest.Accept(hits, lane, value, source, crit, false, false, null, Width, Height, now, row: row);
  }
}
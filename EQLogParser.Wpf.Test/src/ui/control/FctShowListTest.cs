using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * The show list as a table (FctShowList) and the snapshot it travels in (FctConfigState): what the dropdown is built from, what the
   * overlay loads and saves, and what a lane set to "none" does to a side. These are the parts of the redesign that are data rather than
   * drawing, which makes them the parts worth pinning — a row that exists in the enum but not in the table is a number no switch can hide,
   * and a table that quietly drops an entry takes its settings.ini key with it, so a player's saved preference becomes a different row.
   */
  [TestClass]
  public sealed class FctShowListTest
  {
    /* Seventeen, the way the panel reads them: nine rows of numbers and eight words, one alphabetical list. */
    [TestMethod]
    public void TheTableIsThePanel()
    {
      Assert.AreEqual(9, FctShowList.Rows.Count);
      Assert.AreEqual(8, FctShowList.Words.Count);
      Assert.AreEqual(FctShowList.Rows.Count + FctShowList.Words.Count, FctShowList.All.Count);

      // alphabetical because it is a lookup list, not a narrative about a fight
      var labels = FctShowList.All.Select(e => e.Label).ToList();
      var sorted = labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
      CollectionAssert.AreEqual(sorted, labels, "the dropdown is alphabetical: a player looks for a word");

      // two entries sharing a label or a key would either confuse the list or overwrite one setting with another
      Assert.AreEqual(labels.Count, labels.Distinct().Count(), "duplicate label in the show list");
      var keys = FctShowList.All.Select(e => e.Key).ToList();
      Assert.AreEqual(keys.Count, keys.Distinct().Count(), "two entries writing one settings.ini key");

      foreach (var row in FctShowList.Rows)
      {
        Assert.IsFalse(row.IsWord, $"{row.Label} is a row, it gates by what the record is (FctRow)");
      }

      foreach (var word in FctShowList.Words)
      {
        Assert.IsTrue(word.IsWord, $"{word.Label} is a word and needs its Labels constant to gate by text");
        Assert.AreEqual(FctRow.Word, word.Row, "a word has no row of its own; FctIngest.WordShown reads its text instead");
      }
    }

    /*
     * Every real row offered exactly once. FctRow.Word is deliberately absent — it means "not row-gated" — so the enum and the table have to
     * agree on that one exception, and this is where a row added to the enum without a line in the table gets caught: the switch would not
     * exist, so nothing could hide those numbers, which reads as an overlay ignoring its own settings rather than as a missing entry.
     */
    [TestMethod]
    public void EveryRowHasExactlyOneSwitch()
    {
      var rows = FctShowList.Rows.Select(e => e.Row).ToList();

      foreach (var row in Enum.GetValues<FctRow>())
      {
        Assert.AreEqual(row is FctRow.Word ? 0 : 1, rows.Count(r => r == row),
          row is FctRow.Word ? "FctRow.Word means not row-gated, so it must not have a switch" : $"{row} needs exactly one switch");
      }
    }

    /* A fresh settings.ini means everything on: these are opt-outs, so a first overlay shows the whole fight. */
    [TestMethod]
    public void EverythingShipsOn()
    {
      var state = new FctConfigState();

      foreach (var entry in FctShowList.All)
      {
        Assert.IsTrue(entry.Get(state), $"{entry.Label} must default to shown");
      }

      /* The gates themselves agree, so an overlay that never saw a snapshot is the same overlay as a fresh one. Applying "on" to a
         gate that is already on reports no change, which is what keeps the sample loop from restarting on every checkbox in the panel. */
      var ingest = new FctIngest(new System.Random(7));
      foreach (var entry in FctShowList.All)
      {
        Assert.IsFalse(entry.ApplyTo(ingest, shown: true), $"{entry.Label} already shipped on");
      }

      foreach (var row in FctShowList.Rows)
      {
        Assert.IsTrue(ingest.RowShown(row.Row), $"{row.Label} is off on a fresh ingest");
      }
    }

    /* Reading and writing an entry is the same door in both directions — including through Clone, which is how the settings panel stages a
       preview before saving it. A shallow clone would let "Cancel" mutate what the overlay is still drawing. */
    [TestMethod]
    public void EntriesRoundTripThroughAStagedSnapshot()
    {
      var state = new FctConfigState();

      foreach (var entry in FctShowList.All)
      {
        entry.Set(state, false);
      }

      var staged = state.Clone();
      foreach (var entry in FctShowList.All)
      {
        Assert.IsFalse(entry.Get(staged), $"{entry.Label} did not survive the staging copy");
      }

      // and the copy is really a copy: un-hiding one row on the staged snapshot must leave the overlay's own state alone
      var melee = FctShowList.Rows.First(e => e.Row == FctRow.MeleeHits);
      melee.Set(staged, true);
      Assert.IsTrue(melee.Get(staged));
      Assert.IsFalse(melee.Get(state), "the panel's draft and the overlay's live settings are separate states");
    }

    /*
     * A lane set to "none" is how a whole side disappears in split mode — it replaced the old "damage in"/"damage out" switches, so it owes
     * those three behaviours: hide exactly its own side, and leave the other two alone. Healing counts as a side here (FctConfigState.
     * HealingShown), which is the sentence the redesign was for: damage left, healing right, incoming gone.
     */
    [TestMethod]
    public void ALaneSetToNoneHidesItsSideAlone()
    {
      var shown = new FctConfigState();
      Assert.IsTrue(shown.OutgoingShown && shown.IncomingShown && shown.HealingShown, "the shipped layout shows all three");

      var dealtOff = new FctConfigState { DealtLane = FctRailLane.None };
      Assert.IsFalse(dealtOff.OutgoingShown);
      Assert.IsTrue(dealtOff.IncomingShown && dealtOff.HealingShown, "hiding what I deal must not hide anything else");

      var takenOff = new FctConfigState { TakenLane = FctRailLane.None };
      Assert.IsFalse(takenOff.IncomingShown);
      Assert.IsTrue(takenOff.OutgoingShown && takenOff.HealingShown);

      var healsOff = new FctConfigState { HealLane = FctRailLane.None };
      Assert.IsFalse(healsOff.HealingShown);
      Assert.IsTrue(healsOff.OutgoingShown && healsOff.IncomingShown);
    }

    /*
     * Fountain has no columns to hand out, so it has no lane to switch off and reads none as shown rather than hiding a side nobody asked to
     * hide. The setting survives the round trip untouched — a player who picks "none", plays with a fountain, then goes back to split gets the
     * side back off, which is what writing it down was for.
     */
    [TestMethod]
    public void FountainHasNoLanesToHide()
    {
      var fountain = new FctConfigState
      {
        Fountain = true,
        DealtLane = FctRailLane.None,
        TakenLane = FctRailLane.None,
        HealLane = FctRailLane.None,
      };

      Assert.IsTrue(fountain.OutgoingShown && fountain.IncomingShown && fountain.HealingShown,
        "a fountain puts everything on screen whatever the lane combos say");

      var back = fountain.Clone();
      back.Fountain = false;
      Assert.IsFalse(back.OutgoingShown || back.IncomingShown || back.HealingShown,
        "the hidden sides are kept, not resolved away, so returning to split restores what was switched off");
    }

    /* Geometry never sees the none lane: a hidden category is parked in its shipped column (FctConfigState.Placed) so no stage, stream or cell has to
       invent a shape for a lane that does not exist. Nothing spawns there — FctIngest stops it upstream — so the column decides nothing. */
    [TestMethod]
    public void HiddenLanesStillGiveGeometryARealColumn()
    {
      var hidden = new FctConfigState
      {
        Fountain = false,
        DealtLane = FctRailLane.None,
        TakenLane = FctRailLane.None,
        HealLane = FctRailLane.None,
      }.BuildLayout();

      Assert.AreEqual(FctLayoutMode.ByType, hidden.Mode);
      Assert.AreEqual(FctConfigState.DealtLaneDefault, hidden.OutgoingDamageLane);
      Assert.AreEqual(FctConfigState.TakenLaneDefault, hidden.IncomingDamageLane);
      Assert.AreEqual(FctConfigState.HealLaneDefault, hidden.HealLane);
    }

    /* The ini spelling of the off position, both ways. This is the seam where a hand-written settings.ini arrives, and "none" arriving as
       left 1 would show a side somebody deleted on purpose. */
    [TestMethod]
    public void NoneIsARealSettingsToken()
    {
      Assert.AreEqual("none", FctRailLanes.Token(FctRailLane.None));
      Assert.AreEqual(FctRailLane.None, FctRailLanes.Parse("none", FctRailLane.Left1));
      Assert.AreEqual(FctRailLane.None, FctRailLanes.Parse("NONE ", FctRailLane.Left1));
      Assert.AreEqual(-1, FctRailLanes.Index(FctRailLane.None), "no column of its own");

      // garbage still falls back rather than hiding a side nobody switched off
      Assert.AreEqual(FctRailLane.Right2, FctRailLanes.Parse("nowhere", FctRailLane.Right2));
      Assert.AreEqual(FctRailLane.Left2, FctRailLanes.Parse(null, FctRailLane.Left2));
    }

    /* The list the dropdown is built from must also be the list that gets saved, in both kinds: a word saved under a row's key would come back as
       a different switch after a restart, which is the kind of bug nobody can reproduce because it needs a save and a launch to show. */
    [TestMethod]
    public void OneSnapshotDrivesEverySwitch()
    {
      var state = new FctConfigState();
      FctShowList.SetAll(state, false);

      foreach (var entry in FctShowList.All)
      {
        Assert.IsFalse(entry.Get(state), $"{entry.Label} survived SetAll");
      }

      FctShowList.SetAll(state, true);
      var ingest = new FctIngest(new System.Random(9));
      var changed = 0;

      foreach (var entry in FctShowList.All)
      {
        // applying a gate that was already on reports no change, which is what stops the sample loop restarting for nothing
        changed += entry.ApplyTo(ingest, shown: true) ? 1 : 0;
      }

      Assert.AreEqual(0, changed, "a fresh ingest already matches an all-on snapshot");

      /* The snapshot is what the canvas replays onto the gates (FctSkiaCanvas.ApplyGates), so a state that says everything off has to
         silence every kind of number — including the words, whose switches are separate fields on the ingest. If any survived, the panel
         would be showing "nothing selected" over an overlay that still drew it. */
      foreach (var entry in FctShowList.All)
      {
        entry.ApplyTo(ingest, shown: false);
      }

      var hits = new List<FctHitState>();
      foreach (var row in FctShowList.Rows)
      {
        Assert.IsNull(Probe(ingest, hits, row.Row), $"{row.Label} drew while its switch was off");
      }

      Assert.IsNull(Probe(ingest, hits, FctRow.Word, Labels.Miss), "a word drew while every switch was off");

      // and one switch back on draws exactly its own kind
      FctShowList.Rows.First(e => e.Row == FctRow.MeleeHits).ApplyTo(ingest, shown: true);
      Assert.IsNotNull(Probe(ingest, hits, FctRow.MeleeHits), "the snapshot's write never reached the gate");
      Assert.IsNull(Probe(ingest, hits, FctRow.SpellHits, nowMs: 60_000));
    }

    /* One probe through the same Accept the overlay uses, so what is under test is the real gate and not a helper's opinion of it. */
    private static FctHitState Probe(FctIngest ingest, List<FctHitState> hits, FctRow row, string? word = null, double nowMs = 0) =>
      ingest.Accept(hits, FctLane.DamageDealt, 500, "Slash", crit: false, minor: false, periodic: false, fixedText: word,
        900, 600, nowMs, proc: false, row: row);
  }
}

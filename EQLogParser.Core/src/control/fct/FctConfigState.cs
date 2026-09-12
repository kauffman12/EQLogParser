using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * A snapshot of every choice the settings window offers, in the flat form both windows want: the overlay hands one over
   * when configure mode opens (built from what is staged or saved), receives fresh ones on every control change for the
   * live preview, and gets a final copy at Save to write. It carries plain fields because it crosses a window boundary —
   * no behaviour beyond naming what the engine calls each pick, and even that is a mapping, not a decision: the window is
   * furniture around the overlay's canvas, and the ini remains the overlay's business alone.
   */
  internal sealed class FctConfigState
  {
    public bool Fountain;
    public FctMotionStyle Shape = FctMotionStyle.Arc;

    /* Split's three categories: which of the four columns each streams down (FctRailLane) and which way it travels. The
       shipped spread gives every category its own column; a lane can also be FctRailLane.None, which is how a category gets
       switched off in split mode — see OutgoingShown and friends.

       Two categories may share a column — that is what lanes are for — but only while they travel the same way, because a
       column is one queue (FctConveyor) and two trains driving into each other through one queue cannot be spaced by
       anything. ResolveLaneConflicts enforces it; the settings panel does not offer a choice that would need it. */
    /* The shipped split spread: heals left 1 rising, damage in left 2 sinking, damage out right 1 rising - each category its
       own column, and right 2 the free lane, so on first sight the outgoing-damage column owns its whole half (FctStage tiles). */
    internal const FctRailLane HealLaneDefault = FctRailLane.Left1;
    internal const FctRailLane TakenLaneDefault = FctRailLane.Left2;
    internal const FctRailLane DealtLaneDefault = FctRailLane.Right1;

    public FctRailLane HealLane = HealLaneDefault;
    public bool HealUp;
    public FctRailLane TakenLane = TakenLaneDefault;
    public bool TakenUp;
    public FctRailLane DealtLane = DealtLaneDefault;
    public bool DealtUp;

    /*
     * Whether each side has anywhere to go — which, since the lanes grew a "none", is what answers "does this side draw at
     * all": hiding my damage in split mode means giving it no column. Fountain has no lanes to ask; its whole arrangement is
     * the two sides facing each other across a clear middle, so everything draws there and the finer question is left to the
     * rows below (FctIngest's own ShowDealt/ShowTaken/ShowHeals are fed from here).
     *
     * Words follow their side, because a word IS an attack that failed: "miss" is my swing, "block" is theirs, so hiding my
     * outgoing numbers hides my misses with them (FctManager routes each word by who attacked).
     */
    internal bool OutgoingShown => Fountain || DealtLane is not FctRailLane.None;
    internal bool IncomingShown => Fountain || TakenLane is not FctRailLane.None;
    internal bool HealingShown => Fountain || HealLane is not FctRailLane.None;

    public double Threshold;

    /* The nine show-list rows (FctShowList), keyed by row so adding one costs a line in that table rather than a field, a
     * load, a save, a dropdown item and two blocks of assignments. Every row defaults on; a row nobody stored reads as shown
     * (GetRow), which is the same promise FctIngest.RowShown makes about an unknown row and LoadShown makes about a missing
     * key — these are opt-outs, so failing open is the direction that never hides somebody's number by accident. */
    public Dictionary<FctRow, bool> Rows = new()
    {
      [FctRow.MeleeHits] = true,
      [FctRow.MeleeCrits] = true,
      [FctRow.SpellHits] = true,
      [FctRow.SpellCrits] = true,
      [FctRow.PetMelee] = true,
      [FctRow.PetSpells] = true,
      [FctRow.Healing] = true,
      [FctRow.HealingCrits] = true,
      [FctRow.Procs] = true,
    };

    internal bool GetRow(FctRow row) => Rows.TryGetValue(row, out var shown) && shown;

    internal void SetRow(FctRow row, bool shown) => Rows[row] = shown;

    /* The words, one switch each (FctIngest): miss, parry, dodge, block, riposte, resist, absorb, invulnerable. They share
     * the show list with the rows because they answer the same question, and they are addressed by text rather than by row
     * because every complaint about them has been about one word. The threshold never hides a word; only these can. */
    public bool ShowMiss = true;
    public bool ShowParry = true;
    public bool ShowDodge = true;
    public bool ShowBlock = true;
    public bool ShowRiposte = true;
    public bool ShowResist = true;
    public bool ShowAbsorb = true;
    public bool ShowInvulnerable = true;

    /* The words by their Labels constant, so the table that drives the dropdown (FctShowList) and the gate that reads it
     * (FctIngest.WordShown) speak the same identity and neither needs eight names for one kind of thing. An unknown word
     * reads as shown and writes nowhere: a word these switches have never heard of is not somebody's opt-out side effect. */
    internal bool GetWord(string word) => word switch
    {
      Labels.Miss => ShowMiss,
      Labels.Parry => ShowParry,
      Labels.Dodge => ShowDodge,
      Labels.Block => ShowBlock,
      Labels.Riposte => ShowRiposte,
      Labels.Resist => ShowResist,
      Labels.Absorb => ShowAbsorb,
      Labels.Invulnerable => ShowInvulnerable,
      _ => true,
    };

    internal void SetWord(string word, bool shown)
    {
      switch (word)
      {
        case Labels.Miss: ShowMiss = shown; break;
        case Labels.Parry: ShowParry = shown; break;
        case Labels.Dodge: ShowDodge = shown; break;
        case Labels.Block: ShowBlock = shown; break;
        case Labels.Riposte: ShowRiposte = shown; break;
        case Labels.Resist: ShowResist = shown; break;
        case Labels.Absorb: ShowAbsorb = shown; break;
        case Labels.Invulnerable: ShowInvulnerable = shown; break;
      }
    }

    /* Typography rather than layout, so it lives in both modes: where "(source)" sits by its amount. */
    public FctLabelSide LabelSide = FctLabelSide.Below;

    public double TextScale = FctScale.SizeDefault;
    public double CritScale = FctScale.CritSizeDefault;
    public double Speed = FctScale.SpeedDefault;

    /* The demo switch is deliberately NOT a setting (nothing writes it), but the checkbox lives in this window, so it
       rides along in the state for exactly as long as configure mode lasts. */
    public bool SampleData = true;

    /* A copy that can be handed across the window boundary without the two sides sharing a dictionary: previews are
     * disposable, and a preview that edited the staged state behind its owner's back would make Cancel a lie. */
    internal FctConfigState Clone() => new()
    {
      Fountain = Fountain,
      Shape = Shape,
      HealLane = HealLane,
      HealUp = HealUp,
      TakenLane = TakenLane,
      TakenUp = TakenUp,
      DealtLane = DealtLane,
      DealtUp = DealtUp,
      Threshold = Threshold,
      Rows = new Dictionary<FctRow, bool>(Rows),
      ShowMiss = ShowMiss,
      ShowParry = ShowParry,
      ShowDodge = ShowDodge,
      ShowBlock = ShowBlock,
      ShowRiposte = ShowRiposte,
      ShowResist = ShowResist,
      ShowAbsorb = ShowAbsorb,
      ShowInvulnerable = ShowInvulnerable,
      LabelSide = LabelSide,
      TextScale = TextScale,
      CritScale = CritScale,
      Speed = Speed,
      SampleData = SampleData,
    };

    /* The two words the row speaks, resolved to the engine's geometry. Fountain is bands wearing spray — the mode names
     * the motion, and its choices are which way each stream runs: all three dials speak there, healing's included. It
     * has no lane to own in a band, but "heals rise while damage falls" is the most requested sentence in the genre,
     * and a fountain that hid it left players tying their heals to their damage-taken dial by accident. */
    /*
     * Give every category a column it can travel alone in. Priority runs dealt → taken → healing, which is the order people
     * look at them in: my damage keeps the column I aimed for, then what lands on me, then the heals — and a category switched
     * off (FctRailLane.None) owns no column, so it neither conflicts nor gets moved. The move is to the first column nobody has
     * claimed yet, or that only same-direction traffic shares.
     *
     * It runs on the state itself, because there are two ways to arrive here: the settings panel, which cannot express a
     * conflict (LaneAvailable removes those choices), and settings.ini, which somebody can hand-write into one. Returns how
     * many categories were moved so a caller can say so.
     */
    internal int ResolveLaneConflicts()
    {
      if (Fountain)
      {
        return 0;   // bands has no columns to conflict over: every stream owns its side of the strip
      }

      var moved = 0;
      var claimed = new Dictionary<FctRailLane, bool>();

      // the priority, written out once so the loop below stays about the search rather than about which category is which
      (FctRailLane Lane, bool Up, Action<FctRailLane> Move)[] groups =
      [
        (DealtLane, DealtUp, lane => DealtLane = lane),
        (TakenLane, TakenUp, lane => TakenLane = lane),
        (HealLane, HealUp, lane => HealLane = lane),
      ];

      foreach (var group in groups)
      {
        if (group.Lane is FctRailLane.None)
        {
          continue;
        }

        if (!Shares(group.Lane, group.Up, claimed))
        {
          claimed[group.Lane] = group.Up;
          continue;
        }

        var open = FctRailLane.None;
        foreach (var column in FctRailLanes.Columns)
        {
          if (!Shares(column, group.Up, claimed))
          {
            open = column;
            break;
          }
        }

        // nowhere to go means every column is full of opposite traffic; keeping the pick beats inventing a worse one
        if (open is not FctRailLane.None && open != group.Lane)
        {
          group.Move(open);
          moved++;
        }

        claimed[open is FctRailLane.None ? group.Lane : open] = group.Up;
      }

      return moved;
    }

    /* Whether this column and direction would meet a train already booked through it. Free always fits; shared only when the
       traffic runs the same way (FctConveyor's one queue per column). */
    internal static bool LaneAvailable(FctRailLane lane, bool up, FctRailLane other1, bool up1, FctRailLane other2, bool up2) =>
      !Shares(lane, up, other1, up1) && !Shares(lane, up, other2, up2);

    private static bool Shares(FctRailLane lane, bool up, FctRailLane other, bool otherUp) =>
      other is not FctRailLane.None && other == lane && otherUp != up;

    private static bool Shares(FctRailLane lane, bool up, Dictionary<FctRailLane, bool> claimed) =>
      claimed.TryGetValue(lane, out var booked) && booked != up;

    internal FctLayoutChoice BuildLayout()
    {
      // geometry is decided once, so this is where an impossible arrangement has to become a possible one
      ResolveLaneConflicts();

      /* Hidden categories arrive as None rather than parked in some column: an empty lane is a geometric fact now - it
         frees its share of the half to whatever lane is left (FctStage tiles them) - and nothing on a hidden category's
         row ever spawns, so no stage ever has to draw for one. */
      return Fountain
        ? new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left, TakenUp, DealtUp, healUp: HealUp)
        : new FctLayoutChoice(
            FctLayoutMode.ByType, FctRailLanes.SideOf(TakenLane), TakenUp, DealtUp,
            FctRailLanes.SideOf(HealLane), HealUp,
            FctRailLanes.SideOf(TakenLane), FctRailLanes.SideOf(DealtLane),
            HealLane, TakenLane, DealtLane);
    }

    /* The mode clamps the shape, not a branch that ignores it: fountain sprays or freezes, the columns scroll. A state
     * assembled from stale settings resolves to its mode's own motion instead of shipping one the engine would degrade. */
    internal FctMotionStyle BuildMotion() =>
      FctOverlaySettings.ClampShape(Fountain ? FctLayoutMode.Bands : FctLayoutMode.ByType, Shape);

  }
}
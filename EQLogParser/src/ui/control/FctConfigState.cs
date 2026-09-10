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
    public FctMotionStyle Shape = FctMotionStyle.Parabola;

    /* Split's three categories: which of the four columns each streams down (FctRailLane) and which way it travels. The
       shipped spread gives every category its own column; a lane can also be FctRailLane.None, which is how a category gets
       switched off in split mode — see OutgoingShown and friends. */
    internal const FctRailLane HealLaneDefault = FctRailLane.Right1;
    internal const FctRailLane TakenLaneDefault = FctRailLane.Right2;
    internal const FctRailLane DealtLaneDefault = FctRailLane.Left1;

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
    internal FctLayoutChoice BuildLayout() => Fountain
      ? new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left, TakenUp, DealtUp, healUp: HealUp)
      : new FctLayoutChoice(
          FctLayoutMode.ByType, FctRailLanes.SideOf(Placed(TakenLane, TakenLaneDefault)), TakenUp, DealtUp,
          FctRailLanes.SideOf(Placed(HealLane, HealLaneDefault)), HealUp,
          FctRailLanes.SideOf(Placed(TakenLane, TakenLaneDefault)), FctRailLanes.SideOf(Placed(DealtLane, DealtLaneDefault)),
          Placed(HealLane, HealLaneDefault), Placed(TakenLane, TakenLaneDefault), Placed(DealtLane, DealtLaneDefault));

    /* The mode clamps the shape, not a branch that ignores it: fountain sprays or settles, the columns scroll. A state
     * assembled from stale settings resolves to its mode's own motion instead of shipping one the engine would degrade. */
    internal FctMotionStyle BuildMotion() =>
      FctOverlaySettings.ClampShape(Fountain ? FctLayoutMode.Bands : FctLayoutMode.ByType, Shape);

    /* A hidden category is parked in its shipped column rather than carrying FctRailLane.None into geometry: nothing on that
     * row ever spawns (FctIngest stops it), so the column it would have used decides nothing — but a stage, stream or cell
     * should not have to ask what a lane that does not exist looks like. */
    private static FctRailLane Placed(FctRailLane lane, FctRailLane shipped) => lane is FctRailLane.None ? shipped : lane;
  }
}
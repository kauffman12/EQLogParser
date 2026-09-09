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

    /* Split's three categories: which of the four columns each streams down (FctRailLane) and which way it travels. */
    public FctRailLane HealLane = FctRailLane.Right1;
    public bool HealUp;
    public FctRailLane TakenLane = FctRailLane.Right2;
    public bool TakenUp;
    public FctRailLane DealtLane = FctRailLane.Left1;
    public bool DealtUp;

    public double Threshold;
    public bool ShowDealt = true;
    public bool ShowTaken = true;
    public bool ShowHeals = true;
    public bool ShowProcs = true;

    /* The words, one switch each (FctIngest): miss, parry, dodge, block, riposte, resist, absorb, invulnerable. They
     * ride beside the categories because that is where they are gated and where the panel puts them — the "words"
     * combo — not because a word is a category: the threshold never hides one, only these can. */
    public bool ShowMiss = true;
    public bool ShowParry = true;
    public bool ShowDodge = true;
    public bool ShowBlock = true;
    public bool ShowRiposte = true;
    public bool ShowResist = true;
    public bool ShowAbsorb = true;
    public bool ShowInvulnerable = true;

    /* Typography rather than layout, so it lives in both modes: where "(source)" sits by its amount. */
    public FctLabelSide LabelSide = FctLabelSide.Below;

    public double TextScale = FctScale.SizeDefault;
    public double CritScale = FctScale.CritSizeDefault;
    public double Speed = FctScale.SpeedDefault;

    /* The demo switch is deliberately NOT a setting (nothing writes it), but the checkbox lives in this window, so it
       rides along in the state for exactly as long as configure mode lasts. */
    public bool SampleData = true;

    /* The two words the row speaks, resolved to the engine's geometry. Fountain is bands wearing spray — the mode names
     * the motion, and its choices are which way each stream runs: all three dials speak there, healing's included. It
     * has no lane to own in a band, but "heals rise while damage falls" is the most requested sentence in the genre,
     * and a fountain that hid it left players tying their heals to their damage-taken dial by accident. */
    internal FctLayoutChoice BuildLayout() => Fountain
      ? new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left, TakenUp, DealtUp, healUp: HealUp)
      : new FctLayoutChoice(
          FctLayoutMode.ByType, FctRailLanes.SideOf(TakenLane), TakenUp, DealtUp,
          FctRailLanes.SideOf(HealLane), HealUp,
          FctRailLanes.SideOf(TakenLane), FctRailLanes.SideOf(DealtLane),
          HealLane, TakenLane, DealtLane);

    /* The mode clamps the shape, not a branch that ignores it: fountain sprays or settles, the columns scroll. A state
     * assembled from stale settings resolves to its mode's own motion instead of shipping one the engine would degrade. */
    internal FctMotionStyle BuildMotion() =>
      FctOverlaySettings.ClampShape(Fountain ? FctLayoutMode.Bands : FctLayoutMode.ByType, Shape);

    internal FctConfigState Clone() => (FctConfigState)MemberwiseClone();
  }
}
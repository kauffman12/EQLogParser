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

    /* Typography rather than layout, so it lives in both modes: where "(source)" sits by its amount. */
    public FctLabelSide LabelSide = FctLabelSide.Below;

    public double TextScale = FctScale.SizeDefault;
    public double Speed = FctScale.SpeedDefault;

    /* The demo switch is deliberately NOT a setting (nothing writes it), but the checkbox lives in this window, so it
       rides along in the state for exactly as long as configure mode lasts. */
    public bool SampleData = true;

    /* The two words the row speaks, resolved to the engine's geometry. Fountain is bands wearing spray — the mode names
     * the motion, and its only choices are which way each stream runs. */
    internal FctLayoutChoice BuildLayout() => Fountain
      ? new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left, TakenUp, DealtUp)
      : new FctLayoutChoice(
          FctLayoutMode.ByType, FctRailLanes.SideOf(TakenLane), TakenUp, DealtUp,
          FctRailLanes.SideOf(HealLane), HealUp,
          FctRailLanes.SideOf(TakenLane), FctRailLanes.SideOf(DealtLane),
          HealLane, TakenLane, DealtLane);

    internal FctMotionStyle BuildMotion() => Fountain ? FctMotionStyle.Spray : Shape;

    internal FctConfigState Clone() => (FctConfigState)MemberwiseClone();
  }
}
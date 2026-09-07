namespace EQLogParser
{
  /*
   * Which region scheme the overlay is using and where each side sits in it — the halves/bands choice, which side
   * incoming lives on, and which way each side travels. The player-facing half of this (names, defaults, settings.ini
   * keys) lives in FctOverlaySettings; this file is the geometry the layout maths consumes, kept free of ConfigUtil for
   * the same reason as everything else in fct/.
   *
   * Two schemes:
   *
   * bands — the original: a band across the top for my numbers, one across the bottom for what lands on me, and a
   * protected strip between them that stays empty by construction because both sides travel away from it. Direction is
   * fixed (out rises, in sinks) because direction is the "who" carrier there — freeing it would send traffic into the
   * strip.
   *
   * halves — the genre standard (MSBT's own shipped default: two side-by-side areas, incoming left, both scrolling down
   * with an outward bow; see docs/DesignNotes.md for citations). Position carries "who" instead of direction, which is
   * what makes per-side up/down a legal setting: each half owns its whole vertical extent, so a number travelling up
   * on one side cannot meet anything it should not. There is no protected strip to keep clear — the regions do not
   * overlap — and there are no lane columns inside a half: one stream per side, which is also how MSBT ships it.
   *
   * A stage is a value: (choice, canvas size) is all a number's region depends on, so asking twice with the same size
   * answers the same thing, and a resize is a new stage rather than a mutation somebody has to schedule.
   */
  internal enum FctLayoutMode
  {
    Halves,
    Bands,
  }

  /* Which half of the overlay incoming numbers sit in. Outgoing always takes the other one. */
  internal enum FctRegionSide
  {
    Left,
    Right,
  }

  /*
   * The settings-facing choice: mode, incoming's side, and each side's travel direction (up = rises). Stored as a
   * struct rather than four loose properties so ingest holds one value the canvas can swap atomically, and so an
   * equality check is how "did anything change" gets asked.
   */
  internal readonly struct FctLayoutChoice
  {
    public readonly FctLayoutMode Mode;
    public readonly FctRegionSide IncomingSide;

    /* Halves only: travel direction per side. Bands ignores both — its directions are the strip invariant. */
    public readonly bool IncomingUp;
    public readonly bool OutgoingUp;

    public FctLayoutChoice(FctLayoutMode mode, FctRegionSide incomingSide, bool incomingUp, bool outgoingUp)
    {
      Mode = mode;
      IncomingSide = incomingSide;
      IncomingUp = incomingUp;
      OutgoingUp = outgoingUp;
    }

    /*
     * The shipped default follows the genre standard rather than the history: halves, incoming left, both sides down.
     * Nothing is released, so flipping the default costs no existing player their view (docs/DesignNotes.md).
     */
    public static readonly FctLayoutChoice Shipped = new(FctLayoutMode.Halves, FctRegionSide.Left, false, false);

    /* The original top/bottom scheme: its directions are the strip invariant, so only the mode of this choice carries meaning here. */
    public static readonly FctLayoutChoice Bands = new(FctLayoutMode.Bands, FctRegionSide.Left, false, false);

    public FctStage Stage(double w, double h) => Mode switch
    {
      FctLayoutMode.Bands => FctStage.Bands(w, h),
      _ => FctStage.Halves(IncomingSide, IncomingUp, OutgoingUp, w, h),
    };
  }

  /* (choice, size) resolved to the questions the layout actually asks: whose region is this, which way does it travel. */
  internal readonly struct FctStage
  {
    private readonly FctLayoutMode _mode;
    private readonly FctRegionSide _incomingSide;
    private readonly bool _incomingUp;
    private readonly bool _outgoingUp;

    public FctLayoutMode Mode => _mode;
    public double W { get; }
    public double H { get; }

    private FctStage(FctLayoutMode mode, FctRegionSide incomingSide, bool incomingUp, bool outgoingUp, double w, double h)
    {
      _mode = mode;
      _incomingSide = incomingSide;
      _incomingUp = incomingUp;
      _outgoingUp = outgoingUp;
      W = w;
      H = h;
    }

    internal static FctStage Bands(double w, double h) => new(FctLayoutMode.Bands, FctRegionSide.Left, false, false, w, h);

    internal static FctStage Halves(FctRegionSide incomingSide, bool incomingUp, bool outgoingUp, double w, double h)
      => new(FctLayoutMode.Halves, incomingSide, incomingUp, outgoingUp, w, h);

    /*
     * The rect that owns a side's numbers. Bands: the whole canvas (the bands inside it are FctLayout's business).
     * Halves: one of two side-by-side halves meeting at the centre; each insets itself with EdgePad, which is what the
     * seam between them gets.
     */
    public (double X, double Y, double Width, double Height) RegionFor(bool incoming)
    {
      if (Mode is FctLayoutMode.Bands)
      {
        return (0, 0, W, H);
      }

      var leftIsIncoming = _incomingSide == FctRegionSide.Left;
      var useLeft = incoming == leftIsIncoming;
      return (useLeft ? 0 : W / 2, 0, W / 2, H);
    }

    /*
     * The travel sign for a side: +1 rises, -1 sinks. Bands keeps the old rule — out up, in down — because that is the
     * strip invariant. Halves takes it from the per-side settings, which are legal there only because each half owns
     * its whole height.
     */
    public double UpFor(bool incoming) =>
      Mode is FctLayoutMode.Bands
        ? incoming ? -1 : 1
        : incoming ? (_incomingUp ? 1 : -1) : (_outgoingUp ? 1 : -1);

    /*
     * What the style amplitudes — spawn jitter, hold's arc, spray's cone cap — measure against: the whole canvas in
     * bands, one half in halves. A ±9% jitter that was 72 px of an 800 px canvas is ±9% of the half in halves, which is
     * what keeps a stream inside its own territory instead of reaching across the seam for room.
     */
    public double TerritoryFor(bool incoming) => Mode is FctLayoutMode.Bands ? W : RegionFor(incoming).Width;
  }
}

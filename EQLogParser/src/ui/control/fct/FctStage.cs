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
   * by type — the two columns of halves, but the side carries WHAT a number is instead of who it belongs to: healing
   * owns one column, everything else the other, and each direction's travel stays its own setting. That is the view a
   * player asks for ("heals left, damage right, mine up, theirs down") and the one MSBT cannot serve from a single area
   * — his areas scroll one way — so his users assemble it out of Add Scroll Area plus re-mapping the heal events
   * (MSBTOptionsTabs.lua). Two flows sharing one column is exactly what whole-flight placement is for: opposite trains
   * pass, braid around each other, graze bounded and counted, never drop (FctStream).
   *
   * A stage is a value: (choice, canvas size) is all a number's region depends on, so asking twice with the same size
   * answers the same thing, and a resize is a new stage rather than a mutation somebody has to schedule.
   */
  internal enum FctLayoutMode
  {
    Halves,

    /* Halves' columns with the ownership question swapped: by category instead of by direction. */
    ByType,

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

    /* By type only: which column healing owns. Everything else takes the other one. */
    public readonly FctRegionSide HealSide;

    /* Halves and by type: travel direction per side. Bands ignores both — its directions are the strip invariant. */
    public readonly bool IncomingUp;
    public readonly bool OutgoingUp;

    public FctLayoutChoice(FctLayoutMode mode, FctRegionSide incomingSide, bool incomingUp, bool outgoingUp,
      FctRegionSide healSide = FctRegionSide.Left)
    {
      Mode = mode;
      IncomingSide = incomingSide;
      HealSide = healSide;
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
      FctLayoutMode.ByType => FctStage.ByType(HealSide, IncomingUp, OutgoingUp, w, h),
      _ => FctStage.Halves(IncomingSide, IncomingUp, OutgoingUp, w, h),
    };
  }

  /* (choice, size) resolved to the questions the layout actually asks: whose region is this, which way does it travel. */
  internal readonly struct FctStage
  {
    private readonly FctLayoutMode _mode;
    private readonly FctRegionSide _incomingSide;
    private readonly FctRegionSide _healSide;
    private readonly bool _incomingUp;
    private readonly bool _outgoingUp;

    public FctLayoutMode Mode => _mode;
    public double W { get; }
    public double H { get; }

    private FctStage(FctLayoutMode mode, FctRegionSide incomingSide, FctRegionSide healSide, bool incomingUp, bool outgoingUp, double w, double h)
    {
      _mode = mode;
      _incomingSide = incomingSide;
      _healSide = healSide;
      _incomingUp = incomingUp;
      _outgoingUp = outgoingUp;
      W = w;
      H = h;
    }

    internal static FctStage Bands(double w, double h) => new(FctLayoutMode.Bands, FctRegionSide.Left, FctRegionSide.Left, false, false, w, h);

    internal static FctStage Halves(FctRegionSide incomingSide, bool incomingUp, bool outgoingUp, double w, double h)
      => new(FctLayoutMode.Halves, incomingSide, FctRegionSide.Left, incomingUp, outgoingUp, w, h);

    internal static FctStage ByType(FctRegionSide healSide, bool incomingUp, bool outgoingUp, double w, double h)
      => new(FctLayoutMode.ByType, FctRegionSide.Left, healSide, incomingUp, outgoingUp, w, h);

    /*
     * The rect that owns a side's numbers. Bands: the whole canvas (the bands inside it are FctLayout's business).
     * Halves: one of two side-by-side halves meeting at the centre; each insets itself with EdgePad, which is what the
     * seam between them gets.
     */
    public (double X, double Y, double Width, double Height) RegionFor(bool incoming, bool heal = false)
    {
      if (Mode is FctLayoutMode.Bands)
      {
        return (0, 0, W, H);
      }

      /* By type decides by WHAT a number is; halves by who it belongs to. Either answer is one bit against one seam. */
      var useLeft = Mode is FctLayoutMode.ByType
        ? heal == (_healSide == FctRegionSide.Left)
        : incoming == (_incomingSide == FctRegionSide.Left);
      return (useLeft ? 0 : W / 2, 0, W / 2, H);
    }

    /*
     * Which column a NUMBER owns — the question by type exists to ask differently. Bands and halves answer it by who the
     * number belongs to; by type answers it by what the number is: healing goes to its own side, everything else
     * (damage, misses, words) to the other, and each direction's travel setting still applies inside the column. Both
     * flows may therefore occupy one column at once — in halves the two sides' ranges were disjoint by construction,
     * here they are not, and what keeps them apart is that placement scores whole flights against everything sharing
     * the territory (FctStream's rows count neighbours by range, not by direction).
     */
    public (double X, double Y, double Width, double Height) RegionFor(FctHitState hit) => _mode is FctLayoutMode.ByType
      ? hit.Heal == (_healSide == FctRegionSide.Left)
        ? (0, 0, W / 2, H)
        : (W / 2, 0, W / 2, H)
      : RegionFor(hit.Incoming);

    /* Territory for a number: see RegionFor(hit). Bands measures amplitude against the whole canvas either way. */
    public double TerritoryFor(FctHitState hit) => _mode is FctLayoutMode.Bands ? W : RegionFor(hit).Width;

    /*
     * The travel sign for a side: +1 rises, -1 sinks. Bands keeps the old rule — out up, in down — because that is the
     * strip invariant. Halves takes it from the per-side settings, which are legal there only because each half owns
     * its whole height. By type always reads both settings: its columns carry whatever directions were chosen.
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

    /*
     * What a scheme moves with when nothing explicit says otherwise: halves ships with the genre's own default shape (the
     * parabola — MSBT runs it, docs/DesignNotes.md), and bands keeps its quiet hold. First-run configure uses this, and so
     * does a layout change that would leave an illegal style selected.
     */
    public static FctMotionStyle DefaultMotion(FctLayoutMode mode)
      => mode is FctLayoutMode.Bands ? FctMotionStyle.Hold : FctMotionStyle.Parabola;
  }
}

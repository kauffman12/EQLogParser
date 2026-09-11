namespace EQLogParser
{
  /*
   * Which region scheme the overlay is using and where each side sits in it — the bands/by-type choice, which side
   * incoming lives on, and which way each side travels. The player-facing half of this (names, defaults, settings.ini
   * keys) lives in FctOverlaySettings; this file is the geometry the layout maths consumes, kept free of ConfigUtil for
   * the same reason as everything else in fct/.
   *
   * Two schemes, which are the two words the settings panel speaks — fountain and split:
   *
   * bands — the original, and what "fountain" is: a band across the top for my numbers, one across the bottom for what
   * lands on me, and a protected strip between them that stays empty by construction because both sides travel away from
   * it. Direction is the "who" carrier there (out rises, in sinks), so the strip — not a column boundary — is what keeps
   * the two reads apart.
   *
   * by type — "split": columns across the overlay whose side carries WHAT a number is rather than who it belongs to.
   * Four named lanes (FctRailLane), each a quarter of the width owned outright by whoever books it: healing in one,
   * either damage stream in another, and each category's travel dial still its own. That is the view a player asks for
   * ("heals left, damage right, mine up, theirs down") and the one MSBT cannot serve from a single area — his areas
   * scroll one way — so his users assemble it out of Add Scroll Area plus re-mapping the heal events
   * (MSBTOptionsTabs.lua). Here it is one mode.
   *
   * One column carries one train. Categories may share a column — that is the point of lanes — but only in one direction,
   * because two queues driving opposite ways through the same pixels cannot be spaced by anything except luck; the settings
   * refuse the combination (FctConfigState.ResolveLaneConflicts) rather than letting the geometry discover it, and the
   * column itself enforces the rest (FctConveyor).
   *
   * A third scheme lived here for a while — halves, the genre's two side-by-side areas, with one flight-scored stream per
   * side. It came out: no control could select it, no settings value reached it, and every hour spent on its braided
   * columns and congestion valves was an hour spent on geometry nobody could ask for. If it returns it gets written
   * against the lane model rather than dug out of history.
   *
   * A stage is a value: (choice, canvas size) is all a number's region depends on, so asking twice with the same size
   * answers the same thing, and a resize is a new stage rather than a mutation somebody has to schedule.
   */
  internal enum FctLayoutMode
  {
    /* Split: columns assigned by category. The name stayed after halves went because "by type" is what the
     * ownership question actually is — healing here, damage there, whoever booked the lane. */
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

    /* By type: travel direction per side. Bands reads these too since the fountain's dials, but its shipped answers
     * ARE the strip invariant. */
    public readonly bool IncomingUp;
    public readonly bool OutgoingUp;

    /* By type per category: which column the two damage streams own. By type defaults them to the side opposite the
     * healing — the classic heals | damage split — and either can be sent to join the other, or both anywhere else;
     * other schemes do not read them. */
    public readonly FctRegionSide IncomingDamageSide;
    public readonly FctRegionSide OutgoingDamageSide;

    /* By type per category: which way healing travels, whatever direction its damage shares a column with. */
    public readonly bool HealUp;

    /* By type per category: which of the four columns each category streams down (FctRailLane). Resolved at construction
     * — when only a side was given, the side's OUTER lane sits where that half used to be centred — so the stage never
     * asks which form the settings arrived in. */
    public readonly FctRailLane HealLane;
    public readonly FctRailLane IncomingDamageLane;
    public readonly FctRailLane OutgoingDamageLane;

    /* Directions arrive nullable so "not given" can mean the scheme's own default instead of false: bands ships its
     * outward invariant (in sinks, out rises) and that is also what an omitted direction must produce there, while
     * the columns ship both-down. Tests that built a bands choice with bare positional bools keep the old
     * answers explicitly; only omission changes meaning. */
    public FctLayoutChoice(FctLayoutMode mode, FctRegionSide incomingSide, bool? incomingUp = null, bool? outgoingUp = null,
      FctRegionSide healSide = FctRegionSide.Left, bool? healUp = null,
      FctRegionSide? incomingDamageSide = null, FctRegionSide? outgoingDamageSide = null,
      FctRailLane? healLane = null, FctRailLane? incomingDamageLane = null, FctRailLane? outgoingDamageLane = null)
    {
      var oppositeHeal = healSide == FctRegionSide.Left ? FctRegionSide.Right : FctRegionSide.Left;

      Mode = mode;
      IncomingSide = incomingSide;
      HealSide = healSide;
      IncomingUp = incomingUp ?? false;
      OutgoingUp = outgoingUp ?? mode is FctLayoutMode.Bands;
      HealUp = healUp ?? false;
      IncomingDamageSide = incomingDamageSide ?? (mode is FctLayoutMode.ByType ? oppositeHeal : incomingSide);
      OutgoingDamageSide = outgoingDamageSide ?? (mode is FctLayoutMode.ByType ? oppositeHeal
        : incomingSide == FctRegionSide.Left ? FctRegionSide.Right : FctRegionSide.Left);
      HealLane = healLane ?? FctRailLanes.OfSide(HealSide);
      IncomingDamageLane = incomingDamageLane ?? FctRailLanes.OfSide(IncomingDamageSide);
      OutgoingDamageLane = outgoingDamageLane ?? FctRailLanes.OfSide(OutgoingDamageSide);
    }

    /*
     * The original top/bottom scheme, and the engine's own default: it is what the product ships too (FctOverlaySettings
     * reads fountain unless settings.ini says split), so an overlay that has not loaded anything yet and a test that does
     * not name a scheme both get the two bands. Directions omitted on purpose: bands' own defaults ARE the strip invariant
     * (in sinks, out rises), which is what every pre-existing bands test rides.
     */
    public static readonly FctLayoutChoice Bands = new(FctLayoutMode.Bands, FctRegionSide.Left);

    public FctStage Stage(double w, double h) => Mode switch
    {
      FctLayoutMode.Bands => FctStage.Bands(w, h, IncomingUp, OutgoingUp, HealUp),
      FctLayoutMode.ByType => FctStage.ByType(HealSide, IncomingUp, OutgoingUp, w, h, HealUp, IncomingDamageSide, OutgoingDamageSide,
        HealLane, IncomingDamageLane, OutgoingDamageLane),
      // The same rule the settings parse uses: junk draws the shipped scheme rather than reaching the geometry as garbage —
      // and here "junk" can only be a value cast into existence, since every reader of FctLayoutMode produces one of the two.
      _ => FctStage.Bands(w, h, IncomingUp, OutgoingUp, HealUp),
    };
  }

  /* (choice, size) resolved to the questions the layout actually asks: whose region is this, which way does it travel. */
  internal readonly struct FctStage
  {
    private readonly FctLayoutMode _mode;
    private readonly bool _incomingUp;
    private readonly bool _outgoingUp;
    private readonly bool _healUp;
    private readonly FctRegionSide _incomingDamageSide;
    private readonly FctRegionSide _outgoingDamageSide;

    // By type: the same question one level finer — which of the four columns each category streams down (FctRailLane).
    private readonly FctRailLane _healLane;
    private readonly FctRailLane _incomingDamageLane;
    private readonly FctRailLane _outgoingDamageLane;

    public FctLayoutMode Mode => _mode;
    public double W { get; }
    public double H { get; }

    private FctStage(FctLayoutMode mode, FctRegionSide incomingSide, FctRegionSide healSide, bool incomingUp, bool outgoingUp, double w, double h,
      bool healUp = false, FctRegionSide? incomingDamageSide = null, FctRegionSide? outgoingDamageSide = null,
      FctRailLane? healLane = null, FctRailLane? incomingDamageLane = null, FctRailLane? outgoingDamageLane = null)
    {
      _mode = mode;
      _incomingUp = incomingUp;
      _outgoingUp = outgoingUp;
      _healUp = healUp;
      var oppositeHeal = healSide == FctRegionSide.Left ? FctRegionSide.Right : FctRegionSide.Left;
      _incomingDamageSide = incomingDamageSide ?? (mode is FctLayoutMode.ByType ? oppositeHeal : incomingSide);
      _outgoingDamageSide = outgoingDamageSide ?? (mode is FctLayoutMode.ByType ? oppositeHeal
        : incomingSide == FctRegionSide.Left ? FctRegionSide.Right : FctRegionSide.Left);
      _healLane = healLane ?? FctRailLanes.OfSide(healSide);
      _incomingDamageLane = incomingDamageLane ?? FctRailLanes.OfSide(_incomingDamageSide);
      _outgoingDamageLane = outgoingDamageLane ?? FctRailLanes.OfSide(_outgoingDamageSide);

      /* One train per column (FctConveyor): categories booked into the same lane travel together, or a single queue would be
         asked to run two ways at once — which is every row in it landing on top of every other. The direction dials are per
         category, so a settings file can ask for the impossible; my damage owns its column, incoming follows it, heals follow
         whoever got there first. The panel refuses the combination too (FctConfigState.ResolveLaneConflicts), but the rule
         belongs here at the layer that decides where numbers go, so a hand-written ini, a dial turned mid-fight and a test
         harness all agree without anyone having to remember to check twice. */
      if (_mode is FctLayoutMode.ByType)
      {
        if (_incomingDamageLane == _outgoingDamageLane)
        {
          _incomingUp = _outgoingUp;
        }

        if (_healLane == _outgoingDamageLane)
        {
          _healUp = _outgoingUp;
        }
        else if (_healLane == _incomingDamageLane)
        {
          _healUp = _incomingUp;
        }
      }

      W = w;
      H = h;
    }

    /* Bands now carries directions like every other scheme — the fountain preset hands all three dials straight
     * through, healing's included. The defaults stay the old strip invariant (in sinks, out rises; heals sink), so
     * every caller that never mentions a direction draws exactly what bands has always drawn. */
    internal static FctStage Bands(double w, double h, bool incomingUp = false, bool outgoingUp = true, bool healUp = false)
      => new(FctLayoutMode.Bands, FctRegionSide.Left, FctRegionSide.Left, incomingUp, outgoingUp, w, h, healUp: healUp);

    /* Lanes are the ownership question asked one level finer; callers that pass only sides get that side's OUTER lane,
     * which is the quarter whose centre sits where a side's numbers have always been centred. */
    internal static FctStage ByType(FctRegionSide healSide, bool incomingUp, bool outgoingUp, double w, double h,
      bool healUp = false, FctRegionSide? incomingDamageSide = null, FctRegionSide? outgoingDamageSide = null,
      FctRailLane? healLane = null, FctRailLane? incomingDamageLane = null, FctRailLane? outgoingDamageLane = null)
      => new(FctLayoutMode.ByType, FctRegionSide.Left, healSide, incomingUp, outgoingUp, w, h, healUp, incomingDamageSide, outgoingDamageSide,
        healLane, incomingDamageLane, outgoingDamageLane);

    /*
     * The rect that owns a number's geometry — the only region question anything still asks.
     *
     * Bands: the whole canvas, because the bands inside it are FctLayout's business and the protected strip is kept clear
     * by travel direction rather than by a boundary. Split: the LANE (FctRailLane), one quarter of the width owned
     * outright by whoever booked it, which is what makes "my damage never lands in your heals' column" a fact instead of
     * a scored preference. Two categories that booked the same lane share it as one queue (FctConveyor), so they are one
     * train rather than two crowds.
     *
     * There used to be a second overload answering this in halves — whose side, not which column — because halves owned
     * the half outright and had no lanes inside it. Nothing asks that question now, and a region that quietly meant
     * something other than what a row is drawn inside is how a column's numbers came to disagree with their own spine.
     */
    public (double X, double Y, double Width, double Height) RegionFor(FctHitState hit) => _mode is FctLayoutMode.ByType
      ? LaneRect(CategoryLane(hit))
      : (0, 0, W, H);

    /* Which column a number's category owns: four named lanes, each holding its quarter of the width outright.
     * Categories that choose the SAME lane share it as one queue — which is the point; categories that choose different
     * lanes can never be woven into each other's pixels. (Before lanes existed by type answered this in halves and
     * placement quietly invented sub-columns; see FctRailLane.) */
    private FctRailLane CategoryLane(FctHitState hit) =>
      hit.Heal ? _healLane : hit.Incoming ? _incomingDamageLane : _outgoingDamageLane;

    /*
     * Which of the four columns a number owns, as an index — what a conveyor names a lane by (FctConveyor). Bands has no
     * columns to point at, answers -1, and the caller falls back to geometry.
     */
    internal int LaneIndexOf(FctHitState hit) =>
      _mode is FctLayoutMode.ByType ? FctRailLanes.Index(CategoryLane(hit)) : -1;

    private (double X, double Y, double Width, double Height) LaneRect(FctRailLane lane)
    {
      var quarter = W / 4;
      return (FctRailLanes.Index(lane) * quarter, 0, quarter, H);
    }

    /* Territory for a number: see RegionFor(hit). Bands measures amplitude against the whole canvas either way. */
    public double TerritoryFor(FctHitState hit) => _mode is FctLayoutMode.Bands ? W : RegionFor(hit).Width;

    /*
     * The travel sign for a direction: +1 rises, -1 sinks. Both dials are read as given; the strip invariant bands ships
     * with (out up, in down) is the values FctLayoutChoice.Bands arrives with, not a rule applied here — which is what lets
     * the fountain's dials steer at all. Split's columns carry whatever directions were chosen per category, reconciled
     * per lane in the constructor so one column never runs two ways.
     */
    public double UpFor(bool incoming) =>
      incoming ? (_incomingUp ? 1 : -1) : (_outgoingUp ? 1 : -1);

    /* The hit-aware answer: healing gets its own direction wherever the scheme has a heals dial — split (its own column,
     * even when a damage stream shares it) and bands alike. A fountain therefore sprays heals UP while damage
     * falls, or sinks them while everything else rises; the dial was always in the settings, this is where the mode
     * stopped ignoring it. The question has to know what the number IS, not only who it belongs to. Every rail path
     * asks this one. */
    public double UpFor(FctHitState hit) => hit.Heal
      ? _healUp ? 1 : -1
      : UpFor(hit.Incoming);

    /*
     * What a scheme moves with when nothing explicit says otherwise: split ships with the genre's own default shape (the
     * arc, whose bow is MSBT's, docs/DesignNotes.md), and bands keeps its quiet freeze. First-run configure uses this, and so
     * does a layout change that would leave an illegal style selected.
     */
    public static FctMotionStyle DefaultMotion(FctLayoutMode mode)
      => mode is FctLayoutMode.Bands ? FctMotionStyle.Freeze : FctMotionStyle.Arc;
  }
}
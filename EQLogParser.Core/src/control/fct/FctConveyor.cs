using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The deliberate rails: one conveyor per column, and everything riding it travels as ONE train.
   *
   * This is what split mode's rails are for — the straight line and the parabola, which is the shape that ships there; the dial
   * picks the path up the column, not whether the traffic keeps its spacing. Fountain, spray and the free-float styles are choreography — numbers
   * thrown, arcued, fallen — and a player who picks them has asked for a display that reads as an event. Somebody who
   * picks split + line has asked for the opposite: a column they can read top to bottom without missing a number. That
   * promise cannot be kept by placing rows and giving each its own flight time, which is what the rail used to do: a row
   * born into a crowd got a shorter life than the row in front of it (FctStream.Pressure), overtook it, and the two were
   * drawn on top of each other for the rest of the trip. Same pixels, two speeds, and the player correctly reports that
   * "some numbers move faster than others".
   *
   * The genre solved this structurally rather than arithmetically. NAG's scroll areas are a DOM flow column
   * (.fct-content { display:flex; flex-direction:column } in renderer.js): it never positions a number at all, so spacing
   * is exact by construction and the whole stack moves as one when a line arrives or leaves. MSBT's areas queue rows into
   * one column at one scroll rate (MIN_VERTICAL_SPACING = 8 between them). Neither tool overlaps two numbers to make room,
   * and neither speeds up one row on its own; both let overflow leave the area instead. The conveyor keeps that discipline
   * and makes the loss honest — counted in FctIngest.DroppedCount rather than clipped off a window edge.
   *
   * Three rules, and they are the whole design:
   *
   *   1. ONE CLOCK per lane. A lane owns a phase in pixels; every row on it is at (phase − its own birth phase), so the
   *      gap between two rows is whatever it was at entry — forever. Nothing can overtake anything, and "the lane sped up"
   *      is a change to one number that moves everybody by the same amount in the same frame.
   *   2. SPACING IS BOUGHT AT ENTRY, ONCE. A new row pays for its slot behind the last one enrolled: the TALLER of the two,
   *      plus the gap. Height is what FctLayout.TextHeight answers — a crit's own font, a labelled row's source line — so a
   *      big number cannot arrive taller than the air its neighbour reserved (the same lesson FctCellGrid learned as inflated
   *      bounds). For the uniform column a fight is mostly made of, "taller of the two" IS one row height plus the gap: exact
   *      line spacing, nothing thrown away.
   *   3. CONGESTION SCALES THE LANE, NOT THE ROW. Load is measured as rows wanting room against what the column can hold,
   *      and the answer — up to FctStream.PressFloor of the nominal flight time — is applied to the lane's clock, which
   *      everyone on it shares. It ramps (fast to speed up, slower to relax) so a burst never makes the column stutter.
   *
   * Waiting rows live in the caller's list like any other number, at a negative distance: they fold duplicates while they
   * wait (so a DoT barrage costs one row, not twelve), they are invisible (FctMotion.FadeOpacity returns 0 before the
   * mouth), and they get their slot when the lane carries it there. Past BacklogCap the lane is genuinely full at its
   * fastest and the arrival is turned away, counted — the same bargain NAG and MSBT strike, minus the silence.
   */
  internal sealed class FctConveyor
  {
    /* Clear air between two rows on one rail. MSBT's line gap is 8 px; this is a hair wider because it is the mode people
       choose in order to read, and because a row carrying a source line hangs that line into the air below it. */
    /*
     * Zero, on purpose, and it was not always. The gap between two rows on a lane used to be this many pixels ON TOP of the row's own
     * vertical reserve — and that reserve (FctLayout.TextHeight) is already about a third taller than the glyphs it protects, because it
     * has to hold an ascent, a descent, and the halo that blooms out past both. Ten pixels of air above that read as four columns of a
     * ledger with every other line left blank: the column ran out of screen long before it ran out of numbers worth showing. The reserve IS
     * the gap now; what separates two neighbours is the slack inside their own boxes, which is real (it has to be) and invisible (which is
     * the point). Halves keeps a fraction of the same idea (FctStream.RowGapFrac) rather than a fixed pixel count, because it spaces rows
     * by measured height plus a share and has no lane clock to price against.
     */
    public const double LaneGapPx = 0;

    /* How many arrivals may wait behind the mouth of one lane before the next is refused. Twelve is roughly "one screenful
       of backlog": enough that a pull-opening burst, a DoT application or an AoE spike is held and then delivered in order
       instead of thrown away, and small enough that a lane which has been over capacity for a minute has not quietly built
       a queue the player will never see. What the queue costs is lateness, and lateness is cheap next to a lost number. */
    public const int BacklogCap = 12;

    /*
     * How fast a lane may change its mind about speed, per millisecond of elapsed time. Speeding up is nearly immediate:
     * the traffic is already here and every frame at the old rate pushes another row into a queue. Relaxing is five times
     * slower, because the load signal is bursty — an AoE lands twenty numbers in one frame and none for two seconds — and a
     * clock that snaps back the instant the count drops plays the same burst twice: slow, fast, slow again, which is a
     * stutter anyone can see. Both are proportional ramps, so a lane already running fast accelerates in the same wall
     * clock as one running slow.
     */
    private const double RampUpPerMs = 0.004;
    private const double RampDownPerMs = 0.0008;

    /* A slew limit on top of the proportional ramps: no lane may change its accelerator by more than this in one frame. Without
       it a burst moves press by 6-7% per frame, which is fast enough for the eye to catch as the column lurching; with it the
       lane still reaches the floor from nominal inside half a second — the queue holds the traffic meanwhile, so a smoother
       acceleration costs nothing but a slightly later pickup. */
    private const double MaxPressStep = 0.03;

    /* Dead band, as a fraction of current press: load estimates wobble by a row, and a clock that chases a one-row wobble
       is a lane that never settles. Nothing moves unless the target has genuinely left this much ground. */
    private const double RampDeadBandFrac = 0.05;

    /* A frame that arrives two seconds after the last one (tab suspended, GC pause, drag) must not teleport the column or
       count as two seconds of relaxation. Rows re-derive their position from phase every frame, so capping the step costs
       nothing but a slightly longer flight through a stutter. */
    private const double MaxStepMs = 250;

    /* The frame length a pace is quoted against. Named because the tests read a lane's accelerator back out of the pixels it asked for,
       and that arithmetic has to agree with the harness's clock to the millisecond. */
    internal const double FrameMs = 16.0;

    private readonly Dictionary<int, Lane> _lanes = new();

    /*
     * Put this row on its lane: pay for its slot and stamp the numbers its position is read from (FctMotion reads
     * ConveyorQ, FctIngest.PruneExpired expires on it). The row's geometry must already be final — the caller pins it to
     * the mouth of the column first (FctPlacement.Pin), because |Rise| is that row's flight and a flight measured before
     * pinning is a different flight. Returns false only when the lane cannot take it at all: at its fastest, with the
     * backlog full, the arrivals genuinely outrun the rail, and the caller counts the loss.
     */
    public bool Enrol(FctHitState hit, FctStage stage, double now)
    {
      var lane = LaneOf(KeyOf(hit, stage), now);
      if (lane.Waiting >= BacklogCap)
      {
        return false;
      }

      /* The slot's price: the taller of the pair plus the gap, because every box hangs down from its own top — separation has to clear
         whichever of the two is bigger, not their average, which lets a crit behind a miss lean into it by half the difference. Rounded UP
         to whole pixels: a fractional pitch lands glyph baselines a fraction apart from frame to frame, and a column that shimmers by a
         third of a pixel as it scrolls reads as jitter even though every number moves at exactly the right rate. Up, never down, so the
         rounding can never eat clearance. */
      var height = FctLayout.TextHeight(hit);
      var pitch = lane.LastHeight > 0 ? Math.Ceiling(Math.Max(lane.LastHeight, height) + LaneGapPx) : 0.0;

      /* The slot: as far behind the last row enrolled as both of them need. Phase has been moving while that row travelled,
         so an idle lane's next arrival takes the mouth itself (the max), and a busy one gets queued behind the row ahead —
         including behind rows still waiting themselves, which is what makes the backlog a queue rather than a pile. */
      var admit = Math.Max(lane.Phase, lane.LastAdmitPhase + pitch);

      hit.OnConveyor = true;
      hit.ConveyorLane = lane.Key;
      hit.ConveyorBirthPhase = admit;
      hit.ConveyorTravel = Math.Abs(hit.Rise);
      hit.ConveyorQ = lane.Phase - admit;   // ≤ 0: still behind the mouth, waiting for its slot to arrive
      hit.RailPress = lane.Press;           // observability: the lane's accelerator, not this row's

      lane.LastAdmitPhase = admit;
      lane.LastHeight = height;
      lane.Travel = Math.Max(lane.Travel, hit.ConveyorTravel);
      lane.Pitch = pitch > 0 ? pitch : height + LaneGapPx;
      if (hit.ConveyorQ < 0)
      {
        lane.Waiting++;
      }

      return true;
    }

    /*
     * Advance every lane and stamp where its rows now are. Called from FctIngest.PruneExpired — the verb a host already
     * runs once per frame — so a conveyor cannot be left un-wound by a backend that forgot a second call, and there is
     * exactly one clock in the overlay.
     */
    public void Advance(List<FctHitState> hits, double now)
    {
      if (_lanes.Count == 0)
      {
        return;
      }

      foreach (var lane in _lanes.Values)
      {
        lane.OnScreen = 0;
        lane.Waiting = 0;
        Stamp(lane, now);
      }

      for (var i = 0; i < hits.Count; i++)
      {
        var hit = hits[i];
        if (!hit.OnConveyor || !_lanes.TryGetValue(hit.ConveyorLane, out var lane))
        {
          continue;
        }

        /* Position is a difference of phases and nothing else: two rows on one rail keep the distance bought at entry for
           their whole flight, however much the lane sped up or slowed down in between. */
        hit.ConveyorQ = lane.Phase - hit.ConveyorBirthPhase;
        if (hit.ConveyorQ < 0)
        {
          lane.Waiting++;
        }
        else
        {
          lane.OnScreen++;
        }
      }

      foreach (var lane in _lanes.Values)
      {
        Ramp(lane);
      }
    }

    private static int KeyOf(FctHitState hit, FctStage stage)
    {
      /* One train per column, in one direction: the column index and the travel sign are the whole identity. Two categories
         that share a column AND a direction are deliberately one queue — that is what "damage-out and heals both live in
         right 1" means to a rail — and two categories sharing a column with opposite directions cannot be, which is why
         FctConfigState refuses the combination instead of letting two trains meet head-on. */
      var column = stage.LaneIndexOf(hit);

      // no columns (a scheme the conveyor does not run in): fall back to the region's own half so the answer is still stable
      var bucket = column >= 0 ? column : (int)(stage.RegionFor(hit).X / Math.Max(1.0, stage.W));

      return (bucket * 2) + (stage.UpFor(hit) > 0 ? 1 : 0);
    }

    private Lane LaneOf(int key, double now)
    {
      if (_lanes.TryGetValue(key, out var found))
      {
        return found;
      }

      // a new lane starts its clock here: any older phase belongs to columns this one has never carried
      var lane = new Lane { Key = key, LastStampMs = now };
      _lanes[key] = lane;
      return lane;
    }

    /* Run the clock forward to now. Rate is the dial's own px-per-pixel (FctMotion.ParabolaScrollMsPerPx, the pace measured
       playable) scaled by the player's speed setting and by this lane's press — the same arithmetic FinalizeRailTempo does
       per row, done once per lane instead, which is precisely the difference between a convoy and a crowd. */
    private static void Stamp(Lane lane, double now)
    {
      var dt = now - lane.LastStampMs;
      if (dt <= 0)
      {
        return;
      }

      lane.StepMs = Math.Min(dt, MaxStepMs);
      lane.Phase += lane.StepMs / (FctMotion.ParabolaScrollMsPerPx * lane.Press * FctScale.Time);
      lane.LastStampMs = now;
    }

    /*
     * Little's law, spent on the lane instead of on the row: a column holding N rows with room for M needs to clear them
     * N/M times faster than nominal, and no more. Below capacity the answer is 1.0 and the rail keeps the tempo the player
     * dialled; past it the whole lane drives toward FctStream.PressFloor, which still reads as text rather than a blur and
     * clears the region inside about a second and a half, so an overlay stops typing shortly after a fight does.
     */
    private static void Ramp(Lane lane)
    {
      var capacity = Math.Max(1, (int)((lane.Travel + LaneGapPx) / Math.Max(1.0, lane.Pitch)));
      var wanted = Math.Clamp((lane.OnScreen + lane.Waiting) / (double)capacity, 1.0, 1.0 / FctStream.PressFloor);
      var target = 1.0 / wanted;

      var gap = target - lane.Press;
      if (Math.Abs(gap) <= RampDeadBandFrac * lane.Press)
      {
        return;
      }

      var step = Math.Min(MaxPressStep, (gap < 0 ? RampUpPerMs : RampDownPerMs) * lane.StepMs * lane.Press);
      lane.Press = gap < 0 ? Math.Max(target, lane.Press - step) : Math.Min(target, lane.Press + step);
    }

    private sealed class Lane
    {
      public int Key;

      /* Pixels of rail this column has travelled since it opened. Every row's position is a difference against this one
         number, which is what makes "one speed for the whole lane" a fact rather than an intention. */
      public double Phase;

      public double LastStampMs;
      public double StepMs;

      /* The lane's accelerator, 0.45..1: a multiplier on flight TIME (FctStream.RailPress's convention, kept so the two
         rails mean the same number), shared by every row on it and changed for all of them at once. */
      public double Press = 1.0;

      // the last slot sold here, and how tall the row that bought it was: the next price is measured from these
      public double LastAdmitPhase;
      public double LastHeight;

      // what this column's rail is, and what a row on it costs: capacity for the load estimate, both learned at enrolment
      public double Travel;
      public double Pitch = 1.0;

      public int OnScreen;
      public int Waiting;
    }
  }
}

using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * What happens to an incoming hit: fold it into a live number, spawn it, or lose it to the lane cap.
   * Shared by every backend for the same reason as FctLayout — with two renderers, per-copy policy drifts
   * immediately. Rationale and the numbers behind these constants: docs/DesignNotes.md → Floating Combat Text.
   */
  internal sealed class FctIngest
  {
    /*
     * Hard ceiling of concurrent hits per lane. The adaptive lifetime already aims at
     * FctLifeController.Capacity (5-7); this is the backstop that stops a raid AoE stacking twenty
     * overlapping numbers. It merges into a live number before it drops, so totals survive overload.
     */
    private const int LaneCap = 12;

    /*
     * Folding is now only ever collapsing identical hits (same ability, same face value), so how wide the window is has
     * nothing to do with correctness and everything to do with what reads as one exchange. It is generous for that reason:
     * DoT ticks land seconds apart and a trinket proc repeats on its own schedule, and "×4" over 2.5 s is four real hits.
     * There is no sum to get wrong any more, so the only cost of a long window is that a number gains a count late.
     */
    private const double AbsorbWindowMs = 2500;
    private const double AbsorbMaxLifeFrac = 0.6;

    /* Crits do not adapt (they must stay prominent) and do not absorb; this is their whole display time. */
    private const double CritLifetimeMs = 2800;

    /* What "as short as a number may get" means once the player has asked for a faster overlay (§FctScale): below this a hit
     * appears and disappears quicker than it can be read, which is a flicker blamed on the overlay rather than snappiness. */
    private const double MinLifetimeMs = 900;
    private const double MinFadeMs = 200;

    /*
     * A full lane is a decision about who owns the slot, so a newcomer takes one away from something else only when it is
     * clearly the more informative of the two: at least this many times as significant. Significance is face value with a
     * proc discounted — FctStyle and ApplyProcTempo already say a proc is subordinate to the hit that provoked it, so an
     * old proc is the cheapest slot on screen and a big direct cast costs almost nothing to place.
     */
    private const double EvictValueFactor = 2.0;
    private const double ProcSignificanceFrac = 0.5;

    private readonly FctLifeController _life = new();
    private readonly Random _rand;

    public FctIngest(Random rand = null) => _rand = rand ?? new Random();

    /*
     * Motion style for new hits (see FctMotionStyle): how text moves, as opposed to where it goes, which the layout
     * decides and does not ask about. A canvas setting rather than a per-record one: a feed that mixed styles from log
     * line to log line would look like a bug, not a feature.
     */
    public FctMotionStyle Style = FctMotionStyle.Hold;

    /*
     * The region scheme for new hits (see FctStage): where a number goes — which stream it belongs to, which edge it starts
     * from and how far the territory is — as opposed to how it moves, which Style decides. Same canvas-setting contract:
     * in-flight numbers keep the stage they were born under (it is baked into their bands and travel at spawn), so flipping
     * the layout mid-fight changes what comes next rather than teleporting what is already on screen.
     */
    public FctLayoutChoice Layout = FctLayoutChoice.Shipped;

    /* Hits that had nowhere to go, surfaced in the overlay header so overload stays visible. */
    public int DroppedCount { get; private set; }

    /*
     * Numbers below this are never drawn — the MSBT "damage threshold" dial, offered on the configure row. It is a
     * display filter applied at the gate, not a parser change: the log and every counter still see all of it. Zero (the
     * default) means off. Heals and zero-damage labels are exempt by design; see Accept.
     */
    public double Threshold;

    /* How many numbers the threshold has hidden, surfaced next to the drop count so a filter that is working is never
     * mistaken for a filter that is losing things silently. */
    public int HiddenCount { get; private set; }

    /*
     * Which categories of number reach the screen at all: my damage, damage on me, and healing in either direction.
     * These are identity filters — "what story does this overlay tell" — where Threshold is the noise filter, which is
     * why they get their own switch and their own count instead of stretching the ladder. All three default on: the
     * controls are opt-outs, never things somebody has to discover. A category that is off pays nothing — no folding,
     * no placement, no glyphs — and its labels follow it, because a "Miss" belongs to whose damage story it is. The
     * log, the meters and exports never see any of this: FCT only ever decides what to draw.
     */
    public bool ShowDealt = true;
    public bool ShowTaken = true;
    public bool ShowHeals = true;

    /* Procs — the 20% weapon abilities that arrive as their own numbers — asked for a switch of their own: a player who
       wants the swing often reads the proc line as double-counting it, and one who does not wants silence. It is an
       extra opt-out on top of the categories (a proc is damage too), so hiding "my damage" hides procs with it; this
       only ever removes more, never restores. */
    public bool ShowProcs = true;

    /* Counted apart from HiddenCount on purpose: "filtered" is a category the player switched off, "hidden" is a number
     * under their threshold — two choices, so two numbers, and neither one borrows the other's meaning. */
    public int FilteredCount { get; private set; }

    public int LiveCount(List<FctHitState> hits, FctLane lane)
    {
      var live = 0;
      for (var i = 0; i < hits.Count; i++)
      {
        if (hits[i].Lane == lane)
        {
          live++;
        }
      }

      return live;
    }

    /*
     * The single entry point both backends call. Returns the newly spawned hit — the caller still has to
     * build its glyphs — or null when the hit was folded into an existing one or dropped at the cap.
     *
     * `evicting` is called for any hit whose screen space this one took over, which today means pulse mode stealing a full
     * cell. The caller owns releasing what it kept for that hit (glyph runs, halo references), and the hit is already gone
     * from the list by then.
     */
    public FctHitState Accept(List<FctHitState> hits, FctLane lane, double value, string source, bool crit, bool minor, bool periodic,
      string fixedText, double w, double h, double now, bool proc = false, Action<FctHitState> evicting = null)
    {
      if (w < 100 || h < 100)
      {
        return null; // nothing sane can be laid out yet (window not measured)
      }

      var incoming = FctLayout.IsIncoming(lane);          // before pooling: a taken crit stays on the incoming side
      /* Also captured before pooling, for the same reason: a heal crit lands on FctLane.Crit, where its lane no longer says what it was. */
      var heal = lane is FctLane.HealingDealt or FctLane.HealingReceived;
      var pooled = crit ? FctLane.Crit : lane;

      /* The category gate, before even the threshold: whether this kind of number belongs on this overlay at all is a
         question the player already answered, and a filtered hit should not so much as be measured against the dial. */
      if (!(heal ? ShowHeals : incoming ? ShowTaken : ShowDealt) || (proc && !ShowProcs))
      {
        FilteredCount++;
        return null;
      }

      /*
       * The threshold gate, before folding, eviction and everything else: a number the player asked not to see should
       * cost none of that. Only damage is filtered — heals and the fixed-text labels (Miss, Dodge, Resist) are information
       * rather than volume, and hiding "you are being resisted" because the number beside it is small is exactly the
       * surprise this dial must not produce. Nothing disappears silently: HiddenCount says how many it has taken.
       */
      if (Threshold > 0 && fixedText is null && !heal && value < Threshold)
      {
        HiddenCount++;
        return null;
      }

      /* Everything below measures against the side's own region, and a stage is that question answered for this size. */
      var stage = Layout.Stage(w, h);

      /*
       * The parabola is a halves shape: it scrolls straight across whatever region owns the side, and in bands that region
       * is the canvas — strip included. The configure row cannot select it there, but settings.ini can be hand-written,
       * which is why the degradation lives here rather than only in the UI (see FctMotionStyle.Parabola).
       */
      var style = stage.Mode is FctLayoutMode.Bands && FctMotionStyles.IsRail(Style)
        ? FctMotionStyle.Hold
        : Style;

      /*
       * Pulse mode allocates cells, and cells are its capacity: folding a number into a running total that lives in some
       * other cell would hide the fold, and the lane cap would drop hits that a free cell has room for. So the absorb and
       * overflow paths below belong to the travelling styles only.
       */
      var celled = style is FctMotionStyle.Pulse;

      if (fixedText is null)
      {
        if (!celled && !crit && ShouldAbsorb(pooled, periodic) &&
            TryAbsorb(hits, pooled, incoming, proc, periodic, source, value, now))
        {
          return null;
        }
      }

      if (!celled && LiveCount(hits, pooled) >= LaneCap)
      {
        /*
         * The lane is full. A duplicate of something already on screen was folded away above — that attempt does not care
         * about occupancy, so a stream of identical hits never costs a slot.
         *
         * What is left is a number the lane has nowhere to put: take the slot from the least significant number already on
         * screen, but only if this newcomer clearly outranks it, so the thing that disappears is smaller and less worth
         * reading than the thing that arrives. This is what stops the cap from hiding a big cast behind twelve routine
         * swings.
         *
         * Only then count a drop. Everything above either put the number on screen or added it to a count that says how
         * many it stands for.
         */
        var taken = PickEvictionTarget(hits, pooled, incoming, Significance(value, 1, proc));
        if (taken is null)
        {
          DroppedCount++;
          return null;
        }

        hits.Remove(taken);
        evicting?.Invoke(taken);   // same contract as pulse cell-stealing: the backend releases what it kept for it
      }

      var hit = new FctHitState
      {
        Lane = pooled,
        Proc = proc,
        Periodic = periodic,
        Heal = heal,
        Incoming = incoming,
        Style = style,
        SpawnMs = now,
        Source = source,
        SourceLabel = string.IsNullOrEmpty(source) ? null : $"({source})", // once here, not once per frame in the draw pass
        FixedText = fixedText,
        Value = value,
      };

      FctStyle.ApplyTo(hit, pooled, minor || periodic, proc);
      FctLayout.Spawn(hit, stage, _rand);
      AssignLifetime(hit, hits, stage, now);

      /*
       * Seed the width estimate now: the clamp band that keeps text out of the protected center is derived
       * from the drawn width, and the backend does not measure real glyphs until its first draw.
       */
      hit.ValueWidth = FctLayout.EstimateTextWidth(fixedText ?? FctText.FormatHit(value, 1, heal), hit.ValueFontSize);

      if (celled)
      {
        /*
         * Cells when there is room for a grid; on a very small overlay FctCellGrid has nothing to offer and the hit keeps the
         * static placement FctLayout gave it. Losing the layout is fine, losing the number is not.
         */
        if (FctCellGrid.HasRoom(hit, stage))
        {
          if (!FctCellGrid.Assign(hit, hits, stage, now, out var bumped))
          {
            /* Every cell in the block is held by a crit and this is not one: drop it rather than erase a bigger number. */
            DroppedCount++;
            return null;
          }

          if (bumped is not null)
          {
            evicting?.Invoke(bumped);
          }
        }
      }
      else if (FctMotionStyles.IsRail(style) && stage.Mode is not FctLayoutMode.Bands)
      {
        /*
         * The parabola in a side scheme is the stream, not a scatter: one centre column at the spawn edge with braided
         * side columns only for bursts, scored by the same flight maths as every other placement (FctStream). By type
         * runs the same engine — and needs it: two directions can share a column there, and the flight scoring is what
         * keeps opposing trains from wearing the same pixels.
         */
        hit = FctStream.Place(hit, hits, stage, _rand);

        /* The row now knows how far it actually travels; the stream's one rate is only true of final flights. */
        FinalizeRailTempo(hit);
      }
      else
      {
        /*
         * Text that travels gets free placement rather than cells, and FctPlacement is what keeps it off a number already in
         * flight: several legal throws are measured along their whole paths and the least crowded one wins. The hit that comes
         * back may be one of those trials rather than the object that went in.
         */
        hit = FctPlacement.Place(hit, hits, stage, _rand);
      }

      FctMotion.RefreshText(hit);
      hits.Add(hit);
      return hit;
    }

    /* Drops expired hits, newest-last so the list keeps its order. Returns how many went. */
    public int PruneExpired(List<FctHitState> hits, double now, Action<FctHitState> onRemoved = null)
    {
      var removed = 0;
      for (var i = hits.Count - 1; i > -1; i--)
      {
        if (now - hits[i].SpawnMs <= hits[i].LifetimeMs)
        {
          continue;
        }

        onRemoved?.Invoke(hits[i]);
        hits.RemoveAt(i);
        removed++;
      }

      return removed;
    }

    /*
     * Whether an incoming hit may be folded into a live number at all — the fold key itself is TryAbsorb's, and it now
     * includes the face value, so this is only about which kinds of event are allowed to be collapsed.
     *
     * Periodic ticks always may: one number reading "412 ×5" beats five overlapping 412s. Direct damage may too, but in
     * practice only when something identical is on screen — EQ repeats exact values constantly, so the collapse happens for
     * real without bending any rule.
     *
     * Healing direct casts never may, because players read heals one by one and collapsing two casts hides who got patched
     * and for how much. A heal with nowhere to go gets a slot taken for it (PickEvictionTarget) or is counted as dropped;
     * it does not join another heal's number.
     */
    private static bool ShouldAbsorb(FctLane lane, bool periodic) =>
      periodic || lane is FctLane.DamageDealt or FctLane.DamageTaken;

    /*
     * Folds a hit into the live number already showing exactly this — NAG's accumulateHits, with NAG's key plus the face
     * value: same pooled lane, same side, same proc/direct and periodic/direct kind, same ability name, same amount. Then the
     * age rules: the target has to be young enough that one number standing for several still reads as one exchange, and have
     * most of its life left, so a count never lands on something about to fade out. Crits never absorb — each one is the event
     * — and their overload is what the drop counter exists to make visible.
     *
     * Matching on the value is what makes "×N" a fact rather than an estimate: 2,040 ×2 really was two hits of 2,040.
     * Summing instead, which this used to do, put a number on screen that no hit ever landed for and made the player divide
     * to find out what happened.
     *
     * The rest of the key is load-bearing too. Matching on lane alone — the original — let an Immolation tick grow a melee
     * number that then read "(Spinning Attack)", and let two different DoTs taken collapse into whichever landed first; NAG
     * folds only into a component with identical flags, and Mik's Scrolling Battle Text merges only on matching event type
     * *and* skill name.
     */
    private static bool TryAbsorb(List<FctHitState> hits, FctLane lane, bool incoming, bool proc, bool periodic, string source,
      double value, double now)
    {
      for (var i = hits.Count - 1; i > -1; i--)
      {
        var hit = hits[i];
        if (hit.Lane != lane || hit.Blowout || hit.FixedText is not null
            || hit.Incoming != incoming || hit.Proc != proc || hit.Periodic != periodic
            || !string.Equals(hit.Source, source, StringComparison.Ordinal)

            /*
             * "Identical" means identical at the precision the player can read: two hits that draw the same main line are the
             * same number as far as anyone can tell, so 12,480 and 12,520 both being "12.5k" may share a count while 2,040 and
             * 2,041 may not. Compared last, because it is the only part of the key that allocates.
             */
            || !string.Equals(FctText.FormatHitValue(hit.Value), FctText.FormatHitValue(value), StringComparison.Ordinal))
        {
          continue;
        }

        var age = now - hit.SpawnMs;
        if (age > Math.Min(AbsorbWindowMs, hit.LifetimeMs * AbsorbMaxLifeFrac))
        {
          continue;
        }

        /* The one thing a fold does: say so. The drawn amount stays the face value of a single hit. */
        hit.MergeCount++;
        FctMotion.RefreshText(hit);
        return true;
      }

      return false;
    }

    /*
     * What a number is worth keeping on screen: everything it stands for, with a proc discounted because one is subordinate
     * by design. The count matters because eviction is a choice about what to lose — dropping a number that represents six
     * identical hits loses six hits, not one, whatever its face value says.
     */
    private static double Significance(double value, int mergeCount, bool proc) =>
      value * Math.Max(1, mergeCount) * (proc ? ProcSignificanceFrac : 1.0);

    /*
     * The cheapest occupied slot in this lane and side: the least significant number there, excluding crits (a crit keeps
     * the ground its pop and glow bought — stealing it to show something smaller would be backwards) and labels (nothing
     * numeric to compare). Returns null unless the newcomer clearly outranks the weakest occupant, which is the whole
     * point: an occupied lane must not shuffle numbers around for a hit nobody would have missed.
     */
    private static FctHitState PickEvictionTarget(List<FctHitState> hits, FctLane lane, bool incoming, double incomingSignificance)
    {
      FctHitState weakest = null;
      var weakestScore = double.MaxValue;

      for (var i = 0; i < hits.Count; i++)
      {
        var hit = hits[i];
        if (hit.Lane != lane || hit.Incoming != incoming || hit.Blowout || hit.FixedText is not null)
        {
          continue;
        }

        var score = Significance(hit.Value, hit.MergeCount, hit.Proc);
        if (score < weakestScore)
        {
          weakest = hit;
          weakestScore = score;
        }
      }

      return weakest is not null && weakestScore * EvictValueFactor <= incomingSignificance ? weakest : null;
    }

    private void AssignLifetime(FctHitState hit, List<FctHitState> hits, FctStage stage, double now)
    {
      /*
       * The choreographed styles (fountain, spray) share one shape: travel, then accelerate along a fall for the rest
       * of life — no hold phase, and the fade spans exactly the fall. Both mirror that fall on the incoming band in
       * bands mode rather than dropping it: gravity points at the bottom of the screen, and their band is the last
       * thing before that edge, so a literal downward fall parks the number against its own bottom edge for half its
       * life, which reads as stuck rather than as physics. Falling back up toward the gap keeps the overshoot-and-settle
       * shape on both sides, keeps the two directions reading as one animation in opposite signs, and cannot reach the
       * protected strip because the return is a fraction of travel already spent below it.
       *
       * Pulse and Hold fall through to the adaptive lifetime: neither has a fall, so neither needs its life dictated
       * by a choreography. The fall itself is FctLayout.ApplyFall's, because placement may re-roll the origin afterwards
       * and has to be able to ask for it again.
       */
      if (hit.Style is FctMotionStyle.Fountain or FctMotionStyle.Spray)
      {
        // the choreography is the life: travel then fall, no hold phase, and the fade spans exactly the fall. Each style
        // owns its tempo — spray runs shorter, because sharing fountain's flight time was half of why the two looked alike
        // (and hit.Style, not the ingest's current one: a hit keeps the style it was born with)
        var window = hit.Style is FctMotionStyle.Spray ? FctMotion.SprayMotionWindowMs : FctMotion.MotionWindowMs;

        hit.LifetimeMs = window;
        hit.MotionMs = window;
        hit.FadeMs = window * FctMotion.FallPhaseFrac;

        // Rise is already assigned: layout runs before lifetime assignment. FctPlacement re-runs the same call on a trial origin.
        FctLayout.ApplyFall(hit, stage);
        ApplyProcTempo(hit);
        ApplyPlayerTempo(hit);
        return;
      }

      /* The rail's tempo belongs to the stream, not here: placement re-spawns a stream row at its pinned edge,
       * so this hit's Rise is still provisional at this point in Accept — and a duration computed from a distance
       * that changes afterwards is how three rows on one rail each ended up with their own private tempo, phase drift
       * shearing apart exactly the chain this style exists to make. See ApplyRailTempo. */
      if (FctMotionStyles.IsRail(hit.Style))
      {
        return;
      }

      // adaptive display time (see FctLifeController); crits keep a fixed lifetime and stay prominent
      hit.LifetimeMs = hit.Lane == FctLane.Crit ? CritLifetimeMs : _life.NextLifetime(hit.Lane, LiveCount(hits, hit.Lane), now);
      hit.MotionMs = Math.Min(FctMotion.MotionWindowMs, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000); // fade is a share of the life, capped
      ApplyProcTempo(hit);
      ApplyPlayerTempo(hit);
    }

    /*
     * The rail's tempo: ONE SCROLL RATE for everything in split — damage, procs, crits, words, both directions.
     * It is a rate, not a duration: each row's time is its own travel (edge to edge minus the room it reserves for
     * its own height — a crit gives up more road than a miss word does) over the shared px-per-second. The first
     * version shared a DURATION computed from the region instead, which is precisely a per-category speed difference:
     * measured 195.6 px/s for damage against 201.8 for words and slower still for crits, and players read it —
     * correctly — as the streams not agreeing. A shared rate costs almost nothing and keeps every chain property the
     * stream relies on: two rows a beat apart keep that gap of road forever BECAUSE they move at one rate; bows run
     * their own parabolas, and flight-scored placement measures real flights rather than assuming identical phases.
     * Rows that parked at end of travel would all park in one place and the centre column would become a queue for a
     * parking space (FctStream): motion spans the life, so nothing parks. The stream applies it BEFORE scoring
     * candidate columns, because the candidates are whole flights and a trial cloned without a lifetime is a flight
     * that already ended — every column would read as empty (which is exactly how this was first missed).
     */
    internal static void ApplyRailTempo(FctHitState hit, FctStage stage)
    {
      var travel = Math.Abs(hit.Rise);
      hit.LifetimeMs = (travel > 1.0 ? travel : stage.RegionFor(hit).Height) * FctMotion.ParabolaScrollMsPerPx;
      hit.MotionMs = hit.LifetimeMs;
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000);

      /* No player tempo here: placement is about to decide this row's real travel, and FinalizeRailTempo restamps
       * everything once and exactly one time with it. Scaling the estimate here would compound with that. */
    }

    /*
     * The rail's EXACT tempo, stamped after placement has pinned the row on its edge. The tempo that candidate
     * columns were scored with ran on a provisional Rise — respawning the row at its edge happens inside placement —
     * and "every number in split crosses at one speed" is a statement about the real flight, not the estimate: with
     * the shared estimate left in place, rows measured 229-257 px/s against each other. Recomputed from the final
     * travel over the shared scroll rate, then scaled once by the player's speed dial; an assignment rather than a
     * multiplier, so nothing compounds however often placement passes through.
     */
    internal static void FinalizeRailTempo(FctHitState hit)
    {
      if (!FctMotionStyles.IsRail(hit.Style) || Math.Abs(hit.Rise) <= 1.0)
      {
        return;
      }

      hit.LifetimeMs = Math.Abs(hit.Rise) * FctMotion.ParabolaScrollMsPerPx;
      hit.MotionMs = hit.LifetimeMs;
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000);
      ApplyPlayerTempo(hit);
    }

    /*
     * The player's speed setting last and on top of everything above, because it is the outermost dial: proc shortening and the adaptive controller
     * decide a number's tempo, this decides how fast that tempo plays. It arrives as FctScale.Time, a duration - the reciprocal of what the slider
     * measures, converted in one place so that nothing here has to remember which way the dial points. Lifetime, travel and fade scale together:
     * scaling only the lifetime leaves a number hanging in mid-air past the end of its animation, which reads as a stutter and not as a slower overlay.
     *
     * The floor matters because the multipliers compound: a proc is already at 0.7 of its lane's life (ApplyProcTempo) under an adaptive lifetime that
     * shrinks with load, and the fast end of the dial leaves a number on screen for less than half the time it was choreographed at. Without floors that
     * asks for well under a second, which is a flicker rather than a fast overlay.
     *
     * Applied unconditionally, including at the shipped speed: the dial's middle is 0.877 of the measured time rather than 1.0, because the baseline
     * turned out to sit on the slow side of useful (docs/DesignNotes.md). There is no longer an "unset" case in which this should keep its hands off the
     * numbers and let the floors stand down.
     */
    private static void ApplyPlayerTempo(FctHitState hit)
    {
      hit.LifetimeMs = Math.Max(MinLifetimeMs, hit.LifetimeMs * FctScale.Time);
      hit.MotionMs = Math.Min(hit.MotionMs * FctScale.Time, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.FadeMs * FctScale.Time, MinFadeMs, hit.LifetimeMs);
    }

    /*
     * A proc is not the number the player was watching for: items and spell procs fire on their own schedule, often
     * several times a pull, and they land on top of the hit that provoked them. So the whole tempo shortens — travel,
     * hold and fade together, not just the tail — which pairs with the smaller type from FctStyle to keep a proc beside
     * a direct hit instead of competing with it.
     *
     * A crit proc is exempt: by definition that one deserves a look, and two rules quietly reducing the loudest number
     * in the log would be worse than either rule alone. Same exemption FctStyle applies to its size.
     */
    private static void ApplyProcTempo(FctHitState hit)
    {
      if (!hit.Proc || hit.Blowout)
      {
        return;
      }

      hit.LifetimeMs *= FctMotion.ProcTimeFrac;
      hit.MotionMs = Math.Min(hit.MotionMs * FctMotion.ProcTimeFrac, hit.LifetimeMs);
      hit.FadeMs *= FctMotion.ProcTimeFrac;
    }

  }
}
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

    /* A hit under half its lane's rolling median is routine noise; fold it instead of spawning. */
    private const double AbsorbFractionOfMedian = 0.5;

    /* Only fold into a hit young enough that the count-up still reads as part of the same exchange, and
     * one that has most of its life left — absorbing into a fading number would hide the amount. */
    private const double AbsorbWindowMs = 1500;
    private const double AbsorbMaxLifeFrac = 0.6;

    /* Crits do not adapt (they must stay prominent) and do not absorb; this is their whole display time. */
    private const double CritLifetimeMs = 2800;

    /*
     * A full lane is a decision about who owns the slot, so a newcomer takes one away from something else only when it is
     * clearly the more informative of the two: at least this many times as significant. Significance is face value with a
     * proc discounted — FctStyle and ApplyProcTempo already say a proc is subordinate to the hit that provoked it, so an
     * old proc is the cheapest slot on screen and a big direct cast costs almost nothing to place.
     */
    private const double EvictValueFactor = 2.0;
    private const double ProcSignificanceFrac = 0.5;

    private readonly FctLifeController _life = new();
    private readonly FctMedianTracker _median = new();
    private readonly Random _rand;

    public FctIngest(Random rand = null) => _rand = rand ?? new Random();

    /*
     * Motion style for new hits (see FctMotionStyle): how text moves, as opposed to where it goes, which FctLayout decides
     * and does not ask about. A canvas setting rather than a per-record one: a feed that mixed styles from log line to log
     * line would look like a bug, not a feature.
     */
    public FctMotionStyle Style = FctMotionStyle.Hold;

    /* Hits that had nowhere to go, surfaced in the overlay header so overload stays visible. */
    public int DroppedCount { get; private set; }

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
      var pooled = crit ? FctLane.Crit : lane;

      /*
       * Pulse mode allocates cells, and cells are its capacity: folding a number into a running total that lives in some
       * other cell would hide the fold, and the lane cap would drop hits that a free cell has room for. So the absorb and
       * overflow paths below belong to the travelling styles only. The grid spans the canvas because there is one region
       * scheme to lay cells out in; making halves mode available meant that promise was false in it, which is why halves is
       * gone rather than patched (see the FctLayout header).
       */
      var celled = Style is FctMotionStyle.Pulse;

      if (fixedText is null)
      {
        _median.Add(lane, value);
        if (!celled && !crit && ShouldAbsorb(pooled, value, periodic) &&
            TryAbsorb(hits, pooled, incoming, proc, periodic, source, value, now))
        {
          return null;
        }
      }

      if (!celled && LiveCount(hits, pooled) >= LaneCap)
      {
        /*
         * The lane is full. Three options, in the order that loses least.
         *
         * Fold into a number that is about this same ability: at the cap the size test goes away, because a running
         * total of ten procs beats either a counted drop or a fourteenth overlapping number, and the label stays honest
         * now that the fold key includes the ability.
         *
         * Otherwise take the slot from the least significant number already on screen — but only if this newcomer
         * clearly outranks it, so the thing that disappears is smaller and less worth reading than the thing that
         * arrives. This is what stops the cap from hiding a big cast behind twelve routine swings.
         *
         * Only then count a drop. Everything above either put the amount on screen or added it to a total that says
         * what it is.
         */
        if (fixedText is null && !crit && ShouldAbsorb(pooled, value, periodic, relaxed: true) &&
            TryAbsorb(hits, pooled, incoming, proc, periodic, source, value, now, relaxed: true))
        {
          return null;
        }

        var taken = PickEvictionTarget(hits, pooled, incoming, Significance(value, proc));
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
        Incoming = incoming,
        Style = Style,
        SpawnMs = now,
        Source = source,
        FixedText = fixedText,
        TargetValue = value,
        CountBaseValue = value,
      };

      FctStyle.ApplyTo(hit, pooled, minor || periodic, proc);
      FctLayout.Spawn(hit, w, h, _rand);
      AssignLifetime(hit, hits, h, now);

      /*
       * Seed the width estimate now: the clamp band that keeps text out of the protected center is derived
       * from the drawn width, and the backend does not measure real glyphs until its first draw.
       */
      hit.ValueWidth = FctLayout.EstimateTextWidth(fixedText ?? FctText.FormatHitValue(value), hit.ValueFontSize);

      /*
       * Cells when there is room for a grid; on a very small overlay FctCellGrid has nothing to offer and the hit keeps the
       * static placement FctLayout gave it. Losing the layout is fine, losing the number is not.
       */
      if (celled && FctCellGrid.HasRoom(hit, h))
      {
        if (!FctCellGrid.Assign(hit, hits, w, h, now, out var bumped))
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

      FctMotion.RefreshText(hit, 0);
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
     * Whether an incoming amount may be folded into a live number at all — the fold key itself is TryAbsorb's.
     *
     * Periodic ticks always may: one running number per ability beats five overlapping ticks. Healing direct casts never
     * may, because players read heals one by one and merging two casts hides who got patched and for how much — and that
     * rule is asked *before* both the normal fold and the relaxed at-cap one, which is what makes it true at the cap too.
     * A heal with nowhere to go gets a slot taken for it (PickEvictionTarget) or is counted as dropped; it does not get
     * added to somebody else's number.
     *
     * Direct damage folds when it is routine for its lane, meaning under half the running median. Relaxed (lane at cap)
     * every direct hit qualifies: same-ability totals are honest, and losing damage silently is not.
     */
    private bool ShouldAbsorb(FctLane lane, double value, bool periodic, bool relaxed = false)
    {
      if (periodic)
      {
        return true;
      }

      if (lane is not (FctLane.DamageDealt or FctLane.DamageTaken))
      {
        return false;
      }

      if (relaxed)
      {
        return true;
      }

      var median = _median.Median(lane);
      return median > 0 && value < median * AbsorbFractionOfMedian;
    }

    /*
     * Folds amount into a live number about the *same thing* — NAG's accumulateHits count-up, with the key NAG uses.
     * Same pooled lane, same side, same proc/direct and periodic/direct kind, same ability name; then the same age rules
     * (young enough that the count-up reads as one exchange, unless the lane is at its cap and this is the best option
     * left). Crits never absorb either way: a crit number that quietly grows is misleading, and that overload is what the
     * drop counter exists to make visible.
     *
     * The key is load-bearing. Matching on lane alone — which is what this did — let an Immolation tick grow a melee
     * number that then read "(Spinning Attack)", and let two different DoTs taken collapse into whichever landed first.
     * That is not grouping, it is a wrong total wearing the right label; NAG folds only into a component with identical
     * flags, and Mik's Scrolling Battle Text merges only on matching event type *and* skill name.
     */
    private static bool TryAbsorb(List<FctHitState> hits, FctLane lane, bool incoming, bool proc, bool periodic, string source,
      double amount, double now, bool relaxed = false)
    {
      for (var i = hits.Count - 1; i > -1; i--)
      {
        var hit = hits[i];
        if (hit.Lane != lane || hit.Blowout || hit.FixedText is not null
            || hit.Incoming != incoming || hit.Proc != proc || hit.Periodic != periodic
            || !string.Equals(hit.Source, source, StringComparison.Ordinal))
        {
          continue;
        }

        var age = now - hit.SpawnMs;
        if (!relaxed && age > Math.Min(AbsorbWindowMs, hit.LifetimeMs * AbsorbMaxLifeFrac))
        {
          continue;
        }

        hit.CountBaseValue = FctMotion.DisplayValue(hit, age);
        hit.AgeAtCountStartMs = age;
        hit.CountUpMs = FctMotion.CountUpMs;
        hit.TargetValue += amount;
        return true;
      }

      return false;
    }

    /* What a number is worth keeping on screen: its amount, with a proc discounted because one is subordinate by design. */
    private static double Significance(double value, bool proc) => value * (proc ? ProcSignificanceFrac : 1.0);

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

        var score = Significance(hit.TargetValue, hit.Proc);
        if (score < weakestScore)
        {
          weakest = hit;
          weakestScore = score;
        }
      }

      return weakest is not null && weakestScore * EvictValueFactor <= incomingSignificance ? weakest : null;
    }

    private void AssignLifetime(FctHitState hit, List<FctHitState> hits, double h, double now)
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
       * by a choreography.
       */
      // mirrored on the incoming band, where a downward fall has nowhere legal to go
      var mirrored = hit.Incoming;

      if (Style is FctMotionStyle.Fountain or FctMotionStyle.Spray)
      {
        // the choreography is the life: travel then fall, no hold phase, and the fade spans exactly the fall. Each style
        // owns its tempo — spray runs shorter, because sharing fountain's flight time was half of why the two looked alike
        var window = Style is FctMotionStyle.Spray ? FctMotion.SprayMotionWindowMs : FctMotion.MotionWindowMs;

        hit.LifetimeMs = window;
        hit.MotionMs = window;
        hit.FadeMs = window * FctMotion.FallPhaseFrac;

        // Rise is already assigned: layout runs before lifetime assignment
        var depth = FallDepth(hit, h, mirrored);
        hit.FallDist = mirrored ? -depth : depth;
        ApplyProcTempo(hit);
        return;
      }

      // adaptive display time (see FctLifeController); crits keep a fixed lifetime and stay prominent
      hit.LifetimeMs = hit.Lane == FctLane.Crit ? CritLifetimeMs : _life.NextLifetime(hit.Lane, LiveCount(hits, hit.Lane), now);
      hit.MotionMs = Math.Min(FctMotion.MotionWindowMs, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000); // fade is a share of the life, capped
      ApplyProcTempo(hit);
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

    /*
     * How far gravity carries a choreographed hit past its apex, always positive — the caller applies the sign. Spray
     * measures against the height this particular number reached: falling less than it rose is what stops any angle of
     * the cone from returning a number to the band edge it left, which keeps the protected strip clear for every random
     * draw rather than for the lucky ones. A mirrored fountain — any incoming one, whose downward fall would park it on its
     * own band edge — uses a share of how far it sank; an outgoing one takes the canvas-relative throw it has always had,
     * held honest by the band clamp.
     */
    private double FallDepth(FctHitState hit, double h, bool mirrored) =>
      Style is FctMotionStyle.Spray ? Math.Abs(hit.Rise) * FctLayout.SprayFallFrac
        : mirrored ? Math.Abs(hit.Rise) * FctMotion.IncomingFallsBackFrac
        : h * 0.28;
  }
}

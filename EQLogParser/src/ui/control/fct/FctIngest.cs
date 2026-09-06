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
        Incoming = incoming,
        Style = Style,
        SpawnMs = now,
        Source = source,
        FixedText = fixedText,
        Value = value,
      };

      FctStyle.ApplyTo(hit, pooled, minor || periodic, proc);
      FctLayout.Spawn(hit, w, h, _rand);
      AssignLifetime(hit, hits, h, now);

      /*
       * Seed the width estimate now: the clamp band that keeps text out of the protected center is derived
       * from the drawn width, and the backend does not measure real glyphs until its first draw.
       */
      hit.ValueWidth = FctLayout.EstimateTextWidth(fixedText ?? FctText.FormatHit(value, 1), hit.ValueFontSize);

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

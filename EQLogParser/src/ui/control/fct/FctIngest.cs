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

    private readonly FctLifeController _life = new();
    private readonly FctMedianTracker _median = new();
    private readonly Random _rand;

    public FctIngest(Random rand = null) => _rand = rand ?? new Random();

    /*
     * Region scheme handed to FctLayout for new hits (bands by default). The canvas sets it once from settings.ini;
     * changing it only affects hits spawned afterwards, which is what makes flipping it mid-fight worth doing.
     */
    public FctLayoutMode Mode = FctLayoutMode.Bands;

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
     */
    public FctHitState Accept(List<FctHitState> hits, FctLane lane, double value, string source, bool crit, bool minor, bool periodic,
      string fixedText, double w, double h, double now, bool fountain)
    {
      if (w < 100 || h < 100)
      {
        return null; // nothing sane can be laid out yet (window not measured)
      }

      var incoming = FctLayout.IsIncoming(lane);          // before pooling: a taken crit stays on the incoming side
      var pooled = crit ? FctLane.Crit : lane;

      if (fixedText is null)
      {
        _median.Add(lane, value);
        if (!crit && ShouldAbsorb(pooled, value, periodic) && TryAbsorb(hits, pooled, value, now))
        {
          return null;
        }
      }

      if (LiveCount(hits, pooled) >= LaneCap)
      {
        if (fixedText is null && TryAbsorb(hits, pooled, value, now, relaxed: true))
        {
          return null;
        }

        DroppedCount++;
        return null;
      }

      var hit = new FctHitState
      {
        Lane = pooled,
        Incoming = incoming,
        SpawnMs = now,
        Source = source,
        FixedText = fixedText,
        TargetValue = value,
        CountBaseValue = value,
      };

      FctStyle.ApplyTo(hit, pooled, minor || periodic);
      FctLayout.Spawn(hit, w, h, _rand, Mode);
      AssignLifetime(hit, hits, h, now, fountain);

      /*
       * Seed the width estimate now: the clamp band that keeps text out of the protected center is derived
       * from the drawn width, and the backend does not measure real glyphs until its first draw.
       */
      hit.ValueWidth = FctLayout.EstimateTextWidth(fixedText ?? FctText.FormatHitValue(value), hit.ValueFontSize);
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

    /* Periodic ticks always fold (that is the whole point of grouping DoTs); direct hits only when small. */
    private bool ShouldAbsorb(FctLane lane, double value, bool periodic)
    {
      if (periodic)
      {
        return true; // one running number per lane beats five overlapping ticks
      }

      if (lane is not (FctLane.DamageDealt or FctLane.DamageTaken))
      {
        return false; // healing stays one number per cast: players read those individually
      }

      var median = _median.Median(lane);
      return median > 0 && value < median * AbsorbFractionOfMedian;
    }

    /*
     * Folds amount into a live hit of the lane (NAG's accumulateHits count-up). Normally the target has to be
     * young, because absorbing into a number that is already fading hides the amount. At the lane cap there is no
     * good alternative left: the newest hit has more life ahead of it than anything else on screen, and a counted
     * drop is damage nobody ever saw. Crits never absorb either way — a crit number that quietly grows is
     * misleading, and that overload is what the drop counter exists to make visible.
     */
    private static bool TryAbsorb(List<FctHitState> hits, FctLane lane, double amount, double now, bool relaxed = false)
    {
      for (var i = hits.Count - 1; i > -1; i--)
      {
        var hit = hits[i];
        if (hit.Lane != lane || hit.Blowout || hit.FixedText is not null)
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

    private void AssignLifetime(FctHitState hit, List<FctHitState> hits, double h, double now, bool fountain)
    {
      /*
       * A fountain's fall travels downwards, which in bands mode would drive an incoming hit through the bottom of
       * its band and park it there for the rest of its life. The direction cue matters more than the flourish, so
       * incoming hits rise-and-hold even with fountain motion on; halves mode keeps the choreography everywhere.
       */
      if (fountain && !(hit.Incoming && Mode is FctLayoutMode.Bands))
      {
        // the choreography is the life: rise then fall, no hold phase, and the fade spans exactly the fall
        hit.LifetimeMs = FctMotion.MotionWindowMs;
        hit.MotionMs = FctMotion.MotionWindowMs;
        hit.FadeMs = FctMotion.MotionWindowMs * FctMotion.FallPhaseFrac;
        hit.FallDist = h * 0.28;
        return;
      }

      // adaptive display time (see FctLifeController); crits keep a fixed lifetime and stay prominent
      hit.LifetimeMs = hit.Lane == FctLane.Crit ? CritLifetimeMs : _life.NextLifetime(hit.Lane, LiveCount(hits, hit.Lane), now);
      hit.MotionMs = Math.Min(FctMotion.MotionWindowMs, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000); // fade is a share of the life, capped
    }
  }
}

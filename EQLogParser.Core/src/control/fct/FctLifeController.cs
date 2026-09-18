using System;

namespace EQLogParser
{
  /*
   * Adaptive display time per lane. Presentation policy, so it lives with the canvases (Core never sees
   * congestion). At each spawn the lifetime is chosen so the lane's predicted fill (live count + rate x
   * lifetime) stays at or under its capacity: L = clamp(1000 x (capacity - liveCount) / rate, floor, baseline)
   * in ms, rate being hits per second. A doubled incoming rate roughly halves how long entries stay on
   * screen; a lane already at capacity gets the floor.
   *
   * Two things this governor is asked about, and only these. It is asked by STREAM, never by presentation class:
   * the crit badge pools numbers onto FctLane.Crit for colour, halo and folding, but a modern raider crits most of
   * what it deals, so that class is not a stream with its own traffic — the column the number travels in is (see
   * FctHitState.TrafficLane), and counting occupancy by the badge reported an empty lane over a screenful of numbers.
   * And every class asks: crits used to be exempt and hold a fixed lifetime, which at raid tempo left them on screen
   * nearly three times as long as the numbers around them.
   *
   * The floor is where congestion stops and legibility begins, so it sits high — overflow is what folding and
   * eviction are for, not truncating reading time — and the player's speed dial (FctScale.Time) plays whatever this
   * decides up or down. Why the baseline is well under NAG's 7 s: docs/DesignNotes.md → Floating Combat Text.
   */
  internal sealed class FctLifeController
  {
    public const double BaselineMs = 3500;

    /* The shortest time a number may hold the screen, and twice what this was. The old floor traded legibility for
     * headroom that folding and eviction win better, and the one class exempt from adaptation (crits, fixed at 2800)
     * was the one that read better side by side. A table wanting less has FctScale.Time. */
    public const double FloorMs = 2000;
    private const double RateAlpha = 0.25;  // EMA weight of the newest inter-arrival interval

    /* Enum.GetValues allocates an array per call, and this is one lane slot each: counted once, ever. */
    private static readonly int LaneCount = Enum.GetValues<FctLane>().Length;

    private readonly double[] _ratePerSec = new double[LaneCount];
    private readonly double[] _lastSpawnMs = new double[LaneCount];

    /* Comfortable concurrent entries per lane; 0 means no adaptation. */
    public static double Capacity(FctLane lane) => lane switch
    {
      FctLane.DamageDealt or FctLane.DamageTaken => 7,
      FctLane.HealingDealt or FctLane.HealingReceived => 5,
      FctLane.Defensive or FctLane.Missed => 5,
      _ => 0, // Crit: not a stream (see the header) — its numbers are counted and timed as whatever column they came from
    };

    /* Call once per spawn with the stream's current live count. Returns a lifetime in ms; 0 means the lane has no
       capacity and is not governed here at all, so the caller falls back to BaselineMs. */
    public double NextLifetime(FctLane lane, int liveCount, double nowMs)
    {
      if (Capacity(lane) <= 0)
      {
        return 0;
      }

      var i = (int)lane;
      var dt = _lastSpawnMs[i] > 0 ? nowMs - _lastSpawnMs[i] : -1;

      // smooth the arrival rate; after a long gap forget it and start clean
      if (dt is > 0 and < 5000)
      {
        _ratePerSec[i] += RateAlpha * ((1000.0 / dt) - _ratePerSec[i]);
      }
      else
      {
        _ratePerSec[i] = 0;
      }

      _lastSpawnMs[i] = nowMs;
      var rate = _ratePerSec[i];
      if (rate <= 0.05)
      {
        return BaselineMs;
      }

      // slack measured in hits, rate in hits/second: the 1000 is what turns the quotient into milliseconds
      var slack = Math.Max(0, Capacity(lane) - liveCount);
      return Math.Clamp((1000.0 * slack) / rate, FloorMs, BaselineMs);
    }
  }
}
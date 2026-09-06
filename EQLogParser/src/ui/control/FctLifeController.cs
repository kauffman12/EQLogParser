using System;

namespace EQLogParser
{
  /*
   * Adaptive display time per lane. Presentation policy, so it lives with the canvases (Core never sees
   * congestion). At each spawn the lifetime is chosen so the lane's predicted fill (live count + rate x
   * lifetime) stays at or under its capacity: L = clamp(1000 x (capacity - liveCount) / rate, floor, baseline)
   * in ms, rate being hits per second. A doubled incoming rate roughly halves how long entries stay on
   * screen; a lane already at capacity
   * gets the floor. Crits do not adapt (NextLifetime returns 0 for them) — they are rare and must keep
   * standing out. Why the baseline is well under NAG's 7 s: docs/DesignNotes.md → Floating Combat Text.
   */
  internal sealed class FctLifeController
  {
    private const double BaselineMs = 3500;
    private const double FloorMs = 1000;    // below this, numbers stop being readable
    private const double RateAlpha = 0.25;  // EMA weight of the newest inter-arrival interval

    private readonly double[] _ratePerSec = new double[Enum.GetValues(typeof(FctLane)).Length];
    private readonly double[] _lastSpawnMs = new double[Enum.GetValues(typeof(FctLane)).Length];

    /* Comfortable concurrent entries per lane; 0 means no adaptation. */
    public static double Capacity(FctLane lane) => lane switch
    {
      FctLane.DamageDealt or FctLane.DamageTaken => 7,
      FctLane.HealingDealt or FctLane.HealingReceived => 5,
      FctLane.Defensive or FctLane.Missed => 5,
      _ => 0, // Crit: caller keeps its fixed lifetime
    };

    /* Call once per spawn with the lane's current live count. Returns a lifetime in ms (0: don't adapt). */
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

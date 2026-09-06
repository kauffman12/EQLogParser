using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Rolling per-lane median of recent hit values — NAG's "maxHit = 2 x median" idea (see
   * docs/NagFctReference.md). It is what makes one threshold work for a level 10 rogue and a raid-geared
   * wizard, and it follows the character as they get stronger. Presentation policy, so Core never sees it.
   */
  internal sealed class FctMedianTracker
  {
    private const int Window = 100;

    // below this the sample is meaningless — early in a fight everything would look "small"
    private const int MinSamples = 8;

    private readonly Dictionary<FctLane, Queue<double>> _recent = [];

    public void Add(FctLane lane, double value)
    {
      if (!_recent.TryGetValue(lane, out var samples))
      {
        samples = new Queue<double>(Window + 1);
        _recent[lane] = samples;
      }

      samples.Enqueue(value);
      while (samples.Count > Window)
      {
        samples.Dequeue();
      }
    }

    /* 0 until enough of the lane's history is known, which keeps early hits from looking tiny. */
    public double Median(FctLane lane)
    {
      if (!_recent.TryGetValue(lane, out var samples) || samples.Count < MinSamples)
      {
        return 0;
      }

      // once per incoming hit over a <=100 window: an allocation and a sort nobody will notice
      var copy = new double[samples.Count];
      samples.CopyTo(copy, 0);
      Array.Sort(copy);
      return copy[copy.Length / 2];
    }
  }
}

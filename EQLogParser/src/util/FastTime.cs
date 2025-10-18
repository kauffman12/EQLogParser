using System;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser
{
  internal static class FastTime
  {
    // Cached local time aligned to whole seconds
    private static long _baseWholeSecondTicks;     // local ticks, floored to the whole second
    private static long _baseSwTicks;              // Stopwatch ticks at the base time
    private static long _cachedWholeSeconds;       // whole seconds since epoch (as long)
    private static int _arithAdvances;            // arithmetic advances since last resync
    private const int MaxArithmeticAdvances = 5;   // force resync after a few seconds

    public static (double Seconds, long Ticks) Now()
    {
      var swNow = Stopwatch.GetTimestamp();
      var baseSw = Volatile.Read(ref _baseSwTicks);

      // First call — initialize from DateTime.Now
      if (baseSw == 0)
        return Resync(swNow);

      var elapsedSw = swNow - baseSw;
      var freq = Stopwatch.Frequency;

      // < 1s since last base: return cached values, no DateTime.Now hit
      if (elapsedSw < freq)
        return (Volatile.Read(ref _cachedWholeSeconds), Volatile.Read(ref _baseWholeSecondTicks));

      // Whole seconds elapsed (monotonic)
      var elapsedWholeSeconds = elapsedSw / freq;

      // Small gaps: advance arithmetically to avoid DateTime.Now
      if (elapsedWholeSeconds <= MaxArithmeticAdvances)
      {
        var newSecs = Volatile.Read(ref _cachedWholeSeconds) + elapsedWholeSeconds;
        var newTicks = Volatile.Read(ref _baseWholeSecondTicks) + (elapsedWholeSeconds * TimeSpan.TicksPerSecond);
        var newBaseSw = baseSw + (elapsedWholeSeconds * freq);

        Volatile.Write(ref _cachedWholeSeconds, newSecs);
        Volatile.Write(ref _baseWholeSecondTicks, newTicks);
        Volatile.Write(ref _baseSwTicks, newBaseSw);

        var adv = Volatile.Read(ref _arithAdvances) + 1;
        Volatile.Write(ref _arithAdvances, adv);

        // Periodically resync with DateTime.Now to track clock/DST changes
        if (adv >= MaxArithmeticAdvances)
          return Resync(swNow);

        return (newSecs, newTicks);
      }

      // Big gap: resync immediately
      return Resync(swNow);
    }

    private static (double seconds, long ticks) Resync(long swNow)
    {
      var nowTicks = DateTime.Now.Ticks; // local time (as requested)
      var wholeTicks = nowTicks - (nowTicks % TimeSpan.TicksPerSecond);
      var wholeSeconds = wholeTicks / TimeSpan.TicksPerSecond;

      Volatile.Write(ref _baseWholeSecondTicks, wholeTicks);
      Volatile.Write(ref _cachedWholeSeconds, wholeSeconds);
      Volatile.Write(ref _baseSwTicks, swNow);
      Volatile.Write(ref _arithAdvances, 0);

      return (wholeSeconds, wholeTicks);
    }
  }
}

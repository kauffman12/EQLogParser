using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EQLogParser
{
  internal class MonoTime
  {
    // precompute for tiny speed win
    internal static readonly long Freq = Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long NowStamp() => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long SecondsToTicks(double seconds) => (long)Math.Round(seconds * Freq);
  }
}

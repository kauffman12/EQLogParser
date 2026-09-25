using System;

namespace EQLogParser
{
  /*
   * Answers one question about a value's hash: "have you probably seen this before?". It exists so a
   * record cache can spend an entry only on values that repeat.
   *
   * The measurement behind it (docs/DesignNotes.md → What a loaded raid costs in memory): of 1,609,080
   * distinct damage records in one player's ten-day capture, 1,348,947 — 83.8% — are never restated. Each
   * of those holds an entry that answers no lookup ever again while the record itself stays in the store
   * regardless, so the entry is pure cost. Asking "seen before?" first and declining to cache the answer took
   * the damage cache from 1,612,020 entries down to 261,947 for 5 MB of bits: 55 MB of heap in a run of the real
   * pipeline, more than every other damage-record change measured against that capture. (The ungated cache holds
   * a few thousand more entries than there are distinct values because of the record rewrite documented in the
   * note, not because of anything this file does.)
   *
   * It is a Bloom filter, so it can be wrong in the direction of "seen", never in the direction of "not
   * seen" while its bits are intact: a value it has marked is reported as marked. That asymmetry is the
   * whole reason this is safe next to a cache that must not merge events — a false positive buys one entry
   * nobody needed (the behavior of the cache before this class existed), and nothing else about the record
   * is decided from an answer here. No value is ever named, compared or substituted by this file.
   *
   * Saturating is handled by starting over rather than growing: memory is the thing being protected, so a
   * filter that doubled to 20 MB would be worse than one that resets. A reset forgets which values have
   * been seen, which costs at most one shared instance per value that happens to repeat across the reset
   * boundary — bytes again, never correctness.
   *
   * Not thread-safe, matching the caches it serves: one log reader thread feeds both.
   */
  internal sealed class RepeatFilter
  {
    // 10 bits and 4 probes: a fresh filter answers about one sighting in a thousand wrongly, and on the real
    // log this was measured against it answered 399 wrongly out of 1.35M (docs/DesignNotes.md). The rate is
    // worst at the end of a window, which is why the window ends there.
    private const int BitsPerValue = 10;
    private const int Probes = 4;

    private readonly ulong[] _bits;

    // Sightings a window holds before the filter begins again.
    private readonly int _capacity;
    private int _marked;

    internal RepeatFilter(int expectedSightings)
    {
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSightings);

      // The window is counted in sightings, not bits: with Probes bits written per sighting, ten bits per
      // expected sighting reaches the design's false-positive rate exactly at the end of it. Counting to ten
      // times that instead would run the filter long past saturation, where it answers "seen" to everything
      // and the cache behind it behaves like the ungated one.
      _capacity = expectedSightings;
      _bits = new ulong[(long)expectedSightings * BitsPerValue / 64 + 1];
      Bytes = _bits.Length * sizeof(ulong);
    }

    /* Bytes of bits, so a caller can say what the frugality itself costs. */
    internal long Bytes { get; }

    /* Records this hash and reports whether it was probably already recorded. */
    internal bool ProbablySeen(int valueHash)
    {
      var h = (ulong)(uint)valueHash;
      // The second probe step has to be odd, or every probe for an even hash lands on the same few bits.
      var step = ((h * 0x9E3779B97F4A7C15UL) >> 32) | 1;
      var span = (ulong)_bits.Length * 64;
      var seen = true;

      for (var i = 0; i < Probes; i++)
      {
        var bit = (h + (ulong)i * step) % span;
        ref var word = ref _bits[bit / 64];
        var mask = 1UL << (int)(bit % 64);
        if ((word & mask) == 0)
        {
          seen = false;
        }

        word |= mask;
      }

      // One window of sightings is as much history as the bits can hold honestly; past it the filter begins
      // again rather than growing, because memory is the reason it exists.
      if (++_marked >= _capacity)
      {
        Reset();
      }

      return seen;
    }

    internal void Reset()
    {
      Array.Clear(_bits);
      _marked = 0;
    }
  }
}

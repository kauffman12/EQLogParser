using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Keeps one instance per distinct value for the million-per-night records a raid produces, and — the part
   * that was worth the most bytes — spends an entry only on values that turn out to repeat. See
   * docs/DesignNotes.md → What a loaded raid costs in memory.
   *
   * TryGet and Offer are two halves of one operation: look for a shared instance, and if there was none,
   * offer the caller's own instance to be remembered. Calling Offer without the failed TryGet in front of
   * it is not wrong, it just spends an entry on a first sighting, which is what this class stopped doing.
   * The halves are split because the damage path settles a record's spelling between them — that settles
   * the value the entry is keyed on, and the hit path must not pay for interning a value it already has.
   *
   * What comes back out of TryGet is always a value-equal instance, so sharing is invisible to anything that
   * reads a record. Nothing here can merge two different values: the HashSet decides identity by the same
   * Equals/GetHashCode the callers always used, and RepeatFilter only ever decides whether to keep an entry.
   *
   * Not thread-safe, matching the caches it replaces: one log reader thread feeds both.
   */
  internal sealed class RepeatStore<T> where T : class
  {
    private readonly HashSet<T> _kept = [];
    private readonly RepeatFilter _filter;

    internal RepeatStore(int expectedSightings)
    {
      _filter = new RepeatFilter(expectedSightings);
    }

    /* Entries held. */
    internal int Held => _kept.Count;

    /* Sightings let go without an entry, which is the whole point of the filter. Read-only in tests. */
    internal int LetGo { get; private set; }

    /* What the filter itself costs, so a caller can weigh the frugality against its own footprint. */
    internal long FilterBytes => _filter.Bytes;

    /* The shared instance for a value already worth sharing, if there is one. */
    internal bool TryGet(T incoming, out T cached) => _kept.TryGetValue(incoming, out cached);

    /*
     * Remembers this instance for later repeats — unless it is the first time this value has been seen,
     * which 84% of the time it is, and which is why the entry is not taken. The caller keeps its own
     * instance either way and stores that; a repeat of a skipped value is what buys the entry.
     */
    internal void Offer(T incoming)
    {
      if (!_filter.ProbablySeen(incoming.GetHashCode()))
      {
        LetGo++;
        return;
      }

      _kept.Add(incoming);
    }

    /* Drops the shared instances and the sighting history with them. */
    internal void Clear()
    {
      _kept.Clear();
      _filter.Reset();
      LetGo = 0;
    }
  }
}

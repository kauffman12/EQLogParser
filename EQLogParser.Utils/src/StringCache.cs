using System.Collections.Concurrent;

namespace EQLogParser
{
  /// <summary>
  /// Thread-safe string deduplication cache.
  /// Replaces string.Intern to avoid the global lock while still deduplicating
  /// repeated strings across the entire application. Normalizes strings to
  /// title case before caching so casing is deterministic and consistent.
  /// </summary>
  internal static class StringCache
  {
    private static readonly ConcurrentDictionary<string, string> _cache = new();

    /// <summary>
    /// Returns the cached string if it exists, otherwise stores and returns the string.
    /// Normalizes to uppercase first for consistent casing across all callers.
    /// </summary>
    public static string GetOrAdd(string s)
    {
      if (string.IsNullOrEmpty(s)) return s;
      var key = TextUtils.CapitalizeFirst(s);
      return _cache.GetOrAdd(key, key);
    }

    /// <summary>
    /// Returns the cached string if it exists, otherwise stores and returns it unchanged.
    /// Use instead of <see cref="string.Intern"/> for values whose exact text is displayed
    /// (spell names, npc names): deduplicated without the runtime's global intern lock and,
    /// unlike interning, the strings are reclaimable when the cache is cleared.
    /// </summary>
    public static string GetOrAddExact(string s)
    {
      if (string.IsNullOrEmpty(s)) return s;
      return _cache.GetOrAdd(s, s);
    }

    /// <summary>
    /// Clears all cached strings. Call when clearing active data.
    /// </summary>
    internal static void Clear() => _cache.Clear();
  }
}

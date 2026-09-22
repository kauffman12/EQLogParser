using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
    /// Numeric id for a string, matched exactly, for callers that keep records by value and would
    /// rather store four bytes than a reference. Ids start at 1 so that 0 keeps meaning "never
    /// assigned", which is how an unset record field goes on returning null.
    /// </summary>
    public static int GetId(string s)
    {
      if (string.IsNullOrEmpty(s))
      {
        return 0;
      }

      lock (_ids)
      {
        if (!_idByName.TryGetValue(s, out var id))
        {
          id = _namesById.Count + 1;
          _idByName[s] = id;
          _namesById.Add(s);
        }

        return id;
      }
    }

    /// <summary>
    /// The string an id from <see cref="GetId"/> stands for; null for 0 so an unset field stays
    /// unset. Ids are never reused, and clearing the text cache does not clear these tables:
    /// records stored before a session clear still carry ids that have to name themselves.
    /// </summary>
    public static string GetName(int id) => id > 0 && id <= _namesById.Count ? _namesById[id - 1] : null;

    /// <summary>
    /// Clears all cached strings. Call when clearing active data. The id tables are left alone on
    /// purpose — dropping the names would silently rename every record already stored.
    /// </summary>
    internal static void Clear() => _cache.Clear();

    private static readonly object _ids = new();
    private static readonly Dictionary<string, int> _idByName = new(StringComparer.Ordinal);
    private static readonly List<string> _namesById = [];
  }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

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
        if (_idByName.TryGetValue(s, out var known))
        {
          return known;
        }

        var id = ++_lastId;
        var page = (id - 1) / PageSize;
        var slot = (id - 1) % PageSize;
        var pages = _pages;
        if (page >= pages.Length)
        {
          // A fresh page is revealed by publishing a longer directory in a single write, after the name
          // has been stored inside it. A reader holds whichever directory it read, so it sees the name or
          // sees nothing — never a table being grown out from under it. Nothing is ever moved.
          pages = new string[page + 1][];
          Array.Copy(_pages, pages, _pages.Length);
          Volatile.Write(ref pages[page], new string[PageSize]);
        }

        Volatile.Write(ref pages[page][slot], s);
        if (!ReferenceEquals(pages, _pages))
        {
          Volatile.Write(ref _pages, pages);
        }

        _idByName[s] = id;
        return id;
      }
    }

    /// <summary>
    /// The string an id from <see cref="GetId"/> stands for; null for 0 so an unset field stays unset.
    /// </summary>
    /// <remarks>
    /// Takes no lock and copies nothing: this runs once per name per record inside the aggregation loops,
    /// on the UI thread with a parser thread handing out ids. Reads are an index into fixed-size pages, so
    /// the only thing a reader can observe changing is the directory reference, which grows by replacement
    /// (see <see cref="GetId"/>). A list being appended to here would be read half-grown instead.
    /// <para>
    /// Ids are never reused and these tables outlive <see cref="Clear"/>: records already stored carry ids,
    /// and dropping the names would silently rename them.
    /// </para>
    /// </remarks>
    public static string GetName(int id)
    {
      if (id <= 0)
      {
        return null;
      }

      var pages = Volatile.Read(ref _pages);
      var page = (id - 1) / PageSize;
      if (page >= pages.Length || Volatile.Read(ref pages[page]) is not { } names)
      {
        return null;
      }

      return Volatile.Read(ref names[(id - 1) % PageSize]);
    }

    /// <summary>
    /// Clears all cached strings. Call when clearing active data. The id tables are left alone on
    /// purpose — dropping the names would silently rename every record already stored.
    /// </summary>
    internal static void Clear() => _cache.Clear();

    // Pages rather than one growing array: a reader that resolves a name while the parser registers the
    // next thousand of them must never have to care that growth happened at all.
    private const int PageSize = 1024;

    private static readonly object _ids = new();
    private static readonly Dictionary<string, int> _idByName = new(StringComparer.Ordinal);
    private static string[][] _pages = [];
    private static int _lastId;
  }
}

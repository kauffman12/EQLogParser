using System.Collections.Frozen;

namespace EQLogParser
{
  /// <summary>
  /// Shared parsing utilities used by both DamageLineParser and SimpleDamageLineParser.
  /// Designed to be usable in a future standalone parsing project with minimal dependencies.
  /// </summary>
  internal static class ParserUtil
  {
    /// <summary>
    /// Second-person pronouns that should be replaced with the player name.
    /// </summary>
    private static readonly HashSet<string> SecondPerson = new(StringComparer.OrdinalIgnoreCase)
    {
      "you", "yourself", "your"
    };

    /// <summary>
    /// Third-person pronouns that should be replaced with the attacker name.
    /// </summary>
    private static readonly HashSet<string> ThirdPerson = new(StringComparer.OrdinalIgnoreCase)
    {
      "himself", "herself", "itself"
    };

    /// <summary>
    /// Replaces "You"/"Your" with player name, or third-person names with attacker.
    /// Extracted from PlayerRegistry.ReplacePlayer for use in standalone parsing.
    /// </summary>
    internal static string ReplacePlayer(string name, string playerName, string attacker)
    {
      if (string.IsNullOrEmpty(name))
      {
        return name;
      }

      if (ThirdPerson.Contains(name))
      {
        return !string.IsNullOrEmpty(attacker) && !IsUnattributedName(attacker)
          ? attacker
          : name;
      }

      if (SecondPerson.Contains(name))
      {
        return playerName;
      }

      return name;
    }

    /*
     * Names that are NOT entities: what the parser writes into an attacker field when the line itself names no source.
     * "Reverse DS" is a damage-TYPE word (Labels.Rs) that spent years standing where a name belongs, and it read as one -
     * 69,651 facts and 5.74 billion damage on one capture, attributed to nobody and placed by no rule while claiming the
     * spell-casting verdict R21 hands out for "No Caster in Line". Labels.Unk rides along for the other no-source shapes.
     * Every one of them is refused identity here and in R7, so the board keeps the number and drops the imaginary actor.
     */
    internal static bool IsUnattributedName(string name)
      => name == Labels.Unattributed || name == Labels.Rs || name == Labels.Unk;

    /// <summary>
    /// Updates the attacker name: handles corpse suffixes and player name replacement.
    /// </summary>
    /// <param name="attacker">The current attacker name</param>
    /// <param name="playerName">The actual player name (for replacing "You"/"Your")</param>
    /// <param name="subType">The fallback attacker name if attacker is null/empty</param>
    /// <returns>The updated attacker name in uppercase</returns>
    internal static string UpdateAttacker(string attacker, string playerName, string subType)
    {
      if (string.IsNullOrEmpty(attacker))
      {
        attacker = subType;
      }
      else if (attacker.EndsWith("'s corpse", StringComparison.Ordinal) || attacker.EndsWith("`s corpse", StringComparison.Ordinal))
      {
        attacker = attacker[..^9];
      }
      else if (!string.IsNullOrEmpty(playerName))
      {
        attacker = ReplacePlayer(attacker, playerName, attacker);
      }

      return TextUtils.CapitalizeFirst(attacker);
    }

    /// <summary>
    /// Updates the defender name: replaces "You"/"Your" with player name, or third-person with attacker.
    /// </summary>
    internal static string UpdateDefender(string defender, string attacker)
    {
      defender = ReplacePlayer(defender, ConfigUtil.PlayerName, attacker);
      return TextUtils.CapitalizeFirst(defender);
    }

    /// <summary>
    /// Finds the stop index by stripping trailing parentheses (e.g., from modifiers).
    /// </summary>
    internal static int FindStop(string[] split)
    {
      var stop = split.Length - 1;
      if (!string.IsNullOrEmpty(split[stop]) && split[stop][^1] == ')')
      {
        for (var i = stop; i >= 0 && stop > 2; i--)
        {
          if (!string.IsNullOrEmpty(split[i]) && split[i][0] == '(')
          {
            stop = i - 1;
            break;
          }
        }
      }
      return stop;
    }

    /// <summary>
    /// Joins a range of words from a split string array with a single space.
    /// Replaces the common pattern: string.Join(" ", split, start, count)
    /// Uses stackalloc for short results (≤256 chars) to reduce heap allocations.
    /// </summary>
    internal static unsafe string JoinWords(string[] split, int start, int count)
    {
      if (count <= 0) return string.Empty;
      if (count == 1)
      {
        var word = split[start];
        return word ?? string.Empty;
      }

      // Calculate total length needed
      var totalLength = 0;
      var wordCount = 0;
      for (var i = 0; i < count; i++)
      {
        var word = split[start + i];
        if (word is { Length: > 0 })
        {
          totalLength += word.Length;
          wordCount++;
        }
      }

      // Add spaces between words
      totalLength += wordCount > 1 ? wordCount - 1 : 0;

      // For short results, use stackalloc to avoid heap allocation
      if (totalLength <= 256)
      {
        var buffer = stackalloc char[totalLength];
        var pos = 0;
        var first = true;
        for (var i = 0; i < count; i++)
        {
          var word = split[start + i];
          if (word is { Length: > 0 })
          {
            if (!first)
            {
              buffer[pos++] = ' ';
            }
            foreach (var c in word)
            {
              buffer[pos++] = c;
            }
            first = false;
          }
        }
        return new string(buffer, 0, pos);
      }

      // For longer results, use string.Join
      return string.Join(" ", split, start, count);
    }

    /// <summary>
    /// Parses an unsigned integer from a string array element.
    /// Replaces the common pattern: TextUtils.ParseUInt(split[index])
    /// </summary>
    internal static uint ParseUInt(string[] split, int index)
    {
      return TextUtils.ParseUInt(split[index]);
    }

    /*
     * The one hit-type table: verb → display plural, ordinal like the == / switch it replaces. Both doors below read
     * this dictionary (the span door is what LineWords callers use without materializing a string), so the literal
     * list can never exist twice. One behavior note versus the old switch: the plural arms returned the input word
     * ITSELF; this returns an equal literal. Every consumer compares by value, so the difference is invisible.
     */
    // Frozen so the Word door can ask with a span and no materialized string.
    private static readonly FrozenDictionary<string, string> HitTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
      { "bash", "bashes" }, { "backstab", "backstabs" }, { "bite", "bites" }, { "claw", "claws" },
      { "cleave", "cleaves" }, { "crush", "crushes" }, { "frenzy", "frenzies" }, { "gore", "gores" },
      { "hit", "hits" }, { "kick", "kicks" }, { "learn", "learns" }, { "maul", "mauls" },
      { "punch", "punches" }, { "pierce", "pierces" }, { "reave", "reaves" }, { "rend", "rends" },
      { "shoot", "shoots" }, { "slash", "slashes" }, { "slam", "slams" }, { "slice", "slices" },
      { "smash", "smashes" }, { "smite", "smites" }, { "stab", "stabs" }, { "sting", "stings" },
      { "strike", "strikes" }, { "sweep", "sweeps" },
      { "bashes", "bashes" }, { "backstabs", "backstabs" }, { "bites", "bites" }, { "claws", "claws" },
      { "cleaves", "cleaves" }, { "crushes", "crushes" }, { "frenzies", "frenzies" }, { "gores", "gores" },
      { "hits", "hits" }, { "kicks", "kicks" }, { "learns", "learns" }, { "mauls", "mauls" },
      { "punches", "punches" }, { "pierces", "pierces" }, { "reaves", "reaves" }, { "rends", "rends" },
      { "shoots", "shoots" }, { "slashes", "slashes" }, { "slams", "slams" }, { "slices", "slices" },
      { "smashes", "smashes" }, { "smites", "smites" }, { "stabs", "stabs" }, { "stings", "stings" },
      { "strikes", "strikes" }, { "sweeps", "sweeps" },
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Looks up the hit type plural form for a given verb.
    /// Returns null if the word is not a known hit type.
    /// Examples: "bash" → "bashes", "crushes" → "crushes" (plural form)
    /// </summary>
    internal static string GetHitType(string word)
      => word is not null && HitTypes.TryGetValue(word, out var hitType) ? hitType : null;

    /*
     * Span door over a LineWords token. This framework's FrozenDictionary<string, V> has no span TryGetValue (measured,
     * not assumed — the compile fails), so lookups run through a tiny open-addressed table fed FROM HitTypes in the
     * static initializer: one source of literals, no materialized string per probe.
     */
    private const int HitSlotCount = 128; // power of two ≥ 52 entries with low load
    private static readonly string[] HitKeySlots = new string[HitSlotCount];
    private static readonly string[] HitPluralSlots = new string[HitSlotCount];

    static ParserUtil()
    {
      foreach (var (key, plural) in HitTypes)
      {
        var slot = SpanBucket(key.AsSpan());
        while (HitKeySlots[slot] is not null)
        {
          slot = (slot + 1) & (HitSlotCount - 1);
        }

        HitKeySlots[slot] = key;
        HitPluralSlots[slot] = plural;
      }
    }

    private static int SpanBucket(ReadOnlySpan<char> span)
    {
      // FNV-1a, ordinal over UTF-16 units — matches SequenceEqual semantics exactly.
      var hash = 2166136261u;
      for (var i = 0; i < span.Length; i++)
      {
        hash = (hash ^ span[i]) * 16777619u;
      }

      return (int)(hash & (HitSlotCount - 1));
    }

    internal static string GetHitType(LineWords.Word word)
    {
      var span = word.Span;
      if (span.IsEmpty)
      {
        return null;
      }

      var slot = SpanBucket(span);
      while (HitKeySlots[slot] is { } key)
      {
        if (span.SequenceEqual(key.AsSpan()))
        {
          return HitPluralSlots[slot];
        }

        slot = (slot + 1) & (HitSlotCount - 1);
      }

      return null;
    }

    /*
     * LineWords door for TextUtils.ParseSpellOrNpc: raw-join from index to end of line, trim the trailing '.'.
     * Same contract as the string[] version — one materialized name is what the record owns either way. Damage's
     * taunt paths and CastLineParser both read this; a third caller should not re-mirror it.
     */
    internal static string ParseSpellOrNpc(LineWords split, int index) => split.JoinRaw(index, split.Length - index).Trim('.');

    /// <summary>
    /// Checks if a word is a hit type addition (frenzy/frenzies).
    /// </summary>
    internal static bool IsHitTypeAddition(string word) =>
      word is "frenzy" or "frenzies";

    /// <summary>Span door over a LineWords token.</summary>
    internal static bool IsHitTypeAddition(LineWords.Word word) =>
      word == "frenzy" || word == "frenzies";
  }
}

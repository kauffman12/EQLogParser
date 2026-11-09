using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace EQLogParser;

internal readonly record struct SpellNameParts(string Name, string Rank);

// Remove one rank identifier, the rightmost; earlier rank-like text belongs to the name.
// docs/DesignNotes.md → "Spell names and ranks are separate questions".
internal sealed partial class SpellNameRules
{
  private static readonly FrozenSet<string> RankWords = new[]
  {
    "Azia", "Beza", "Caza", "Dena", "Ena", "Geza", "Kala", "Ozah", "Vena"
  }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

  private readonly FrozenSet<string> _baseNames;
  private readonly ConcurrentDictionary<string, SpellNameParts> _cache = new(StringComparer.Ordinal);

  internal SpellNameRules(IEnumerable<string> names)
  {
    var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in names)
    {
      var split = SplitRank(name);
      if (split.Rank.Length > 0) bases.Add(split.Name);
    }
    _baseNames = bases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
  }

  internal SpellNameParts Split(string full)
  {
    if (_cache.TryGetValue(full, out var cached)) return cached;

    // A base established by a ranked sibling must not lose its own numeral or refinement word.
    var split = _baseNames.Contains(full) ? new SpellNameParts(full, string.Empty) : SplitRank(full);
    split = new SpellNameParts(StringCache.GetOrAddExact(split.Name), StringCache.GetOrAddExact(split.Rank));
    _cache[full] = split;
    return split;
  }

  private static SpellNameParts SplitRank(string full)
  {
    var start = -1;
    var length = 0;
    var rank = string.Empty;
    foreach (Match match in ExplicitRankRegex().Matches(full))
    {
      var value = match.Groups["rank"].Value;
      if (!IsNumeral(value)) continue;
      start = match.Index;
      length = match.Length;
      rank = value;
    }

    foreach (Match token in TokenRegex().Matches(full))
    {
      // The numeral inside "Rk. III" is part of that identifier, not another candidate.
      if (token.Index < start + length) continue;
      var value = token.Value.Trim('(', ')');
      var isWord = RankWords.Contains(value);
      var isTrailingNumeral = full.AsSpan(token.Index + token.Length).Trim().IsEmpty
        && IsNumeral(token.Value);
      if (!isWord && !isTrailingNumeral) continue;
      start = token.Index;
      length = token.Length;
      rank = value;
    }

    if (start < 0) return new SpellNameParts(full, string.Empty);

    var before = full[..start].TrimEnd();
    var after = full[(start + length)..].TrimStart();
    var name = before.Length == 0 ? after : after.Length == 0 ? before : before + " " + after;
    return name.Length > 0 ? new SpellNameParts(name, rank) : new SpellNameParts(full, string.Empty);
  }

  private static bool IsNumeral(string token) => token.Length > 0
    && (token.All(c => c is >= '0' and <= '9') || RomanRegex().IsMatch(token));

  [GeneratedRegex(@"(?<!\S)\(?(?:Rk|Mk)\.?\s*(?<rank>[IVXLCDM]+|[0-9]+)\)?(?!\S)",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex ExplicitRankRegex();

  [GeneratedRegex(@"\S+")]
  private static partial Regex TokenRegex();

  [GeneratedRegex(@"^M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex RomanRegex();
}

using System.Collections.Frozen;

namespace EQLogParser;

internal enum SpellResist
{
  Undefined = -2, Reflected = -1, Unresistable = 0, Magic, Fire, Cold, Poison, Disease, Lowest, Average, Physical, Corruption
}

internal class SpellData
{
  public string Id { get; set; }
  public string Name { get; set; }
  public string NameAbbrv { get; set; }
  public ushort Duration { get; set; }
  public ushort MaxHits { get; set; }
  public bool IsBeneficial { get; set; }
  public SpellResist Resist { get; set; }
  public short Damaging { get; set; }
  public byte Target { get; set; }
  public ushort ClassMask { get; set; }
  public byte Level { get; set; }
  public bool HasAmbiguity { get; set; }
  public string LandsOnYou { get; set; }
  public string LandsOnOther { get; set; }
  public bool SongWindow { get; set; }
  public string WearOff { get; set; }
  public byte Proc { get; set; }
  public byte Adps { get; set; }
  public byte Rank { get; set; }
  public bool Charm { get; set; }
  public bool Mgb { get; set; }
  public bool SeenRecently { get; set; }
  public bool IsUnknown { get; set; }
}

internal class SpellCountData
{
  public Dictionary<string, Dictionary<string, uint>> PlayerCastCounts { get; set; } = [];
  public Dictionary<string, Dictionary<string, uint>> PlayerInterruptedCounts { get; set; } = [];
  public Dictionary<string, Dictionary<string, uint>> PlayerReceivedCounts { get; set; } = [];
  public Dictionary<string, uint> MaxCastCounts { get; set; } = [];
  public Dictionary<string, uint> MaxReceivedCounts { get; set; } = [];
  public Dictionary<string, SpellData> UniqueSpells { get; set; } = [];
  public Dictionary<string, bool> UniquePlayers { get; set; } = [];
}

/*
 * One level of a spell-word tree (LandsOnOther / LandsOnYou / WearOff messages walked from their tail word).
 *
 * Two forms, because the two halves of its life are different: `spells.txt` INSERTS words, and after that every read is
 * a probe by a slice of a log line. The insert side is an ordinary Dictionary; the read side is a FrozenDictionary, which
 * is the shape whose `AlternateLookup<ReadOnlySpan<char>>` answers without materialising the key. That is worth naming:
 * probing by `split.StringAt(i)` allocated **5,305,309 small strings — 26.9 MB of characters — per real capture**, one per
 * tree level visited, in a walk where a miss at the first level is the common case. Ordinal matching is unchanged: both
 * forms use the default (ordinal) string comparer, so "Renewal" still does not find "renewal".
 *
 * The builder dictionary is dropped on Freeze, so the capture keeps only the compact read form.
 */
internal sealed class SpellTreeNode
{
  private static readonly FrozenDictionary<string, SpellTreeNode> NoWords =
    new Dictionary<string, SpellTreeNode>().ToFrozenDictionary();

  private Dictionary<string, SpellTreeNode> _building = new(StringComparer.Ordinal);
  private FrozenDictionary<string, SpellTreeNode> _words = NoWords;

  public List<SpellData> SpellData { get; set; } = [];

  /// <summary>Insert during the data-file load. A write after <see cref="Freeze"/> is a bug, not a late addition: it would
  /// never be read, since the frozen form is what every probe uses.</summary>
  internal void AddWord(string word, SpellTreeNode child)
  {
    if (_building is null) throw new InvalidOperationException("spell tree written after Freeze()");
    _building[word] = child;
  }

  /// <summary>The insert-side lookup, tolerant of a node that is already frozen (nothing builds on top of one today).</summary>
  internal bool TryBuildingWord(string word, out SpellTreeNode child)
  {
    if (_building is not null) return _building.TryGetValue(word, out child);

    // A node already frozen answers from its read form; nothing builds on top of one today, and AddWord refuses it.
    return _words.TryGetValue(word, out child);
  }

  /// <summary>Freeze this level and everything below it. Called once per tree, after the data file is fully read.</summary>
  internal void Freeze()
  {
    if (_building is null) return;

    _words = _building.Count == 0 ? NoWords : _building.ToFrozenDictionary(StringComparer.Ordinal);
    _building = null;

    foreach (var child in _words.Values) child.Freeze();
  }

  /// <summary>The read door: a word as a slice of the line it came from, ordinal, allocating nothing on a hit.</summary>
  internal bool TryWord(ReadOnlySpan<char> word, out SpellTreeNode child)
    => _words.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(word, out child);
}

/*
 * WHAT the walk landed on and how far back it consumed. A VALUE type: this was one heap object per spell-tree search,
 * 2,336,056 of them over one capture, and every consumer takes a copy of the two fields by hand anyway (the three
 * `TryGet…` doors reassign `SpellData` to their own filtered list before handing the answer to the caller).
 */
internal struct SpellTreeResult
{
  public List<SpellData> SpellData { get; set; }
  public int DataIndex { get; set; }
}

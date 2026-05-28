using System;
using System.Collections.Generic;

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

internal class SpellTreeNode
{
  public List<SpellData> SpellData { get; set; } = [];
  public Dictionary<string, SpellTreeNode> Words { get; set; } = [];
}

internal class SpellTreeResult
{
  public List<SpellData> SpellData { get; set; }
  public int DataIndex { get; set; }
}

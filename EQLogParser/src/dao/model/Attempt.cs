using System.Collections.Generic;

namespace EQLogParser;

internal class Attempt
{
  public uint Absorbs { get; set; }
  public uint Blocks { get; set; }
  public uint Dodges { get; set; }
  public uint Misses { get; set; }
  public uint Parries { get; set; }
  public uint Invulnerable { get; set; }
  public uint Max { get; set; }
  public uint MaxPotentialHit { get; set; }
  public uint Min { get; set; }
  public uint BaneHits { get; set; }
  public uint Hits { get; set; }
  public uint AssHits { get; set; }
  public uint CritHits { get; set; }
  public uint DoubleBowHits { get; set; }
  public uint FlurryHits { get; set; }
  public uint BowHits { get; set; }
  public uint HeadHits { get; set; }
  public uint FinishingHits { get; set; }
  public uint LuckyHits { get; set; }
  public uint MeleeAttempts { get; set; }
  public uint MeleeHits { get; set; }
  public uint NonTwincastCritHits { get; set; }
  public uint NonTwincastLuckyHits { get; set; }
  public uint SpellHits { get; set; }
  public uint StrikethroughHits { get; set; }
  public uint RampageHits { get; set; }
  public uint RegularMeleeHits { get; set; }
  public uint RiposteHits { get; set; }
  public uint SlayHits { get; set; }
  public uint TwincastHits { get; set; }
  public long Total { get; set; }
  public long TotalAss { get; set; }
  public long TotalCrit { get; set; }
  public long TotalFinishing { get; set; }
  public long TotalHead { get; set; }
  public long TotalLucky { get; set; }
  public long TotalNonTwincast { get; set; }
  public long TotalNonTwincastCrit { get; set; }
  public long TotalNonTwincastLucky { get; set; }
  public long TotalRiposte { get; set; }
  public long TotalSlay { get; set; }
  public Dictionary<long, int> CritFreqValues { get; } = [];
  public Dictionary<long, int> NonCritFreqValues { get; } = [];
}

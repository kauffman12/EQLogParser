namespace EQLogParser.Mirror
{
  // Per-name roll-up inside a derived fight (Phase 1: damage/hits; Phase 2 adds by-type).
  internal sealed class NameAgg
  {
    public long Damage;
    public uint Hits;
    public string PetOwner;   // set when the name is a line-owned pet/warder ("X`s pet" -> X)
    public double BeginTime = double.NaN;
    public double UpdateTime = double.NaN;

    public void Add(uint total, bool isHit, string owner, double time)
    {
      if (isHit)
      {
        Damage += total;
        Hits++;
      }
      PetOwner ??= owner;
      UpdateTime = time;
      BeginTime = double.IsNaN(BeginTime) ? time : BeginTime;
    }
  }

  // One derived fight — the re-derivable replacement for a slice of FightManager state.
  // Aggregates are keyed by RAW name (classification enters only at roll-up, D3); the fact range
  // [FactStart, FactEnd) is contiguous in the fact table, which makes targeted re-derivation
  // (D4 choice (b)) a sequential memory pass.
  internal sealed class DerivedFight
  {
    public string Name;                 // interned boss key (exactly as the current pipeline keys fights)
    public int Id;                      // creation order, 1-based (mirrors Fight.Id ordering for reports)
    public double BeginTime = double.PositiveInfinity;
    public double LastTime = double.NegativeInfinity;
    public bool Dead;

    // Projection-only: the row exists because a charm window put this name on the enemy side
    // (charmed raider), or its facts were owned while such a window was open. Rendered as a
    // status word so "Illuminai" in the list is never mistaken for a mob.
    public bool CharmedOwned;

    // Direction split of DamageTotal (hits only): damage received by the row's owner versus
    // damage it dealt. DamageTotal stays the engagement sum.
    public long DamageToOwner;
    public long DamageByOwner;

    public double BeginDamageTime = double.NaN;
    public double LastDamageTime = double.NaN;
    public double BeginTankingTime = double.NaN;
    public double LastTankingTime = double.NaN;

    // ordinal position in the non-tanking fight list (fights appear there on their first
    // boss-directed hit, mirroring EventsNewNonTankingFight); -1 = never had a hit
    public int NonTankingOrder = -1;

    public long DamageTotal;
    public uint DamageHits;
    public long TankTotal;
    public uint TankHits;
    public bool HasDamageActions;       // mirrors "DamageBlocks.Count > 0" (any boss-directed record)
    public bool HasTankingActions;      // any tank-directed record

    public int FactStart = -1;
    public int FactEnd;                 // exclusive

    // roll-ups keyed by raw attacker name (player-side only, matching Fight.PlayerDamageTotals)
    public Dictionary<string, NameAgg> PlayerRollup { get; } = [];
    // keyed by raw defender name (matching Fight.PlayerTankTotals)
    public Dictionary<string, NameAgg> TankRollup { get; } = [];

    public double EndTime => LastTime;

    public string BeginTimeString => DateUtil.FormatDotNetDateSeconds(BeginTime);
  }
}

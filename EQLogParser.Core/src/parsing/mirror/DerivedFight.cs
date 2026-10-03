namespace EQLogParser.Mirror
{
  /*
   * How a row stopped being an engagement. `Dead` stays the flag every consumer already reads — the list
   * styles it, the overlay counts encounters — and this says WHY, because two of these four are kills and
   * one of them is not:
   *
   *   Slain    the name died to the raid.
   *   Charmed  "<name> has been charmed." The raid stopped fighting this mob by making it theirs, which is
   *            counted as its death (the encounter with it is over, and the alternative — a row that runs on
   *            until an inactivity gap swallows it — hides what happened). If the charmed mob then dies, that
   *            second death belongs to OUR side of the ledger: it dies like a party member does, and the
   *            projection skips it so one corpse cannot hand the raid two kills with the same name.
   */
  internal enum DerivedFightEnd : byte
  {
    Open = 0,      // still open when the list was built (live capture), or the log ran out
    Gap = 1,       // inactivity past FightProjection.EngagementGapS
    Slain = 2,     // the name died to the raid
    Charmed = 3,   // charmed away: counted as this NPC's death, see above
  }

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

    // "Fight N" section this row belongs to, stamped by Sectionizer.StampGroupIds over the whole list.
    // It is the number a stats run reads as Fight.GroupId (legacy assigns it in FightTable as fights are
    // processed), so a derived selection that spans an inactivity gap groups into the same columns the
    // legacy list would have used. Stays 1 for a list nobody has sectioned.
    public int GroupId = 1;
    public double BeginTime = double.PositiveInfinity;
    public double LastTime = double.NegativeInfinity;
    public bool Dead;

    // Why the row ended. Only meaningful next to a row that ended: an open (live) row reports Open.
    public DerivedFightEnd EndReason;

    // Projection-only: the row exists because a charm window put this name on the enemy side
    // (charmed raider), or its facts were owned while such a window was open. Rendered as a
    // status word so "Illuminai" in the list is never mistaken for a mob.
    public bool CharmedOwned;

    /*
     * Projection-only: this row is OUR PET — the name's identity is an NPC and a charm window had it on our side,
     * so what the row holds is a pet's output (and the raid's stray swings on it). Pets get no fight-list row
     * (CharmPetRows), unlike a charmed raid member, who is an encounter the raid has to fight: her row carries
     * `CharmedOwned` without `RaidPet`, stays in the list and reads "charmed" in the status column.
     * See CharmRowProjectionTest.
     */
    public bool RaidPet;

    // The listed encounter this pet row continues: the same name's row that the charm itself closed. Null when the
    // raid never fought the mob before taking it, and the entry point CharmPetRows uses to hand a hidden row back
    // to a stats build (its span starts AFTER this row's ends, so overlap alone can never reach it).
    public DerivedFight EncounterRow;


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

    /*
     * How long this row covers, in the unit the rest of the product counts seconds in: INCLUSIVE, so a fight whose
     * first and last fact share one timestamp lived ONE second and not zero. That convention is not ours to drop - it
     * is TimeSegment.Total (`EndTime - BeginTime + 1`), which is the denominator every DPS number on the damage board
     * is divided by, and it is what FightManager writes into the legacy tooltip (`var ttl = LastTime - BeginTime + 1`)
     * and what FightSummarySource reproduces from it. Print this span exclusive and the grid contradicts the board
     * one click away: a name hit once at 18:56:18 would read "00:00" here while its summary says "Time Alive: 1s" and
     * divides its damage by one second - and a duration of 0 is a DPS of infinity.
     *
     * Zero only when the row has no span to speak of (an unset bound), because TimeSpan.FromSeconds would rather
     * throw than format a NaN.
     */
    public double DurationSeconds
      => double.IsFinite(BeginTime) && double.IsFinite(LastTime) ? Math.Max(1d, LastTime - BeginTime + 1d) : 0d;

    public string BeginTimeString => DateUtil.FormatDotNetDateSeconds(BeginTime);
  }
}

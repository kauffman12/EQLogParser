using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EQLogParser
{
  public class DamageRecord
  {
    public long Damage { get; set; }
    public string Attacker { get; set; }
    public string AttackerPetType { get; set; }
    public string AttackerOwner { get; set; }
    public string Defender { get; set; }
    public string DefenderPetType { get; set; }
    public string DefenderOwner { get; set; }
    public string Type { get; set; }
    public string Action { get; set; }
    public Dictionary<string, byte> Modifiers { get; set; }
  }

  public class Hit
  {
    public long Max { get; set; }
    public long Count { get; set; }
    public long CritCount { get; set; }
    public long TotalDamage { get; set; }
    public List<long> Values { get; set; }
  }

  public class DamageStats
  {
    public Dictionary<string, Hit> HitMap { get; set; }
    public long TotalDamage { get; set; }
    public long Count { get; set; }
    public long Max { get; set; }
    public long CritCount { get; set; }
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public string Owner { get; set; }
    public bool IsPet { get; set; }
  }

  public class ProcessLine
  {
    public string Line { get; set; }
    public int State { get; set; }
    public DateTime CurrentTime { get; set; }
    public string TimeString { get; set; }
    public string ActionPart { get; set; }
    public int OptionalIndex { get; set; }
  }

  public class Player
  {
    public string Name { get; set; }
  }

  public class NonPlayer
  {
    public const string BREAK_TIME = "Break Time";
    public string BeginTimeString { get; set; }
    public string Name { get; set; }
    public Dictionary<string, DamageStats> DamageMap { get; set; }
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public long ID { get; set; }
    public string CorrectMapKey {get; set;}
    public int FightID { get; set; }
  }

  public class PetMapping
  {
    public string Owner { get; set; }
    public string Pet { get; set; }
  }

  public class CombinedStats
  {
    public string TargetTitle { get; set; }
    public string DamageTitle { get; set; }
    public double TimeDiff { get; set; }
    public List<PlayerStats> StatsList { get; set; }
    public Dictionary<string, List<PlayerStats>> Children { get; set; }
    public Dictionary<string, List<PlayerSubStats>> SubStats { get; set; }
    public PlayerStats RaidStats { get; set; }
    public SortedSet<long> NpcIDs { get; set; }
  }

  public class PlayerSubStats
  {
    public int Rank { get; set; }
    public string Name { get; set; }
    public long Damage { get; set; }
    public double TotalSeconds { get; set; }
    public long DPS { get; set; }
    public long Hits { get; set; }
    public string HitType { get; set; }
    public long Max { get; set; }
    public long Avg { get; set; }
    public long CritHits { get; set; }
    public decimal CritRate { get; set; }
    public string Details { get; set; }
  }

  public class PlayerStats : PlayerSubStats
  {
    public Dictionary<string, PlayerSubStats> SubStats { get; set; }
    public Dictionary<int, DateTime> BeginTimes { get; set; }
    public Dictionary<int, DateTime> LastTimes { get; set; }
    public Dictionary<int, double> TimeDiffs { get; set; }
  }
}

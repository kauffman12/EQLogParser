using System;
using System.Collections.Generic;

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
    public int Count { get; set; }
    public int CritCount { get; set; }
    public int DoubleBowShotCount { get; set; }
    public int FlurryCount { get; set; }
    public int LuckyCount { get; set; }
    public int TwincastCount { get; set; }
    public int RampageCount { get; set; }
    public int SlayUndeadCount { get; set; }
    public int WildRampageCount { get; set; }
    public long TotalDamage { get; set; }
    public long TotalCritDamage { get; set; }
    public long TotalLuckyDamage { get; set; }
  }

  public class DamageStats : Hit
  {
    public Dictionary<string, Hit> HitMap { get; set; }
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public string Owner { get; set; }
    public bool IsPet { get; set; }
  }

  public class DamageProcessedEvent
  {
    public ProcessLine ProcessLine { get; set; }
    public DamageRecord Record { get; set; }
  }

  public class ProcessLine
  {
    public string Line { get; set; }
    public DateTime CurrentTime { get; set; }
    public string TimeString { get; set; }
    public string ActionPart { get; set; }
    public int OptionalIndex { get; set; }
    public string OptionalData { get; set; }
  }

  public class Player
  {
    public string Name { get; set; }
  }

  public class DamageAtTime
  {
    public DateTime CurrentTime { get; set; }
    public Dictionary<string, long> PlayerDamage { get; set; }
    public int FightID { get; set; }
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

  public class SortableName
  {
    public string Name { get; set; }
  }

  public class SpellCast : ReceivedSpell
  {
    public string Spell { get; set; }
    public string Caster { get; set; }
  }

  public class ReceivedSpell
  {
    public string SpellAbbrv { get; set; }
    public string Receiver { get; set; }
    public DateTime BeginTime { get; set; }
  }

  public class SpellData
  {
    public string ID { get; set; }
    public string Spell { get; set; }
    public string SpellAbbrv { get; set; }
    public bool Beneficial { get; set; }
    public int ClassMask { get; set; }
    public string LandsOnYou { get; set; }
    public string LandsOnOther { get; set; }
  }

  public class SpellCounts
  {
    public List<string> SpellList { get; set; }
    public Dictionary<string, int> TotalCountMap { get; set; }
    public Dictionary<string, Dictionary<string, int>> PlayerCountMap { get; set; }
    public Dictionary<string, int> UniqueSpellCounts { get; set; }
    public List<string> SortedPlayers { get; set; }
  }

  public class SpellCountRow
  {
    public string Spell { get; set; }
    public int[] Values { get; set; }
    public bool IsReceived { get; set; }
  }

  public class CombinedStats
  {
    public string TargetTitle { get; set; }
    public string TimeTitle { get; set; }
    public string DamageTitle { get; set; }
    public double TimeDiff { get; set; }
    public List<PlayerStats> StatsList { get; set; }
    public Dictionary<string, List<PlayerStats>> Children { get; set; }
    public Dictionary<string, List<PlayerSubStats>> SubStats { get; set; }
    public PlayerStats RaidStats { get; set; }
    public SortedSet<long> NpcIDs { get; set; }
    public Dictionary<string, byte> UniqueClasses { get; set; }
  }

  public class DPSChartData
  {
    public Dictionary<string, List<long>> Values { get; set; }
    public List<string> XAxisLabels { get; set; }
  }

  public class DPSSnapshotEvent
  {
    public long DPS { get; set; }
    public string Name { get; set; }
    public DateTime CurrentTime { get; set; }
  }

  public class StatsSummary
  {
    public string Title { get; set; }
    public string RankedPlayers { get; set; }
  }

  public class PlayerSubStats
  {
    public int Rank { get; set; }
    public string Name { get; set; }
    public long TotalDamage { get; set; }
    public long TotalCritDamage { get; set; }
    public long TotalLuckyDamage { get; set; }
    public double TotalSeconds { get; set; }
    public long DPS { get; set; }
    public int Hits { get; set; }
    public string HitType { get; set; }
    public long Max { get; set; }
    public long Avg { get; set; }
    public long AvgCrit { get; set; }
    public long AvgLucky { get; set; }
    public int CritHits { get; set; }
    public int LuckyHits { get; set; }
    public int TwincastHits { get; set; }
    public decimal CritRate { get; set; }
    public decimal LuckRate { get; set; }
    public decimal TwincastRate { get; set; }
    public decimal Percent { get; set; }
    public string PercentString { get; set; }
    public string ClassName { get; set; }
  }

  public class PlayerStats : PlayerSubStats
  {
    public Dictionary<string, PlayerSubStats> SubStats { get; set; }
    public Dictionary<int, DateTime> BeginTimes { get; set; }
    public Dictionary<int, DateTime> LastTimes { get; set; }
    public Dictionary<int, double> TimeDiffs { get; set; }
    public int FirstFightID { get; set; }
    public int LastFightID { get; set; }
  }
}

using System;
using System.Collections.Generic;

namespace EQLogParser
{
  public class SortableNameComparer : IComparer<SortableName>
  {
    public int Compare(SortableName x, SortableName y)
    {
      return x.Name.CompareTo(y.Name);
    }
  }

  public static class Labels
  {
    public const string DD_TYPE = "Direct Damage";
    public const string DOT_TYPE = "DoT Tick";
    public const string DS_TYPE = "Damage Shield";
    public const string BANE_TYPE = "Bane Damage";
    public const string PROC_TYPE = "Proc";
    public const string RESIST_TYPE = "Resisted Spells";
  }

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
    public string Modifiers { get; set; }
    public string Spell { get; set; }
  }

  public class Hit
  {
    public long Max { get; set; }
    public int BaneCount { get; set; }
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
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public Dictionary<long, int> CritFreqValues { get; set; }
    public Dictionary<long, int> NonCritFreqValues { get; set; }
  }

  public class DamageStats : Hit
  {
    public Dictionary<string, Hit> HitMap { get; set; }
    public Dictionary<string, Hit> SpellDoTMap { get; set; }
    public Dictionary<string, Hit> SpellDDMap { get; set; }
    public Dictionary<string, Hit> SpellProcMap { get; set; }
    public string Owner { get; set; }
    public bool IsPet { get; set; }
  }

  public class DamageProcessedEvent
  {
    public ProcessLine ProcessLine { get; set; }
    public DamageRecord Record { get; set; }
  }

  public class ResistProcessedEvent
  {
    public ProcessLine ProcessLine { get; set; }
    public string Defender { get; set; }
    public string Spell { get; set; }
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

  public class DamageAtTime
  {
    public DateTime CurrentTime { get; set; }
    public Dictionary<string, long> PlayerDamage { get; set; }
    public int GroupID { get; set; }
  }

  public class NonPlayer
  {
    public const string BREAK_TIME = "Break Time";
    public string BeginTimeString { get; set; }
    public string Name { get; set; }
    public Dictionary<string, DamageStats> DamageMap { get; set; }
    public Dictionary<string, Dictionary<string, int>> ResistMap { get; set; }
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public int ID { get; set; }
    public string CorrectMapKey {get; set;}
    public int GroupID { get; set; }
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

  public class PlayerAction
  {
    public DateTime BeginTime { get; set; }
  }

  public class PlayerDeath : PlayerAction
  {
    public string Player { get; set; }
    public string Npc { get; set; }
  }

  public class ReceivedSpell : PlayerAction
  {
    public string Receiver { get; set; }
    public SpellData SpellData { get; set; }
  }

  public class SpellCast : ReceivedSpell
  {
    public string Spell { get; set; }
    public string Caster { get; set; }
    public string SpellAbbrv { get; set; }
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
    public bool Damaging { get; set; }
    public bool IsProc { get; set; }
  }

  public class SpellCountData
  {
    public Dictionary<string, Dictionary<string, int>> PlayerCastCounts { get; set; }
    public Dictionary<string, Dictionary<string, int>> PlayerReceivedCounts { get; set; }
    public Dictionary<string, int> MaxCastCounts { get; set; }
    public Dictionary<string, int> MaxReceivedCounts { get; set; }
    public Dictionary<string, SpellData> UniqueSpells { get; set; }
  }

  public class SpellCountRow
  {
    public string Spell { get; set; }
    public double[] Values { get; set; }
    public bool IsReceived { get; set; }
  }

  public class CombinedStats
  {
    public string TargetTitle { get; set; }
    public string TimeTitle { get; set; }
    public string DamageTitle { get; set; }
    public List<PlayerStats> StatsList { get; set; }
    public Dictionary<string, List<PlayerStats>> Children { get; set; }
    public PlayerStats RaidStats { get; set; }
    public Dictionary<string, byte> UniqueClasses { get; set; }
  }

  public class ChartData
  {
    public Dictionary<string, List<long>> Values { get; set; }
    public List<string> XAxisLabels { get; set; }
  }

  public class HitFreqChartData
  {
    public string HitType { get; set; }
    public List<int> CritYValues { get; set; }
    public List<long> CritXValues { get; set; }
    public List<int> NonCritYValues { get; set; }
    public List<long> NonCritXValues { get; set; }
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
    public string ShortTitle { get; set; }
    public string RankedPlayers { get; set; }
  }

  public class PlayerSubStats
  {
    public int Rank { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public long TotalDamage { get; set; }
    public long TotalCritDamage { get; set; }
    public long TotalLuckyDamage { get; set; }
    public double TotalSeconds { get; set; }
    public long DPS { get; set; }
    public long SDPS { get; set; }
    public int BaneHits { get; set; }
    public int Hits { get; set; }
    public int Resists { get; set; }
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
    public decimal ResistRate { get; set; }
    public decimal Percent { get; set; }
    public decimal PercentOfRaid { get; set; }
    public int Deaths { get; set; }
    public string ClassName { get; set; }
    public List<DateTime> BeginTimes { get; set; }
    public List<DateTime> LastTimes { get; set; }
    public List<double> TimeDiffs { get; set; }
    public Dictionary<long, int> CritFreqValues { get; set; }
    public Dictionary<long, int> NonCritFreqValues { get; set; }
  }

  public class PlayerStats : PlayerSubStats
  {
    public Dictionary<string, PlayerSubStats> SubStats { get; set; }
    public string OrigName { get; set; }
  }
}

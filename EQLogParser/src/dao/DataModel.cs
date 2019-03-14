using System;
using System.Collections.Generic;

namespace EQLogParser
{
  public class DataPointComparer : IComparer<DataPoint>
  {
    public int Compare(DataPoint x, DataPoint y)
    {
      return x.CurrentTime.CompareTo(y.CurrentTime);
    }
  }

  public class TimedActionComparer : IComparer<TimedAction>
  {
    public int Compare(TimedAction x, TimedAction y)
    {
      return x.BeginTime.CompareTo(y.BeginTime);
    }
  }

  public class SortableNameComparer : IComparer<SortableName>
  {
    public int Compare(SortableName x, SortableName y)
    {
      return x.Name.CompareTo(y.Name);
    }
  }

  public static class Labels
  {
    public const string DD_NAME = "Direct Damage";
    public const string DOT_NAME = "DoT Tick";
    public const string DS_NAME = "Damage Shield";
    public const string BANE_NAME = "Bane Damage";
    public const string PROC_NAME = "Proc";
    public const string RESIST_NAME = "Resisted Spells";
    public const string HOT_NAME = "HoT Tick";
    public const string HEAL_NAME = "Heal";
  }

  public class DataPointEvent
  {
    public DataPoint Data { get; set; }
    public string EventType { get; set; }
    public Dictionary<string, byte> NpcNames { get; set; }
  }

  public class DamageProcessedEvent
  {
    public DamageRecord Record { get; set; }
    public string TimeString { get; set; }
  }

  public class HealProcessedEvent
  {
    public HealRecord Record { get; set; }
  }

  public class ResistProcessedEvent
  {
    public ResistRecord Record { get; set; }
  }

  public class ProcessLine
  {
    public string Line { get; set; }
    public double CurrentTime { get; set; }
    public string TimeString { get; set; }
    public string ActionPart { get; set; }
    public int OptionalIndex { get; set; }
    public string OptionalData { get; set; }
  }

  public class TimedAction
  {
    public double BeginTime { get; set; }
  }

  public class FullTimedAction : TimedAction
  {
    public double LastTime { get; set; }
  }

  public class ResistRecord : TimedAction
  {
    public string Spell { get; set; }
  }

  public class HitRecord : TimedAction
  {
    public long Total { get; set; }
    public long OverTotal { get; set; }
    public string Type { get; set; }
    public string SubType { get; set; }
    public string Modifiers { get; set; }
  }

  public class HealRecord : HitRecord
  {
    public string Healer { get; set; }
    public string Healed { get; set; }
  }

  public class DamageRecord : HitRecord
  {
    public string Attacker { get; set; }
    public bool IsAttackerPet { get; set; }
    public string AttackerOwner { get; set; }
    public string Defender { get; set; }
    public bool IsDefenderPet { get; set; }
    public string DefenderOwner { get; set; }
  }

  public class Hit : FullTimedAction
  {
    public long Max { get; set; }
    public int BaneHits{ get; set; }
    public int Hits { get; set; }
    public int CritHits { get; set; }
    public int LuckyHits { get; set; }
    public int TwincastHits { get; set; }
    public long Total { get; set; }
    public long TotalCrit { get; set; }
    public long TotalLucky { get; set; }
    public Dictionary<long, int> CritFreqValues { get; set; }
    public Dictionary<long, int> NonCritFreqValues { get; set; }
  }

  public class NonPlayer : FullTimedAction
  {
    public const string BREAK_TIME = "Break Time";
    public string BeginTimeString { get; set; }
    public string Name { get; set; }
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

  public class PlayerDeath : TimedAction
  {
    public string Player { get; set; }
    public string Npc { get; set; }
  }

  public class ReceivedSpell : TimedAction
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
    public string TotalTitle { get; set; }
    public List<PlayerStats> StatsList { get; set; }
    public PlayerStats RaidStats { get; set; }
  }

  public class CombinedDamageStats : CombinedStats
  {
    public Dictionary<string, List<PlayerStats>> Children { get; set; }
    public Dictionary<string, byte> UniqueClasses { get; set; }
  }

  public class CombinedHealStats : CombinedStats
  {
    public Dictionary<string, byte> UniqueClasses { get; set; }
  }

  public class DataPoint
  {
    public string Name { get; set; }
    public long Total { get; set; }
    public long Rolling { get; set; }
    public long VPS { get; set; }
    public double BeginTime { get; set; }
    public double CurrentTime { get; set; }
  }

  public class HitFreqChartData
  {
    public string HitType { get; set; }
    public List<int> CritYValues { get; set; }
    public List<long> CritXValues { get; set; }
    public List<int> NonCritYValues { get; set; }
    public List<long> NonCritXValues { get; set; }
  }

  public class StatsSummary
  {
    public string Title { get; set; }
    public string ShortTitle { get; set; }
    public string RankedPlayers { get; set; }
  }

  public class PlayerSubStats : Hit
  {
    public int Rank { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public double TotalSeconds { get; set; }
    public long DPS { get; set; }
    public long SDPS { get; set; }
    public long Extra { get; set; }
    public int Resists { get; set; }
    public long Avg { get; set; }
    public long AvgCrit { get; set; }
    public long AvgLucky { get; set; }
    public decimal CritRate { get; set; }
    public decimal ExtraRate { get; set; }
    public decimal LuckRate { get; set; }
    public decimal TwincastRate { get; set; }
    public decimal ResistRate { get; set; }
    public decimal Percent { get; set; }
    public decimal PercentOfRaid { get; set; }
    public int Deaths { get; set; }
    public string ClassName { get; set; }
    public List<double> BeginTimes { get; set; }
    public List<double> LastTimes { get; set; }
    public List<double> TimeDiffs { get; set; }
  }

  public class PlayerStats : PlayerSubStats
  {
    public Dictionary<string, PlayerSubStats> SubStats { get; set; }
    public Dictionary<string, PlayerSubStats> SubStats2 { get; set; }
    public string OrigName { get; set; }
  }
}

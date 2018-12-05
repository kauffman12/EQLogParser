using System;
using System.Collections.Generic;

namespace EQLogParser
{
  public class DataTypes
  {
    public static string[] DAMAGE_LIST = {
      "bash", "bite", "backstab", "claw", "crush", "frenzies on", "frenzy on", "gore", "hit",
      "kick", "maul", "punch", "pierce", "rend", "shoot", "slash", "slam", "slice", "smash", "sting", "strike"
    };
  }

  public class DamageRecord
  {
    public string Attacker { get; set; }
    public long Damage { get; set; }
    public string Defender { get; set; }
    public bool IsPet { get; set; }
    public string Type { get; set; }
  }

  public class DamageStats
  {
    public long Damage { get; set; }
    public long Hits { get; set; }
    public long Max { get; set; }
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public bool PetIncluded { get; set; }
  }

  public class ProcessLine
  {
    public string Line { get; set; }
    public int State { get; set; }
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
  }

  public class PetMapping
  {
    public string Owner { get; set; }
    public string Pet { get; set; }
  }

  public class CombinedStats
  {
    public string Title { get; set; }
    public double TimeDiff { get; set; }
    public List<PlayerStats> StatsList { get; set; }
    public PlayerStats RaidStats { get; set; }
  }

  public class PlayerStats
  {
    public int Rank { get; set; }
    public long Damage { get; set; }
    public long DPS { get; set; }
    public long Hits { get; set; }
    public long Max { get; set; }
    public long Avg { get; set; }
    public string Details { get; set; }
    public string Name { get; set; }
    public DateTime BeginTime { get; set; }
    public DateTime LastTime { get; set; }
    public double TimeDiff { get; set; }
  }
}

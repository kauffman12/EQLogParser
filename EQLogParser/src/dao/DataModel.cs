using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace EQLogParser
{
  public static class Labels
  {
    public const string DD = "Direct Damage";
    public const string DOT = "DoT Tick";
    public const string DS = "Damage Shield";
    public const string BANE = "Bane Damage";
    public const string PROC = "Proc";
    public const string RESIST = "Resisted Spells";
    public const string HOT = "HoT Tick";
    public const string HEAL = "Direct Heal";
    public const string MELEE = "Melee";
    public const string SELFHEAL = "Melee Heal";
    public const string NODATA = "No Data Available";
    public const string UNASSIGNED = "Unk Pet Owner";
    public const string UNKSPELL = "Unk Spell";
    public const string UNKPLAYER = "Unk Player";
    public const string RAID = "Totals";
    public const string RIPOSTE = "Riposte";
    public const string RECEIVEDHEALPARSE = "Received Healing";
    public const string HEALPARSE = "Healing";
    public const string TANKPARSE = "Tanking";
    public const string TOPHEALSPARSE = "Top Heals";
    public const string DAMAGEPARSE = "Damage";
    public const string MISS = "Miss";
  }

  public static class ChatChannels
  {
    public const string AUCTION = "Auction";
    public const string SAY = "Say";
    public const string GUILD = "Guild";
    public const string FELLOWSHIP = "Fellowship";
    public const string TELL = "Tell";
    public const string SHOUT = "Shout";
    public const string GROUP = "Group";
    public const string RAID = "Raid";
    public const string OOC = "OOC";
  }

  public static class LineParsing
  {
    public const int ACTIONINDEX = 27;
  }

  public static class TableColors
  {
    public const string EMPTYICON = "#00ffffff";
    public const string ACTIVEICON = "#5191c1";
  }

  public interface ISummaryBuilder
  {
    StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers, bool showSpecial);
  }

  internal interface IAction { }

  public class ColorItem
  {
    public SolidColorBrush Brush { get; set; }
    public string Name { get; set; }
  }

  public class AutoCompleteText
  {
    public string Text { get; set; }
    public List<string> Items { get; } = new List<string>();
  }

  public class ChannelDetails
  {
    public string Text { get; set; }
    public string SelectedText { get; set; }
    public bool IsChecked { get; set; }
  }

  public class ChatType
  {
    public string Channel { get; set; }
    public string Sender { get; set; }
    public string Receiver { get; set; }
    public bool SenderIsYou { get; set; }
    public string Line { get; set; }
    public int AfterSenderIndex { get; set; }
    public int KeywordStart { get; set; }
  }

  public class ParseData
  {
    public CombinedStats CombinedStats { get; set; }
    public ISummaryBuilder Builder { get; set; }
    public List<PlayerStats> Selected { get; } = new List<PlayerStats>();
  }

  public class TimedAction : IAction
  {
    public double BeginTime { get; set; }
  }

  public class FullTimedAction : TimedAction
  {
    public double LastTime { get; set; }
  }

  public class PlayerStatsSelectionChangedEventArgs : EventArgs
  {
    public List<PlayerStats> Selected { get; } = new List<PlayerStats>();
    public CombinedStats CurrentStats { get; set; }
  }

  public class DamageProcessedEvent
  {
    public DamageRecord Record { get; set; }
    public string OrigTimeString { get; set; }
    public double BeginTime { get; set; }
  }

  public class DataPointEvent
  {
    public string Action { get; set; }
    public RecordGroupCollection Iterator { get; set; }
    public List<PlayerStats> Selected { get; } = new List<PlayerStats>();
    public Predicate<object> Filter { get; set; }
  }

  public class GenerateStatsOptions
  {
    public string Name { get; set; }
    public List<Fight> Npcs { get; } = new List<Fight>();
    public bool RequestChartData { get; set; }
    public bool RequestSummaryData { get; set; }
  }

  public class StatsGenerationEvent
  {
    public string Type { get; set; }
    public string State { get; set; }
    public CombinedStats CombinedStats { get; set; }
    public List<List<ActionBlock>> Groups { get; } = new List<List<ActionBlock>>();
    public int UniqueGroupCount { get; set; }
  }

  public class ChatLine : TimedAction
  {
    public string Line { get; set; }
  }

  public class ProcessLine
  {
    public string Line { get; set; }
    public double CurrentTime { get; set; }
    public string TimeString { get; set; }
    public string ActionPart { get; set; }
    public int OptionalIndex { get; set; }
  }

  public class ResistRecord : IAction
  {
    public string Spell { get; set; }
  }

  public class HitRecord : IAction
  {
    public uint Total { get; set; }
    public uint OverTotal { get; set; }
    public string Type { get; set; }
    public string SubType { get; set; }
    public int ModifiersMask { get; set; }
  }

  public class HealRecord : HitRecord
  {
    public string Healer { get; set; }
    public string Healed { get; set; }
  }

  public class DamageRecord : HitRecord
  {
    public string Attacker { get; set; }
    public string AttackerOwner { get; set; }
    public string Defender { get; set; }
    public string DefenderOwner { get; set; }
  }

  public class LootRecord : IAction
  {
    public string Item { get; set; }
    public uint Quantity { get; set; }
    public string Player { get; set; }
    public string Npc { get; set; }
    public bool IsCurrency { get; set; }
  }

  public class DeathRecord : IAction
  {
    public string Killed { get; set; }
    public string Killer { get; set; }
  }

  public class MezBreakRecord : IAction
  {
    public string Breaker { get; set; }
    public string Awakened { get; set; }
  }

  public class ZoneRecord : IAction
  {
    public string Zone { get; set; }
  }

  public class LineData
  {
    public string Line { get; set; }
    public string Action { get; set; }
  }

  internal class HitLogRow : HitRecord
  {
    public string Actor { get; set; }
    public string Acted { get; set; }
    public uint Count { get; set; }
    public uint CritCount { get; set; }
    public uint LuckyCount { get; set; }
    public uint TwincastCount { get; set; }
    public double Time { get; set; }
    public bool IsPet { get; set; }
    public bool IsGroupingEnabled { get; set; }
    public string TimeSince { get; set; }
  }

  internal class EventRow
  {
    public double Time { get; set; }
    public string Actor { get; set; }
    public string Target { get; set; }
    public string Event { get; set; }
  }

  internal class LootRow : LootRecord
  {
    public double Time { get; set; }
  }

  public class ActionBlock : TimedAction
  {
    internal List<IAction> Actions { get; } = new List<IAction>();
  }

  public class Attempt
  {
    public uint Max { get; set; }
    public uint BaneHits { get; set; }
    public uint Hits { get; set; }
    public uint Misses { get; set; }
    public uint CritHits { get; set; }
    public uint LuckyHits { get; set; }
    public uint MeleeAttempts { get; set; }
    public uint MeleeHits { get; set; }
    public uint StrikethroughHits { get; set; }
    public uint RiposteHits { get; set; }
    public uint RampageHits { get; set; }
    public uint TwincastHits { get; set; }
    public long Total { get; set; }
    public long TotalCrit { get; set; }
    public long TotalLucky { get; set; }
    public double LastTime { get; set; }
    public Dictionary<long, int> CritFreqValues { get; } = new Dictionary<long, int>();
    public Dictionary<long, int> NonCritFreqValues { get; } = new Dictionary<long, int>();
  }

  public class Fight : FullTimedAction
  {
    public const string BREAKTIME = "Break Time";
    public string BeginTimeString { get; set; }
    public string Name { get; set; }
    public int Id { get; set; }
    public string CorrectMapKey { get; set; }
    public int GroupId { get; set; }
    public long Total { get; set; }
    public List<ActionBlock> DamageBlocks { get; } = new List<ActionBlock>();
    public Dictionary<string, TimeSegment> DamageSegments { get; } = new Dictionary<string, TimeSegment>();
    public Dictionary<string, Dictionary<string, TimeSegment>> DamageSubSegments { get; } = new Dictionary<string, Dictionary<string, TimeSegment>>();
    public Dictionary<string, TimeSegment> InitialTankSegments { get; } = new Dictionary<string, TimeSegment>();
    public Dictionary<string, Dictionary<string, TimeSegment>> InitialTankSubSegments { get; } = new Dictionary<string, Dictionary<string, TimeSegment>>();
    public List<ActionBlock> TankingBlocks { get; } = new List<ActionBlock>();
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

  public class SpecialSpell : TimedAction
  {
    public string Code { get; set; }
    public string Player { get; set; }
  }

  public class ReceivedSpell : IAction
  {
    public string Receiver { get; set; }
    public SpellData SpellData { get; set; }

    public List<SpellData> Ambiguity { get; set; }
  }

  public class SpellCast : ReceivedSpell
  {
    public string Spell { get; set; }
    public string Caster { get; set; }
  }

  public class SpellData
  {
    public string ID { get; set; }
    public string Name { get; set; }
    public string NameAbbrv { get; set; }
    public ushort Duration { get; set; }
    public ushort MaxHits { get; set; }
    public bool IsBeneficial { get; set; }
    public byte Target { get; set; }
    public ushort ClassMask { get; set; }
    public ushort Level { get; set; }
    public string LandsOnYou { get; set; }
    public string LandsOnOther { get; set; }
    public bool Damaging { get; set; }
    public bool IsProc { get; set; }
    public ushort Adps { get; set; }
  }

  public class SpellCountData
  {
    public Dictionary<string, Dictionary<string, uint>> PlayerCastCounts { get; } = new Dictionary<string, Dictionary<string, uint>>();
    public Dictionary<string, Dictionary<string, uint>> PlayerReceivedCounts { get; } = new Dictionary<string, Dictionary<string, uint>>();
    public Dictionary<string, uint> MaxCastCounts { get; } = new Dictionary<string, uint>();
    public Dictionary<string, uint> MaxReceivedCounts { get; } = new Dictionary<string, uint>();
    public Dictionary<string, SpellData> UniqueSpells { get; } = new Dictionary<string, SpellData>();
  }

  public class SpellCountRow
  {
    public string Spell { get; set; }
    public List<double> Values { get; } = new List<double>();
    public bool IsReceived { get; set; }
    public string IconColor { get; set; }
  }

  public class SpellCountsSerialized
  {
    public List<string> PlayerNames { get; } = new List<string>();
    public SpellCountData TheSpellData { get; set; }
  }

  public class CombinedStats
  {
    public string TargetTitle { get; set; }
    public string TimeTitle { get; set; }
    public string TotalTitle { get; set; }
    public string FullTitle { get; set; }
    public string ShortTitle { get; set; }
    public List<PlayerStats> StatsList { get; } = new List<PlayerStats>();
    public List<PlayerStats> ExpandedStatsList { get; } = new List<PlayerStats>();
    public PlayerStats RaidStats { get; set; }
    public Dictionary<string, byte> UniqueClasses { get; } = new Dictionary<string, byte>();
    public Dictionary<string, List<PlayerStats>> Children { get; } = new Dictionary<string, List<PlayerStats>>();
  }

  public class OverlayDamageStats : CombinedStats
  {
    public double BeginTime { get; set; }
    public double LastTime { get; set; }
    public Dictionary<string, PlayerStats> TopLevelStats { get; } = new Dictionary<string, PlayerStats>();
    public Dictionary<string, PlayerStats> AggregateStats { get; } = new Dictionary<string, PlayerStats>();
    public Dictionary<string, PlayerStats> IndividualStats { get; } = new Dictionary<string, PlayerStats>();
    public List<Fight> InactiveFights { get; } = new List<Fight>();
  }

  public class DataPoint
  {
    public long Avg { get; set; }
    public string Name { get; set; }
    public string PlayerName { get; set; }
    public int ModifiersMask { get; set; }
    public long Total { get; set; }
    public long RollingTotal { get; set; }
    public uint RollingHits { get; set; }
    public uint RollingCritHits { get; set; }
    public long VPS { get; set; }
    public double CritRate { get; set; }
    public double BeginTime { get; set; }
    public double CurrentTime { get; set; }
  }

  public class HitFreqChartData
  {
    public string HitType { get; set; }
    public List<int> CritYValues { get; } = new List<int>();
    public List<long> CritXValues { get; } = new List<long>();
    public List<int> NonCritYValues { get; } = new List<int>();
    public List<long> NonCritXValues { get; } = new List<long>();
  }

  public class StatsSummary
  {
    public string Title { get; set; }
    public string RankedPlayers { get; set; }
  }

  public class PlayerSubStats : Attempt
  {
    public ushort Rank { get; set; }
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
    public double CritRate { get; set; }
    public double ExtraRate { get; set; }
    public double MeleeHitRate { get; set; }
    public double LuckRate { get; set; }
    public double StrikethroughRate { get; set; }
    public double RampageRate { get; set; }
    public double RiposteRate { get; set; }
    public double TwincastRate { get; set; }
    public double ResistRate { get; set; }
    public double Percent { get; set; }
    public double PercentOfRaid { get; set; }
    public string Special { get; set; }
    public string ClassName { get; set; }
    public TimeRange Ranges { get; } = new TimeRange();
  }

  public class PlayerStats : PlayerSubStats
  {
    public Dictionary<string, PlayerSubStats> SubStats { get; } = new Dictionary<string, PlayerSubStats>();
    public Dictionary<string, PlayerSubStats> SubStats2 { get; } = new Dictionary<string, PlayerSubStats>();
    public string OrigName { get; set; }
    public double CalcTime { get; set; }
  }
}

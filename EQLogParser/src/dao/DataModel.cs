using Syncfusion.UI.Xaml.TreeView.Engine;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

namespace EQLogParser
{
  internal interface ISummaryBuilder
  {
    StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showPetLabel, bool showDPS, bool showTotals,
      bool rankPlayers, bool showSpecial, bool showTime, string customTitle);
  }

  internal class AudioTrigger : NotificationObject
  {
    [Editable(false)]
    public string Name { get; set; }
    [Display(Name = "Use Regex")]
    [Editable(false)]
    public bool UseRegex { get; set; }
    [Display(Name = "Text to Speak")]
    [Editable(false)]
    public string Speak { get; set; }
    [Editable(false)]
    public string Pattern { get; set; }
    [Editable(false)]
    public int Priority { get; set; } = 5;
    public double LastTriggered;
  }

  internal class AudioTriggerPropertyViewModel
  {
    public object SelectedAudioTrigger { get; set; }
  }

  internal class AudioTriggerData
  {
    public bool? IsEnabled { get; set; } = false;
    public bool IsExpanded { get; set; } = false;
    public string Name { get; set; }
    public List<AudioTriggerData> Nodes { get; set; }
    public AudioTrigger TriggerData { get; set; }
  }

  internal class AudioTriggerTreeViewNode : TreeViewNode
  {
    public bool IsTrigger { get; set; }
    public AudioTriggerData SerializedData{ get; set; }
  }

  internal interface IAction { }

  internal class DataPoint
  {
    public long Avg { get; set; }
    public string Name { get; set; }
    public string PlayerName { get; set; }
    public int ModifiersMask { get; set; }
    public long Total { get; set; }
    public string Type { get; set; }
    public long FightTotal { get; set; }
    public uint FightHits { get; set; }
    public uint FightCritHits { get; set; }
    public uint FightTcHits { get; set; }
    public long RollingTotal { get; set; }
    public long RollingDps { get; set; }
    public long CritsPerSecond { get; set; }
    public long TcPerSecond { get; set; }
    public long AttemptsPerSecond { get; set; }
    public long HitsPerSecond { get; set; }
    public long TotalPerSecond { get; set; }
    public long ValuePerSecond { get; set; }
    public double CritRate { get; set; }
    public double TcRate { get; set; }
    public double BeginTime { get; set; }
    public double CurrentTime { get; set; }
    public DateTime DateTime { get; set; }
  }

  internal class ComboBoxItemDetails
  {
    public string Text { get; set; }
    public string SelectedText { get; set; }
    public bool IsChecked { get; set; }
    public string Value { get; set; }
  }

  internal class AutoCompleteText
  {
    public string Text { get; set; }
    public List<string> Items { get; } = new List<string>();
  }

  internal class ChatType
  {
    public string Channel { get; set; }
    public string Sender { get; set; }
    public string Receiver { get; set; }
    public bool SenderIsYou { get; set; }
    public string Text { get; set; }
    public int TextStart { get; set; }
    public int KeywordStart { get; set; }
    public double BeginTime { get; set; }
  }

  internal class ParseData
  {
    public CombinedStats CombinedStats { get; set; }
    public ISummaryBuilder Builder { get; set; }
    public List<PlayerStats> Selected { get; } = new List<PlayerStats>();
  }

  internal class TimedAction : IAction
  {
    public double BeginTime { get; set; }
  }

  internal class FullTimedAction : TimedAction
  {
    public double LastTime { get; set; }
  }

  internal class PlayerStatsSelectionChangedEventArgs : EventArgs
  {
    public List<PlayerStats> Selected { get; } = new List<PlayerStats>();
    public CombinedStats CurrentStats { get; set; }
  }

  internal class DamageProcessedEvent
  {
    public DamageRecord Record { get; set; }
    public double BeginTime { get; set; }
  }

  internal class DataPointEvent
  {
    public string Action { get; set; }
    public RecordGroupCollection Iterator { get; set; }
    public List<PlayerStats> Selected { get; } = new List<PlayerStats>();
  }

  internal class GenerateStatsOptions
  {
    public List<Fight> Npcs { get; } = new List<Fight>();
    public long MaxSeconds { get; set; } = -1;
    public long MinSeconds { get; set; } = -1;
    public int DamageType { get; set; }
  }

  internal class StatsGenerationEvent
  {
    public bool Limited { get; set; } = false;
    public string Type { get; set; }
    public string State { get; set; }
    public CombinedStats CombinedStats { get; set; }
    public List<List<ActionBlock>> Groups { get; } = new List<List<ActionBlock>>();
    public int UniqueGroupCount { get; set; }
  }

  internal class ResistRecord : IAction
  {
    public string Spell { get; set; }
    public string Defender { get; set; }
  }

  internal class RandomRecord : IAction
  {
    public string Player { get; set; }
    public int Rolled { get; set; }
    public int To { get; set; }
    public int From { get; set; }
  }

  internal class HitRecord : IAction
  {
    public uint Total { get; set; }
    public uint OverTotal { get; set; }
    public string Type { get; set; }
    public string SubType { get; set; }
    public int ModifiersMask { get; set; }
  }

  internal class HealRecord : HitRecord
  {
    public string Healer { get; set; }
    public string Healed { get; set; }
  }

  internal class DamageRecord : HitRecord
  {
    public string Attacker { get; set; }
    public string AttackerOwner { get; set; }
    public string Defender { get; set; }
    public string DefenderOwner { get; set; }
    public bool AttackerIsSpell { get; set; }
  }

  internal class LootRecord : IAction
  {
    public string Item { get; set; }
    public uint Quantity { get; set; }
    public string Player { get; set; }
    public string Npc { get; set; }
    public bool IsCurrency { get; set; }
  }

  internal class TauntRecord : IAction
  {
    public string Player { get; set; }
    public string Npc { get; set; }
    public bool Success { get; set; }
    public bool IsImproved { get; set; }
  }

  internal class TauntEvent
  {
    public TauntRecord Record { get; set; }
    public double BeginTime { get; set; }
  }

  internal class DeathRecord : IAction
  {
    public string Killed { get; set; }
    public string Killer { get; set; }
    public string Message { get; set; }
    public string Previous { get; set; }
  }

  internal class DeathEvent : DeathRecord
  {
    public double BeginTime { get; set; }
  }

  internal class MezBreakRecord : IAction
  {
    public string Breaker { get; set; }
    public string Awakened { get; set; }
  }

  internal class ZoneRecord : IAction
  {
    public string Zone { get; set; }
  }

  internal class LineData
  {
    public string Action { get; set; }
    public double BeginTime { get; set; }
    public long LineNumber { get; set; }
  }

  internal class LootRow : LootRecord
  {
    public double Time { get; set; }
  }

  internal class ActionBlock : TimedAction
  {
    public List<IAction> Actions { get; } = new List<IAction>();
  }

  internal class Attempt
  {
    public uint Absorbs { get; set; }
    public uint Blocks { get; set; }
    public uint Dodges { get; set; }
    public uint Misses { get; set; }
    public uint Parries { get; set; }
    public uint Invulnerable { get; set; }
    public uint Max { get; set; }
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
    public Dictionary<long, int> CritFreqValues { get; } = new Dictionary<long, int>();
    public Dictionary<long, int> NonCritFreqValues { get; } = new Dictionary<long, int>();
  }

  internal class Fight : FullTimedAction, INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private bool searchResult;
    public bool IsSearchResult
    {
      get { return searchResult; }
      set
      {
        searchResult = value;
        OnPropertyChanged();
      }
    }

    public const string BREAKTIME = "Break Time";

    public bool Dead { get; set; } = false;
    public double BeginDamageTime { get; set; } = double.NaN;
    public double BeginTankingTime { get; set; } = double.NaN;
    public double LastDamageTime { get; set; }
    public double LastTankingTime { get; set; }
    public string BeginTimeString { get; set; }
    public string Name { get; set; }
    public long Id { get; set; }
    public string CorrectMapKey { get; set; }
    public int GroupId { get; set; }
    public uint SortId { get; set; }
    public int NonTankingGroupId { get; set; }
    public bool IsInactivity { get; set; } = false;
    public long Total { get; set; }
    public long DamageHits { get; set; }
    public long TankHits { get; set; }
    public string TooltipText { get; set; }
    public ConcurrentDictionary<string, FightTotalDamage> PlayerTotals { get; } = new ConcurrentDictionary<string, FightTotalDamage>();
    public List<ActionBlock> DamageBlocks { get; } = new List<ActionBlock>();
    public Dictionary<string, TimeSegment> DamageSegments { get; } = new Dictionary<string, TimeSegment>();
    public Dictionary<string, Dictionary<string, TimeSegment>> DamageSubSegments { get; } = new Dictionary<string, Dictionary<string, TimeSegment>>();
    public Dictionary<string, TimeSegment> InitialTankSegments { get; } = new Dictionary<string, TimeSegment>();
    public Dictionary<string, Dictionary<string, TimeSegment>> InitialTankSubSegments { get; } = new Dictionary<string, Dictionary<string, TimeSegment>>();
    public List<ActionBlock> TankingBlocks { get; } = new List<ActionBlock>();
    public List<ActionBlock> TauntBlocks { get; } = new List<ActionBlock>();
    public Dictionary<string, SpellDamageStats> DoTDamage { get; } = new Dictionary<string, SpellDamageStats>();
    public Dictionary<string, SpellDamageStats> DDDamage { get; } = new Dictionary<string, SpellDamageStats>();
    public Dictionary<string, SpellDamageStats> ProcDamage { get; } = new Dictionary<string, SpellDamageStats>();
  }

  internal class FightTotalDamage
  {
    public long Damage { get; set; }
    public string Name { get; set; }
    public string PetOwner { get; set; }
    public double UpdateTime { get; set; }
    public double BeginTime { get; set; }
  }

  public class SpellDamageStats
  {
    internal uint Count { get; set; }
    internal ulong Total { get; set; }
    internal uint Max { get; set; }
    internal string Spell { get; set; }
    internal string Caster { get; set; }
  }

  internal class PetMapping
  {
    public string Owner { get; set; }
    public string Pet { get; set; }
  }

  internal class SpecialSpell : TimedAction
  {
    public string Code { get; set; }
    public string Player { get; set; }
  }

  internal class ReceivedSpell : TimedAction
  {
    public string Receiver { get; set; }
    public SpellData SpellData { get; set; }

    public List<SpellData> Ambiguity { get; } = new List<SpellData>();
  }

  internal class SpellCast : ReceivedSpell
  {
    public string Spell { get; set; }
    public string Caster { get; set; }
    public bool Interrupted { get; set; } = false;
  }

  internal class SpellData
  {
    public string ID { get; set; }
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
    public string LandsOnYou { get; set; }
    public string LandsOnOther { get; set; }
    public bool SongWindow { get; set; }
    public string WearOff { get; set; }
    public byte Proc { get; set; }
    public byte Adps { get; set; }
    public byte Rank { get; set; }
    public bool Mgb { get; set; }
  }

  internal class SpellCountData
  {
    public Dictionary<string, Dictionary<string, uint>> PlayerCastCounts { get; set; } = new Dictionary<string, Dictionary<string, uint>>();
    public Dictionary<string, Dictionary<string, uint>> PlayerInterruptedCounts { get; set; } = new Dictionary<string, Dictionary<string, uint>>();
    public Dictionary<string, Dictionary<string, uint>> PlayerReceivedCounts { get; set; } = new Dictionary<string, Dictionary<string, uint>>();
    public Dictionary<string, uint> MaxCastCounts { get; set; } = new Dictionary<string, uint>();
    public Dictionary<string, uint> MaxReceivedCounts { get; set; } = new Dictionary<string, uint>();
    public Dictionary<string, SpellData> UniqueSpells { get; set; } = new Dictionary<string, SpellData>();
  }

  internal class OverlayPlayerTotal
  {
    internal long Damage { get; set; }
    internal TimeRange Range { get; set; }
    internal string Name { get; set; }
    internal double UpdateTime { get; set; }
  }

  internal class CombinedStats
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

  internal class StatsSummary
  {
    public string Title { get; set; }
    public string RankedPlayers { get; set; }
  }

  internal class PlayerSubStats : Attempt
  {
    public long BestSec { get; set; }
    public long BestSecTemp { get; set; }
    public ushort Rank { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public double TotalSeconds { get; set; }
    public long DPS { get; set; }
    public long SDPS { get; set; }
    public long Extra { get; set; }
    public long Potential { get; set; }
    public int Resists { get; set; }
    public long Avg { get; set; }
    public long AvgCrit { get; set; }
    public long AvgLucky { get; set; }
    public long AvgNonTwincast { get; set; }
    public long AvgNonTwincastCrit { get; set; }
    public long AvgNonTwincastLucky { get; set; }
    public float CritRate { get; set; }
    public float DoubleBowRate { get; set; }
    public float ExtraRate { get; set; }
    public float FlurryRate { get; set; }
    public float MeleeAccRate { get; set; }
    public float MeleeHitRate { get; set; }
    public float LuckRate { get; set; }
    public float StrikethroughRate { get; set; }
    public float RampageRate { get; set; }
    public float RiposteRate { get; set; }
    public float TwincastRate { get; set; }
    public float ResistRate { get; set; }
    public float Percent { get; set; }
    public float PercentOfRaid { get; set; }
    public string Special { get; set; }
    public uint MeleeUndefended { get; set; }
    public string ClassName { get; set; }
    public string Key { get; set; }
    public TimeRange Ranges { get; } = new TimeRange();
  }

  internal class SubStatsBreakdown : PlayerSubStats
  {
    public List<PlayerSubStats> Children { get; set; } = new List<PlayerSubStats>();
  }

  internal class PlayerStats : PlayerSubStats
  {
    public List<DeathEvent> Deaths { get; } = new List<DeathEvent>();
    public ConcurrentDictionary<string, string> Specials { get; } = new ConcurrentDictionary<string, string>();
    public List<PlayerSubStats> SubStats { get; } = new List<PlayerSubStats>();
    public List<PlayerSubStats> SubStats2 { get; } = new List<PlayerSubStats>();
    public PlayerStats MoreStats { get; set; }
    public bool IsTopLevel { get; set; } = true;
    public string OrigName { get; set; }
    public double MaxTime { get; set; }
    public double MinTime { get; set; }
  }

}

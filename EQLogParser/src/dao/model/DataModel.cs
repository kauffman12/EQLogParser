using LiteDB;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace EQLogParser
{
  internal interface IDocumentContent
  {
    public void HideContent();
  }

  internal class PiperVoice
  {
    public string Name { get; set; }
    public string Model { get; set; }
    public string Config { get; set; }
    public int Sample { get; set; }
  }

  internal class PiperVoiceData
  {
    public List<PiperVoice> Voices { get; set; }
  }

  internal class LexiconItem : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _replace;
    public string Replace
    {
      get => _replace;
      set
      {
        if (_replace == value) return;
        _replace = value;
        OnPropertyChanged();
      }
    }

    private string _with;
    public string With
    {
      get => _with;
      set
      {
        if (_with == value) return;
        _with = value;
        OnPropertyChanged();
      }
    }
  }

  internal class TrustedPlayer : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _name;
    public string Name
    {
      get => _name;
      set
      {
        if (_name == value) return;
        _name = value;
        OnPropertyChanged();
      }
    }
  }

  internal class ResistCount
  {
    public uint Landed { get; set; }
    public uint Resisted { get; set; }
  }

  internal class NpcResistStats
  {
    public ObjectId Id { get; set; }
    public string Npc { get; set; }
    public Dictionary<SpellResist, ResistCount> ByResist { get; set; } = new();
  }

  internal interface IAction;

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

  internal class ComboBoxItemDetails : NotificationObject
  {
    public ComboBoxItemDetails()
    {
    }

    public ComboBoxItemDetails(bool isChecked, string text)
    {
      IsChecked = isChecked;
      Text = text;
    }

    public string Text { get; set; }
    public string SelectedText { get; set; }
    public bool IsChecked { get; set; }
    public string Value { get; set; }
  }

  internal class ParseData
  {
    public CombinedStats CombinedStats { get; set; }
    public List<PlayerStats> Selected { get; } = [];
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
    public List<PlayerStats> Selected { get; } = [];
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
    public List<PlayerStats> Selected { get; } = [];
  }

  internal class GenerateStatsOptions
  {
    public List<Fight> Npcs { get; } = [];
    public long MaxSeconds { get; set; } = -1;
    public long MinSeconds { get; set; } = -1;
    public int DamageType { get; set; }
    public TimeRange AllRanges { get; set; }
  }

  internal class StatsGenerationEvent
  {
    public bool Limited { get; set; }
    public string Type { get; set; }
    public string State { get; set; }
    public CombinedStats CombinedStats { get; set; }
    public List<List<ActionGroup>> Groups { get; } = [];
    public int UniqueGroupCount { get; set; }
  }

  internal class ResistRecord : IAction
  {
    public string Attacker { get; set; }
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

  public class HitRecord : IAction
  {
    public uint Total { get; set; }
    public uint OverTotal { get; set; }
    public string Type { get; set; }
    public string SubType { get; set; }
    public short ModifiersMask { get; set; }
  }

  internal class HealRecord : HitRecord
  {
    public string Healer { get; set; }
    public string Healed { get; set; }
  }

  [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
  internal class DamageRecord : HitRecord
  {
    public string Attacker { get; set; }
    public string AttackerOwner { get; set; }
    public string Defender { get; set; }
    public string DefenderOwner { get; set; }
    public bool AttackerIsSpell { get; set; }

    public override bool Equals(object obj)
    {
      return obj is DamageRecord other && Attacker == other.Attacker && AttackerOwner == other.AttackerOwner && Defender == other.Defender &&
        DefenderOwner == other.DefenderOwner && AttackerIsSpell == other.AttackerIsSpell && Total == other.Total &&
        OverTotal == other.OverTotal && Type == other.Type && SubType == other.SubType && ModifiersMask == other.ModifiersMask;
    }

    public override int GetHashCode()
    {
      var hash1 = HashCode.Combine(Attacker, AttackerOwner, Defender, DefenderOwner, AttackerIsSpell, Total);
      var hash2 = HashCode.Combine(OverTotal, Type, SubType, ModifiersMask);
      return HashCode.Combine(hash1, hash2);
    }
  }

  [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
  internal class LootRecord : IAction
  {
    public string Player { get; set; }
    public string Item { get; set; }
    public uint Quantity { get; set; }
    public string Npc { get; set; }
    public bool IsCurrency { get; set; }

    public override bool Equals(object obj)
    {
      return obj is LootRecord other && Item == other.Item && Quantity == other.Quantity && Player == other.Player &&
        Npc == other.Npc && IsCurrency == other.IsCurrency;
    }

    public override int GetHashCode()
    {
      return HashCode.Combine(Item, Quantity, Player, Npc, IsCurrency);
    }
  }

  internal class SpecialRecord : IAction
  {
    public string Code { get; set; }
    public string Player { get; set; }
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

  internal class DeathEvent
  {
    public DeathRecord Record { get; set; }
    public double BeginTime { get; set; }
  }

  internal class MezBreakRecord : IAction
  {
    public string Breaker { get; set; }
    public string Awakened { get; set; }
  }

  internal class PlayerClassMapping
  {
    public string Player { get; set; }
    public string ClassName { get; set; }
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
    public string[] Split { get; set; }
  }

  internal class ActionGroup : TimedAction
  {
    public List<IAction> Actions { get; } = [];
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

  internal class Defender
  {
    public string Name { get; set; }
    public double BeginTime { get; set; } = double.NaN;
    public bool Dead { get; set; }
    public List<DamageRecord> Records { get; init; } = [];
  }

  internal class Fight : FullTimedAction, INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private bool _searchResult;
    public bool IsSearchResult
    {
      get => _searchResult;
      set
      {
        _searchResult = value;
        OnPropertyChanged();
      }
    }

    public const string Breaktime = "Break Time";
    // keeping these 3 fields in this order for display
    public uint SortId { get; set; }
    public long DamageTotal { get; set; }
    public string Name { get; set; }
    public bool Dead { get; set; }
    public double BeginDamageTime { get; set; } = double.NaN;
    public double BeginTankingTime { get; set; } = double.NaN;
    public double LastDamageTime { get; set; } = double.NaN;
    public double LastTankingTime { get; set; } = double.NaN;
    public string BeginTimeString { get; set; }
    public long Id { get; set; }
    public string CorrectMapKey { get; set; }
    public int GroupId { get; set; }
    public int NonTankingGroupId { get; set; }
    public bool IsInactivity { get; set; }
    public long TankTotal { get; set; }
    public long DamageHits { get; set; }
    public long TankHits { get; set; }
    public string TooltipText { get; set; }
    public ConcurrentDictionary<string, FightTotalDamage> PlayerDamageTotals { get; } = new();
    public ConcurrentDictionary<string, FightTotalDamage> PlayerTankTotals { get; } = new();
    public List<ActionGroup> DamageBlocks { get; } = [];
    public Dictionary<string, TimeSegment> DamageSegments { get; } = [];
    public Dictionary<string, Dictionary<string, TimeSegment>> DamageSubSegments { get; } = [];
    public Dictionary<string, TimeSegment> TankSegments { get; } = [];
    public Dictionary<string, Dictionary<string, TimeSegment>> TankSubSegments { get; } = [];
    public List<ActionGroup> TankingBlocks { get; } = [];
    public List<ActionGroup> TauntBlocks { get; } = [];
    public Dictionary<string, SpellDamageStats> DoTDamage { get; } = [];
    public Dictionary<string, SpellDamageStats> DdDamage { get; } = [];
    public Dictionary<string, SpellDamageStats> ProcDamage { get; } = [];
  }

  internal class FightTotalDamage
  {
    public long Damage { get; set; }
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

  internal class PetMapping(string pet, string owner)
  {
    public string Owner { get; set; } = owner;
    public string Pet { get; set; } = pet;
  }

  internal class ReceivedSpell : IAction
  {
    public string Receiver { get; set; }
    public SpellData SpellData { get; set; }
    public bool IsWearOff { get; set; }
    public List<SpellData> Ambiguity { get; } = [];
  }

  internal class SpellCast : IAction
  {
    public string Spell { get; set; }
    public SpellData SpellData { get; set; }
    public string Caster { get; set; }
    public bool Interrupted { get; set; }
  }

  internal class SpellData
  {
    public string Id { get; set; }
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
    public bool HasAmbiguity { get; set; }
    public string LandsOnYou { get; set; }
    public string LandsOnOther { get; set; }
    public bool SongWindow { get; set; }
    public string WearOff { get; set; }
    public byte Proc { get; set; }
    public byte Adps { get; set; }
    public byte Rank { get; set; }
    public bool Mgb { get; set; }
    public bool SeenRecently { get; set; }
    public bool IsUnknown { get; set; }
  }

  internal class SpellCountData
  {
    public Dictionary<string, Dictionary<string, uint>> PlayerCastCounts { get; set; } = [];
    public Dictionary<string, Dictionary<string, uint>> PlayerInterruptedCounts { get; set; } = [];
    public Dictionary<string, Dictionary<string, uint>> PlayerReceivedCounts { get; set; } = [];
    public Dictionary<string, uint> MaxCastCounts { get; set; } = [];
    public Dictionary<string, uint> MaxReceivedCounts { get; set; } = [];
    public Dictionary<string, SpellData> UniqueSpells { get; set; } = [];
    public Dictionary<string, bool> UniquePlayers { get; set; } = [];
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
    public List<PlayerStats> StatsList { get; } = [];
    public List<PlayerStats> ExpandedStatsList { get; } = [];
    public PlayerStats RaidStats { get; init; }
    public Dictionary<string, string> PlayerClasses { get; init; }
    public List<string> UniqueClasses { get; } = [];
    public Dictionary<string, List<PlayerStats>> Children { get; } = [];
  }

  internal class DamageOverlayStats
  {
    public CombinedStats DamageStats { get; set; }
    public CombinedStats TankStats { get; set; }
    public double LastUpdateTicks { get; set; }
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
    public long Dps { get; set; }
    public long Sdps { get; set; }
    public long Pdps { get; set; }
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
    public TimeRange Ranges { get; set; } = new();
    public TimeRange AllRanges { get; set; } = new();
    public List<PlayerSubStats> SubSubStats { get; } = [];
  }

  internal class SubStatsBreakdown : PlayerSubStats
  {
    public List<PlayerSubStats> Children { get; set; } = [];
  }

  internal class PlayerStats : PlayerSubStats, System.ComponentModel.INotifyPropertyChanged
  {
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public List<DeathEvent> Deaths { get; } = [];
    public ConcurrentDictionary<string, string> Specials { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, int>> ResistCounts { get; } = new();
    public List<PlayerSubStats> SubStats { get; } = [];
    public List<PlayerSubStats> SubStats2 { get; } = [];
    public PlayerStats MoreStats { get; set; }
    public bool IsTopLevel { get; set; } = true;
    public string OrigName { get; set; }
    public double MaxTime { get; set; }
    public double MinTime { get; set; }
    public double MaxBeginTime { get; set; }
    public double MinBeginTime { get; set; }

    private int _assignedGroup;
    public int AssignedGroup
    {
      get => _assignedGroup;
      set
      {
        if (_assignedGroup != value)
        {
          _assignedGroup = value;
          OnPropertyChanged();
        }
      }
    }

    // For Syncfusion TreeGrid expansion state preservation
private bool _isExpanded;
    public bool IsExpanded
    {
      get => _isExpanded;
      set
      {
        if (_isExpanded != value)
        {
          _isExpanded = value;
          OnPropertyChanged();
        }
      }
    }

    // For identifying group header rows (used to hide ComboBox in Group View)
    private bool _isGroupHeader;
    public bool IsGroupHeader
    {
      get => _isGroupHeader;
      set
      {
        if (_isGroupHeader != value)
        {
          _isGroupHeader = value;
          OnPropertyChanged();
        }
      }
    }
  }
}
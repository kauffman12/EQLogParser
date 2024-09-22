using AutoMapper;
using LiteDB;
using Syncfusion.UI.Xaml.TreeView.Engine;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  internal class MappingProfile : Profile
  {
    public MappingProfile()
    {
      CreateMap<TriggerNode, TriggerNode>();
      CreateMap<ExportTriggerNode, TriggerNode>();
      CreateMap<LegacyOverlay, Overlay>();
      CreateMap<LootRecord, LootRecord>();
    }
  }

  internal interface ISummaryBuilder
  {
    StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showPetLabel, bool showDps, bool showTotals,
      bool rankPlayers, bool showSpecial, bool showTime, string customTitle);
  }

  internal interface IDocumentContent
  {
    public void HideContent();
  }

  internal class LexiconItem
  {
    public string Replace { get; set; }
    public string With { get; set; }
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

  internal class TimerData
  {
    public CancellationTokenSource CancelSource { get; set; }
    public CancellationTokenSource WarningSource { get; set; }
    public string DisplayName { get; set; }
    public long BeginTicks { get; set; }
    public long EndTicks { get; set; }
    public long ResetTicks { get; set; }
    public long ResetDurationTicks { get; set; }
    public long DurationTicks { get; set; }
    public List<string> TimerOverlayIds { get; set; }
    public int TriggerAgainOption { get; set; }
    public int TimerType { get; set; }
    public string Key { get; set; }
    public string EndEarlyPattern { get; set; }
    public string EndEarlyPattern2 { get; set; }
    public Regex EndEarlyRegex { get; set; }
    public Regex EndEarlyRegex2 { get; set; }
    public List<NumberOptions> EndEarlyRegexNOptions { get; set; }
    public List<NumberOptions> EndEarlyRegex2NOptions { get; set; }
    public MatchCollection OriginalMatches { get; set; }
    public MatchCollection PreviousMatches { get; set; }
    public long RepeatedCount { get; set; } = -1;
    public string ActiveColor { get; set; }
    public string FontColor { get; set; }
    public LineData RepeatingTimerLineData { get; set; }
    public int TimesToLoopCount { get; set; }
    public BitmapImage TimerIcon { get; set; }
  }

  internal class NumberOptions
  {
    public uint Value { get; set; }
    public string Key { get; set; }
    public string Op { get; set; }
  }

  internal class Overlay
  {
    public string OverlayComments { get; set; }
    public string FontSize { get; set; } = "12pt";
    public int SortBy { get; set; }
    public string FontColor { get; set; } = "#FFFFFFFF";
    public string FontFamily { get; set; } = "Segoe UI";
    public string ActiveColor { get; set; } = "#FF1D397E";
    public string BackgroundColor { get; set; } = "#5F000000";
    public string IdleColor { get; set; } = "#FF8f1515";
    public string ResetColor { get; set; } = "#FF8f1515";
    public string OverlayColor { get; set; } = "#00000000";
    public double IdleTimeoutSeconds { get; set; }
    public long FadeDelay { get; set; } = 10;
    public bool UseStandardTime { get; set; }
    public bool IsTimerOverlay { get; set; }
    public bool IsTextOverlay { get; set; }
    public bool IsDefault { get; set; }
    public bool ShowActive { get; set; } = true;
    public bool ShowIdle { get; set; } = true;
    public bool ShowReset { get; set; } = true;
    public int TimerMode { get; set; }
    public long Height { get; set; } = 400;
    public long Width { get; set; } = 300;
    public long Top { get; set; } = 200;
    public long Left { get; set; } = 100;
  }

  internal class LegacyOverlay : Overlay
  {
    public string Id { get; set; }
    public string Name { get; set; }
  }

  internal class OverlayWindowData
  {
    public Window TheWindow { get; set; }
    public long RemoveTicks { get; set; } = -1;
    public bool IsCooldown { get; set; } = false;
  }

  internal class Trigger
  {
    public double LastTriggered { get; set; }
    public string AltTimerName { get; set; }
    public string Comments { get; set; }
    public double RepeatedResetTime { get; set; } = 0.75;
    public double DurationSeconds { get; set; }
    public bool EnableTimer { get; set; }
    public int TimerType { get; set; }
    public string EndEarlyPattern { get; set; }
    public string EndEarlyPattern2 { get; set; }
    public bool EndUseRegex { get; set; }
    public bool EndUseRegex2 { get; set; }
    public long WorstEvalTime { get; set; } = -1;
    public string Pattern { get; set; }
    public string PreviousPattern { get; set; }
    public long Priority { get; set; } = 3;
    public int TriggerAgainOption { get; set; }
    public bool UseRegex { get; set; }
    public bool PreviousUseRegex { get; set; }
    public string ActiveColor { get; set; } = null;
    public string FontColor { get; set; } = null;
    public string IconSource { get; set; } = null;
    public List<string> SelectedOverlays { get; set; } = [];
    public double ResetDurationSeconds { get; set; }
    public long WarningSeconds { get; set; }
    public string EndEarlyTextToDisplay { get; set; }
    public string EndTextToDisplay { get; set; }
    public string TextToDisplay { get; set; }
    public string WarningTextToDisplay { get; set; }
    public string EndEarlyTextToSpeak { get; set; }
    public string EndTextToSpeak { get; set; }
    public string TextToSpeak { get; set; }
    public string WarningTextToSpeak { get; set; }
    public string SoundToPlay { get; set; }
    public string EndEarlySoundToPlay { get; set; }
    public string EndSoundToPlay { get; set; }
    public string WarningSoundToPlay { get; set; }
    public string TextToShare { get; set; }
    public long TimesToLoop { get; set; }
    public double LockoutTime { get; set; }
    public int Volume { get; set; } = 4; // no increase
  }

  internal class TimerOverlayPropertyModel : Overlay
  {
    public TimeSpan IdleTimeoutTimeSpan { get; set; }
    public SolidColorBrush FontBrush { get; set; }
    public SolidColorBrush ActiveBrush { get; set; }
    public SolidColorBrush IdleBrush { get; set; }
    public SolidColorBrush ResetBrush { get; set; }
    public SolidColorBrush BackgroundBrush { get; set; }
    public SolidColorBrush OverlayBrush { get; set; }
    // preview referenced dynamically
    public string TimerBarPreview { get; set; }
    public TriggerNode Node { get; set; }
  }

  internal class TextOverlayPropertyModel : Overlay
  {
    public SolidColorBrush FontBrush { get; set; }
    public SolidColorBrush OverlayBrush { get; set; }
    public TriggerNode Node { get; set; }
  }

  internal class TriggerPropertyModel : Trigger
  {
    public SolidColorBrush TriggerActiveBrush { get; set; }
    public SolidColorBrush TriggerFontBrush { get; set; }
    public BitmapImage TriggerIconSource { get; set; }
    public ObservableCollection<ComboBoxItemDetails> SelectedTextOverlays { get; set; }
    public ObservableCollection<ComboBoxItemDetails> SelectedTimerOverlays { get; set; }
    public TimeSpan DurationTimeSpan { get; set; }
    public TimeSpan ResetDurationTimeSpan { get; set; }
    public string SoundOrText { get; set; }
    public string EndEarlySoundOrText { get; set; }
    public string EndSoundOrText { get; set; }
    public string WarningSoundOrText { get; set; }
    public TriggerNode Node { get; set; }
    public DependencyObject DataContext { get; set; }
  }

  internal class TriggerState
  {
    [BsonId]
    public string Id { get; set; }
    public Dictionary<string, bool?> Enabled { get; set; } = new();
  }

  internal class TriggerNode
  {
    [BsonId]
    public string Id { get; set; }
    public bool IsExpanded { get; set; }
    public string Name { get; set; }
    public Trigger TriggerData { get; set; }
    public Overlay OverlayData { get; set; }
    public int Index { get; set; }
    public string Parent { get; set; }
  }

  internal class TriggerCharacter
  {
    public string Id { get; set; }
    public string Name { get; set; }
    public string FilePath { get; set; }
    public bool IsEnabled { get; set; }
    public string Voice { get; set; }
    public int VoiceRate { get; set; }
    public string ActiveColor { get; set; } = null;
    public string FontColor { get; set; } = null;
    [BsonIgnore] public bool? IsWaiting { get; set; } = true;
  }

  internal class TriggerConfig
  {
    [BsonId]
    public string Id { get; set; }
    public bool IsAdvanced { get; set; }
    public List<TriggerCharacter> Characters { get; set; } = [];
    public bool IsEnabled { get; set; }
    public string Voice { get; set; }
    public int VoiceRate { get; set; }
    public int Volume { get; set; }
  }

  internal class ExportTriggerNode : TriggerNode
  {
    public List<ExportTriggerNode> Nodes { get; set; } = [];
  }

  internal class LegacyTriggerNode
  {
    public bool? IsEnabled { get; set; } = false;
    public bool IsExpanded { get; set; } = false;
    public string Name { get; set; }
    public List<LegacyTriggerNode> Nodes { get; set; } = [];
    public Trigger TriggerData { get; set; }
    public LegacyOverlay OverlayData { get; set; }
  }

  internal class TriggerTreeViewNode : TreeViewNode
  {
    public TriggerNode SerializedData { get; set; }
    public bool IsTrigger() => SerializedData?.TriggerData != null;
    public bool IsOverlay() => SerializedData?.OverlayData != null;
    public bool IsDir() => !IsOverlay() && !IsTrigger();
    public bool IsRecentlyMerged { get; set; }
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
    public ISummaryBuilder Builder { get; set; }
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
    public bool Limited { get; set; } = false;
    public string Type { get; set; }
    public string State { get; set; }
    public CombinedStats CombinedStats { get; set; }
    public List<List<ActionGroup>> Groups { get; } = [];
    public int UniqueGroupCount { get; set; }
    public ISummaryBuilder Source { get; set; }
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
    public bool Dead { get; set; } = false;
    public double BeginDamageTime { get; set; } = double.NaN;
    public double BeginTankingTime { get; set; } = double.NaN;
    public double LastDamageTime { get; set; } = double.NaN;
    public double LastTankingTime { get; set; } = double.NaN;
    public string BeginTimeString { get; set; }
    public long Id { get; set; }
    public string CorrectMapKey { get; set; }
    public int GroupId { get; set; }
    public int NonTankingGroupId { get; set; }
    public bool IsInactivity { get; set; } = false;
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

  internal class PetMapping
  {
    public string Owner { get; set; }
    public string Pet { get; set; }
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
    public bool Interrupted { get; set; } = false;
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
    public PlayerStats RaidStats { get; set; }
    public Dictionary<string, byte> UniqueClasses { get; } = [];
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
    public TimeRange Ranges { get; } = new();
    public TimeRange AllRanges { get; set; } = new();
  }

  internal class SubStatsBreakdown : PlayerSubStats
  {
    public List<PlayerSubStats> Children { get; set; } = [];
  }

  internal class PlayerStats : PlayerSubStats
  {
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
  }

}

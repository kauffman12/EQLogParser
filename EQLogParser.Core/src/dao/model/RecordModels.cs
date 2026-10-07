using LiteDB;
using System.Diagnostics.CodeAnalysis;

namespace EQLogParser
{
  internal interface IAction;


  internal class TimedAction : IAction
  {
    public double BeginTime { get; set; }
  }


  internal class FullTimedAction : TimedAction
  {
    public double LastTime { get; set; }
  }


  internal class ActionGroup : TimedAction
  {
    public List<IAction> Actions { get; } = [];
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




  /* A parsed "X resisted Y's spell" line, raised by MiscLineParser for consumers that watch events rather than
   * query the store (today: FCT). The record itself is already stored when the event fires. */
  internal class ResistEvent
  {
    public ResistRecord Record { get; set; }
    public double BeginTime { get; set; }

    /* live tail line vs initial replay load — see LineData.IsMonitor */
    public bool IsMonitor { get; set; }
  }

  internal class DamageProcessedEvent
  {
    public DamageRecord Record { get; set; }
    public double BeginTime { get; set; }

    // The raw action line as parsed (D1: consumers like CombatCapture read line-intrinsic evidence
    // — e.g. "X`s pet" ownership — without a registry or a second parse).
    public string Action { get; set; }

    /* live tail line vs initial replay load - see LineData.IsMonitor */
    public bool IsMonitor { get; set; }
  }

  internal class HealProcessedEvent
  {
    public HealRecord Record { get; set; }
    public double BeginTime { get; set; }

    public bool IsMonitor { get; set; }
  }


  internal class DataPointEvent
  {
    public string Action { get; set; }
    public RecordGroupCollection Iterator { get; set; }
    public List<PlayerStats> Selected { get; } = [];
    public List<GroupEntry> SelectedGroups { get; } = [];
  }

  internal class TauntEvent
  {
    public TauntRecord Record { get; set; }
    public double BeginTime { get; set; }
  }


  internal class DeathEvent
  {
    public DeathRecord Record { get; set; }
    public double BeginTime { get; set; }
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


  internal class WhoRosterRecord
  {
    public long BeginTicks { get; set; }
    public Dictionary<string, int> Players { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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


  internal class DeathRecord : IAction
  {
    public string Killed { get; set; }
    public string Killer { get; set; }
    public string Message { get; set; }
    public string Previous { get; set; }
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


  internal class ReceivedSpell : IAction
  {
    /*
     * The answer for "this buff line named exactly one spell". Read-only by agreement: the only writer is
     * AddAmbiguity below, so no consumer can populate a buff that had nothing to be ambiguous about.
     */
    private static readonly List<SpellData> NoCandidates = [];

    public string Receiver { get; set; }
    public SpellData SpellData { get; set; }
    public bool IsWearOff { get; set; }

    /*
     * The spell rows this spell NAME stood for, when it stood for more than one. Null is the normal state and stays
     * null: an inline initializer charged one List object (plus its backing array the moment anything was added) to
     * every received-spell line. Measured on one capture's heap snapshot: 656,686 ReceivedSpell objects and 762,549
     * List<SpellData> — some 60 MB of list shells for a field that means "this name was ambiguous", the overwhelming
     * majority of them empty, because a buff line that matches one row of spells.txt has nothing to record.
     *
     * Two rules ride on this: readers ask HasAmbiguity before looking at the list (that is what the resolution path
     * already did), and a COPY of a received spell — a wear-off, the second sighting of the same buff — arrives with
     * no candidates at all, which is exactly what it was before; only the parse that resolved an ambiguous name
     * supplies them.
     */
    private List<SpellData> _ambiguity;

    /// <summary>The name resolved to more than one spell row. Ask this rather than counting a list.</summary>
    public bool HasAmbiguity => _ambiguity is { Count: > 0 };

    /// <summary>Candidates for an ambiguous name; the shared empty list when the name was not ambiguous.</summary>
    public List<SpellData> Ambiguity => _ambiguity ?? NoCandidates;

    /// <summary>The one writer. Nothing else may add to <see cref="Ambiguity"/>.</summary>
    internal void AddAmbiguity(List<SpellData> candidates)
    {
      if (candidates is not { Count: > 0 }) return;
      (_ambiguity ??= new List<SpellData>(candidates.Count)).AddRange(candidates);
    }

    /// <summary>How many rows the name stood for; 0 means one match and no list allocated.</summary>
    internal int AmbiguityCount => _ambiguity?.Count ?? 0;
  }


  internal class SpellCast : IAction
  {
    public string Spell { get; set; }
    public SpellData SpellData { get; set; }
    public string Caster { get; set; }
    public bool Interrupted { get; set; }
  }




  internal class PetMapping(string pet, string owner)
  {
    public string Owner { get; set; } = owner;
    public string Pet { get; set; } = pet;
  }


  internal class Defender
  {
    public string Name { get; set; }
    public double BeginTime { get; set; } = double.NaN;
    public bool Dead { get; set; }
    public List<DamageRecord> Records { get; init; } = [];
  }


}

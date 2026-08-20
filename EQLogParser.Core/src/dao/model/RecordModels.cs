using LiteDB;
using System.Collections.Generic;
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




  internal class DamageProcessedEvent
  {
    public DamageRecord Record { get; set; }
    public double BeginTime { get; set; }
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

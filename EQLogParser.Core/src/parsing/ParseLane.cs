using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The state of ONE line stream: the events that stream raises, and what its parsers carry from one line to the next
   * (PreviousAction, the crit pairs, the dice-roll follow-up). Carries are per-stream because a second ingest worker
   * feeding process-static state would poison the first capture's carries. One lane = one consumer thread, bound at
   * LogProcessor.LinkTo and released in its finally; an unbound thread is the Main lane.
   *
   * Deliberately NOT here: PlayerRegistry (identity memory is process-wide) and the interned name pools (per fact table).
   * Trigger readers feed TriggerProcessor's pipeline and never enter this lane.
   */
  internal sealed class ParseLane
  {
    /// <summary>The stream every static forwarder lands on, and what a thread that never bound a lane parses on.</summary>
    internal static readonly ParseLane Main = new();

    [ThreadStatic]
    private static ParseLane _current;

    internal ParseLane()
    {
    }

    /// <summary>Who this thread is parsing for right now. Unbound means Main.</summary>
    internal static ParseLane Current => _current ?? Main;

    /// <summary>Bind this thread to a lane (null = Main) and hand back what it replaced, so a nested bind can restore it.</summary>
    internal static ParseLane BindCurrentThread(ParseLane lane)
    {
      var prior = _current;
      _current = lane;
      return prior;
    }

    // ---- events of this stream ---------------------------------------------------------------
    // Plain fields, not `event`s: raise sites hoist the field into a local and skip the event object when nobody listens.
    // Mutate them only through Subscribe/Unsubscribe — two captures crossing must not silently drop a handler.

    internal Action<ResistEvent> Resist;
    internal Action<DamageProcessedEvent> DamageProcessed;
    internal Action<TauntEvent> NewTaunt;
    internal Action<DeathEvent> NewDeath;
    internal Action<HealProcessedEvent> HealProcessed;
    internal Action<string, double, byte> Evidence;             // PreLineParser: name, time, kind
    internal Action<string, string, double> WhoRoster;          // name, class, time
    internal Action<string, double> LooterIdentified;
    internal Action<string, double> CompanionCalled;            // the SUMMONER whose companion arrived
    internal Action<string, double, bool, string> Charm;        // name, time, is-start, owner
    internal Action<MezBreakRecord> MezBreak;
    internal Action<string, string, double> CastEvidence;       // player, spell name, time

    /// <summary>Atomic add: the raise path still reads one field, so this costs nothing per line.</summary>
    internal static void Subscribe<T>(ref T slot, T handler) where T : Delegate
    {
      var current = Volatile.Read(ref slot);
      while (Interlocked.CompareExchange(ref slot, (T)Delegate.Combine(current, handler), current) != current)
      {
        current = Volatile.Read(ref slot);
      }
    }

    /// <summary>Atomic remove of a handler this lane added.</summary>
    internal static void Unsubscribe<T>(ref T slot, T handler) where T : Delegate
    {
      var current = Volatile.Read(ref slot);
      while (Interlocked.CompareExchange(ref slot, (T)Delegate.Remove(current, handler), current) != current)
      {
        current = Volatile.Read(ref slot);
      }
    }

    // ---- what this stream carries from line to line ------------------------------------------

    /// <summary>The sentence before a death line (DeathRecord.Previous).</summary>
    internal string PreviousAction;
    internal readonly CharmCastHistory CharmCasts = new();

    /// <summary>A crit whose damage line arrives up to a second later rides here until it does.</summary>
    internal DamageLineParser.DelayRecord DelayCritRecord;

    /// <summary>Old-format crit pairing: the swing whose spell type the next line may name.</summary>
    internal DamageLineParser.OldCritData LastCrit;

    /*
     * Spell name → damage class, memoised from immutable spell data. One table per asking kind because an unplaced name's answer
     * IS the kind that asked; a shared table would need the kind in its key. Plain Dictionary is safe: only this lane's consumer
     * touches it.
     */
    internal Dictionary<string, string> SpellDdTypeCache = [];
    internal Dictionary<string, string> SpellDotTypeCache = [];

    /// <summary>Dice-roll follow-up: the roller named by the previous line and its line number (the follow-up must be next).</summary>
    internal string RandomPlayer;

    internal long LastRolledLine = -1;
  }
}

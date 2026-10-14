using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The state of ONE line stream.
   *
   * Every parse event the pipeline raises, and every fact a parser carries from one line to the next
   * (`PreviousAction` for a death's last sentence, the delayed-crit pair, the old-format crit pairing, the dice-roll
   * follow-up line), describes a sequence of lines that arrived in order. That state used to be process-static —
   * exactly right while one reader thread fed one capture, and the wall for anything else: a second ingest worker
   * would fan its lines into the first worker's capture and poison its carry.
   *
   * A lane makes the stream the unit of ownership. One lane = one consumer thread bound at its door
   * (`LogProcessor.LinkTo` binds, unbinds in its finally) + the events that stream raises + what it carries across
   * lines. `Main` is the historical single stream: every existing subscriber (FctManager, DamageRibbon, the test
   * suite) reaches Main through the parsers' static forwarders, so the app running the way it always ran — one
   * reader, one capture — behaves bit-identically, and an unbound thread IS the Main lane.
   *
   * The production combat processor binds Main for its bulk read and monitor tail. Trigger readers instead feed
   * TriggerProcessor's separate pipeline and never enter this lane. A lane instance lets an isolated consumer bind
   * without raising into the active capture's tables. Keeping that door explicit is the point: otherwise a raise from
   * any stream reaches every subscriber in the process. What is deliberately NOT here: `PlayerRegistry` (identity
   * memory is process-wide by design — its verdicts are shared, and its events reach whichever captures subscribed)
   * and the interned name pools (per fact table).
   */
  internal sealed class ParseLane
  {
    // The one stream the app has always had: every static forwarder below lands here, and every thread
    // that never bound a lane parses on it.
    internal static readonly ParseLane Main = new();

    [ThreadStatic]
    private static ParseLane _current;

    internal ParseLane()
    {
    }

    // Who this thread is parsing for right now. Unbound means Main, which is what "the normal pipeline" means.
    internal static ParseLane Current => _current ?? Main;

    internal static void BindCurrentThread(ParseLane lane) => _current = lane;

    // ---- events of this stream ---------------------------------------------------------------
    // Plain delegate fields, not `event` declarations: the hot raise sites hoist the field into a local and skip
    // building the event object when nobody is listening (the pattern DamageLineParser/HealingLineParser already
    // used for exactly that reason), and subscribe/unsubscribe happens only at a capture's Start/Stop boundary.

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

    // ---- what this stream carries from line to line ------------------------------------------

    // The sentence before a death line (DeathRecord.Previous).
    internal string PreviousAction;

    // A crit whose damage line arrives up to a second later rides here until it does.
    internal DamageLineParser.DelayRecord DelayCritRecord;

    // Old-format crit pairing: the swing whose spell type the next line may name.
    internal DamageLineParser.OldCritData LastCrit;

    // Spell name -> damage class, memoised from the immutable spell data: ONE dictionary per kind of line that asks, because for a name the
    // spell data cannot place, the answer IS the asking kind (a DoT line gets "DoT", a direct-damage line gets "Dd"). One shared table would
    // have to encode the kind in its key — which is what this replaced: every lookup built "Dd=" + name (or "DoT=" + name) as an allocation,
    // on hits included, to say which of these two tables it meant. Two lookups, no string.
    //
    // A plain Dictionary is safe here because exactly one thread — this lane's consumer — ever touches it; sharing one process-wide would need a
    // concurrent map and pay locking on every lookup for a cache that costs nothing to duplicate per stream.
    internal Dictionary<string, string> SpellDdTypeCache = [];
    internal Dictionary<string, string> SpellDotTypeCache = [];

    // Dice-roll follow-up: the roller named by the previous line, and its line number (the follow-up must be next).
    internal string RandomPlayer;
    internal long LastRolledLine = -1;
  }
}

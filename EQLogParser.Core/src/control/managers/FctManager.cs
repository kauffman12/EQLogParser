using System.Collections.Concurrent;
using System.Threading;

namespace EQLogParser
{
  /*
   * Feeds floating combat text from parsed records. Both parser events fire during historical
   * replay (opening a log file) and live monitoring alike, but every event carries IsMonitor
   * (threaded from LogReader's line flag via LineData), so replay records are simply dropped.
   *
   * Events arrive on the log reader thread. Records go on an unbounded-cost-free queue rather than
   * into a per-record dispatcher post; the overlay drains it once per rendered frame, which batches
   * naturally on the frame clock and drops whatever queued up behind a stalled UI instead of
   * replaying a burst seconds after it happened. Enabled gates all work so an overlay nobody opened
   * costs nothing in the parse loop. See docs/DesignNotes.md → Floating Combat Text.
   */
  internal class FctManager : IDisposable
  {
    // ceiling on queued-but-undrawn commands: a frozen UI loses numbers rather than memory
    internal const int MaxPending = 512;

    /*
     * Commands older than this are dropped at drain time (the burst is over, the text would be stale). A
     * field rather than a const so unit tests can force the stale path without sleeping.
     */
    internal long MaxQueueAgeMs = 500;

    // singleton with set for unit test, like FightManager
    internal static FctManager Instance { get; set; } = new();

    /*
     * Replaces the singleton and subscribes the new instance to the parsers. Each FctOverlayWindow owns a
     * manager and disposes it on close; without this a disposed instance would linger in Instance with its
     * parser handlers detached, and the next overlay would silently get no feed.
     */
    internal static FctManager Create() => Instance = new FctManager();

    private readonly ConcurrentQueue<FctHitCommand> _pending = [];
    private int _dropped;

    /* Set by the overlay while it is visible. Reader thread reads it on every record, so it is a
     * volatile field rather than a property. */
    internal volatile bool Enabled;

    internal int DroppedCount => Volatile.Read(ref _dropped);
    internal int PendingCount => _pending.Count;

    private FctManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamage;
      HealingLineParser.EventsHealProcessed += HandleHeal;
      MiscLineParser.EventsResistProcessed += HandleResist;
    }

    /* Drops the pending feed and unsubscribes from the parsers, so a replaced Instance does not stay
     * live behind the new one. Unit tests dispose the singleton they swapped out. */
    public void Dispose()
    {
      Enabled = false;
      DamageLineParser.EventsDamageProcessed -= HandleDamage;
      HealingLineParser.EventsHealProcessed -= HandleHeal;
      MiscLineParser.EventsResistProcessed -= HandleResist;
      Clear();
    }

    /* Called when the overlay goes away: never show yesterday's fight when it comes back. */
    internal void Clear()
    {
      while (_pending.TryDequeue(out _))
      {
        // drain only
      }
    }

    /* Pops everything queued, dropping commands older than MaxQueueAgeMs. Returns the count kept. */
    internal int DrainTo(List<FctHitCommand> destination)
    {
      var now = Environment.TickCount64;
      while (_pending.TryDequeue(out var command))
      {
        if (now - command.EnqueueTick > MaxQueueAgeMs)
        {
          Interlocked.Increment(ref _dropped);
          continue;
        }

        destination.Add(command);
      }

      return destination.Count;
    }

    /* Internal so unit tests can drive the feed without parsing log lines. */
    internal void HandleDamage(DamageProcessedEvent e)
    {
      if (!Enabled || e.Record is null || !e.IsMonitor)
      {
        return;
      }

      var record = e.Record;
      var iAmAttacker = record.Attacker == ConfigUtil.PlayerName ||
                         PlayerRegistry.Instance.GetPlayerFromPet(record.Attacker) == ConfigUtil.PlayerName;
      var iAmDefender = record.Defender == ConfigUtil.PlayerName ||
                        PlayerRegistry.Instance.GetPlayerFromPet(record.Defender) == ConfigUtil.PlayerName;

      // FCT covers my character's fight only; anything else is noise
      if (!iAmAttacker && !iAmDefender)
      {
        return;
      }

      // evades arrive as zero-damage records carrying a label in Type (see DamageLineParser)
      if (record.Total == 0 && IsDefensiveLabel(record.Type))
      {
        Enqueue(new FctHitCommand
        {
          Lane = iAmDefender ? FctLane.Defensive : FctLane.Missed,
          ValueText = record.Type,
          Source = DisplaySource(record),
        });
        return;
      }

      if (record.Total == 0)
      {
        return; // nothing numeric to show
      }

      Enqueue(new FctHitCommand
      {
        Lane = iAmAttacker ? FctLane.DamageDealt : FctLane.DamageTaken,
        Crit = LineModifiersParser.IsCrit(record.ModifiersMask),
        Periodic = record.Type == Labels.Dot,
        // the parser labels a proc by looking the spell up in procs.txt, so this is not a guess at wording
        Proc = record.Type == Labels.Proc,
        // Total is the amount actually dealt (damage records never carry OverTotal today)
        Value = record.Total,
        Source = DisplaySource(record),
        // the glyph'd events: mask flags, plus Decapitation whose only tell is the spell name in SubType
        Special = LineModifiersParser.SpecialFor(record.ModifiersMask, record.SubType),
      });
    }

    internal void HandleHeal(HealProcessedEvent e)
    {
      if (!Enabled || e.Record is null || !e.IsMonitor)
      {
        return;
      }

      var healedMe = e.Record.Healed == ConfigUtil.PlayerName ||
                     PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Healed) == ConfigUtil.PlayerName;
      var dealtByMe = e.Record.Healer == ConfigUtil.PlayerName ||
                      PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Healer) == ConfigUtil.PlayerName;

      if (!healedMe && !dealtByMe)
      {
        return; // party-wide healing lands with the group config
      }

      // EQ heal lines read "for 9409 (11000)": Total is the effective amount, OverTotal the gross
      // (it already includes Total when present) — show effective and drop zero-effective overheals
      if (e.Record.Total == 0)
      {
        return;
      }

      Enqueue(new FctHitCommand
      {
        // a self-heal reads as healing on me
        Lane = healedMe ? FctLane.HealingReceived : FctLane.HealingDealt,
        Crit = LineModifiersParser.IsCrit(e.Record.ModifiersMask),
        Periodic = e.Record.Type == Labels.Hot,
        Value = e.Record.Total,
        Source = string.IsNullOrEmpty(e.Record.SubType) ? null : e.Record.SubType,
      });
    }

    /*
     * A resist is a punch that was blocked, wearing a spell: same one-word label, same lane rule — my spell failed
     * goes to Missed (outgoing), I resisted theirs goes to Defensive. MiscLineParser already recognised and stored
     * the line ("Restless Tijoely resisted your Stormjolt Vortex Effect!"); this turns it into the label command,
     * because until now the event had no listener and FCT showed nothing at all for a resisted cast.
     *
     * The parser's "your" branch leaves "pet's " glued to the spell when the line was "resisted your pet's X" —
     * stripped here so the source line reads as the spell, not the grammar around it. FCT covers my character only,
     * like the damage feed: a party mate's resisted cast is noise.
     */
    internal void HandleResist(ResistEvent e)
    {
      if (!Enabled || e.Record is null || !e.IsMonitor)
      {
        return;
      }

      var iAmCaster = e.Record.Attacker == ConfigUtil.PlayerName ||
                      PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Attacker) == ConfigUtil.PlayerName;
      var iAmResister = e.Record.Defender == ConfigUtil.PlayerName ||
                        e.Record.Defender == "You" || // a line worded with the literal "You" still means me
                        PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Defender) == ConfigUtil.PlayerName;

      if (!iAmCaster && !iAmResister)
      {
        return;
      }

      var spell = e.Record.Spell;
      if (spell is not null && spell.StartsWith("pet's ", StringComparison.Ordinal))
      {
        spell = spell[6..];
      }

      Enqueue(new FctHitCommand
      {
        Lane = iAmCaster ? FctLane.Missed : FctLane.Defensive,
        ValueText = Labels.Resist,
        Source = spell,
      });
    }

    /* The labels DamageLineParser assigns to zero-damage evade lines. */
    private static bool IsDefensiveLabel(string type) =>
      type is Labels.Miss or Labels.Dodge or Labels.Block or Labels.Parry or Labels.Riposte or Labels.Absorb or Labels.Invulnerable;

    /*
     * Melee records carry the attack verb in SubType ("Crushes"), and so do the zero-damage evade lines: their verb
     * comes from "X tries to crush Y, but ..." even though Type holds the label instead of Labels.Melee. Everything
     * else carries a spell name, which is a proper noun and must never be conjugated — Crown of Stars is not Crown of
     * Star. Without the evade clause the same swing reads "Crush" under a damage number and "Crushes" under MISS or
     * DODGE, and that inconsistency costs more here than anywhere else in the app because overlay text is read at a
     * glance, by someone who is not looking for grammar.
     */
    private static string DisplaySource(DamageRecord record) =>
      string.IsNullOrEmpty(record.SubType) ? null
        : record.Type == Labels.Melee || IsDefensiveLabel(record.Type) ? SingularizeVerb(record.SubType)
        : record.SubType;

    /* Display-only polish: melee lines capture the third-person verb ("bites", "crushes") and FCT
     * reads better with the base form. Only ever applied to Labels.Melee SubTypes. */
    private static string SingularizeVerb(string verb)
    {
      if (string.IsNullOrEmpty(verb) || !verb.EndsWith('s') || verb.EndsWith("ss", StringComparison.Ordinal))
      {
        return verb;
      }

      var lower = verb.ToLowerInvariant();
      var dropsEs = lower.EndsWith("ses", StringComparison.Ordinal) || lower.EndsWith("xes", StringComparison.Ordinal) ||
                    lower.EndsWith("zes", StringComparison.Ordinal) || lower.EndsWith("ches", StringComparison.Ordinal) ||
                    lower.EndsWith("shes", StringComparison.Ordinal);
      return dropsEs ? verb[..^2] : verb[..^1];
    }

    private void Enqueue(FctHitCommand command)
    {
      // a consumer that stopped draining must not turn into unbounded memory: drop the oldest first
      while (_pending.Count >= MaxPending && _pending.TryDequeue(out _))
      {
        Interlocked.Increment(ref _dropped);
      }

      command.EnqueueTick = Environment.TickCount64;
      _pending.Enqueue(command);
    }
  }
}
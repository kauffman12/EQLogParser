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
     *
     * The instance being replaced is disposed here rather than left to its window: a replaced manager is by
     * definition nobody's manager any more, and it stays subscribed to every parser until something lets go,
     * which is a feed that keeps queueing for a window that will never drain it. Dispose is idempotent, so a
     * window that already disposed its own manager costs nothing extra.
     */
    internal static FctManager Create()
    {
      Instance?.Dispose();
      return Instance = new FctManager();
    }

    private readonly ConcurrentQueue<FctHitCommand> _pending = [];
    private int _dropped;

    /*
     * How many commands are waiting. Counted by hand because ConcurrentQueue<T>.Count is O(n) — it walks every
     * segment — and Enqueue asks on the log thread for every record: asking it directly turns a raid pull into
     * quadratic work on exactly the path that has to stay cheap. Approximate under a concurrent drain, which is
     * fine — it is a backpressure ceiling, not a number anyone reads.
     */
    private int _pendingCount;

    /* Set by the overlay while it is visible. Reader thread reads it on every record, so it is a
     * volatile field rather than a property. */
    internal volatile bool Enabled;

    internal int DroppedCount => Volatile.Read(ref _dropped);

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

      Interlocked.Exchange(ref _pendingCount, 0);
    }

    /* Pops everything queued, dropping commands older than MaxQueueAgeMs. Returns the count kept. */
    internal int DrainTo(List<FctHitCommand> destination)
    {
      var now = Environment.TickCount64;
      while (_pending.TryDequeue(out var command))
      {
        Interlocked.Decrement(ref _pendingCount);
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

      /* Which of my pets, if either, is on this line: the pet's own numbers get rows of their own (FctRow), so "counts as
         me" and "is my pet" have to stay separate questions. Damage landing ON the pet is still just damage taken — there
         is no pet row for that, because "who was hit" is not something a player filters. */
      var petAttacker = PlayerRegistry.Instance.GetPlayerFromPet(record.Attacker) == ConfigUtil.PlayerName;
      var iAmAttacker = record.Attacker == ConfigUtil.PlayerName || petAttacker;
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
          Row = FctRow.Word, // the words answer to their own switches, never to a row (FctIngest.WordShown)
          ValueText = record.Type,
          Source = DisplaySource(record),
        });
        return;
      }

      if (record.Total == 0)
      {
        return; // nothing numeric to show
      }

      var crit = LineModifiersParser.IsCrit(record.ModifiersMask);

      // the parser labels a proc by looking the spell up in procs.txt, so this is not a guess at wording
      var proc = record.Type == Labels.Proc;

      Enqueue(new FctHitCommand
      {
        Lane = iAmAttacker ? FctLane.DamageDealt : FctLane.DamageTaken,
        Row = DamageRow(proc, petAttacker, record.Type == Labels.Melee, crit),
        Crit = crit,
        Periodic = record.Type == Labels.Dot,
        Proc = proc,
        // Total is the amount actually dealt (damage records never carry OverTotal today)
        Value = record.Total,
        Source = DisplaySource(record),
        // the glyph'd events: mask flags, plus Decapitation whose only tell is the spell name in SubType
        Special = LineModifiersParser.SpecialFor(record.ModifiersMask, record.SubType),
      });
    }

    /*
     * Healing means healing ON me — that is the whole rule, and it drops two things at once: the numbers from heals I cast on
     * other people (the overlay is a picture of what is happening to you, and your own outgoing heals are spam to the one
     * player who already knows they worked) and heals landing on the pet, which are not mine to count either. A self-heal still
     * shows, because its line names me as the healed once the parser has had it.
     *
     * Pronouns are not handled here on purpose: HealingLineParser runs every name through ParserUtil.ReplacePlayer, which is what
     * turns "You have been healed over time for 1063 hit points by Roar of the Lion" into a record carrying my character name. A
     * second pronoun list down here would be a second place to be wrong about who "you" is.
     */
    internal void HandleHeal(HealProcessedEvent e)
    {
      if (!Enabled || e.Record is null || !e.IsMonitor)
      {
        return;
      }

      var record = e.Record;
      if (record.Healed != ConfigUtil.PlayerName)
      {
        return; // cast on somebody else, or on the pet: not my number
      }

      // EQ heal lines read "for 9409 (11000)": Total is the effective amount, OverTotal the gross
      // (it already includes Total when present) — show effective and drop zero-effective overheals
      if (record.Total == 0)
      {
        return;
      }

      var crit = LineModifiersParser.IsCrit(record.ModifiersMask);

      Enqueue(new FctHitCommand
      {
        Lane = FctLane.HealingReceived,
        Row = crit ? FctRow.HealingCrits : FctRow.Healing,
        Crit = crit,
        Periodic = record.Type == Labels.Hot,
        Value = record.Total,
        Source = string.IsNullOrEmpty(record.SubType) ? null : record.SubType,
      });
    }

    /*
     * The show-list row for a damage record, and the ordering is the design (FctRow): a proc is its own event so it outranks
     * both who fired it and what fired it; then the pet, whose numbers a player wants apart from their own whether it swings
     * or casts; then kind, and crit inside each kind. Melee means Labels.Melee — everything else the parser can hand here
     * (Direct Damage, Bane, Damage Shield, Reverse DS, Other Damage, DoT ticks) is "a spell" to the person reading the
     * overlay, which is also what they call it.
     *
     * Totality is the property that matters: one of the four bottom rows answers for every non-proc, non-pet record, and
     * FctManagerTest walks the whole combination table so a new Labels kind cannot arrive unassigned and default to being
     * ungated. Note this never returns Word: a record with no number took the label branch above.
     */
    internal static FctRow DamageRow(bool proc, bool pet, bool melee, bool crit)
    {
      if (proc)
      {
        return FctRow.Procs;
      }

      if (pet)
      {
        return melee ? FctRow.PetMelee : FctRow.PetSpells;
      }

      if (crit)
      {
        return melee ? FctRow.MeleeCrits : FctRow.SpellCrits;
      }

      return melee ? FctRow.MeleeHits : FctRow.SpellHits;
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
        Row = FctRow.Word, // a word, so its switch is chosen by its text (FctIngest.WordShown), not by a row
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
      command.EnqueueTick = Environment.TickCount64;
      _pending.Enqueue(command);

      /* A consumer that stopped draining must not turn into unbounded memory, so the ceiling is enforced after
       * the write with a front-throw of the oldest: the queue holds at most MaxPending commands, and every one
       * that goes is counted where the player can see it. */
      var queued = Interlocked.Increment(ref _pendingCount);
      while (queued > MaxPending)
      {
        if (!_pending.TryDequeue(out _))
        {
          break; // a drain raced ahead of us and took what was queued; nothing left to drop
        }

        Interlocked.Increment(ref _dropped);
        queued = Interlocked.Decrement(ref _pendingCount);
      }
    }
  }
}
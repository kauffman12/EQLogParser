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
    /*
     * WHICH CONTENT this event's iterator walks: the sequence number of the stats build that produced it (StatsBuildTrace gives
     * every build a unique, increasing number). A chart that has already aggregated generation N can reuse those aggregates when
     * another UPDATE arrives carrying N again — which is not hypothetical: a summary pane fires "UPDATE" on its own interactions
     * (selecting rows, changing a view option), and each such fire re-walked the whole night. Measured on the field run: two
     * `DamageChart UPDATE` passes over 4,660,915 records producing identical output (5 lines, 36,163 points), 1,589 ms and
     * 1,107 ms, with no stats build between them.
     *
     * -1 means "unstamped": the content is not tied to a known build and must be walked. Builders stamp at build time, so a build
     * always invalidates; only a fire BETWEEN builds can reuse. That asymmetry is the safety of the whole scheme.
     */
    public long DataGeneration { get; set; } = -1;

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
      _ambiguity = SharedCandidates.For(candidates);
    }

    /*
     * One shared, never-again-written candidate list per DISTINCT set of spell rows.
     *
     * Why this exists: the caller hands over a list it just built — CastLineParser walks the LandsOn tree and gets a fresh, receiver-filtered
     * List<SpellData> for every buff line whose name stands for several ranks — and storing a copy of that per line meant one list (plus its
     * backing array) per ambiguous sight in a raid log, all of them holding the same handful of rows. Measured on a field snapshot as the
     * largest object-count growth during live play: List<SpellData> climbing with ReceivedSpell beside it. The previous fix here removed the
     * list that unambiguous lines were paying; this one removes the copies ambiguous lines were paying.
     *
     * Three rules make sharing safe rather than a bug waiting for a writer:
     *
     *   1. **Keyed by identity of elements, not by spell name.** The candidate SET is what must match, and it can legitimately differ for one
     *      name depending on which tree path produced it (who the effect landed on). Keying on a name could hand a receiver candidates they
     *      were never offered, and the failure would be a rank resolved from the wrong row with every board still looking correct.
     *   2. **A collision is answered privately, never by sharing.** On a key hit the stored list is verified element-by-element (reference
     *      identity of the same database rows); on any mismatch we return our own copy and do not publish it. Being wrong here points only
     *      away from sharing, which costs bytes and nothing else.
     *   3. **The value is copied once and treated as frozen** — the caller's list is a transient the tree may hand out again, so we never keep
     *      it. Nothing in this codebase writes to Ambiguity (readers ask Count / FindAll / FirstOrDefault / foreach), and that law is what lets
     *      a thousand sights share one instance; a future writer would have to stop sharing, not join it.
     *
     * The ceiling is about the number of distinct ambiguous names in a capture (a few hundred at most), so the memo cannot become the thing
     * it removes: past SharedSetCeiling we stop publishing and go back to one private copy per line, which is exactly yesterday's behaviour.
     */
    internal static class SharedCandidates
    {
      // Widest candidate set seen is a handful of ranks; 8 slots covers a name with every rank in the book.
      private const int SharedSetCeiling = 4096;

      private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, List<SpellData>> Sets = new();

      // Distinct candidate sets published — for the heap ledger, and to see whether the ceiling is ever near.
      internal static int SharedSetCount => Sets.Count;

      internal static List<SpellData> For(List<SpellData> candidates)
      {
        var key = KeyOf(candidates);

        if (Sets.TryGetValue(key, out var shared))
        {
          return SameContent(shared, candidates) ? shared : Copy(candidates);
        }

        if (Sets.Count >= SharedSetCeiling)
        {
          return Copy(candidates);
        }

        var fresh = Copy(candidates);
        return Sets.GetOrAdd(key, fresh);
      }

      private static List<SpellData> Copy(List<SpellData> candidates) => new List<SpellData>(candidates);

      // FNV-1a over the reference identities of the rows: no allocation, and stable for the life of the process.
      private static ulong KeyOf(List<SpellData> candidates)
      {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < candidates.Count; i++)
        {
          hash = (hash ^ (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(candidates[i])) * 1099511628211UL;
          hash = (hash ^ (ulong)(uint)i) * 1099511628211UL;
        }

        return (hash ^ (ulong)(uint)candidates.Count) * 1099511628211UL;
      }

      private static bool SameContent(List<SpellData> shared, List<SpellData> candidates)
      {
        if (shared.Count != candidates.Count) return false;
        for (var i = 0; i < shared.Count; i++)
        {
          if (!ReferenceEquals(shared[i], candidates[i])) return false;
        }

        return true;
      }
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

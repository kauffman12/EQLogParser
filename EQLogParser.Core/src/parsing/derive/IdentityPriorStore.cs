using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in optional
 * strings/kinds because a name legitimately has no class, no owner and no verdict. Stating that is not the same as
 * switching on warnings across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * What previous captures on THIS server concluded about a name, kept so a log that says little about a name does
   * not have to pretend it never met one. A prior is a display fallback and nothing more in this version: the census
   * uses it when the current capture's rules reached no verdict, and the derive that fills fight rows never reads it.
   *
   * WHY IT STAYS OUT OF CLASSIFICATION (for now). R7 builds sides out of what the timeline already knows - it reads
   * every defender's kind to decide an attacker's. Feed it yesterday's conclusions and today's inference is arguing
   * with itself: a guess that survives only because it was written down, repeated until it looks like evidence. That
   * is the same failure this project already documented for players.txt (names collected 2012-2026 read as "people",
   * ClassificationRules.cs:330-337). Moving priors INTO the derive is a real option, but it has to arrive with the
   * derived-vs-legacy parity tests in hand, not as a convenience.
   *
   * Three rules keep the file honest:
   *
   * - ONLY WHAT A LATER LOG MIGHT NOT ANSWER AGAIN IS RECORDED by Record. An entry means "some rule read this name off
   * an EVENT in a real log" - a target frame, a /who roster, guild speech, a charm line, the graph - and the rule code
   * is stored with it. A verdict whose input the app owns forever is not memory, it is a restatement; see
   * WorthRemembering for that vocabulary. Operator verdicts live in identity-overrides.txt (Manual) and pet mappings in
   * petmapping.txt: copying any of those in here would launder an assertion into statistics.
   *
   * THE SECOND LANE: THE ROSTER BIT. The rule above forbids Record from writing membership, and it still does - what
   * changed is that MEMBERSHIP moved here, because players.txt could not carry the one fact this file already models:
   * the class a learned name was seen casting (see RememberRoster). So a row is now two statements that share a name
   * and nothing else:
   *
   *   Kind/Reason/SeenAtS/Sightings  what a rule read off an event, on the LOG's clock, aged against this file's newest
   *                                  sighting ("IT EXPIRES" below).
   *   Ours/Class                     what this application called the name, aged on the WALL clock at
   *                                  PlayerRegistry.StaleDays like players.txt and petmapping.txt always were.
   *
   * Both stamps are dotnet-epoch seconds - the same number players.txt rows carry - so one field (SeenAtS) serves both
   * lanes; what differs is WHICH clock retires it, and a roster row is never retired by the rule lane's "90 days behind
   * the newest sighting". A roster row with no time (SeenAtS <= 0) is a statement, not an observation, and outlives
   * every dial.
   *
   * - AGREEMENT IS IDEMPOTENT PER CAPTURE. Derivation re-runs whenever a filter or an override changes, so a
   * counter bumped per pass would report "41 captures agreed" for one evening re-derived 41 times. Sighting time is
   * the LOG's last event, not the clock, and the count advances only on a strictly newer capture.
   *
   * - IT EXPIRES. Pruned against the newest entry in the file (not the wall clock, so replaying old backups does
   * not nuke the ledger) plus a size cap. Seasons change and mob names get reused by players; an old confident NPC
   * row must be able to die of age rather than needing someone to notice it.
   */
  internal sealed class IdentityPriorStore
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    public static IdentityPriorStore Instance { get; } = new();

    /*
     * One row, two lanes (see the header). Kind/Reason/SeenAtS/Sightings are what a rule read off an event; Ours/Class
     * are what this application called the name and when it last saw it. A name can hold both, either or - before this
     * file knew about the roster - only the first four, which is why the loader accepts a four-field row.
     *
     * Sightings counts CAPTURES that agreed on Kind+Reason, so a row that exists only because the roster named it has 0:
     * nobody witnessed anything, and "remembered" must not read as "confirmed once".
     */
    /*
     * Owner is the PET lane: "this name's master", the statement petmapping.txt used to hold. It is null for everything
     * that is not a pet, and — like `Ours` — it is NOT a verdict: an owner text says nothing about whether the name is a
     * player or a mob, so a row can carry one with Kind still Unknown. Both lanes are membership-shaped, which is why both
     * bypass the verdict allowlist on load and both age on PlayerRegistry.StaleDays rather than on the rule lane's clock.
     */
    internal readonly record struct Prior(IdentityKind Kind, string Reason, long SeenAtS, int Sightings, bool Ours, string? Class, string? Owner = null);

    // Staleness is measured against the newest sighting in the file, so a player rebuilding last season's logs keeps
    // what they had; the cap is a memory guard for a name-heavy server played for years. Neither reaches a roster row:
    // membership ages on PlayerRegistry.StaleDays against the wall clock (PruneRosterLocked).
    private const long StaleS = 90L * 24 * 60 * 60;
    private const int MaxEntries = 25_000;

    /*
     * The provenance word of a row that exists because the ROSTER named it. Deliberately absent from RememberedRules:
     * Record can neither produce it nor upgrade it, and IdentityVocabulary gives it the one tooltip that is true for it
     * ("carried over from the roster this app saved") - never a filename, never a rule code.
     */
    internal const string RosterReason = "Imported";

    private readonly object _gate = new();
    private readonly Dictionary<string, Prior> _byName = new(StringComparer.OrdinalIgnoreCase);
    private string _serverName = string.Empty;

    /// <summary>Loads the ledger for the current ConfigUtil.ServerName.</summary>
    public void Init() => Init(ConfigUtil.ServerName);

    /*
     * Which server's ledger is loaded right now. A caller that wants to WRITE into it from the outside (the roster
     * importer) asks this first, because Save() files rows under whatever name this field holds: writing "server B's"
     * names while this object still answers to server A is how one folder quietly acquires another's roster.
     */
    internal string ServerName => _serverName;

    /*
     * One file write for a caller that applied many rows through the persist:false overloads. Without it an importer of
     * a thousand names rewrites the whole ledger a thousand times; with it the batch is atomic in the only sense this
     * file can offer, since the rows are already in memory either way.
     */
    internal void FlushChanges() => Save();

    /*
     * Counts how many times this store has been (re)loaded - one bump per log open, and per test fixture. It exists so a caller that
     * asks a LOAD-SCOPED question once ("did this folder's legacy files already move in?") can tell a second ask inside the same load
     * from a fresh load of the same folder: the answer must be stable while an open is in progress, because the roster import writes
     * the very ledger whose existence the pet-map import is about to check. See LegacyPlayerImport.ClaimMigration.
     */
    internal long LoadGeneration { get; private set; }

    public void Init(string serverName)
    {
      LoadGeneration++;
      var loaded = new Dictionary<string, Prior>(StringComparer.OrdinalIgnoreCase);
      var dropped = 0;                       // rows the gate below refused, see the log line at the end
      var roster = 0;                        // rows carrying the roster bit, whatever else they hold
      var owners = 0;                        // rows carrying a pet's owner, whatever else they hold
      _serverName = serverName ?? string.Empty;

      if (!string.IsNullOrEmpty(_serverName))
      {
        foreach (var (name, value) in ConfigUtil.ReadIdentityPriors(_serverName))
        {
          /*
           * Name=Kind|Reason|SeenAtS|Sightings[|Ours[|Class[|Owner]]]. A four-field row is the shape written before the
           * roster lane existed and loads unchanged; anything SHORTER or longer is dropped rather than repaired, because a
           * half-read row would put a name on the list with a verdict nobody wrote. The tail fields were appended in the
           * order the lanes arrived, and each is optional, so a file written by any older build still parses.
           */
          var parts = value.Split('|');
          if (parts.Length is < 4 or > 7 || string.IsNullOrEmpty(name)) continue;
          if (!Enum.TryParse<IdentityKind>(parts[0], out var kind)) continue;
          if (!long.TryParse(parts[2], out var seenAt) || !int.TryParse(parts[3], out var sightings)) continue;

          /*
           * The roster bit bypasses the allowlist below, and it may carry Kind.Unknown. That is not a hole in the gate:
           * what the gate refuses is a VERDICT restated from a file the app always has, and this bit is not a verdict -
           * it is the membership players.txt used to hold, which now has nowhere else to live because the class lives
           * beside it. It buys a name no authority in the rules: IdentityLookup reads it as "the app's own list", and a
           * capture's evidence outvotes it the moment the capture says anything.
           */
          var ours = parts.Length >= 5 && bool.TryParse(parts[4], out var flag) && flag;
          var className = parts.Length >= 6 && parts[5].Length > 0 ? parts[5] : null;
          var owner = parts.Length == 7 && parts[6].Length > 0 ? parts[6] : null;

          // Files written before this gate existed carry restated-database rows ("Name=Npc|R6-npcdb|..."). They are not
          // read back, and the load writes what is left so the noise leaves the FILE instead of being quietly dropped
          // again every start: whoever opens this file should find only entries worth arguing about.
          //
          /*
           * One allowlist decides what a VERDICT needs to survive a load, and that is deliberate - no by-name special
           * cases next to it. The
           * concrete reason the gate must stay an ALLOWLIST: "R5-called" (a build that read "X is called to it owner." the
           * wrong way round stamped its subject Pet Certain, and the subject is the SUMMONER - MiscLineParser's census) is
           * simply absent from RememberedRules now, so a ledger written by that build loses those rows here and Save()
           * rewrites them out. Had the retired code been refused by name instead, the next retired rule would have arrived
           * with a second list to forget.
           */
          /*
           * Both memory lanes bypass the verdict allowlist for the same reason given above: membership and ownership are
           * not verdicts, and the gate exists only to stop this file restating answers it always has (npcs.txt, spell
           * data). An owner-only row is what a petmapping.txt import looks like.
           */
          if (ours) roster++;
          else if (owner is not null) owners++;
          else if (kind == IdentityKind.Unknown || !WorthRemembering(parts[1])) { dropped++; continue; }

          // Sightings keeps its own value rather than being floored at 1: a roster-only row legitimately has none, and
          // rounding it up would advertise one witnessed capture for a name nothing was ever read off.
          loaded[name] = new Prior(kind, parts[1] ?? string.Empty, seenAt, Math.Max(0, sightings), ours, className, owner);
        }
      }

      bool aged;
      lock (_gate)
      {
        _byName.Clear();
        foreach (var (name, prior) in loaded) _byName[name] = prior;

        // Membership ages while nobody is looking too: an operator who stopped playing this server in the spring should
        // not find its names still treated as raid members, and no rule pass has to run for that to be noticed.
        aged = PruneRosterLocked();
      }

      // One line per log opened, because "did the memory load?" is otherwise unanswerable from a player's log file:
      // the census just quietly shows more names. The dropped count says what this rewrite took out, so an operator who
      // hand-edited the file can see whether their edit was read or refused.
      if (loaded.Count > 0 || dropped > 0)
        Log.Info($"Identity priors for {_serverName}: {loaded.Count} remembered verdicts"
                 + (roster > 0 ? $", {roster} carried over from the roster" : string.Empty)
                 + (owners > 0 ? $", {owners} carrying a pet owner" : string.Empty)
                 + (dropped > 0
                    ? $", {dropped} rows refused (restated database answers, or companion claims from a build that "
                      + "read the line backwards) and rewritten out of the file"
                    : string.Empty));

      if (dropped > 0 || aged) Save();
    }

    public int Count
    {
      get { lock (_gate) return _byName.Count; }
    }

    /// <summary>True with the remembered verdict for a name. Names this server has never shown are absent.</summary>
    public bool TryGet(string name, out Prior prior)
    {
      if (string.IsNullOrEmpty(name)) { prior = default; return false; }
      lock (_gate) return _byName.TryGetValue(name, out prior);
    }

    /// <summary>Snapshot for display, sorted by name.</summary>
    public List<KeyValuePair<string, Prior>> All()
    {
      lock (_gate)
      {
        var list = new List<KeyValuePair<string, Prior>>(_byName);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
      }
    }

    /// <summary>Forget one name ("clear prior" in the UI). Never touches the other identity files.</summary>
    public void Remove(string name)
    {
      if (string.IsNullOrEmpty(name)) return;
      bool removed;
      lock (_gate) removed = _byName.Remove(name);
      if (removed) Save();
    }

    /*
     * Fold one finished capture into the ledger. Called AFTER the rules pass, with the names that capture mentioned
     * and the engine's own last-event time; every argument may be empty and simply records nothing.
     *
     * captureEndS is the log's clock rather than DateTimeOffset.UtcNow for the idempotency rule above: re-deriving
     * the same file reports the same time, so agreement cannot inflate, and two captures of the same evening taken an
     * hour apart still count as the sighting they were.
     */
    public void Record(EntityTimeline? timeline, IReadOnlyList<string>? names, long captureEndS)
    {
      if (timeline is null || names is not { Count: > 0 }) return;

      var changed = false;
      lock (_gate)
      {
        foreach (var name in names)
        {
          if (string.IsNullOrEmpty(name)) continue;

          var kind = timeline.IdentityWithSource(name, out var reason);

          // Asked before the Unknown test, and it also governs an entry the ledger ALREADY holds: a capture that can
          // only restate npcs.txt must not downgrade "R7-graph" to "R6-npcdb", nor spend a sighting on saying nothing
          // new. The remembered reason then stays the strongest thing any log has actually witnessed.
          if (!WorthRemembering(reason)) continue;
          if (kind == IdentityKind.Unknown) continue;

          _byName.TryGetValue(name, out var existing);
          if (existing.Kind != kind)
          {
            /*
             * A changed verdict is a fresh belief, not the old one plus one: keeping the count would advertise
             * "agreed 40 times" about a conclusion that was wrong for 39 of them. The time is the MAX rather than the
             * capture's own end because this row may also be carrying the roster bit, whose stamp is when the operator
             * last saw the name: replaying a two-year-old backup must not rewind that and let the wall-clock prune take
             * a still-active raid member's class away. Record touches Kind/Reason/Sightings and nothing the roster owns.
             */
            _byName[name] = existing with { Kind = kind, Reason = reason!, SeenAtS = Math.Max(existing.SeenAtS, captureEndS), Sightings = 1 };
            changed = true;
            continue;
          }

          var sightings = captureEndS > existing.SeenAtS ? existing.Sightings + 1 : existing.Sightings;
          if (existing.SeenAtS == captureEndS && existing.Sightings == sightings && existing.Reason == reason) continue;

          _byName[name] = existing with { Reason = reason!, SeenAtS = Math.Max(existing.SeenAtS, captureEndS), Sightings = sightings };
          changed = true;
        }

        if (PruneLocked()) changed = true;
        if (PruneRosterLocked()) changed = true;
      }

      if (changed) Save();
    }

    /*
     * ROSTER LANE: "this application called the name one of ours, and here is the class it was seen using". Membership
     * is not an identity verdict and this method never makes one - an existing Kind/Reason/Sightings survive untouched,
     * so a name the graph decided is NPC keeps that decision while still sitting on the roster (the two statements are
     * allowed to disagree; IdentityLookup is where the reading order lives).
     *
     * seenAtS is the SAME dotnet-epoch number players.txt rows carry (0 = "a statement": a hand-typed name nobody ever
     * saw do anything, which no dial retires). It moves FORWARD only: re-importing an old list, or confirming a name in
     * a replayed backup, must not age an active player out, and the import has to be idempotent - running it twice on
     * the same list changes nothing, so a rolled-back players.txt can be imported again without side effects.
     */
    public void RememberRoster(string? name, long seenAtS, string? className) => RememberRoster(name, seenAtS, className, persist: true);

    /*
     * persist:false is for the ingest path (the parser confirming a name on a loot line), which is batched by
     * PlayerRegistry's own save timer exactly as players.txt was; operator writes persist at once. The in-memory answer
     * is immediate either way - that is what "is this one of ours?" reads.
     */
    internal void RememberRoster(string? name, long seenAtS, string? className, bool persist)
    {
      if (string.IsNullOrEmpty(name)) return;

      var classOf = string.IsNullOrEmpty(className) ? null : className;
      bool changed;
      lock (_gate)
      {
        if (!_byName.TryGetValue(name, out var existing))
        {
          /*
           * A name the ledger has never heard. Kind stays Unknown on purpose: being on the list is not evidence of what
           * the name IS, and a row that claimed Player would let the roster outvote every later rule at equal strength.
           */
          _byName[name] = new Prior(IdentityKind.Unknown, RosterReason, Math.Max(0, seenAtS), 0, true, classOf);
          changed = true;
        }
        else
        {
          var updated = existing with
          {
            Ours = true,
            // A name already carrying a learned class keeps it unless this write has one: the roster's untyped rows
            // (the hand-typed half of players.txt) are silence about class, not an erasure of it.
            Class = classOf ?? existing.Class,
            SeenAtS = Math.Max(existing.SeenAtS, Math.Max(0, seenAtS)),
          };
          changed = updated != existing;
          if (changed) _byName[name] = updated;
        }
      }

      if (changed && persist) Save();
    }

    /*
     * An operator's DEFAULT class for a name — the value the resolver falls back to when this capture observed none
     * (docs/DesignNotes.md -> "The class precedence law"). Separate from `RememberRoster` on purpose: that method treats an
     * absent class as silence and keeps whatever the row held, which is right for ingest (a loot line confirming a name says
     * nothing about class) and wrong here, where a blank selection MEANS "no default any more". Null therefore assigns null.
     *
     * seenAtS stays 0 on a brand-new row: an edit is a statement, not a sighting, so it does not start an age clock — the law
     * that keeps `init:true` on every load (`SeenAtS <= 0` never retires). Kind stays Unknown for the same reason the roster
     * lane always leaves it: this says "one of ours, called a bard", not "the rules proved Player".
     */
    public void SetRosterClass(string? name, string? className)
    {
      if (string.IsNullOrEmpty(name) || "You".Equals(name, StringComparison.OrdinalIgnoreCase)) return;

      var classOf = string.IsNullOrEmpty(className) ? null : className;
      bool changed;
      lock (_gate)
      {
        if (!_byName.TryGetValue(name, out var existing))
        {
          _byName[name] = new Prior(IdentityKind.Unknown, RosterReason, 0, 0, true, classOf);
          changed = true;
        }
        else
        {
          var updated = existing with { Ours = true, Class = classOf };
          changed = updated != existing;
          if (changed) _byName[name] = updated;
        }
      }

      if (changed) Save();
    }

    /// <summary>Take the roster bit off a name. A remembered VERDICT on the same name is a different statement and stays.</summary>
    public void ForgetRoster(string? name) => ForgetRoster(name, persist: true);

    internal void ForgetRoster(string? name, bool persist)
    {
      if (string.IsNullOrEmpty(name)) return;

      bool changed;
      lock (_gate)
      {
        if (!_byName.TryGetValue(name, out var existing)) { changed = false; }
        else if (!existing.Ours) { changed = false; }
        else if (existing.Kind != IdentityKind.Unknown && WorthRemembering(existing.Reason))
        {
          // The rule row survives with its own clock; only the membership and the class that rode with it leave.
          _byName[name] = existing with { Ours = false, Class = null };
          changed = true;
        }
        else if (!string.IsNullOrEmpty(existing.Owner))
        {
          // No verdict worth keeping, but "X's pet" and "X was on the roster" are two statements: the mapping outlives
          // the removal that took the membership off, exactly as it outlives a ForgetPet clearing its own lane.
          _byName[name] = existing with { Ours = false, Class = null };
          changed = true;
        }
        else
        {
          // Nothing underneath but the roster's own claim, so the row has no reason to exist.
          _byName.Remove(name);
          changed = true;
        }
      }

      if (changed && persist) Save();
    }

    /// <summary>True when this application's roster carries the name; className is what it was last seen as.</summary>
    public bool TryGetRoster(string? name, out string? className)
    {
      if (string.IsNullOrEmpty(name)) { className = null; return false; }
      lock (_gate)
      {
        if (_byName.TryGetValue(name, out var prior) && prior.Ours) { className = prior.Class; return true; }
      }
      className = null;
      return false;
    }

    public bool TryGetRoster(string? name) => TryGetRoster(name, out _);

    /// <summary>Snapshot of the roster lane for the registry to seed itself from at log open, sorted by name.</summary>
    public List<KeyValuePair<string, Prior>> RosterEntries()
    {
      lock (_gate)
      {
        var list = new List<KeyValuePair<string, Prior>>(_byName.Count);
        foreach (var (name, prior) in _byName)
        {
          if (prior.Ours) list.Add(new KeyValuePair<string, Prior>(name, prior));
        }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
      }
    }

    /// <summary>One pass after another server's ledger was read: the file exists but holds no membership yet.</summary>
    public bool HasRosterRows
    {
      get
      {
        lock (_gate)
        {
          foreach (var prior in _byName.Values) if (prior.Ours) return true;
        }
        return false;
      }
    }

    /*
     * PET LANE — "whose pet is this", the statement petmapping.txt carried (`Fluffy=Ziggy|4021234567`).
     *
     * Same shape as the roster lane and for the same reasons: it is NOT a verdict, it moves its stamp FORWARD only (so an
     * old list can be imported twice with no effect and a replayed backup cannot age an active pet out), a hand-typed row
     * carries 0 which means "a statement, never retires", and clearing it leaves any witnessed verdict underneath alone.
     * Owner text is stored verbatim, INCLUDING the unassigned text petmapping.txt writes for a pet nobody has mapped
     * (Labels.Unassigned, "Unknown Pet Owner") —
     * that string is data the Pet Owners grid shows and edits, and deciding here whether it counts as an owner is one of
     * the two places a rule and a UI could disagree about what a row means.
     */

    /// <summary>What this application wrote down about ownership: "this application remembers it" — not a rule code.</summary>
    public const string OwnerReason = "PetMap";

    public void RememberPet(string? petName, string? owner, long seenAtS) => RememberPet(petName, owner, seenAtS, persist: true);

    internal void RememberPet(string? petName, string? owner, long seenAtS, bool persist)
    {
      if (string.IsNullOrEmpty(petName) || string.IsNullOrEmpty(owner)) return;

      bool changed;
      lock (_gate)
      {
        if (!_byName.TryGetValue(petName, out var existing))
        {
          /*
           * A name the ledger never heard: Kind stays Unknown because ownership is not evidence of what a name IS. A row
           * that claimed Pet here would let an old mapping file outvote tonight's rules about a name that grew up into a
           * player-shaped something else.
           */
          _byName[petName] = new Prior(IdentityKind.Unknown, OwnerReason, Math.Max(0, seenAtS), 0, false, null, owner);
          changed = true;
        }
        else
        {
          var updated = existing with
          {
            Owner = owner,
            SeenAtS = Math.Max(existing.SeenAtS, Math.Max(0, seenAtS)),
            // A row that existed only as a verdict gains the ownership provenance word nothing else can supply; a row
            // already owned by this lane keeps its word (a capture-learned owner does not rewrite an operator's import).
            Reason = WorthRemembering(existing.Reason) || existing.Ours ? existing.Reason : OwnerReason,
          };
          changed = updated != existing;
          if (changed) _byName[petName] = updated;
        }
      }

      if (changed && persist) Save();
    }

    /// <summary>The owner this application remembers for a pet name; null when it has no mapping.</summary>
    public bool TryGetOwner(string? name, out string? owner)
    {
      if (string.IsNullOrEmpty(name)) { owner = null; return false; }
      lock (_gate)
      {
        if (_byName.TryGetValue(name, out var prior) && !string.IsNullOrEmpty(prior.Owner)) { owner = prior.Owner; return true; }
      }
      owner = null;
      return false;
    }

    public bool TryGetOwner(string? name) => TryGetOwner(name, out _);

    /// <summary>Snapshot of the ownership lane for the registry to seed itself from at log open, sorted by pet name.</summary>
    public List<KeyValuePair<string, Prior>> PetEntries()
    {
      lock (_gate)
      {
        var list = new List<KeyValuePair<string, Prior>>(_byName.Count);
        foreach (var (name, prior) in _byName)
        {
          if (!string.IsNullOrEmpty(prior.Owner)) list.Add(new KeyValuePair<string, Prior>(name, prior));
        }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
      }
    }

    /// <summary>True when this ledger already carries ownership — the gate the petmapping.txt import asks.</summary>
    public bool HasOwnerRows
    {
      get
      {
        lock (_gate)
        {
          foreach (var prior in _byName.Values) if (!string.IsNullOrEmpty(prior.Owner)) return true;
        }
        return false;
      }
    }

    /// <summary>Drop a mapping. A verdict or roster bit on the same name is a different statement and stays.</summary>
    public void ForgetPet(string? name) => ForgetPet(name, persist: true);

    internal void ForgetPet(string? name, bool persist)
    {
      bool changed;
      lock (_gate) changed = ClearOwnerLocked(name);
      if (changed && persist) Save();
    }

    // Caller holds the gate. Returns true when a mapping was actually removed.
    private bool ClearOwnerLocked(string? name)
    {
      if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out var existing) || string.IsNullOrEmpty(existing.Owner)) return false;

      var cleared = existing with { Owner = null };
      // Nothing else in the row was earned by evidence or by membership: an imported owner-only row has no reason to stay.
      if (!cleared.Ours && (cleared.Kind == IdentityKind.Unknown || !WorthRemembering(cleared.Reason))) _byName.Remove(name);
      else _byName[name] = cleared;
      return true;
    }

    /*
     * THE GATE: what this file is allowed to remember. An ALLOWLIST of rule families, one per EVENT a log had to
     * contain for the rule to speak - a target frame (R1), a /who roster (R2), chat and zone presence (R3), a
     * recognisable cast (R4), "X is called to it owner" (R5-companion — the name in that line is the SUMMONER), the
     * opposition graph (R7), a charm line (R9), the
     * merc signature (R13), heals from our side (R15/R18), a drink or a bite (R17), an eye of their own to hit (R19). Those are exactly the things a future log might not say again, which is the only reason to write one down —
     * R19 belongs in the list even though it decided nothing on the eight reference captures (design doc, "An eye
     * is not a combatant"): a name whose ONLY sighting is the eye bearing it is exactly the kind of thing the next
     * log may never restate.
     *
     * What stays out, and why each is noise rather than memory:
     *
     *   R6-npcdb    npcs.txt ships with the program, so it answers for that name in EVERY capture that mentions it -
     *               remembering the answer adds a row and no knowledge. Worse, if somebody later corrects that file,
     *               the ledger keeps contradicting the correction until the entry dies of age.
     *   R14-shape   "a bone walker" takes an article: the input is the name's own spelling, which travels with it.
     *   R16-comma   "Teknaz, Lord Misery" carries a title: same, the shape is the whole input.
     *   R5-owner    "X`s pet" states its owner inside the name, and the durable half of that claim (who owns what)
     *               belongs to petmapping.txt, where the operator can actually see and edit it.
     *   R0-local    this session's own character, from settings - present by construction.
     *   R10/Manual  the operator's verdict: identity-overrides.txt IS that file, and copying it in here would report a
     *               human assertion as "N captures agreed".
     *   RegistrySeed / "You"  this session's roster inputs, not evidence.
     *   Prior       this file reading itself. Recording it is how a wrong name becomes permanently right.
     *
     * An allowlist rather than a blocklist because the two failure modes are not equal weight: leave a rule that reads
     * LINES off this list and we simply remember nothing about those names until someone adds it (small, self-healing,
     * and today's census still shows the live verdict); put a rule on it whose input is permanent and every log on
     * earth starts filing its built-in answers as experience. So a new rule has to ASK to be remembered, and
     * IdentityPriorStoreTest asserts the vocabulary at its size.
     */
    private static readonly string[] RememberedRules =
    [
      "R1-", "R2-", "R3-", "R4-", "R5-companion", "R7-", "R9-", "R13-", "R15-", "R17-", "R18-", "R19-", "R22-", "R23-", "R24-",
    ];

    /// <summary>True when a rule code names an event only the capture could have supplied, i.e. worth remembering.</summary>
    internal static bool WorthRemembering(string? reason)
    {
      if (string.IsNullOrEmpty(reason)) return false;

      foreach (var rule in RememberedRules)
      {
        // Ordinal: these are vocabulary words written by ClassificationRules, never text lifted from the log. A trailing
        // separator on each entry is what keeps "R1-" from matching "R10-manual", and lets suffixed codes through
        // ("R5-owner:Sancus" style reasons carry the owner after a colon, so matching is a prefix test by design).
        if (reason.StartsWith(rule, StringComparison.Ordinal)) return true;
      }

      return false;
    }

    /*
     * Caller holds the gate. Roster rows are EXEMPT from both halves of this prune - the size cap included, because the
     * cap is a guard against a decade of inferred verdicts and evicting "this is one of ours" under somebody who is
     * still playing would be the file silently forgetting its own list. What a roster row does instead is age on the
     * wall clock next door (PruneRosterLocked).
     */
    private bool PruneLocked()
    {
      if (_byName.Count == 0) return false;

      long newest = long.MinValue;
      foreach (var prior in _byName.Values) newest = Math.Max(newest, prior.SeenAtS);

      var changed = false;
      if (newest > StaleS)
      {
        List<string>? doomed = null;
        foreach (var (name, prior) in _byName)
        {
          if (prior.Ours) continue;
          if (newest - prior.SeenAtS > StaleS) (doomed ??= []).Add(name);
        }
        if (doomed is not null)
        {
          foreach (var name in doomed)
          {
            _byName.TryGetValue(name, out var entry);

            // The ownership lane does not age on THIS clock. "Fluffy belongs to Ziggy" has its own dial (PruneRosterLocked's
            // StaleDays against the wall), and dropping it here would retire a mapping nobody asked to lose just because a
            // different name was re-learned late in the file - which is what an import followed by one new log does. What dies
            // on this clock is the VERDICT lane: a stale Kind/Reason is rewritten out, leaving the row holding exactly what a
            // mapping with no witnessed verdict holds (the SeenAtS survives - it is the stamp the wall-clock dial reads).
            if (entry.Owner is not null)
            {
              _byName[name] = new Prior(IdentityKind.Unknown, OwnerReason, entry.SeenAtS, 0, false, null, entry.Owner);
            }
            else
            {
              _byName.Remove(name);
            }
          }
          changed = true;
        }
      }

      // Both memory lanes are exempt: the cap guards against a decade of INFERRED verdicts, and evicting "this is one of
      // ours" or "Fluffy belongs to Ziggy" would be the file silently forgetting data somebody keeps by hand.
      var candidates = new List<KeyValuePair<string, Prior>>(_byName.Count);
      foreach (var (name, prior) in _byName)
      {
        if (!prior.Ours && prior.Owner is null) candidates.Add(new KeyValuePair<string, Prior>(name, prior));
      }
      if (candidates.Count <= MaxEntries) return changed;

      // Over the cap, keep the most recently seen: an entry that has not been confirmed for longer is the one whose
      // claim is likeliest to be stale anyway.
      candidates.Sort(static (a, b) => b.Value.SeenAtS.CompareTo(a.Value.SeenAtS));
      foreach (var entry in candidates.Skip(MaxEntries)) _byName.Remove(entry.Key);
      return true;
    }

    /*
     * Caller holds the gate. The roster lane's own expiry: the SAME number of days players.txt and petmapping.txt were
     * aged at (PlayerRegistry.StaleDays) against the wall clock, so one dial ages every memory this program keeps.
     * Two exemptions are load-bearing:
     *
     *   SeenAtS <= 0   a statement, not an observation. The hand-typed half of an old players.txt carried no time at
     *                  all; retiring those is how a curated list dies silently.
     *   future stamps  nothing retires backwards, so a log whose clock runs ahead cannot age the list.
     *
     * Aging takes the MEMBERSHIP off (and the class with it), leaving a rule row that earned its place: what expires is
     * "the operator's list", not a verdict some capture witnessed.
     */
    private bool PruneRosterLocked()
    {
      if (_byName.Count == 0) return false;

      var nowS = (long)DateUtil.ToDotNetSeconds(DateTime.Now);
      var staleS = (long)PlayerRegistry.StaleDays * 24 * 60 * 60;
      List<string>? expired = null;
      List<string>? expiredPets = null;
      foreach (var (name, prior) in _byName)
      {
        if (prior.SeenAtS <= 0) continue;              // a statement, not an observation: never retires
        if (nowS - prior.SeenAtS <= staleS) continue;
        if (prior.Ours) (expired ??= []).Add(name);
        if (!string.IsNullOrEmpty(prior.Owner)) (expiredPets ??= []).Add(name);
      }
      if (expired is null && expiredPets is null) return false;

      expired?.ForEach(ForgetRosterLocked);
      // The call's bool answer is deliberately dropped here: this pass reports what aged out, it does not act per name.
      expiredPets?.ForEach(name => ClearOwnerLocked(name));

      var what = new List<string>();
      if (expired is { Count: > 0 }) what.Add($"{expired.Count} roster names aged out");
      if (expiredPets is { Count: > 0 }) what.Add($"{expiredPets.Count} pet mappings aged out");
      Log.Info($"Identity priors for {_serverName}: {string.Join(", ", what)} " +
               $"({PlayerRegistry.StaleDays} days without a sighting)");
      return true;
    }

    // Caller holds the gate. Same rule as the public ForgetRoster: a witnessed verdict underneath stays.
    private void ForgetRosterLocked(string name)
    {
      if (!_byName.TryGetValue(name, out var existing) || !existing.Ours) return;
      if (existing.Kind != IdentityKind.Unknown && WorthRemembering(existing.Reason)) _byName[name] = existing with { Ours = false, Class = null };
      else _byName.Remove(name);
    }

    private void Save()
    {
      if (string.IsNullOrEmpty(_serverName)) return;   // nowhere to put it; keep the session's memory

      List<KeyValuePair<string, string>> lines;
      lock (_gate)
      {
        /*
         * Membership is aged on the way OUT, not only at load. This file is the roster's only home now (players.txt stopped being
         * written 2026-10-09), and the writer that used to age it was PlayerRegistry's own save timer — so a flush applies StaleDays
         * against the wall clock BEFORE serializing, and an expired name leaves the FILE instead of sitting in it unread.
         * PruneRosterLocked takes the membership and its class off while a witnessed verdict underneath survives, and it is silent
         * about nothing: the line it logs is the only notice an operator gets that the list shrank.
         */
        PruneRosterLocked();

        lines = new List<KeyValuePair<string, string>>(_byName.Count);
        foreach (var (name, prior) in _byName)
        {
          // '=' is the key/value separator LoadProperties splits on, and a rule code could in principle carry one -
          // dropping it keeps every saved line parseable rather than silently unlinking the row below it. The class is
          // written last so a name whose class somehow contains a separator cannot shift the fields in front of it.
          var reason = (prior.Reason ?? string.Empty).Replace("=", string.Empty);
          var className = (prior.Class ?? string.Empty).Replace("=", string.Empty).Replace("|", string.Empty);
          var owner = (prior.Owner ?? string.Empty).Replace("=", string.Empty).Replace("|", string.Empty);
          lines.Add(new KeyValuePair<string, string>(name,
            $"{prior.Kind}|{reason}|{prior.SeenAtS}|{prior.Sightings}|{(prior.Ours ? "True" : "False")}|{className}"
            + (owner.Length > 0 ? $"|{owner}" : string.Empty)));
        }
      }
      ConfigUtil.SaveIdentityPriors(lines, _serverName);
    }
  }
}

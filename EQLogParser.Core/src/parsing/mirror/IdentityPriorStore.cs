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

namespace EQLogParser.Mirror
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
   * mirror-vs-legacy parity tests in hand, not as a convenience.
   *
   * Three rules keep the file honest:
   *
   * - ONLY WHAT A LATER LOG MIGHT NOT ANSWER AGAIN IS RECORDED. An entry means "some rule read this name off an
   * EVENT in a real log" - a target frame, a /who roster, guild speech, a charm line, the graph - and the rule code
   * is stored with it. A verdict whose input the app owns forever is not memory, it is a restatement; see
   * WorthRemembering for that vocabulary. Operator verdicts live in mirror-overrides.txt (Manual), roster membership
   * in players.txt and pet mappings in petmapping.txt: copying any of those in here would launder an assertion into
   * statistics. Names the operator rejected are skipped outright - "no claim" outranks our memory.
   *
   * - AGREEMENT IS IDEMPOTENT PER CAPTURE. The mirror re-derives whenever a filter or an override changes, so a
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

    /// <summary>One remembered verdict, with the reason that produced it and how often it was agreed.</summary>
    internal readonly record struct Prior(IdentityKind Kind, string Reason, long SeenAtS, int Sightings);

    // Staleness is measured against the newest sighting in the file, so a player rebuilding last season's logs keeps
    // what they had; the cap is a memory guard for a name-heavy server played for years.
    private const long StaleS = 90L * 24 * 60 * 60;
    private const int MaxEntries = 25_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Prior> _byName = new(StringComparer.OrdinalIgnoreCase);
    private string _serverName = string.Empty;

    /// <summary>Loads the ledger for the current ConfigUtil.ServerName.</summary>
    public void Init() => Init(ConfigUtil.ServerName);

    public void Init(string serverName)
    {
      var loaded = new Dictionary<string, Prior>(StringComparer.OrdinalIgnoreCase);
      var dropped = 0;                       // rows the gate below refused, see the log line at the end
      _serverName = serverName ?? string.Empty;

      if (!string.IsNullOrEmpty(_serverName))
      {
        foreach (var (name, value) in ConfigUtil.ReadIdentityPriors(_serverName))
        {
          // Name=Kind|Reason|SeenAtS|Sightings. Anything malformed is dropped rather than repaired: a half-read row
          // would put a name on the list with a verdict nobody wrote.
          var parts = value.Split('|');
          if (parts.Length != 4 || string.IsNullOrEmpty(name)) continue;
          if (!Enum.TryParse<IdentityKind>(parts[0], out var kind) || kind == IdentityKind.Unknown) continue;
          if (!long.TryParse(parts[2], out var seenAt) || !int.TryParse(parts[3], out var sightings)) continue;

          // Files written before the gate below existed carry restated-database rows ("Name=Npc|R6-npcdb|..."). They
          // are not read back, and the load writes what is left so the noise leaves the FILE instead of being quietly
          // dropped again every start: whoever opens this file should find only entries worth arguing about.
          if (!WorthRemembering(parts[1])) { dropped++; continue; }

          loaded[name] = new Prior(kind, parts[1] ?? string.Empty, seenAt, Math.Max(1, sightings));
        }
      }

      lock (_gate)
      {
        _byName.Clear();
        foreach (var (name, prior) in loaded) _byName[name] = prior;
      }

      // One line per log opened, because "did the memory load?" is otherwise unanswerable from a player's log file:
      // the census just quietly shows more names. The dropped count says what this rewrite took out, so an operator who
      // hand-edited the file can see whether their edit was read or refused.
      if (loaded.Count > 0 || dropped > 0)
        Log.Info($"Identity priors for {_serverName}: {loaded.Count} remembered verdicts"
                 + (dropped > 0 ? $", {dropped} rows refused as restatements and rewritten out of the file" : string.Empty));

      if (dropped > 0) Save();
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
     * and the mirror's own last-event time; every argument may be empty and simply records nothing.
     *
     * captureEndS is the log's clock rather than DateTimeOffset.UtcNow for the idempotency rule above: re-deriving
     * the same file reports the same time, so agreement cannot inflate, and two captures of the same evening taken an
     * hour apart still count as the sighting they were.
     */
    public void Record(EntityTimeline? timeline, IReadOnlyList<string>? names, PlayerRegistry? registry, long captureEndS)
    {
      if (timeline is null || names is not { Count: > 0 }) return;

      var changed = false;
      lock (_gate)
      {
        foreach (var name in names)
        {
          if (string.IsNullOrEmpty(name)) continue;

          // The operator's "no claim" and their verdicts are their own files' business; a rejected name in particular
          // must not be able to come back wearing our memory of last season.
          if (registry is not null && registry.IsRejectedPlayer(name)) continue;

          var kind = timeline.IdentityWithSource(name, out var reason);

          // Asked before the Unknown test, and it also governs an entry the ledger ALREADY holds: a capture that can
          // only restate npcs.txt must not downgrade "R7-graph" to "R6-npcdb", nor spend a sighting on saying nothing
          // new. The remembered reason then stays the strongest thing any log has actually witnessed.
          if (!WorthRemembering(reason)) continue;
          if (kind == IdentityKind.Unknown) continue;

          _byName.TryGetValue(name, out var existing);
          if (existing.Kind != kind)
          {
            // A changed verdict is a fresh belief, not the old one plus one: keeping the count would advertise
            // "agreed 40 times" about a conclusion that was wrong for 39 of them.
            _byName[name] = new Prior(kind, reason!, captureEndS, 1);
            changed = true;
            continue;
          }

          var sightings = captureEndS > existing.SeenAtS ? existing.Sightings + 1 : existing.Sightings;
          if (existing.SeenAtS == captureEndS && existing.Sightings == sightings && existing.Reason == reason) continue;

          _byName[name] = existing with { Reason = reason!, SeenAtS = Math.Max(existing.SeenAtS, captureEndS), Sightings = sightings };
          changed = true;
        }

        if (PruneLocked()) changed = true;
      }

      if (changed) Save();
    }

    /*
     * THE GATE: what this file is allowed to remember. An ALLOWLIST of rule families, one per EVENT a log had to
     * contain for the rule to speak - a target frame (R1), a /who roster (R2), chat and zone presence (R3), a
     * recognisable cast (R4), "X is called to it owner" (R5-called), the opposition graph (R7), a charm line (R9), the
     * merc signature (R13), heals from our side (R15/R18), a drink or a bite (R17). Those are exactly the things a
     * future log might not say again, which is the only reason to write one down.
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
     *   R10/Manual  the operator's verdict: mirror-overrides.txt IS that file, and copying it in here would report a
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
      "R1-", "R2-", "R3-", "R4-", "R5-called", "R7-", "R9-", "R13-", "R15-", "R17-", "R18-",
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

    // Caller holds the gate. Returns true when something left the dictionary.
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
          if (newest - prior.SeenAtS > StaleS) (doomed ??= []).Add(name);
        }
        if (doomed is not null)
        {
          foreach (var name in doomed) _byName.Remove(name);
          changed = true;
        }
      }

      if (_byName.Count <= MaxEntries) return changed;

      // Over the cap, keep the most recently seen: an entry that has not been confirmed for longer is the one whose
      // claim is likeliest to be stale anyway.
      var byAge = new List<KeyValuePair<string, Prior>>(_byName);
      byAge.Sort(static (a, b) => b.Value.SeenAtS.CompareTo(a.Value.SeenAtS));
      foreach (var entry in byAge.Skip(MaxEntries)) _byName.Remove(entry.Key);
      return true;
    }

    private void Save()
    {
      if (string.IsNullOrEmpty(_serverName)) return;   // nowhere to put it; keep the session's memory

      List<KeyValuePair<string, string>> lines;
      lock (_gate)
      {
        lines = new List<KeyValuePair<string, string>>(_byName.Count);
        foreach (var (name, prior) in _byName)
        {
          // '=' is the key/value separator LoadProperties splits on, and a rule code could in principle carry one -
          // dropping it keeps every saved line parseable rather than silently unlinking the row below it.
          var reason = (prior.Reason ?? string.Empty).Replace("=", string.Empty);
          lines.Add(new KeyValuePair<string, string>(name, $"{prior.Kind}|{reason}|{prior.SeenAtS}|{prior.Sightings}"));
        }
      }
      ConfigUtil.SaveIdentityPriors(lines, _serverName);
    }
  }
}

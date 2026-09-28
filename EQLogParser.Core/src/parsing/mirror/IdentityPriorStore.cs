using System;
using System.Collections.Generic;

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
   *   - ONLY LINE EVIDENCE IS RECORDED. An entry means "some rule read this name off a line in a real log": the rule
     *   code is stored with it. Operator verdicts live in mirror-overrides.txt (Manual) and roster membership in
     players.txt; copying either in here would launder an assertion into statistics. Names the operator rejected are
     skipped outright - "no claim" outranks our memory.
   *   - AGREEMENT IS IDEMPOTENT PER CAPTURE. The mirror re-derives whenever a filter or an override changes, so a
     counter bumped per pass would report "41 captures agreed" for one evening re-derived 41 times. Sighting time is
     the LOG's last event, not the clock, and the count advances only on a strictly newer capture.
   *   - IT EXPIRES. Pruned against the newest entry in the file (not the wall clock, so replaying old backups does
     not nuke the ledger) plus a size cap. Seasons change and mob names get reused by players; an old confident NPC
     row must be able to die of age rather than needing someone to notice it.
   */
  internal sealed class IdentityPriorStore
  {
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
          loaded[name] = new Prior(kind, parts[1] ?? string.Empty, seenAt, Math.Max(1, sightings));
        }
      }

      lock (_gate)
      {
        _byName.Clear();
        foreach (var (name, prior) in loaded) _byName[name] = prior;
      }
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
          if (kind == IdentityKind.Unknown || !FromLineEvidence(reason)) continue;

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
     * What counts as having READ something off a line. Manual is R10 (the operator), RegistrySeed / "You" are this
     * session's roster files, and "Prior" is this file talking to itself - recording any of them would turn an input
     * into statistics about itself, which is how a wrong name becomes permanently right.
     */
    private static bool FromLineEvidence(string? reason)
      => !string.IsNullOrEmpty(reason)
         && !reason.StartsWith("Manual", StringComparison.Ordinal)
         && !reason.StartsWith("RegistrySeed", StringComparison.Ordinal)
         && !reason.Equals("You", StringComparison.Ordinal)
         && !reason.StartsWith("Prior", StringComparison.Ordinal);

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

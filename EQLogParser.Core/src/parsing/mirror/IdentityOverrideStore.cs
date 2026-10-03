using System;
using System.Collections.Generic;

namespace EQLogParser.Mirror
{
  /*
   * R10, kept: what the operator said a name IS, saved per server and replayed on every rebuild.
   *
   * The rules are inference, and inference is wrong in a specific, predictable place - a name whose evidence
   * never crosses a gate. A pet the raid stopped healing early (R18's breadth), an alt that joined before the
   * log opened and never spoke, a merc the roster line never spelled out. The branch's whole premise is that
   * classification stays dynamic, and "dynamic" has to include the one input no parser can have: somebody who
   * knows saying "that is Goruuk's pet", once, in the right-click menu of a fight row.
   *
   * Three properties this exists to guarantee:
   *
   *   - IT OUTRANKS EVERY RULE. Applied at RuleStrength.Manual through ClassificationRules.ApplyManualOverride,
   *     retroactively over the whole log. A rule that could beat an override would be the system telling the
   *     operator they are wrong about their own raid, on every subsequent pass, forever.
   *   - IT SURVIVES. Rules are re-derived from scratch on each pass and the timeline is rebuilt, so an override
   *     held only in that timeline evaporates at the next derive; it lives here, and this is replayed into every
   *     new timeline (DeriveEngine.RunDeriveAsync). Saved next to petmapping.txt, per server, because a name
   *     that means "our pet" on One's Everfrost is not the same claim on another server.
   *   - IT IS ADDITIVE AND REVERSIBLE. It stores one verdict per name; "clear override" removes the line and the
   *     rules' own answer comes back untouched, with no re-parse needed (the facts are unchanged - this is a
   *     reading, not an edit of history).
   *
   * Names compare case-insensitively like every other entity lookup in this pipeline: the parser capitalizes the
   * names it hands out while the evidence lines keep what EQ wrote, and an ordinal key here would make a saved
   * verdict silently miss the name it was saved for.
   */
  internal sealed class IdentityOverrideStore
  {
    public static IdentityOverrideStore Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, IdentityKind> _byName = new(StringComparer.OrdinalIgnoreCase);
    private string _serverName = string.Empty;

    /// <summary>Loads the file for the current ConfigUtil.ServerName (MainWindow calls this where the
    /// registry is loaded, i.e. whenever the server of the opened log changes).</summary>
    public void Init() => Init(ConfigUtil.ServerName);

    public void Init(string serverName)
    {
      var loaded = new Dictionary<string, IdentityKind>(StringComparer.OrdinalIgnoreCase);
      _serverName = serverName ?? string.Empty;

      // A server name is part of the path, so without one there is nothing to read: stay empty rather than
      // guessing at a folder. The in-memory behaviour below still works for a session.
      if (!string.IsNullOrEmpty(_serverName))
        foreach (var (name, value) in ConfigUtil.ReadIdentityOverrides(_serverName))
          if (!string.IsNullOrEmpty(name) && Enum.TryParse<IdentityKind>(value, out var kind)) loaded[name] = kind;

      lock (_gate)
      {
        _byName.Clear();
        foreach (var (name, kind) in loaded) _byName[name] = kind;
      }
    }

    public int Count
    {
      get { lock (_gate) return _byName.Count; }
    }

    public bool TryGet(string name, out IdentityKind kind)
    {
      if (string.IsNullOrEmpty(name)) { kind = default; return false; }
      lock (_gate) return _byName.TryGetValue(name, out kind);
    }

    /// <summary>Snapshot for display/tooltips, sorted by name.</summary>
    public List<KeyValuePair<string, IdentityKind>> All()
    {
      lock (_gate)
      {
        var list = new List<KeyValuePair<string, IdentityKind>>(_byName);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
      }
    }

    public void Set(string name, IdentityKind kind) => Apply([name], kind);

    public void Remove(string name) => Apply([name], null);

    /*
     * One write for a whole selection: the grid lets a user ctrl-click twenty rows and say "all of these are
     * pets", and saving after each name would rewrite the file twenty times for one decision - and leave a
     * half-applied file on disk if the second write failed.
     */
    public void Apply(IReadOnlyCollection<string> names, IdentityKind? kind)
    {
      if (names is not { Count: > 0 }) return;

      var changed = false;
      lock (_gate)
      {
        foreach (var name in names)
        {
          if (string.IsNullOrEmpty(name)) continue;
          if (kind is { } k) _byName[name] = k;
          else if (_byName.Remove(name)) changed = true;
          else continue;
          changed = true;
        }
      }

      if (changed) Save();
    }

    // Replays every saved verdict into a freshly built timeline. Manual strength means order does not
    // matter - nothing in ClassificationRules or RegistrySeed can outvote it - but this runs last anyway so a
    // reader sees the override as the final word on the name.
    public void Apply(EntityTimeline timeline)
    {
      List<KeyValuePair<string, IdentityKind>> snapshot;
      lock (_gate) snapshot = new List<KeyValuePair<string, IdentityKind>>(_byName);
      foreach (var (name, kind) in snapshot)
        ClassificationRules.ApplyManualOverride(timeline, name, kind);
    }

    private void Save()
    {
      if (string.IsNullOrEmpty(_serverName)) return;   // nowhere to put it; keep the session's memory
      List<KeyValuePair<string, string>> lines;
      lock (_gate)
      {
        lines = new List<KeyValuePair<string, string>>(_byName.Count);
        foreach (var (name, kind) in _byName) lines.Add(new KeyValuePair<string, string>(name, kind.ToString()));
      }
      ConfigUtil.SaveIdentityOverrides(lines, _serverName);
    }
  }
}

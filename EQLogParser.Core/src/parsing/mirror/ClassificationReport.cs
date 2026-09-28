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
   * One row per name a capture mentions, with what the classifier concluded and why. Built for the Names window:
   * the place an operator goes to AUDIT classification rather than to notice one mistake mid-fight (that is what the
   * fight grids' right-click "Set as …" is for), so this shows the verdict, the evidence code behind it, whether the
   * verdict is the operator's own, and how much of the capture the name actually did.
   *
   * WHY A SNAPSHOT AND NOT A VIEW. The timeline is mutable per-thread state that the next derive rebuilds from zero;
   * a window bound to it would change rows under the reader mid-scroll, and could be rebuilt on another thread while
   * the grid enumerates it. This is a copy taken at a moment, cheap to hold (~6,000 rows for a 10 million line capture).
   *
   * THE CENSUS IS EVERY INTERNED NAME. Both fact streams resolve names through ONE pool (HealFactTable interns into
   * the damage table, so index N is the same string in both), and interning happens when a name appears on any line —
   * attacker, defender or healer. So InternedNames already answers "which names did this log mention", including a
   * boss that never swung and a pet that only ever got healed. Names with no facts at all are added too when the
   * operator or the roster knows them (a rejection with zero evidence is exactly the row you want to find again).
   *
   * THE AS-OF POLICY IS "WHATEVER THE STRONGEST CLAIM SAYS". IdentityWithSource is timeless: strongest claim wins,
   * ties go to the later one. A name whose kind genuinely changed mid-capture — charmed, un-petted, R13 resurrected —
   * gets ONE row and no history here; its charm key lives in EntityTimeline's own windows and a nicer treatment of
   * those is deliberately deferred (2026-08: decision recorded as "a is fine"). Reason codes are surfaced verbatim so
   * the interesting rows are still findable by their source ("Manual", "R14-article", …).
   */
  internal sealed class ClassificationReport
  {
    /// <summary>One name: verdict, provenance, operator state, and what it did in this capture.</summary>
    internal sealed class Row
    {
      public string Name { get; init; } = string.Empty;

      /// <summary>What the pipeline concluded. Already override-aware: an operator verdict reads back as Manual.</summary>
      public IdentityKind Kind { get; init; }

      /// <summary>The rule that decided, e.g. "R1-target", "R6-npcdb", "Manual". Empty when nothing claimed the name.</summary>
      public string Reason { get; init; } = string.Empty;

      /// <summary>This name carries an operator verdict in mirror-overrides.txt (and can be reverted).</summary>
      public bool IsOperatorVerdict { get; init; }

      /// <summary>The operator rejected it: no claim, and nothing auto-adds it again. See PlayerRegistry.IsRejected.</summary>
      public bool IsRejected { get; init; }

      /// <summary>Class from the roster block or an ability word, null when neither supplied one.</summary>
      public string? Class { get; init; }

      /// <summary>True when the players.txt entry carries no evidence timestamp, i.e. somebody typed it.</summary>
      public bool TypedEntry { get; init; }

      /// <summary>Unix seconds of the last auto-confirmation, 0 for a hand-typed entry that never proved itself.</summary>
      public double LastSeenUnixSeconds { get; init; }

      /// <summary>Owner learned from petmapping.txt / an "X's pet" line, null when nothing claims ownership.</summary>
      public string? PetOwner { get; init; }

      /// <summary>What the legacy roster (players.txt + typed PlayerName) answers for this name today.</summary>
      public bool LegacySaysPlayer { get; init; }

      /// <summary>True when the name appears in neither fact stream — an operator/roster-only row.</summary>
      public bool HasFacts { get; init; }

      /*
       * The verdict came from THIS server's earlier logs, not from this capture. Set only where this log's own rules
       * reached no conclusion and nobody has overridden the name: a prior never competes with evidence, it fills the
       * silence, and the UI has to be able to say so (reason reads "Prior:R6-npcdb" with the count and date beside it).
       */
      public bool IsPrior { get; init; }

      /// <summary>How many captures agreed on the remembered verdict, and the log time of the newest one.</summary>
      public int PriorSightings { get; init; }
      public long PriorSeenAtS { get; init; }

      /// <summary>Sum of this name's damage facts (its own hits; a defender gets no credit for being hit).</summary>
      public double Damage { get; init; }

      /// <summary>Sum of this name's heal facts, over-heal excluded — Total is what landed.</summary>
      public double Healing { get; init; }

      /// <summary>Facts with this name as attacker or healer: how busy the row was, not how effective.</summary>
      public long Events { get; init; }

      /*
       * The roster says "one of ours" and the classifier says NPC. That direction is the only real disagreement, and
       * it is the list an operator acts on: a raid member pushed to the enemy column loses her damage on the flip.
       *
       * Unknown is deliberately NOT a disagreement — it means "this capture has no opinion", which is what every
       * guild alt who sat out this log reads, along with the 12-42% of facts whose names legacy never verified
       * (docs/combat-mirror-design.md). Flagging absence would paint the list red and bury the rows that are actual
       * contradictions. Pets and mercs are excluded for the same reason they are not contradictions: players.txt
       * legitimately holds pet OWNER names, and a pet/merc verdict costs its owner nothing.
       */
      public bool IsDisagreement => LegacySaysPlayer && Kind == IdentityKind.Npc;

      /*
       * Nothing has any claim on this name: no kind from this capture's lines, no operator verdict, not in the roster,
       * and NOT rejected (a rejection is a decision, this is an absence). These are the rows the Names window sorts last
       * and the only ones worth spending an override on - a busy name nobody can place is where the rules are missing
       * something, while an Unknown guild alt who sat the fight out is just an alt.
       */
      public bool IsUnresolved => Kind == IdentityKind.Unknown && !IsRejected && Class is null;
    }

    /// <summary>Rows in display order: raid-side kinds first, then busiest, then by name.</summary>
    public IReadOnlyList<Row> Rows { get; }

    /// <summary>Every name the census lists: fact pool plus every hand-written row.</summary>
    public int TotalNames { get; }

    public int TotalFacts { get; }
    public int Players { get; }
    public int Pets { get; }
    public int Mercs { get; }
    public int Npcs { get; }
    public int Unknown { get; }
    public int Rejected { get; }
    public int OperatorVerdicts { get; }
    public int Disagreements { get; }

    /*
     * Names the capture itself could not place, counting only rows that appear in the fact streams. The distinction is
     * the whole point: Unnamed spans the roster too (a verified player absent from this fight reads Unknown), so a plain
     * "unknown" count overstates the classifier's gap by every alt and idle pet. Rows held up by a prior are excluded -
     * those got an answer, just not from this file - which is why IsPrior exists separately.
     */
    public int UnresolvedInCapture { get; }

    private ClassificationReport(IReadOnlyList<Row> rows, int totalFacts, int players, int pets, int mercs, int npcs,
                                 int unknown, int rejected, int operatorVerdicts, int disagreements,
                                 int totalNames, int unresolvedInCapture)
    {
      TotalNames = totalNames;
      UnresolvedInCapture = unresolvedInCapture;
      Rows = rows;
      TotalFacts = totalFacts;
      Players = players;
      Pets = pets;
      Mercs = mercs;
      Npcs = npcs;
      Unknown = unknown;
      Rejected = rejected;
      OperatorVerdicts = operatorVerdicts;
      Disagreements = disagreements;
    }

    /// <summary>Looks a name up case-insensitively, the way every entity lookup in this pipeline compares.</summary>
    public Row? Find(string name)
    {
      foreach (var row in Rows)
      {
        if (string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) return row;
      }
      return null;
    }

    /*
     * Build the census. Any argument may be null and simply contributes nothing, so a window can open on a capture
     * with no heals, before the first derive (no timeline), or with the stores unloaded — an empty column beats
     * refusing to open.
     *
     * COST, since this runs on whatever thread a window opens on: one pass over each fact stream writing into arrays
     * indexed by name id (no hashing, no allocation per fact) and one dictionary walk per name. On a 7.98 million
     * fact capture the passes are tens of milliseconds; the per-name work is ~6,000 IdentityWithSource calls against
     * lists of one or two claims. Nothing here re-parses or re-derives.
     */
    /*
     * `priors` is optional and only ever FILLS A GAP: a name this capture's rules could not place may borrow what an
     * earlier capture on the same server concluded (IdentityPriorStore). That is the whole extent of cross-log memory
     * in this build — fight rows and every other consumer still see a timeline derived from this file alone, because R7
     * builds sides out of what the timeline knows and yesterday's guess must not become today's evidence.
     */
    public static ClassificationReport Build(EntityTimeline? timeline, DamageFactTable? damageFacts,
                                             HealFactTable? healFacts, MirrorOverrideStore? overrides,
                                             PlayerRegistry? registry, IdentityPriorStore? priors = null)
    {
      var names = damageFacts?.InternedNames;
      var count = names?.Count ?? 0;

      double[] damage = count > 0 ? new double[count] : [];
      double[] healing = count > 0 ? new double[count] : [];
      long[] events = count > 0 ? new long[count] : [];

      if (damageFacts is not null)
      {
        foreach (var f in damageFacts.Facts)
        {
          damage[f.AtkIdx] += f.Total;
          events[f.AtkIdx]++;
        }
      }
      if (healFacts is not null)
      {
        foreach (var h in healFacts.Heals)
        {
          healing[h.HealerIdx] += h.Total;
          events[h.HealerIdx]++;
        }
      }

      // Index by display name, case-insensitively: the pool keeps one id per entity however the line spelled it, but
      // registry and override rows are their own strings and have to fold onto that same row.
      var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
      if (names is not null)
      {
        for (short i = 0; i < names.Count; i++)
        {
          AddRow(rows, names[i], timeline, overrides, registry, priors, damage[i], healing[i], events[i], hasFacts: true);
        }
      }

      /*
       * Names the log never mentioned still belong on the list. They are the whole reason an operator came here:
       * a rejection they wrote, a name typed for an alt who missed this raid, and - most importantly - a VERDICT on
       * a name this capture happens not to contain, which would otherwise look like the override was ignored. It
       * wasn't; there is simply nothing to apply it to, and the list has to say so rather than go quiet.
       */
      if (overrides is not null)
      {
        foreach (var entry in overrides.All())
        {
          if (!rows.ContainsKey(entry.Key)) AddRow(rows, entry.Key, timeline, overrides, registry, priors, 0, 0, 0, hasFacts: false);
        }
      }
      if (registry is not null)
      {
        foreach (var name in RosterNames(registry))
        {
          if (!rows.ContainsKey(name)) AddRow(rows, name, timeline, overrides, registry, priors, 0, 0, 0, hasFacts: false);
        }
      }

      var list = new List<Row>(rows.Values);
      list.Sort(static (a, b) =>
      {
        var rank = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
        if (rank != 0) return rank;
        var byWork = (b.Damage + b.Healing).CompareTo(a.Damage + a.Healing);
        return byWork != 0 ? byWork : string.CompareOrdinal(a.Name, b.Name);
      });

      var report = new ClassificationReport(
        list,
        totalFacts: (damageFacts?.FactCount ?? 0) + (healFacts?.HealCount ?? 0),
        players: Count(list, IdentityKind.Player),
        pets: Count(list, IdentityKind.Pet),
        mercs: Count(list, IdentityKind.Merc),
        npcs: Count(list, IdentityKind.Npc),
        unknown: Count(list, IdentityKind.Unknown),
        rejected: list.Count(static r => r.IsRejected),
        operatorVerdicts: list.Count(static r => r.IsOperatorVerdict),
        disagreements: list.Count(static r => r.IsDisagreement),
        totalNames: list.Count,
        unresolvedInCapture: list.Count(static r => r.HasFacts && r.IsUnresolved));
      return report;
    }

    /*
     * The names the roster knows that this log never mentioned: the verified list (which also holds the typed rows,
     * stamped at load), every rejection, and both halves of every saved pet pair — a mapping whose owner is absent
     * from this capture is exactly the stale row worth making visible instead of letting it fold into a pet with no
     * owner. Copies, not views: these dictionaries are being written by the parser while a window enumerates, and the
     * census wants one consistent picture. Mercs are not listed here because nothing persists them — a merc only ever
     * appears through lines, so it is already in the pool.
     */
    private static IEnumerable<string> RosterNames(PlayerRegistry registry)
    {
      foreach (var name in registry.GetVerifiedPlayers()) yield return name;
      foreach (var name in registry.GetRejectedPlayers()) yield return name;
      foreach (var map in registry.GetPetMappings())
      {
        yield return map.Pet;
        yield return map.Owner;
      }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static Row AddRow(Dictionary<string, Row> rows, string name, EntityTimeline? timeline,
                              MirrorOverrideStore? overrides, PlayerRegistry? registry, IdentityPriorStore? priors,
                              double damage, double healing, long events, bool hasFacts)
    {
      var kind = IdentityKind.Unknown;
      string source = string.Empty;
      if (timeline is not null)
      {
        kind = timeline.IdentityWithSource(name, out var why);
        source = why ?? string.Empty;
      }

      // No timeline (window opened before the first derive) still has to show an operator's own verdict rather than
      // "Unknown", or the list contradicts the file it was built from.
      IdentityKind verdict = IdentityKind.Unknown;
      var isOperator = overrides is not null && overrides.TryGet(name, out verdict);
      if (isOperator && kind == IdentityKind.Unknown)
      {
        kind = verdict;
        source = "Manual";
      }

      var rejected = registry?.IsRejectedPlayer(name) ?? false;

      // Last resort, and deliberately behind both the rules and the operator: borrow yesterday's verdict only when
      // this capture said nothing about the name AND nobody has claimed it. A rejected name gets nothing at all -
      // "no claim" is the operator speaking about exactly this situation.
      int priorSightings = 0;
      long priorSeenAt = 0;
      var isPrior = false;
      if (kind == IdentityKind.Unknown && !isOperator && !rejected
          && priors is not null && priors.TryGet(name, out var prior))
      {
        kind = prior.Kind;
        source = $"Prior:{prior.Reason}";
        isPrior = true;
        priorSightings = prior.Sightings;
        priorSeenAt = prior.SeenAtS;
      }

      var playerClass = NullIfEmpty(registry?.GetDefaultPlayerClass(name));
      var petOwner = NullIfEmpty(registry?.GetPlayerFromPet(name));
      var legacyPlayer = registry?.IsVerifiedPlayer(name) ?? false;
      double lastSeen = 0;
      var known = registry is not null && registry.TryGetVerifiedEntry(name, out lastSeen);
      var typed = !known || lastSeen <= 0;   // no timestamp = somebody typed this row
      if (typed) lastSeen = 0;

      var row = new Row
      {
        Name = name,
        Kind = kind,
        Reason = source,
        IsOperatorVerdict = isOperator,
        IsRejected = rejected,
        Class = playerClass,
        TypedEntry = typed,
        LastSeenUnixSeconds = lastSeen,
        PetOwner = petOwner,
        LegacySaysPlayer = legacyPlayer,
        HasFacts = hasFacts,
        IsPrior = isPrior,
        PriorSightings = priorSightings,
        PriorSeenAtS = priorSeenAt,
        Damage = damage,
        Healing = healing,
        Events = events,
      };
      rows[name] = row;
      return row;
    }

    private static int KindRank(IdentityKind kind) => kind switch
    {
      IdentityKind.Player => 0,
      IdentityKind.Pet => 1,
      IdentityKind.Merc => 2,
      IdentityKind.Npc => 3,
      _ => 4,
    };

    private static int Count(IReadOnlyList<Row> rows, IdentityKind kind)
    {
      var n = 0;
      foreach (var r in rows) if (r.Kind == kind) n++;
      return n;
    }
  }

  /*
   * The three things an operator can DO about a name, in one place, so the Names window and the fight grids'
   * right-click menus cannot drift into writing different files again (they do today: "Add player" writes
   * players.txt while "Set as Pet" writes mirror-overrides.txt).
   *
   * Every command leaves the capture untouched — these are readings, not edits of history — so the caller re-runs
   * the derive afterwards (MirrorSession.RunDeriveAsync) and the whole board updates without a re-parse. Nothing here
   * touches a dispatcher: keep it callable from a test and from a background command.
   */
  internal static class ClassificationCommands
  {
    /// <summary>"That is a player / pet / merc / NPC." Writes the verdict that outranks every rule, and drops a
    /// rejection: an explicit claim supersedes "make no claim about this name".</summary>
    public static void SetVerdict(MirrorOverrideStore overrides, PlayerRegistry registry, string name, IdentityKind kind)
    {
      overrides.Set(name, kind);
      registry.ClearRejectedPlayer(name);
    }

    /// <summary>"I was wrong, let the rules answer again." The rules' own conclusion comes back on the next derive;
    /// a roster entry learned from loot lines is NOT deleted by this — that is what Reject is for.</summary>
    public static void ClearVerdict(MirrorOverrideStore overrides, string name) => overrides.Remove(name);

    /// <summary>"Forget what this server's older logs concluded about this name." Removes the ledger row only —
    /// verdicts, roster membership and pet mappings are separate files and stay as they are. With no entry left the
    /// name reads Unknown again on a capture whose own lines say nothing.</summary>
    public static void ClearPrior(IdentityPriorStore priors, string name) => priors.Remove(name);

    /// <summary>"Not a player, and stop guessing." No identity claim at all (see Row.IsRejected), so the rules can
    /// still conclude something from evidence in this log; pet mappings are left alone.</summary>
    public static void Reject(MirrorOverrideStore overrides, PlayerRegistry registry, string name)
    {
      overrides.Remove(name);
      // The roster's own removal verb IS the tombstone: it writes `!Name`, refuses the learning paths from here on,
      // and (since 2026-08) leaves pet mappings alone.
      registry.RemoveVerifiedPlayer(name);
    }
  }
}

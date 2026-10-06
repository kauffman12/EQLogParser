using System;
using System.Collections.Generic;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in optional
 * strings/kinds because a name legitimately has no class, no owner and no verdict. Stating that is not the same as
 * switching on warnings across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
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

      /// <summary>This name carries an operator verdict in identity-overrides.txt (and can be reverted).</summary>
      public bool IsOperatorVerdict { get; init; }

      /// <summary>Class for this name: what the class engine last recorded - a spell-learned class wins over
      /// the roster block or ability-word default; null when none of them supplied one.</summary>
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

      /*
       * The concrete thing a SPELL-BASED verdict came from, e.g. "Boastful Bellow I.". R4 and R20 claim a name from a
       * cast line and their reason tag says only which rule fired, so the census walks the evidence rows to name the
       * cast itself: the FIRST class-safe/pet-safe cast of that name, which is the claim the timeline resolved to.
       *
       * Display detail and nothing more — no verdict, digest or file reads it, which is why the rule tags themselves
       * stay clean vocabulary words (`R4-spell`, not `R4-spell:Boastful Bellow`): the digest hashes sources, the prior
       * ledger persists them, and a spell name in either would be a format change to identity's own language for the
       * sake of a tooltip. Null for every rule that is not spell-based.
       */
      public string? ReasonDetail { get; init; }

      /*
       * How many DIFFERENT raid-side names healed this name. R15 decides on BREADTH (so many distinct Strong casters,
       * not so many lines), so this is the number that actually answered "why does that mob-shaped name read as one of
       * ours?" — worth a tooltip line and, like ReasonDetail, read by nothing but the window.
       *
       * It is filled for heal-based verdicts only (see HealCasterProof). On every other row 0 means "not asked", NOT
       * "nobody healed them": a name the whole raid pets but R1-target already placed gets no walk over the heal stream.
       */
      public int HealedByCasters { get; init; }

      /// <summary>Sum of this name's damage facts (its own hits; a defender gets no credit for being hit).</summary>
      /// <summary>Damage facts this name dealt whose DEFENDER reads player-side. Feeds the hover's "Damaged players".</summary>
      public int HitsOnRaid { get; init; }

      /// <summary>Damage facts this name dealt whose defender reads Npc — the other half of that clause.</summary>
      public int HitsOnMobs { get; init; }

      /// <summary>The Type cell's word: the kind's word, plus direction for a Spell ("Enemy Spell" / "Our Spell").</summary>
      public string TypeDisplay { get; init; } = string.Empty;

      /*
       * Everything ELSE that applies to this name, one short phrase per line (up to four, so the hover plus its own proof line
       * stays a five-line note): the other rules that claimed it, ranked by how directly the app knows, then what the capture
       * watched it do. Empty for a name one rule claimed and whose facts point nowhere — most rows are exactly that, and they
       * hover as the single sentence they always did. Built in Core so the words are testable anywhere, retained as ONE string
       * per row (the pane already kept one) rather than a list per row: no new per-row allocation on a 4,000-name census.
       */
      public string OtherEvidence { get; init; } = string.Empty;

      public double Damage { get; init; }

      /// <summary>Sum of this name's heal facts, over-heal excluded — Total is what landed.</summary>
      public double Healing { get; init; }

      /// <summary>Facts with this name as attacker or healer: how busy the row was, not how effective.</summary>
      public long Events { get; init; }

      /*
       * The roster says "one of ours" and the classifier says NPC. That direction is the only real disagreement, and
       * it is the thing an operator acts on: a raid member pushed to the enemy column loses her damage on the flip.
       *
       * A ROW-level fact and nothing more. It used to feed a `Disagreements` counter that a header strip printed above the
       * grid; the strip was removed on request (the pane is the table alone), so counting these became a number with no
       * place to be read - and "counted somewhere invisible" is not a reason to keep computing it. If a surface for the
       * whole-capture count ever comes back, this property is its input.
       *
       * Unknown is deliberately NOT a disagreement — it means "this capture has no opinion", which is what every
       * guild alt who sat out this log reads, along with the 12-42% of facts whose names legacy never verified
       * (docs/DesignNotes.md → "What a capture proves with empty memory"). Flagging absence would paint the list red and bury the rows that are actual
       * contradictions. Pets and mercs are excluded for the same reason they are not contradictions: players.txt
       * legitimately holds pet OWNER names, and a pet/merc verdict costs its owner nothing.
       */
      public bool IsDisagreement => LegacySaysPlayer && Kind is IdentityKind.Npc or IdentityKind.Spell;

      /*
       * Nothing has any claim on this name: no kind from this capture's lines, no operator verdict, not in the roster.
       * These are the rows the Names window sorts last
       * and the only ones worth spending an override on - a busy name nobody can place is where the rules are missing
       * something, while an Unknown guild alt who sat the fight out is just an alt.
       */
      public bool IsUnresolved => Kind == IdentityKind.Unknown && Class is null;

      /*
       * Whether an operator may still change this row's Type — false where the answer is forced by the name itself, so the
       * window shows no pencil instead of offering three wrong answers next to the one right one (a summon whose spelling
       * carries its owner, or a spell effect that is not a fighter). See IdentityVocabulary.CanOverrule for the two cases
       * and for why an operator's own verdict always keeps its icon: taking a claim back has to stay possible.
       */
      public bool Overrulable => IdentityVocabulary.CanOverrule(Name, Reason);
    }

    /// <summary>Rows in display order: raid-side kinds first, then busiest, then by name.</summary>
    public IReadOnlyList<Row> Rows { get; }

    /*
     * The census's own counters. NOTHING on screen prints them: the Names window is the grid alone (no header, no status
     * line — see docs/DesignNotes.md), so these are diagnostics, asserted by tests and available to whoever puts a surface
     * back. The three that had neither a surface nor a single test reader (TotalFacts, OperatorVerdicts, Disagreements) are
     * gone rather than parked; the rest stay because a classification pass is judged by its shape, and this is that shape.
     */
    /// <summary>Every name the census lists: fact pool plus every hand-written row.</summary>
    public int TotalNames { get; }

    public int Players { get; }
    public int Pets { get; }
    public int Mercs { get; }
    public int Npcs { get; }

    /// <summary>Names the capture identified as a spell standing in a fighter's slot (R21). Computed, not printed: the pane
    /// shows no counters (docs/DesignNotes.md), and this one exists so a test can hold the kind's population steady.</summary>
    public int Spells { get; }

    public int Unknown { get; }

    /*
     * Names the capture itself could not place, counting only rows that appear in the fact streams. The distinction is
     * the whole point: Unnamed spans the roster too (a verified player absent from this fight reads Unknown), so a plain
     * "unknown" count overstates the classifier's gap by every alt and idle pet. Rows held up by a prior are excluded -
     * those got an answer, just not from this file - which is why IsPrior exists separately.
     */
    public int UnresolvedInCapture { get; }

    private ClassificationReport(IReadOnlyList<Row> rows, int players, int pets, int mercs, int npcs, int spells,
                                 int unknown, int totalNames, int unresolvedInCapture)
    {
      TotalNames = totalNames;
      UnresolvedInCapture = unresolvedInCapture;
      Rows = rows;
      Players = players;
      Pets = pets;
      Mercs = mercs;
      Npcs = npcs;
      Spells = spells;
      Unknown = unknown;
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
                                             HealFactTable? healFacts, IdentityOverrideStore? overrides,
                                             PlayerRegistry? registry, IdentityPriorStore? priors = null)
    {
      var names = damageFacts?.InternedNames;
      var count = names?.Count ?? 0;

      double[] damage = count > 0 ? new double[count] : [];
      double[] healing = count > 0 ? new double[count] : [];
      long[] events = count > 0 ? new long[count] : [];

      /*
       * WHOSE SIDE each member of the name pool reads, resolved ONCE per name into a pool-sized array (a few thousand lookups,
       * ~3 KB per kind) so the fact walk below stays two array reads and an increment: no dictionary touched per fact. On a
       * 2.27M-fact capture that is the difference between free and another fifth of a second on a window's census, which is why
       * this is not written as `timeline.IdentityAt(defender, t)` inside the loop. The FINAL (+infinity) verdict is what counts,
       * so the sentence a row prints can never contradict the Type column beside it; charm windows are deliberately not
       * consulted — "Damaged players" means "hit names this capture called players", the cheap claim and the honest one.
       */
      IdentityKind[] verdict = count > 0 ? new IdentityKind[count] : [];
      int[] onRaid = count > 0 ? new int[count] : [];
      int[] onMobs = count > 0 ? new int[count] : [];

      if (timeline is not null && count > 0)
      {
        for (short i = 0; i < names!.Count; i++) verdict[i] = timeline.IdentityAt(names[i], double.PositiveInfinity);
      }

      if (damageFacts is not null)
      {
        foreach (var f in damageFacts.Facts)
        {
          damage[f.AtkIdx] += f.Total;
          events[f.AtkIdx]++;

          var defKind = verdict[f.DefIdx];
          if (defKind is IdentityKind.Player or IdentityKind.Merc or IdentityKind.Pet) onRaid[f.AtkIdx]++;
          else if (defKind is IdentityKind.Npc) onMobs[f.AtkIdx]++;
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

      // Which cast earned each spell-based verdict (see Row.ReasonDetail). One walk over the evidence rows — identity
      // evidence is sparse by design ("common, but not per-hit") and only cast lines the R4/R20 gates accept are kept.
      var castProof = BuildCastProof(damageFacts);

      // What this capture watched people CAST, gathered only when cross-log memory is in play (see AddRow). One walk over the
      // evidence rows alongside BuildCastProof's; nothing at all when there is no ledger to read.
      var seenCasts = BuildCastNames(damageFacts, priors);

      // How wide a heal-based verdict's crowd was (see Row.HealedByCasters). Asks for nothing until a row reads
      // "R15-healed", so the walk is paid only by captures that actually rest on that rule.
      var healProof = new HealCasterProof(timeline, healFacts);

      // Index by display name, case-insensitively: the pool keeps one id per entity however the line spelled it, but
      // registry and override rows are their own strings and have to fold onto that same row.
      var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
      if (names is not null)
      {
        for (short i = 0; i < names.Count; i++)
        {
          AddRow(rows, names[i], timeline, overrides, registry, priors, castProof, seenCasts, healProof, damage[i], healing[i],
                 events[i], hasFacts: true, hitsOnRaid: onRaid[i], hitsOnMobs: onMobs[i]);
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
          if (!rows.ContainsKey(entry.Key)) AddRow(rows, entry.Key, timeline, overrides, registry, priors, castProof, seenCasts, healProof, 0, 0, 0, hasFacts: false);
        }
      }
      if (registry is not null)
      {
        foreach (var name in RosterNames(registry))
        {
          if (!rows.ContainsKey(name)) AddRow(rows, name, timeline, overrides, registry, priors, castProof, seenCasts, healProof, 0, 0, 0, hasFacts: false);
        }
      }

      /*
       * And the ledger's names, for the same reason. A name this server's earlier logs concluded on, which this capture
       * never mentions, belongs on the list with its prior verdict beside it — that is the entire use of cross-log
       * memory: an operator looking up a mob they have seen before gets an answer instead of an empty search box.
       *
       * Display only, exactly like every other prior: the rules ran without it (ClassificationRules never reads the
       * ledger), and the row says where its Kind came from ("Prior:R6-npcdb") and that no fact in this log backs it.
       */
      if (priors is not null)
      {
        foreach (var entry in priors.All())
        {
          if (!rows.ContainsKey(entry.Key)) AddRow(rows, entry.Key, timeline, overrides, registry, priors, castProof, seenCasts, healProof, 0, 0, 0, hasFacts: false);
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
        players: Count(list, IdentityKind.Player),
        pets: Count(list, IdentityKind.Pet),
        mercs: Count(list, IdentityKind.Merc),
        npcs: Count(list, IdentityKind.Npc),
        spells: Count(list, IdentityKind.Spell),
        unknown: Count(list, IdentityKind.Unknown),
        totalNames: list.Count,
        unresolvedInCapture: list.Count(static r => r.HasFacts && r.IsUnresolved));
      return report;
    }

    /*
     * The names the roster knows that this log never mentioned: the verified list (which also holds the typed rows,
     * stamped at load) and both halves of every saved pet pair — a mapping whose owner is absent
     * from this capture is exactly the stale row worth making visible instead of letting it fold into a pet with no
     * owner. Copies, not views: these dictionaries are being written by the parser while a window enumerates, and the
     * census wants one consistent picture. Mercs are not listed here because nothing persists them — a merc only ever
     * appears through lines, so it is already in the pool.
     */
    private static IEnumerable<string> RosterNames(PlayerRegistry registry)
    {
      foreach (var name in registry.GetVerifiedPlayers()) yield return name;
      foreach (var map in registry.GetPetMappings())
      {
        yield return map.Pet;
        yield return map.Owner;
      }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /*
     * Which cast earned a spell-based verdict, kept SEPARATELY PER RULE GATE because the two gates claim different
     * kinds: `IsClassSafeCast` is R4's (the caster is a player) and `IsPetCastSpell` is R20's (the caster is somebody's
     * pet). One shared "first accepted cast" map would let a name that cast both — the pet-spell line first, the class
     * line later — read "R4-spell" beside the PET's spell, crediting the wrong line for the verdict. First per gate,
     * because that is the claim the timeline resolved to: a later restatement is what SetIdentity's dedupe drops.
     *
     * A third spell rule has to be listed here too (and in Row.ReasonDetail's own gate); a name missing from these maps
     * simply gets no detail.
     */
    private sealed class CastProof
    {
      private Dictionary<string, string>? _classSafe;
      private Dictionary<string, string>? _petSpell;

      /// <summary>The first cast THIS capture's spell rules accepted for this name, chosen by the rule that won.</summary>
      public string? For(string source, string name) => source switch
      {
        _ when source.StartsWith("R4-spell", StringComparison.Ordinal) => Look(_classSafe, name),
        _ when source.StartsWith("R20-petspell", StringComparison.Ordinal) => Look(_petSpell, name),
        _ => null,   // every other rule has no cast behind it, and "Prior:R4-spell" is another log's conclusion
      };

      public void AddClassSafe(string caster, string spell) => (_classSafe ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)).TryAdd(caster, spell);

      public void AddPetSpell(string caster, string spell) => (_petSpell ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)).TryAdd(caster, spell);

      private static string? Look(Dictionary<string, string>? map, string name)
        => map is not null && map.TryGetValue(name, out var spell) ? spell : null;
    }

    /// <summary>First accepted cast per caster and gate, for Row.ReasonDetail. Empty when the capture has no evidence rows.</summary>
    /*
     * The spell names this capture saw being cast (`X begins casting Y.`), read off the same evidence rows BuildCastProof walks.
     * Returned only when a prior store will actually be consulted, so the ordinary pass pays nothing: with no ledger there is no
     * remembered verdict for these names to argue with, and the answer would never be displayed.
     */
    private static HashSet<string>? BuildCastNames(DamageFactTable? facts, IdentityPriorStore? priors)
    {
      if (facts is null || priors is null || priors.Count == 0) return null;

      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var evidence = facts.Evidence;
      for (var i = 0; i < evidence.Length; i++)
      {
        if (evidence[i].Kind != EvidenceFact.EvCast) continue;
        var spell = facts.AuxOf(evidence[i].AuxIdx);
        if (!string.IsNullOrEmpty(spell)) seen.Add(spell!);
      }
      return seen;
    }

    private static CastProof BuildCastProof(DamageFactTable? facts)
    {
      var proof = new CastProof();
      if (facts is null || facts.EvidenceCount == 0) return proof;

      foreach (var e in facts.Evidence)
      {
        if (e.Kind != EvidenceFact.EvCast) continue;
        var spell = facts.AuxOf(e.AuxIdx);
        if (string.IsNullOrEmpty(spell)) continue;
        var caster = facts.NameOf(e.NameIdx);
        if (string.IsNullOrEmpty(caster)) continue;

        // THE SAME GATES the rules apply, or the tooltip would name a cast that claimed nothing.
        if (ClassificationRules.IsClassSafeCast(spell!)) proof.AddClassSafe(caster!, spell!);
        else if (ClassificationRules.IsPetCastSpell(spell!)) proof.AddPetSpell(caster!, spell!);
      }
      return proof;
    }

    private static Row AddRow(Dictionary<string, Row> rows, string name, EntityTimeline? timeline,
                              IdentityOverrideStore? overrides, PlayerRegistry? registry, IdentityPriorStore? priors,
                              CastProof castProof, HashSet<string>? seenCasts, HealCasterProof healProof,
                              double damage, double healing, long events, bool hasFacts,
                              int hitsOnRaid = 0, int hitsOnMobs = 0)
    {
      var kind = IdentityKind.Unknown;
      string source = string.Empty;
      if (timeline is not null)
      {
        kind = timeline.IdentityWithSource(name, out var why);
        source = why ?? string.Empty;
      }

      /*
       * The operator's own word outranks the rules. It always did in practice - DeriveEngine.Classify replays identity-overrides.txt
       * into every timeline at Manual strength, so the timeline usually already says it - but that made the report depend on WHICH
       * timeline it was handed. The Names pane now reads the timeline the last expensive pass published instead of re-running the rule
       * book for itself (docs/DesignNotes.md → "The Names census reads the derive's own timeline"), and a name overruled since that pass
       * would otherwise answer with the verdict the player just rejected. Saying it here costs one dictionary lookup per row and removes
       * a hidden precondition from every caller.
       *
       * When the two agree, the source the rules wrote stays: "R10-manual" is the truer provenance than "Manual" when the timeline really
       * did carry the claim, and the Why column's word is what a reader greps for.
       */
      IdentityKind verdict = IdentityKind.Unknown;
      var isOperator = overrides is not null && overrides.TryGet(name, out verdict);
      if (isOperator && kind != verdict)
      {
        kind = verdict;
        source = "Manual";
      }

      // Last resort, and deliberately behind both the rules and the operator: borrow yesterday's verdict only when
      // this capture said nothing about the name AND nobody has claimed it.
      int priorSightings = 0;
      long priorSeenAt = 0;
      var isPrior = false;
      if (kind == IdentityKind.Unknown && !isOperator
          && priors is not null && priors.TryGet(name, out var prior))
      {
        /*
         * Memory steps aside for what this file watched happen. `X begins casting Y.` is the game itself naming Y as a spell, and
         * an older build — or a night whose rules were weaker — could remember such a name as a fighter: the report a player
         * measured listed "Asphyxiating Grasp Rk. III" (a Magian discipline's rank) among the raid's opponents with yesterday's
         * Player verdict behind it. Borrowing an answer is only right while NOTHING fresher speaks, and this capture saw the cast,
         * so it says A Spell here rather than re-stating the ledger. Deliberately not a timeline claim: that name has no facts in
         * this log (which is why memory produced its row in the first place), and an entry for it would move the derive's state
         * digest — buying a full rebuild every pass over a verdict no board reads. Measured on eqlog_Kizant_xegony-09-20-25.txt:
         * 1,044 cast tokens, 0 of them named in a combat line; the correction is for the names memory already put on the list.
         */
        if (seenCasts is not null && seenCasts.Contains(name))
        {
          kind = IdentityKind.Spell;
          source = "R21-spellcast";
        }
        else
        {
          kind = prior.Kind;
          source = $"Prior:{prior.Reason}";
          isPrior = true;
          priorSightings = prior.Sightings;
          priorSeenAt = prior.SeenAtS;
        }
      }

      // Last-known, not just the default: GetDefaultPlayerClass sees only the roster block and ability words,
      // so every class the engine LEARNED from casting (CastLineParser's records - including what R4's
      // signature families write) was invisible in the one window built to show identities. The legacy
      // Verified Players grid displayed learned classes; the replacement must not read less than it did.
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
        // Named by the rule that actually fired (see CastProof): only a verdict THIS capture's spell rules wrote gets a
        // cast beside it, and never one that claimed the other kind.
        ReasonDetail = castProof.For(source, name),
        HealedByCasters = healProof.For(source, name),
        IsOperatorVerdict = isOperator,
        Class = NullIfEmpty(registry?.GetLastKnownPlayerClass(name)),
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
        HitsOnRaid = hitsOnRaid,
        HitsOnMobs = hitsOnMobs,
        TypeDisplay = IdentityVocabulary.TypeWordFor(kind, hitsOnRaid, hitsOnMobs),
        OtherEvidence = BuildOtherEvidence(timeline, name, source, hitsOnRaid, hitsOnMobs),
      };
      rows[name] = row;
      return row;
    }

    /*
     * Distinct raid-side casters per healed name, for Row.HealedByCasters.
     *
     * LAZY ON PURPOSE. The walk is the expensive kind — one `IdentityAt` per heal line, because R15 counts only casters
     * whose own identity is already Strong and raid-side — and a capture in which no name rests on R15 should not pay
     * for it just because somebody opened a window. The first row that reads "R15-healed" builds the table; every later
     * one reuses it.
     *
     * THE SAME GATES R15 APPLIES (self-heals skipped, healer Strong + raid-side via ClassificationRules.IsRaidSideKind),
     * or the tooltip would count a crowd the rule did not require — mob healers included, which is how a boss healed by
     * its own cleric could report "healed by 20 players". The MINIMA themselves are not re-checked here: this number
     * describes a verdict the rules already made, it does not second-guess it.
     */
    private sealed class HealCasterProof
    {
      private readonly EntityTimeline? _timeline;
      private readonly HealFactTable? _heals;
      private Dictionary<string, HashSet<string>>? _byName;

      public HealCasterProof(EntityTimeline? timeline, HealFactTable? heals)
      {
        _timeline = timeline;
        _heals = heals;
      }

      /// <summary>Caster breadth for a heal-based verdict; answers 0 without touching the store for any other rule.</summary>
      public int For(string? source, string name)
      {
        if (source is not { Length: > 0 } || !source.StartsWith("R15-healed", StringComparison.Ordinal)) return 0;
        Ensure();
        return _byName is not null && _byName.TryGetValue(name, out var casters) ? casters.Count : 0;
      }

      private void Ensure()
      {
        if (_byName is not null) return;
        var table = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        _byName = table;

        if (_timeline is null || _heals is null) return;
        var heals = _heals.Heals;
        for (var i = 0; i < heals.Length; i++)
        {
          var h = heals[i];
          var healer = _heals.NameOf(h.HealerIdx);
          var healed = _heals.NameOf(h.HealedIdx);
          if (healer == healed) continue;                       // a self-heal says nothing about who tends them

          var hk = _timeline.IdentityAt(healer, h.TimeS, out var hs, out _);
          if (hs < RuleStrength.Strong || !ClassificationRules.IsRaidSideKind(hk)) continue;

          if (!table.TryGetValue(healed, out var casters)) casters = table[healed] = new HashSet<string>(StringComparer.Ordinal);
          casters.Add(healer);
        }
      }
    }

    /*
     * The hover's extra lines: every other claim on the name, plus the one fact clause, ranked and capped.
     *
     * Cost is per NAME, not per fact — ClaimsOf hands back the timeline's own list without copying it (the caller must not
     * mutate), ProofText maps to interned literals, and the whole thing returns a single string (empty in the common case of one
     * claim and no direction). A 4,000-name census allocates a handful of small lists for the rows that have several claims and
     * nothing at all for the rest.
     */
    private static string BuildOtherEvidence(EntityTimeline? timeline, string name, string winningSource,
                                             int hitsOnRaid, int hitsOnMobs)
    {
      const int maxExtraLines = 4;   // IdentityVocabulary/NamesTable prints the head proof line; five lines is the budget

      if (timeline is null) return string.Empty;
      var claims = timeline.ClaimsOf(name);
      var direction = IdentityVocabulary.DirectionPhrase(hitsOnRaid, hitsOnMobs);
      if (claims.Count == 0 && direction is null) return string.Empty;

      List<(int Rank, int Strength, string Phrase)>? lines = null;
      var seenPhrases = new HashSet<string>(StringComparer.Ordinal);

      foreach (var claim in claims)
      {
        // The winner is the head line the pane already prints; listing it twice would be padding.
        if (string.Equals(claim.Source, winningSource, StringComparison.Ordinal)) continue;

        var phrase = IdentityVocabulary.ProofText(claim.Source, claim.Kind);
        if (phrase.Length == 0 || !seenPhrases.Add(phrase)) continue;
        (lines ??= new List<(int, int, string)>()).Add((IdentityVocabulary.ClaimRank(claim.Source), claim.Strength, phrase));
      }

      // The fact clause competes with the claims on rank rather than always trailing them: for a Spell row it is the sentence
      // the reader came for ("Damaged players"), and it outranks the R21 shape claim that got the row its kind.
      if (direction is not null && seenPhrases.Add(direction))
        (lines ??= new List<(int, int, string)>()).Add((IdentityVocabulary.FactClauseRank, 0, direction));

      if (lines is null || lines.Count == 0) return string.Empty;

      var ordered = lines.OrderBy(l => l.Rank, Comparer<int>.Create(static (a, b) => b.CompareTo(a)))
                         .ThenByDescending(l => l.Strength)
                         .ThenBy(l => l.Phrase, StringComparer.Ordinal)
                         .Take(maxExtraLines);
      return string.Join('\n', ordered.Select(l => l.Phrase));
    }

    private static int KindRank(IdentityKind kind) => kind switch
    {
      IdentityKind.Player => 0,
      IdentityKind.Pet => 1,
      IdentityKind.Merc => 2,
      IdentityKind.Npc => 3,
      IdentityKind.Spell => 4,
      _ => 5,
    };

    private static int Count(IReadOnlyList<Row> rows, IdentityKind kind)
    {
      var n = 0;
      foreach (var r in rows) if (r.Kind == kind) n++;
      return n;
    }
  }

  /*
   * The two things an operator can DO about a name in this engine, in one place, so the Names window and the fight grids'
   * right-click menus cannot drift into writing different files again (they do today: "Add player" writes
   * players.txt while "Set as Pet" writes identity-overrides.txt).
   *
   * Every command leaves the capture untouched — these are readings, not edits of history — so the caller re-runs
   * the derive afterwards (DeriveEngine.RunDeriveAsync) and the whole board updates without a re-parse. Nothing here
   * touches a dispatcher: keep it callable from a test and from a background command.
   */
  internal static class ClassificationCommands
  {
    /// <summary>"That is a player / pet / merc / NPC." Writes the verdict that outranks every rule.</summary>
    public static void SetVerdict(IdentityOverrideStore overrides, string name, IdentityKind kind) =>
      overrides.Set(name, kind);

    /// <summary>"I was wrong, let the rules answer again." The rules' own conclusion comes back on the next derive;
    /// a roster entry learned from loot lines is NOT deleted by this — it takes a verdict of the other kind, or the
    /// roster's own removal, which lives on PlayerRegistry.</summary>
    public static void ClearVerdict(IdentityOverrideStore overrides, string name) => overrides.Remove(name);

    /// <summary>"Forget what this server's older logs concluded about this name." Removes the ledger row only —
    /// verdicts, roster membership and pet mappings are separate files and stay as they are. With no entry left the
    /// name reads Unknown again on a capture whose own lines say nothing.</summary>
    public static void ClearPrior(IdentityPriorStore priors, string name) => priors.Remove(name);

  }
}

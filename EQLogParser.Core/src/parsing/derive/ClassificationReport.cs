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
       * It is filled for heal-based verdicts only (see CensusCarry.CasterBreadth). On every other row 0 means "not asked", NOT
       * "nobody healed them": a name the whole raid pets but R1-target already placed gets no walk over the heal stream.
       */
      public int HealedByCasters { get; init; }

      /// <summary>Sum of this name's damage facts (its own hits; a defender gets no credit for being hit).</summary>
      /// <summary>Damage facts this name dealt whose DEFENDER reads player-side. Feeds the hover's "Damaged Players".</summary>
      public int HitsOnRaid { get; init; }

      /// <summary>Damage facts this name dealt whose defender reads Npc — the other half of that clause.</summary>
      public int HitsOnMobs { get; init; }

      /// <summary>Damage facts whose defender reads Pet — its own victim group, because "players" is not the word for them.</summary>
      public int HitsOnPets { get; init; }

      /// <summary>Damage facts where attacker and defender are the SAME name: a reflect, a feedback, a beam bounced off one's own chest.</summary>
      public int SelfHits { get; init; }

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
     * A supplied carry folds only new ordinals and re-buckets hit pairs under current verdicts; without one, the fold starts at zero.
     * Nothing here re-parses or re-derives. Costs and invalidation: docs/DesignNotes.md →
     * "A census carries what it already counted; only the healer crowd asks a stamp".
     */
    /*
     * `priors` is optional and only ever FILLS A GAP: a name this capture's rules could not place may borrow what an
     * earlier capture on the same server concluded (IdentityPriorStore). That is the whole extent of cross-log memory
     * in this build — fight rows and every other consumer still see a timeline derived from this file alone, because R7
     * builds sides out of what the timeline knows and yesterday's guess must not become today's evidence.
     */
    public static ClassificationReport Build(EntityTimeline? timeline, DamageFactTable? damageFacts,
                                             HealFactTable? healFacts, IdentityOverrideStore? overrides,
                                             PlayerRegistry? registry, IdentityPriorStore? priors = null, CensusCarry? carry = null)
    {
            /*
             * ONE boundary, read in THIS order: the fact bound first, then the pool size. The census can run mid-read, and every id under that bound was
             * interned before its fact was published, so a pool count taken afterwards covers the walk. Taking it first sized the arrays behind a name
             * still being added and indexed past the end (CensusGrowingPoolTest). A stale answer is fine; a crash stops the list updating.
             */
      var factLimit = damageFacts?.FactCount ?? 0;
      var healLimit = healFacts?.HealCount ?? 0;
      var names = damageFacts?.InternedNames;
      var count = names?.Count ?? 0;

      // A caller without a carry folds from zero through the same path. RetainDecoded keeps decoded chunks through the fold;
      // the engine holds an outer lease through row construction too, including the lazy healer proof.
      // docs/DesignNotes.md → "A census carries what it already counted; only the healer crowd asks a stamp".
      var scan = carry ?? new CensusCarry();
      using (damageFacts?.RetainDecoded())
      using (healFacts?.RetainDecoded())
      {
        // Folded even with no stores at all, so a carry can never keep an answer — or a table reference — from a store this census does not have.
        scan.Fold(damageFacts, healFacts, timeline, names, count, factLimit, healLimit);
      }

      var damage = scan.Damage;
      var healing = scan.Healing;
      var events = scan.Events;
      int[] onRaid = scan.OnRaid;
      int[] onMobs = scan.OnMobs;
      int[] onPets = scan.OnPets;
      int[] onSelf = scan.SelfHits;

      // Index by display name, case-insensitively: the pool keeps one id per entity however the line spelled it, but
      // registry and override rows are their own strings and have to fold onto that same row.
      var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
      if (names is not null)
      {
        // The snapshot, not names.Count: the pool may have grown since this census chose its boundary, and a row built from an
        // index past the arrays is exactly the crash this whole block exists to prevent.
        for (var i = 0; i < count; i++)
        {
          AddRow(rows, names[i], timeline, overrides, registry, priors, scan, damage[i], healing[i],
                 events[i], hasFacts: true, hitsOnRaid: onRaid[i], hitsOnNpcs: onMobs[i],
                 hitsOnPets: onPets[i], hitsOnSelf: onSelf[i]);
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
          if (!rows.ContainsKey(entry.Key)) AddRow(rows, entry.Key, timeline, overrides, registry, priors, scan, 0, 0, 0, hasFacts: false);
        }
      }
      if (registry is not null)
      {
        foreach (var name in RosterNames(registry))
        {
          if (!rows.ContainsKey(name)) AddRow(rows, name, timeline, overrides, registry, priors, scan, 0, 0, 0, hasFacts: false);
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
          if (!rows.ContainsKey(entry.Key)) AddRow(rows, entry.Key, timeline, overrides, registry, priors, scan, 0, 0, 0, hasFacts: false);
        }
      }

      /*
       * And the names whose claim the operator just took back (2026-11). The take-back REMOVES — override, ledger row, registry
       * claims — so on a name whose every claim came from memory there is nothing left for any of the four sources above to list,
       * and the row the click was made on disappeared out of the window the click happened in. That reads as a delete, which is the
       * one thing this verb is not; the operator asked instead that such a name "be switched to Unknown" (docs/DesignNotes.md →
       * "A take-back keeps its row, reading what it did").
       *
       * LAST of the five sources on purpose: a name this capture's facts, overrides, roster or ledger still carry was listed
       * above and keeps its answer, and AddRow asks the timeline before it will say "Taken Back" at all. So this loop can only ever
       * add a row for "un-written by hand, unanswered by the log", and it asserts nothing about the name — see TakenBackNames.
       */
      foreach (var name in TakenBackNames.All())
      {
        if (!rows.ContainsKey(name)) AddRow(rows, name, timeline, overrides, registry, priors, scan, 0, 0, 0, hasFacts: false, withdrawnClaim: true);
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

    private static Row AddRow(Dictionary<string, Row> rows, string name, EntityTimeline? timeline,
                              IdentityOverrideStore? overrides, PlayerRegistry? registry, IdentityPriorStore? priors,
                              CensusCarry scan,
                              double damage, double healing, long events, bool hasFacts,
                              int hitsOnRaid = 0, int hitsOnNpcs = 0, int hitsOnPets = 0, int hitsOnSelf = 0,
                              bool withdrawnClaim = false)
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
        kind = prior.Kind;
        source = $"Prior:{prior.Reason}";
        isPrior = true;
        priorSightings = prior.Sightings;
        priorSeenAt = prior.SeenAtS;
      }

      /*
       * The one provenance that is neither a rule nor a store: the operator withdrew this name's claim and NOTHING has answered
       * since — no fact, no override, no roster row, no ledger verdict, nothing this timeline watched. That state used to print as
       * the bare absence ("Not Placed" / "Nothing Identified It") on a row that then was not there at all; now the row exists and
       * says what made it exist, because a name sitting on the list reading "nothing identified it" with no explanation is parser
       * evidence, not an answer to "why is my click here?". A name this capture DOES place never reaches this line: the kind, the
       * rule's word and the facts above all still stand, which is the asymmetry the take-back is built on.
       */
      if (withdrawnClaim && kind == IdentityKind.Unknown && !isOperator) source = TakenBackNames.Word;

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
        // Named by the rule that actually fired (see CensusCarry.CastProofFor): only a verdict THIS capture's spell rules wrote gets a
        // cast beside it, and never one that claimed the other kind.
        ReasonDetail = scan.CastProofFor(source, name),
        HealedByCasters = scan.CasterBreadth(source, name),
        IsOperatorVerdict = isOperator,
        // Assigned first, then what this capture learned (PlayerRegistry.GetAssignedOrLastKnownPlayerClass): this is the column an operator
        // WRITES, and with capture-first order a written class was invisible on exactly the rows whose pencil shows - a Player row that cast
        // something. Boards keep reading GetPlayerClass(name, t), which stays time-correct.
        Class = NullIfEmpty(registry?.GetAssignedOrLastKnownPlayerClass(name)),
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
        HitsOnMobs = hitsOnNpcs,
        HitsOnPets = hitsOnPets,
        SelfHits = hitsOnSelf,
        // Pets count as raid-side for WHO A SPELL BELONGS TO (a pet is one of ours); the fact clause below splits them out again.
        TypeDisplay = IdentityVocabulary.TypeWordFor(kind, hitsOnRaid + hitsOnPets, hitsOnNpcs),
        OtherEvidence = BuildOtherEvidence(timeline, name, source, kind, hitsOnRaid, hitsOnNpcs, hitsOnPets, hitsOnSelf),
      };
      rows[name] = row;
      return row;
    }

    /*
     * The hover's extra lines: every other claim on the name, plus the one fact clause, ranked and capped.
     *
     * Cost is per NAME, not per fact — ClaimsOf hands back the timeline's own list without copying it (the caller must not
     * mutate), ProofText maps to interned literals, and the whole thing returns a single string (empty in the common case of one
     * claim and no direction). A 4,000-name census allocates a handful of small lists for the rows that have several claims and
     * nothing at all for the rest. The ONE lookup beyond the timeline is the spell-database question, and it is asked only of a
     * row that already reads Spell (dozens on a night's capture), never per fact and never per name.
     */
    private static string BuildOtherEvidence(EntityTimeline? timeline, string name, string winningSource, IdentityKind kind,
                                             int hitsOnRaid, int hitsOnNpcs, int hitsOnPets = 0, int hitsOnSelf = 0)
    {
      /*
       * IdentityVocabulary/NamesTable prints the head proof line, so ten lines is the whole hover. Asked directly for ten
       * instead of five: the rows this binds are the interesting ones — a raider claimed by chat, a guild achievement, an eye,
       * a loot line and the fact clause had four of their six lines cut off, and the truncated half was the part that explained
       * the verdict. The cap stays a cap rather than disappearing: it is what keeps a hover a tooltip instead of a report.
       */
      const int maxExtraLines = 9;

      if (timeline is null) return string.Empty;
      var claims = timeline.ClaimsOf(name);
      var direction = IdentityVocabulary.DirectionPhrase(hitsOnRaid, hitsOnNpcs, hitsOnPets, hitsOnSelf);
      if (claims.Count == 0 && direction is null) return string.Empty;

      List<(int Rank, int Strength, string Phrase)>? lines = null;
      var seenPhrases = new HashSet<string>(StringComparer.Ordinal);
      var spellListStated = false;

      foreach (var claim in claims)
      {
        // R21's spell-LIST proof is the DB answer already; no second line says it again.
        if (IdentityVocabulary.IsSpellListClaim(claim.Source)) spellListStated = true;

        // The winner is the head line the pane already prints; listing it twice would be padding.
        if (string.Equals(claim.Source, winningSource, StringComparison.Ordinal)) continue;

        var phrase = IdentityVocabulary.ProofText(claim.Source, claim.Kind);
        if (phrase.Length == 0 || !seenPhrases.Add(phrase)) continue;
        (lines ??= new List<(int, int, string)>()).Add((IdentityVocabulary.ClaimRank(claim.Source), claim.Strength, phrase));
      }

      /*
       * A Spell row also says whether the shipped spell database knows the name. The head line says what the capture wrote
       * (an effect stood where a caster belongs); "is that effect in this build's data" is the question a reader asks next,
       * and membership decides nothing about the kind. Sorts with the other database-grade claims.
       */
      if (kind == IdentityKind.Spell && !spellListStated)
      {
        var dbPhrase = ClassificationRules.SpellNamed(name) ? IdentityVocabulary.InSpellDbPhrase : IdentityVocabulary.NotInSpellDbPhrase;
        if (seenPhrases.Add(dbPhrase))
          (lines ??= new List<(int, int, string)>()).Add((IdentityVocabulary.ClaimRank("R21-spelleffect"), 0, dbPhrase));
      }

      // The fact clause competes with the claims on rank rather than always trailing them: for a Spell row it is the sentence
      // the reader came for ("Damaged Players"), and it outranks the R21 shape claim that got the row its kind.
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
}

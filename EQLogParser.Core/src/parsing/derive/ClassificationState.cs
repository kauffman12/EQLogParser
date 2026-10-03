#nullable enable annotations
using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * One replayable assertion the evidence stage wrote, so a later pass can re-create the store without
   * re-walking the evidence it came from. The digest EntityTimeline folds is a commutative sum, so replaying
   * these in append order — or any order — lands the same value a full walk would.
   */
  internal readonly record struct TimelineClaim(bool IsAffiliation, string Name, int Kind, int Strength,
                                                string Source, double T0, double T1, string Owner)
  {
    public void Apply(EntityTimeline timeline)
    {
      if (IsAffiliation)
        timeline.AddAffiliation((AffiliationKind)Kind, Name, T0, T1, Strength, Source, Owner);
      else
        timeline.SetIdentity(Name, (IdentityKind)Kind, Strength, Source, T0);
    }
  }

  /*
   * The carried memory of ClassificationRules.Apply: cursors into the capture's append-only streams plus the
   * per-rule aggregates the expensive stages build over them. Without it every full pass re-walks every fact,
   * death, and heal a capture holds — measured as the remaining ~265 ms of a 309 ms classify on
   * eqlog_Incogitable_xegony (R9 charm windows 83, R18 healed-pet intervals 67, R15 heal breadth 54, R7 graph 36,
   * line evidence 15-25, R5 ownership 15), paid again at the full-pass cadence for the whole night.
   *
   * WHAT THE DESIGN PRESERVES, first, because it is the reason the design is shaped this way: a classifying
   * pass runs over a FRESH timeline (DeriveEngine.Classify), and a rule's walk therefore sees exactly the
   * verdicts the stages before it produced THIS pass — never its own earlier passes' claims, never later
   * stages' (that blindness is deliberate in places: R9's break signal reads "a defender called ours", and old
   * passes' R7/R18 claims were never supposed to answer that). A carried TIMELINE would leak those claims
   * backward through stage order and quietly change the rule book; this class carries only aggregates, and
   * Apply replays them onto each fresh store in stage order — every stage's view is then identical to what a
   * full replay of the same facts builds.
   *
   * THE GATE, per expensive rule: `boundary digest == the one recorded when this stage last ran`. The digest
   * at a stage boundary is a hash of everything above it (roster seed + prior stages' replayed asserts). Equal
   * means nothing upstream changed and no input moved — so the rule's aggregates still say what walking from
   * zero would compute, and its streams resume at their cursors. Any change anywhere above — a new registry
   * entry seeding in, an earlier stage claiming a name from a fresh line, an override... no: overrides apply
   * last and touch no rule's input; that is also why this design needs no invalidation for them (docs R10) —
   * moves the digest, and the rule rebuilds its aggregates from zero, exactly reproducing the full walk. So a
   * carried aggregate can lag knowledge, never contradict it:
   *
   *   - R6/R14/R16 stay full pool sweeps (microseconds; their yield-check reads this pass's store).
   *   - line evidence and R5 need no gate: every assertion they make is a function of the NAME or EVIDENCE ROW
   *     alone. Their carried lists replay old asserts; only [cursor..tail] is walked.
   *   - R15, R9, R7, R18 read verdicts while walking (healer strength, defender side, "hit one of ours", edge
   *     endpoints), so each gates on its own boundary digest and rebuilds when it moved.
   *   - R18's intervals and R9's window affiliations replay by REBUILDING from carried aggregates each pass
   *     (a healed pet's interval tail extends; a charm list grows) — unchanged writes hit the timeline's
   *     dedupe, so identical replays move nothing.
   *
   * Inputs that advance no stream and precede no digest — npcs.txt/spells.txt reloads, chat-channel settings —
   * are session-static by construction (loaded at open), same as before this class existed; a data reload
   * happens by re-opening the log, which makes a new DeriveEngine and so a new instance of this class.
   */
  internal sealed class ClassificationState
  {
    // ---- stream cursors (append-only tables; a cursor only ever moves forward) ----

    internal int EvidenceCursor;                 // line-evidence stage over facts.Evidence
    internal int R5PoolCursor;                   // ownership sweep over facts.InternedNames
    internal int R5FactCursor;                   // ownership sweep over FlagOwnerInLine facts
    internal int R15HealCursor;                  // R15 heal walk
    internal int R15VetoCursor;                  // R15 swing-back veto over facts.Facts
    internal int R7FactCursor;                   // R7 edge walk
    internal int R18HealCursor;                  // R18 heal walk
    internal int R18VetoCursor;                  // R18 swing-back veto

    /*
     * Boundary digests (EntityTimeline.StateStamp as seen at each gated stage's ENTRY). A stage resumes from
     * its cursors exactly while this matches the digest at the same entry next pass. long.MinValue starts
     * every rule unbuilt, so the first pass of a session walks from zero whatever the store holds.
     */
    internal long DigestR15 = long.MinValue;
    internal long DigestR9 = long.MinValue;
    internal long DigestR7 = long.MinValue;
    internal long DigestR18 = long.MinValue;

    // ---- line evidence: replayable direct asserts + the target-frame sets the ladder runs over ----

    internal readonly List<TimelineClaim> EvidenceClaims = [];
    internal readonly HashSet<TimelineClaim> EvidenceClaimed = [];

    // Carried so the ladder needs only unseen rows; it still runs (over the full carried sets) every pass.
    internal readonly HashSet<string> TargetNpc = new(StringComparer.Ordinal);
    internal readonly HashSet<string> TargetPlayer = new(StringComparer.Ordinal);
    internal readonly HashSet<string> PlayerBehavior = new(StringComparer.Ordinal);

    // ---- R5: claimed summon names, replayed onto each fresh store before the sweeps resume ----
    // (the set is for dedupe while feeding; the list preserves append order for the replay)

    internal readonly HashSet<string> OwnerClaimed = new(StringComparer.Ordinal);
    internal readonly List<string> OwnerClaimNames = [];

    // ---- R15/R18: heal-side candidates and the swing-back veto counted over ALL attackers (a name can
    // become a candidate long after its attacks streamed past the cursor, and a from-zero walk counts
    // those older swings — so the veto cannot be keyed on candidates). The two rules keep SEPARATE
    // structures: each reads verdicts at its own point of the stage order (R18 after R9/R7), and sharing
    // one feed would silently re-date those readings. ----

    internal readonly Dictionary<string, ClassificationRules.HealEdgeAgg> R15Candidates = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, AttackVeto> R15Vetos = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, ClassificationRules.HealEdgeAgg> R18Candidates = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, AttackVeto> R18Vetos = new(StringComparer.Ordinal);

    // ---- R7: per-attacker side evidence; the eval loop runs over the carry every pass like today ----

    internal readonly Dictionary<string, ClassificationRules.SideAgg> GraphAggs = new(StringComparer.Ordinal);

    // ---- R9: charm event tables + window counters, interpreted only by CharmWindowPolicy (see CharmCarry) ----

    internal CharmCarry? Charms;

    // Everything back to the pre-capture state; cursors and digests alike, because what gets thrown away
    // together must come back together. Tests use it to assert the first pass of a session is a full walk.
    public void Reset()
    {
      EvidenceCursor = 0;
      R5PoolCursor = 0;
      R5FactCursor = 0;
      R15HealCursor = 0;
      R15VetoCursor = 0;
      R7FactCursor = 0;
      R18HealCursor = 0;
      R18VetoCursor = 0;

      DigestR15 = long.MinValue;
      DigestR9 = long.MinValue;
      DigestR7 = long.MinValue;
      DigestR18 = long.MinValue;

      EvidenceClaims.Clear();
      EvidenceClaimed.Clear();
      TargetNpc.Clear();
      TargetPlayer.Clear();
      PlayerBehavior.Clear();
      OwnerClaimed.Clear();
      OwnerClaimNames.Clear();
      R15Candidates.Clear();
      R15Vetos.Clear();
      R18Candidates.Clear();
      R18Vetos.Clear();
      GraphAggs.Clear();
      Charms = null;
    }

    // Swing-back counters for one attacker name, read by the R15/R18 share gates.
    internal struct AttackVeto
    {
      public int Edges;
      public int RaidSideEdges;
    }
  }
}

#nullable enable annotations
namespace EQLogParser.Mirror
{
  // Strength scale for the rule catalog (docs/batch-parsing-plan.md): higher wins on conflict and
  // a weaker rule never retracts a stronger assignment (EntityTimeline read-time resolution).
  internal static class RuleStrength
  {
    public const int Manual = 1000;  // R10 operator override
    public const int Certain = 100;  // line-intrinsic truth: targeted verdicts, who roster, ownership in line
    public const int Strong = 60;    // behavior only your side performs: join/leave, guild/group/raid speech
    public const int Medium = 30;    // strong corroboration: multi-word NPC name hit, graph inference
    public const int Weak = 10;      // single-source hints kept for context only
  }

  // What a rules pass additionally reports (never part of the identity state itself).
  internal sealed class MirrorRuleOutcome
  {
    // Names that got both Targeted (Player) and Targeted (NPC) verdicts in one run — the target
    // frame never contradicts itself in measured logs, so any hit here is genuine conflict
    // material (illusion state, log bug) for the comparison report. NPC wins the tie.
    public List<string> Conflicts { get; } = [];

    // Every charm span the run produced, with its owner (or the fact that no caster was named), its end
    // reason and the same-name ambiguity count. Report/tooltip input; the affiliation intervals derived
    // from these are what the deriver reads.
    public List<CharmWindow> Charms { get; } = [];

    // Names R18 put an ownership interval on: the target frame called them an NPC, and the raid's healing
    // says otherwise. Each one is damage that used to sit in the enemy column of the fight list.
    public List<string> OurPets { get; } = [];

    // Stages that THREW this run, with their streak count and full exception text (Core owns no logger — the
    // caller writes these to the player's log). See RunStage: a failed stage costs its own verdicts and nothing
    // else, so a non-empty list here means degraded-but-live, which is exactly what must never be silent.
    public List<string> FailedRules { get; } = [];

    // Stages this run RETIRED after repeated consecutive failures — one announcement each, logged once,
    // skipped for the rest of the session (ResetRuleHealth hands the rule book back to the next capture).
    public List<string> RetiredRules { get; } = [];
  }

  // Phase 2 rules (identity slice): turn the captured evidence facts into retroactive identity
  // assignments and time-scoped affiliations. One pass per completed run - the fact table is the
  // input, so this stays replay-safe and never touches ingest-time state (D3).
  //
  // Identity is a global statement: when stronger evidence arrives late, the read-time resolution
  // corrects the name's classification for its WHOLE timeline retroactively. The one time-scoped
  // concept is charm: an npc stays an npc and merely holds a Friendly affiliation window
  // (wear-off OR death closes it) - "a different instance while charmed", nothing more.
  //
  // Deliberate exclusions, all catalog-mandated:
  //  - Legacy registry verification events are NOT applied here. Cold-mode rebuild measures what the
  //    rules alone can recover; the fidelity path (harness SeedIdentity) still exercises them.
  //  - R4 tier 2 (other single-class spells) waits for its corroboration rule; only tier-1 names
  //    (EQDataStore.IsClassSafeSpellName: spires, curated epics, and the versioned rank families
  //    censused to zero mob-shaped casters — bard, beastlord, berserker, cleric, and the War|Ber
  //    battle leaps whose multi-bit mask claims identity but never a class) assert identity here.
  //  - R20-petspell (EQDataStore.PetCastSpellFamilies) claims the CASTER as Pet with no owner: the
  //    snare line names nobody but the pet. Owner evidence keeps coming from the possessive words
  //    and "My leader is" chat, not from this rule.
  //  - Combat MODIFIERS assert nothing: `Finishing Blow` looks player-exclusive (8,200+ lines, zero
  //    mob or pet shaped attackers) but every attacker already carries a stronger claim, so the mask
  //    stays stats-only rather than becoming a zero-yield identity rule.
  internal static class ClassificationRules
  {
    // Channels whose speech proves the sender is on your side (R3). Say/tell excluded: hostile
    // mobs speak those, and a pet's "X tells you" reports would be misattribution bait. In these
    // logs group chat rides "tells the group," — ChatLineParser already classifies it as Group.
    private static readonly HashSet<string> PlayerChannels = new(StringComparer.OrdinalIgnoreCase)
    {
      ChatChannels.Guild, ChatChannels.Group, ChatChannels.Raid, ChatChannels.Fellowship
    };

    /*
     * Name suffixes that carry ownership inside the name itself (R5). The vocabulary is CLOSED and
     * measured: two sweeps over six captures (2022-2026, ~3.7 GB) counting every `` `s <word> `` run give
     *   pet        343,078 (Incogitable) / 1,884,906 (Kizant 2026)
     *   ward        11,354 /    96,250   <- the summon word in the 2025/2026 logs
     *   warder      42,915 /        43   <- the older spelling of the same thing, nearly extinct
     *   familiar       171 /        12
     *   mount           28 /         0
     * Everything else that turned up (`s corpse, `s Acolyte, `s Heart) is an NPC's own possessive text,
     * not ownership, so it stays out. Matching is case-insensitive, and not for decoration: all 28 of
     * Incogitable's mount possessives are printed `` `s Mount `` (a case-sensitive sweep finds zero of them),
     * and the logs also print `` `s Warder ``.
     *
     * What this list CANNOT do is name a pet whose owner never appears in the text. Over the six captures,
     * the log's own possessive lines prove 18/96 of the owners the operator's registry knows (petmapping.txt),
     * because most pets are given custom names (``Dangle``, ``Bigboned``, ``Useless``) that print bare.
     *
     * This list is also what CombatMirror flags on facts, and what FightDeriver's PetOwner and a
     * materialized record's AttackerOwner cut with - one copy, in OwnerInName, on purpose.
     */
    private static readonly string[] OwnerSuffixes = ["`s pet", "`s warder", "`s ward", "`s familiar", "`s mount"];

    // R19's shape: the prefix a summoned eye carries in front of its owner's name, and the three eyes that are
    // worth counting. See EyeSummonOwnerInName for both lists and what they are measured on.
    private const string EyePrefix = "Eye of ";
    private static readonly string[] CountableEyes = ["Veeshan", "Despair", "Mother"];

    // R7 tunables (increment 3): opponent breadth counts INSTANCES, not distinct names — raid
    // pulls reuse names constantly ("a skeleton" dies, the next "a skeleton" is a new instance,
    // separated by a combat-gap). Charmed-player raids are why inference only ever fires for
    // names with NO other identity evidence and requires every defender already classified on
    // one side: a charmed raider attacking allies is never ambiguous because the allies carry
    // their own Certain/Strong assignments.
    private const int OpponentInstances = 3;   // three waves of "a skeleton" count as three
    private const double BurstGapS = 30;       // gap that splits one name into separate instances
    private const double MinSpanS = 60;        // evidence must spread over a real engagement

    /*
     * How much unclassified opposition an inference may tolerate (R7). It used to be none at all: ONE
     * defender the rules had not named vetoed the whole aggregation, which read as a hair trigger on real
     * logs - `Squirticus` carries 8,909 attack edges, a handful of which pointed at names nothing had yet
     * classified, and those few left it unclassified for the entire capture (they read as 0 unknown now, the
     * shape and heal rules having named them since).
     * A share (not a count) keeps the guard's purpose: a melee pile that is mostly unclassified still
     * proves nothing. Measured over the six captures, the names this lets through have unknown shares of
     * 0.0-2 % and their raid-side opposition is friendly fire (cleave), not combat.
     */
    private const double MaxUnknownEdgeShare = 0.02;

    /*
     * R15 (healed by our side) tunables. A raid AoE heal waters the mob stack too - "Yokii healed an
     * arcborn wraith for 2 hit points by Summer's Deluge Rk. II." - so one edge is not evidence; a
     * *population* of heals from more than one caster is. Crumb heals run at 1-2 hit points from a single
     * caster, while the mercs/pets this rule exists for are healed thousands of times by half the raid.
     */
    private const int HealEdgeMinLines = 10;      // heal lines aimed at the name
    private const int HealEdgeMinHealers = 2;     // from at least two different our-side healers
    private const double HealEdgeMaxRaidAttackShare = 0.02;   // its own swings at our side, same rationale as above

    /*
     * R18 (our pet, not their mob) tunables. R15 can only speak for names that are still UNKNOWN, and the
     * expensive case is the one it is forbidden to touch: a name carrying `Targeted (NPC)` is Certain(100), so no
     * amount of healing inference may relabel it - which is exactly why `Useless`, `Dangle`, `Bigboned`, `Muavanne`,
     * `Breshanna` and `Zarpog` keep their own fight-list rows and ~36% of the branch's cross-check mismatch mass.
     * R18 therefore never touches IDENTITY. It writes an ownership INTERVAL, which is the thing the projection
     * already has for saying "this NPC is ours right now" (R9-charm being the other writer).
     *
     * The gate is the discriminator the fourth audit measured, and it is BREADTH, not volume: pets take heals
     * from 19-52 distinct raid casters while every genuine hostile in the same six captures tops out at 10
     * (`Zelnithak`, `Captain Kar the Unmovable`, `Rufus Invictus`, `Tallongast, The Egg`, `an echo`), and the crumb
     * -heal shape this must not fire on - `Hand of the King`, 377 lines of 1-2 point raid-AoE overheal - sits at 8.
     * 15 is above every hostile ever measured, including the crumb case, and below the weakest pet (19) with room
     * for a smaller raid. Volume alone would be the wrong dial: a boss rained on by raid AoE out-lines a pet.
     *
     * What the gate deliberately MISSES, measured on eqlog_Kizant_xegony-09-03-26 (4.8 M damage facts, 2.67 M
     * heals): the `X`s pet` names that R5 already owns bottom out at 11-12 distinct casters, so a CUSTOM-named pet
     * at that breadth is left on the enemy side rather than guessed at — the two names this rule does claim there
     * sit at 22 (`Stormclaw`, a wolf in npcs.txt buffed by the whole raid and biting The Colossus of Skylance,
     * 12.3 G across 62,208 edges) and 17 (`Funky`, `Targeted (NPC)`, 2.6 G). A dial at 10 would take those two and
     * every hostile that ever got raid AoE with them; a pet being missed is a row in the wrong column, a hostile
     * being claimed is a hole in the meter nobody can find.
     */
    private const int OurPetMinCasters = 15;                  // distinct our-side healers, ever
    private const int OurPetMinHealLines = HealEdgeMinLines;  // same floor as R15: one line proves nothing
    private const double OurPetMaxRaidAttackShare = 0.02;     // a name that swings at us is not ours
    private const double OurPetTailS = 300;                   // keeps our own trailing facts folded after the top-ups stop

    // heals is optional: the damage stream alone classifies exactly as it did before this table existed.
    /*
     * `state` carries every expensive stage's aggregates and stream cursors across passes; see ClassificationState
     * for the full contract. Passing null runs the rule book exactly as it always did — which is what a test, the
     * name census, and a session's first pass do. Handing in a carried state makes each costly walk resume where
     * it stopped while that rule's BOUNDARY DIGEST still matches the store at the same point of this pass: equal
     * means nothing upstream moved, so resuming reproduces a full replay term for term; any change rebuilds that
     * rule from zero, exactly as this method always did. Two properties this keeps whole: `timeline` is a fresh
     * store every pass (MirrorSession.Classify), so a rule's walk sees what the stages before it produced THIS
     * pass — never its own earlier passes' claims, never later stages' — and carried aggregates are replayed onto
     * that store in stage order. Overrides apply last and enter no rule's input, which is why removal self-heals
     * on the next pass exactly as before (docs R10) and no invalidation hook exists for them.
     */
    public static MirrorRuleOutcome Apply(IFactTable facts, EntityTimeline timeline, IHealFactTable? heals = null,
                                         ClassificationState? state = null)
    {
      state ??= new ClassificationState();   // throwaway state == the original from-zero replay
      var outcome = new MirrorRuleOutcome();

      /*
       * Every stage runs under its own guard (RunStage): one rule's exception costs that rule's verdicts and NOTHING
       * else — the pass keeps every conclusion the other stages already reached, the failure is reported on the
       * outcome for the caller to log, and a stage that fails repeatedly RETIRES itself rather than freezing the
       * whole identity pipeline. Rules only ADD evidence, and every consumer already handles a name nothing has
       * placed (that is the state at the start of every log), so the degradation direction is "less known", never
       * "wrong side". A stage that retires mid-session moves the timeline's StateStamp for later rebuilds, so the
       * carry gate notices and re-folds rather than mixing rule books across passes.
       */
      RunStage("R0 local player", outcome, () => ApplyLocalPlayer(timeline));
      RunStage("line evidence", outcome, () => ApplyEvidence(facts, timeline, outcome, state));
      RunStage("R5 ownership", outcome, () => ApplyOwnershipFlags(facts, timeline, state));
      RunStage("R6 npcs.txt", outcome, () => ApplyNpcDatabase(facts, timeline));

      // Name shape before the graph, because the graph reads it: an article-shaped defender is no longer an
      // unknown edge, so attackers that were starved by "a skeleton" being unclassified get their evidence.
      RunStage("R14 name shape", outcome, () => ApplyNameShape(facts, timeline));

      // Before R7 for the same reason - our-side defenders are what R7-side needs to call an attacker hostile.
      if (heals is not null) RunStage("R15 healed by raid side", outcome, () => ApplyHealedByRaidSide(facts, heals, timeline, state));

      // The other marker the client writes inside a name. After R15 on purpose: R15 only considers names still
      // Unknown, so a summon whose name happens to carry a comma would lose the heal evidence to punctuation.
      RunStage("comma title", outcome, () => ApplyCommaTitle(facts, timeline));

      // Charm after everything that decides sides, before the graph: a window ends when the charmed name
      // swings at somebody these rules put on our side, so it has to see settled identities.
      var charms = new List<CharmWindow>();
      RunStage("R9 charm windows", outcome,
               () =>
               {
                 state.Charms ??= new CharmCarry();
                 var boundary = timeline.StateStamp();
                 var frozen = state.DigestR9 == boundary;
                 state.DigestR9 = boundary;
                 charms.AddRange(CharmWindowPolicy.Apply(facts, timeline, state.Charms, frozen));
               });
      outcome.Charms.AddRange(charms);

      RunStage("R7 graph", outcome,
               () => ApplyGraphInference(facts, timeline, [.. charms.Select(w => (w.Name, w.T0, w.T1))], state));

      // Last, because it reads everything above: which NPC-verdict names are actually ours. It has to run
      // after the charm pass to know which names a window already explains, and after the graph because a
      // name R7 could classify as an attacker is not one the raid owns.
      if (heals is not null)
        RunStage("R18 healed-pet intervals", outcome,
                 () => outcome.OurPets.AddRange(ApplyHealedPetIntervals(facts, heals, timeline,
                                                                        [.. charms.Select(w => w.Name)], state)));

      return outcome;
    }

    /*
     * Per-stage health. A stage that THREW is a bug in one check, not in the capture: it gets counted, reported,
     * and after RuleFailureRetries failures IN A ROW retires — itself only. One clean run pays off a stage's
     * streak (transient conditions are what most of these exceptions actually are); a deterministic poison fact
     * gets logged five times and then skipped instead of making every full pass of the night fail.
     *
     * Process state, consulted by name; passes are serialized by the session's derive gate. ResetRuleHealth is
     * called when a new session starts (a different capture must get the full rule book) and by tests.
     */
    private const int RuleFailureRetries = 5;
    private static readonly Dictionary<string, int> StageFailures = [];
    private static readonly HashSet<string> RetiredStageNames = [];

    /// <summary>
    /// Test-only: a stage that throws RETHROWS instead of being swallowed, so a rule dying mid-pass fails the test
    /// loudly with its real stack rather than reading as "the rules found nothing" — the failure class this codebase
    /// keeps meeting (charm flips that flipped nothing, R5 claims that never replayed). Off in production on purpose:
    /// one bad fact must not cost a raid night every line. The health tests drive the swallow-on-purpose semantics and
    /// turn this off while they do; the test assemblies turn it on at assembly init.
    /// </summary>
    internal static bool FailFastStages;

    internal static void ResetRuleHealth()
    {
      StageFailures.Clear();
      RetiredStageNames.Clear();
    }

    // Internal for its own tests (ClassificationRuleHealthTest); Apply is the only production caller.
    internal static void RunStage(string name, MirrorRuleOutcome outcome, Action work)
    {
      if (RetiredStageNames.Contains(name)) return;
      try
      {
        work();
        StageFailures.Remove(name);
      }
      catch (Exception ex)
      {
        if (FailFastStages)
          throw;   // tests: a silent stage death is a failed run, not an empty column

        var streak = (StageFailures.TryGetValue(name, out var seen) ? seen : 0) + 1;
        StageFailures[name] = streak;
        outcome.FailedRules.Add($"{name} (#{streak}): {ex}");
        if (streak >= RuleFailureRetries)
        {
          RetiredStageNames.Add(name);
          outcome.RetiredRules.Add(name);
        }
      }
    }

    // R0: the log's own author. Every "You" in the file is the local player by construction,
    // and their name comes from the filename itself (App sets ConfigUtil.PlayerName; the test
    // harness derives it the same way from eqlog_(Player)_(Server).txt). Self cannot self-target,
    // so without this rule the local player can sit below weaker evidence all log.
    private static void ApplyLocalPlayer(EntityTimeline timeline)
    {
      timeline.SetIdentity(ChatType.You, IdentityKind.Player, RuleStrength.Certain, "R0-local", double.NegativeInfinity);
      if (!string.IsNullOrEmpty(ConfigUtil.PlayerName))
      {
        timeline.SetIdentity(ConfigUtil.PlayerName, IdentityKind.Player, RuleStrength.Certain, "R0-local", double.NegativeInfinity);
      }
    }

    // R10: the override UI ("set as player / merc / pet / npc") calls through here, and MirrorOverrideStore
    // replays what the operator saved on every rebuild. Manual strength outranks every rule, retroactively:
    // this is the one input that is allowed to reason "the rules are wrong about this name".
    public static void ApplyManualOverride(EntityTimeline timeline, string name, IdentityKind kind)
      => timeline.SetIdentity(name, kind, RuleStrength.Manual, "R10-manual");

    // R12 (operator history: players.txt / petmapping.txt) is NOT a method here on purpose. It lives in
    // RegistrySeed.Apply, which the app AND the measurement harness call, so there is exactly one warm path and
    // cold-mode rebuild still sees nothing but rules. An earlier ApplyHistory(knownPlayers, knownPets) sat here
    // unread by anybody and disagreed with RegistrySeed on both kind (Player vs Pet) and strength (60 vs 8);
    // two seeding paths that disagree are how a later reader gets the pet question wrong twice.

    /*
     * R5's ownership word: the owner a line-owned name carries inside itself ("Sancus`s pet" -> "Sancus"), null
     * when the name has no such suffix. Three places have to cut the name exactly the same way - the rules pass
     * that claims the pet, the fight row's PetOwner, and a materialized damage record, whose AttackerOwner is what
     * folds a pet's damage under its owner in DamageStatsBuilder - so it is written once, here.
     */
    internal static string OwnerInName(string name)
    {
      if (string.IsNullOrEmpty(name)) return null;
      foreach (var suffix in OwnerSuffixes)
      {
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
      }

      return null;
    }

    /*
     * The owner a name names, or null when the cut is not a person: "Akini, Xanathan`s Warder" is one summon
     * named after two masters, and the text in front of the word is "Akini, Xanathan". Claiming a player by
     * that string would invent a raid member with a comma in it - the pet claim stands on its own (R5 proves
     * ownership, and an owned summon is player-side whatever its name is), so only the OWNER claim is dropped.
     */
    private static string UsableOwnerInName(string name)
    {
      var owner = OwnerInName(name);
      return string.IsNullOrEmpty(owner) || owner.Contains(',') ? null : owner;
    }

    /*
     * R19's shape: the eye a player summons is written as `Eye of <owner>` - the owner's own name carried inside
     * the summon's name, with no possessive and no owner line anywhere in the file. Eight captures 2022-2026
     * (~4.2 GB) say what one is good for: it is NEVER an attacker (0 lines in every capture), only the name inside
     * it ever strikes it, all 348 of those hits land for exactly 1 point each, and it dies immediately (376
     * `was/has been slain by` lines, plus `is rent by decrepit wrath.` / `looks pale.`). So it is not a combatant,
     * and the one thing worth taking off such a line is the ownership fact it states for free.
     *
     * The suffix is accepted as any non-empty text, because owning is only ever CLAIMED when an entity attacked
     * with exactly that string (see EvEyeOwnedStrike below). That is what keeps a strange suffix from inventing a
     * raider: no match, no claim, and no guessing about where a name ends.
     *
     * Three eyes are worth counting and refuse membership here. Legacy's own guard (DamageLineParser.InIgnoreList)
     * kept exactly Veeshan, Despair and Mother out of the ignore list, so this inherits that decision rather than
     * re-litigating it from four years of logs that contain none of them. The list is CLOSED -
     * TheCountableEyeListIsThreeWordsNoMore refuses a fourth, which arrives as data plus a test, never as a wider pattern.
     *
     * Ask this only about a NAME SLOT: an attacker, a defender, a heal target. `Eye of Zomm` is the spell that makes
     * the eye, `activates Eye of the Storm Rk. III.` is a discipline and `Anyone need Eye of Mother in Theater?` is
     * an item in guild chat - all three are in these logs, none of them is an entity.
     */
    /// <summary>The owner a summoned eye names, or null when this name is not an ignored summon.</summary>
    internal static string EyeSummonOwnerInName(string name)
    {
      if (string.IsNullOrEmpty(name) || !name.StartsWith(EyePrefix, StringComparison.OrdinalIgnoreCase)) return null;

      foreach (var keep in CountableEyes)
      {
        if (name.EndsWith(keep, StringComparison.OrdinalIgnoreCase)) return null;
      }

      var owner = name[EyePrefix.Length..].Trim();
      return owner.Length == 0 ? null : owner;
    }

    // Identity evidence only. Charm WINDOWS are not built here: they need settled sides to know when a
    // charm broke, so they come later in Apply through CharmWindowPolicy. What this pass does is stamp the
    // fact that a name was charmed at all (identity stays Npc — a charmed mob is an NPC on our side for a
    // while, never a player).
    /*
     * The evidence sweep is pure input: every assertion here is a function of the evidence row itself (identity
     * asserts do not read verdicts), so it needs no clock — it just resumes at state.EvidenceCursor and lets the
     * carried target-frame sets hold the history. The ladder below then runs over the FULL carried sets each
     * pass: its SetIdentity writes dedupe against the carried timeline, so re-running the ladder costs a hash
     * walk and moves neither store nor digest, while an evidence row arriving late still re-decides its name.
     */
    private static void ApplyEvidence(IFactTable facts, EntityTimeline timeline, MirrorRuleOutcome outcome,
                                      ClassificationState state)
    {

      // Replay of everything this stage ever asserted, onto this pass's fresh store, before anything new is
      // walked. The full ladder below re-derives its own asserts from the carried sets, so only the direct
      // writes need a list; identical replays hit the timeline's dedupe and move no digest.
      foreach (var claim in state.EvidenceClaims) claim.Apply(timeline);

      // Target-frame verdicts and player-side behavior are collected per name and applied AFTER
      // the sweep as a deterministic ladder (order of evidence arrival must not matter).
      var targetNpc = state.TargetNpc;
      var targetPlayer = state.TargetPlayer;
      var playerBehavior = state.PlayerBehavior;

      var evidence = facts.Evidence;
      for (var ei = state.EvidenceCursor; ei < evidence.Length; ei++)
      {
        var e = evidence[ei];
        var name = facts.NameOf(e.NameIdx);
        switch (e.Kind)
        {
          case EvidenceFact.EvTargetedPlayer:
            targetPlayer.Add(name);
            break;

          case EvidenceFact.EvTargetedNpc:
            targetNpc.Add(name);
            break;

          case EvidenceFact.EvJoinedRaid:
          case EvidenceFact.EvLeftRaid:
          case EvidenceFact.EvJoinedGroup:
          case EvidenceFact.EvLeftGroup:
          case EvidenceFact.EvRaidLeader:
            playerBehavior.Add(name);
            Claim(timeline, state,name, IdentityKind.Player, RuleStrength.Strong, "R3-presence", double.NegativeInfinity);
            break;

          case EvidenceFact.EvMercJoinedGroup:
            // legacy isPossiblePlayerName said no - an oddly-named hireling, authoritative enough.
            Claim(timeline, state,name, IdentityKind.Merc, RuleStrength.Strong, "R3-merc", double.NegativeInfinity);
            break;

          case EvidenceFact.EvWhoRoster:
            playerBehavior.Add(name);
            Claim(timeline, state,name, IdentityKind.Player, RuleStrength.Certain, "R2-who", double.NegativeInfinity);
            break;

          case EvidenceFact.EvChat:
            // "You" is the local player slot, already verified through the registry path.
            if (PlayerChannels.Contains(facts.AuxOf(e.AuxIdx) ?? string.Empty) && name != ChatType.You)
            {
              playerBehavior.Add(name);
              Claim(timeline, state,name, IdentityKind.Player, RuleStrength.Strong, "R3-chat", double.NegativeInfinity);
            }
            break;

          case EvidenceFact.EvSelfFeeds:
            // R17: the actor on a consume line ("Glug… / Chomp… <name> takes a drink / bite from …"). Only a
            // player character carries a flask or a loaf. Strong rather than Certain, so a Targeted (NPC) verdict
            // on the same name still wins (docs/combat-mirror-design.md R17).
            playerBehavior.Add(name);
            Claim(timeline, state,name, IdentityKind.Player, RuleStrength.Strong, "R17-selffeed", double.NegativeInfinity);
            break;

          case EvidenceFact.EvEyeOwnedStrike:
            /*
             * R19: the eye named after you struck by YOU means you called it, and only a player character summons
             * one. Strong, not Certain - the ownership is the line's own, but who swings at an eye is not (anybody
             * can kill one: 5 of the 376 eye death lines name a killer who is not the owner, which is exactly why
             * the parser only reports a striker whose name is INSIDE the eye).
             */
            playerBehavior.Add(name);
            Claim(timeline, state,name, IdentityKind.Player, RuleStrength.Strong, "R19-eyeowner", double.NegativeInfinity);
            break;

          case EvidenceFact.EvCalledToOwner:
            Claim(timeline, state,name, IdentityKind.Pet, RuleStrength.Certain, "R5-called", double.NegativeInfinity);
            Claim(timeline, state,AffiliationKind.Friendly, name, e.TimeS, double.PositiveInfinity, RuleStrength.Certain, "R5-called");
            break;

          case EvidenceFact.EvCharmStart:
            // A charmed mob is an NPC that is temporarily on your side (identity Npc stays the
            // stable answer; the Friendly interval is time-scoped, per D3). One write is enough even though a
            // charm confirm line writes "a skeleton" and every line where that mob is the SUBJECT writes
            // "A skeleton": EntityTimeline keys identity case-insensitively, so SideAt finds this assignment
            // whichever spelling it was asked with (docs/combat-mirror-design.md).
            Claim(timeline, state,name, IdentityKind.Npc, RuleStrength.Strong, "R9-charm", double.NegativeInfinity);
            break;

          case EvidenceFact.EvCharmEnd:
            // Consumed by CharmWindowPolicy, which pairs it with the sighting it belongs to.
            break;

          case EvidenceFact.EvCast:
            // R4 tier 1 only until tier 2 gets its corroboration rule.
            var castSpell = facts.AuxOf(e.AuxIdx);
            if (IsClassSafeCast(castSpell))
            {
              Claim(timeline, state,name, IdentityKind.Player, RuleStrength.Certain, "R4-spell", double.NegativeInfinity);
            }
            else if (IsPetCastSpell(castSpell))
            {
              // Strong, not Certain: the spell proves the caster is somebody's pet, but says nothing
              // about WHOSE - a verified owner claim (R5, Certain) still outranks this, and a name
              // that is also a verified player keeps Player.
              Claim(timeline, state,name, IdentityKind.Pet, RuleStrength.Strong, "R20-petspell", double.NegativeInfinity);
            }
            break;
        }
      }

      state.EvidenceCursor = evidence.Length;

      // Target-frame ladder (Certain beats everything behavioral, regardless of arrival order):
      //  Player frame wins over NPC frame but is reported; NPC frame + player-shaped behavior
      //  (joins, whitelisted speech, who roster) is the merc signature - the target frame knows
      //  what the text cannot: it is not a player.
      foreach (var name in targetNpc)
      {
        if (targetPlayer.Contains(name))
        {
          outcome.Conflicts.Add(name);
          timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Certain, "R1-conflict", double.NegativeInfinity);
          continue;
        }
        var kind = playerBehavior.Contains(name) ? IdentityKind.Merc : IdentityKind.Npc;
        timeline.SetIdentity(name, kind, RuleStrength.Certain, kind == IdentityKind.Merc ? "R13-merc" : "R1-target", double.NegativeInfinity);
      }
      foreach (var name in targetPlayer)
      {
        if (!targetNpc.Contains(name))
        {
          timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Certain, "R1-target", double.NegativeInfinity);
        }
      }
    }

    /*
     * The claim is a function of the name text alone, so both input streams resume at their cursors: a summon is
     * claimed when its NAME first enters the pool (names are append-only ids), and an `Owner:` flagged fact is
     * claimed when the flag first streams past. The carried `claimed` set costs one hash insert per claim ever and
     * keeps a re-run of both sweeps free (no clock needed: no verdict is read anywhere below).
     */
    /*
     * The evidence stage's write seam: the assertion lands on the timeline AND in the replay list (deduped -
     * ten thousand join-lines name one raider, and the replay should hold it once). The target-frame ladder
     * below writes through `timeline` directly instead: its asserts are re-derived from the carried sets every
     * pass, so they need no list.
     */
    private static void Claim(EntityTimeline timeline, ClassificationState state, string name, IdentityKind kind,
                              int strength, string source, double effectiveFrom = double.NegativeInfinity)
    {
      timeline.SetIdentity(name, kind, strength, source, effectiveFrom);
      var claim = new TimelineClaim(false, name, (int)kind, strength, source, effectiveFrom, 0d, null);
      if (state.EvidenceClaimed.Add(claim)) state.EvidenceClaims.Add(claim);
    }

    private static void Claim(EntityTimeline timeline, ClassificationState state, AffiliationKind kind, string name,
                              double t0, double t1, int strength, string source, string owner = null)
    {
      timeline.AddAffiliation(kind, name, t0, t1, strength, source, owner);
      var claim = new TimelineClaim(true, name, (int)kind, strength, source, t0, t1, owner);
      if (state.EvidenceClaimed.Add(claim)) state.EvidenceClaims.Add(claim);
    }

    private static void ApplyOwnershipFlags(IFactTable facts, EntityTimeline timeline, ClassificationState state)
    {
      // Replay of every claim ever made, onto this pass's fresh store, before the sweeps resume; a scratch
      // `claimed` set per name keeps chain handling identical to the original visit.
      foreach (var claimedName in state.OwnerClaimNames)
        ClaimOwnedSummon(timeline, claimedName, new HashSet<string>(StringComparer.Ordinal));

      // The verdict depends only on the NAME, so it is taken from the name pool: one pass per distinct name.
      // Sweeping names rather than facts is what lets a summon that never swung a weapon be owned too -
      // "Tuona`s ward" appears in these captures only as something a raid member heals, and a ward nobody
      // owns is a stray row in the display list forever.
      var claimed = state.OwnerClaimed;
      var names = facts.InternedNames;
      for (var i = state.R5PoolCursor; i < names.Count; i++)
      {
        var name = names[i];
        if (OwnerInName(name) is null) continue;
        if (!claimed.Contains(name)) state.OwnerClaimNames.Add(name);   // registered for replay onto future fresh stores
        ClaimOwnedSummon(timeline, name, claimed);
      }
      state.R5PoolCursor = names.Count;

      // Explicit "Owner: X" annotations: the line says ownership without saying it in the name, and only the
      // flag knows. (Heal lines have no raw text on the fact, so they carry the name-shape case above.)
      var factsList = facts.Facts;
      for (var i = state.R5FactCursor; i < factsList.Length; i++)
      {
        if ((factsList[i].Flags & DamageFact.FlagOwnerInLine) == 0) continue;
        var flagged = facts.NameOf(factsList[i].AtkIdx);
        if (!claimed.Contains(flagged)) state.OwnerClaimNames.Add(flagged);
        ClaimOwnedSummon(timeline, flagged, claimed);
      }
      state.R5FactCursor = factsList.Length;
    }

    private static void ClaimOwnedSummon(EntityTimeline timeline, string name, HashSet<string> claimed)
    {
      if (string.IsNullOrEmpty(name) || !claimed.Add(name)) return;

      var source = "R5-owner";
      var owner = UsableOwnerInName(name);

      timeline.SetIdentity(name, IdentityKind.Pet, RuleStrength.Certain, owner is null ? source : $"{source}:{owner}", double.NegativeInfinity);
      if (owner != null)
      {
        // The named owner gets corroboration only: the line proves the pet, not the person.
        timeline.SetIdentity(owner, IdentityKind.Player, RuleStrength.Medium, source);

        // Owner-keyed pet claim: registry petmapping keys are pet NAMES, but log evidence is
        // often only "X`s pet" - scoring pairs through the owner side is what makes the pet
        // metric measurable (docs increment 3).
        timeline.AddAffiliation(AffiliationKind.PetOfPlayer, name, double.NegativeInfinity, double.PositiveInfinity, RuleStrength.Certain, $"{source}:{owner}");
      }
    }

    /*
     * R14 - the article IS the game's own marker. A name the client writes as "a skeleton", "an aetherial
     * hydra" or "The Custodian" is a thing, not a person: player names never take an article, and custom pet
     * names are printed bare ("Useless", never "a Useless"). Measured across the six captures, of 872 names
     * the existing rules already call Npc in Incogitable, 681 are article-shaped; among the 772 article-shaped
     * names only two read as Player, and both were Medium guesses, not line evidence.
     *
     * Strong? No - Medium, with two guards, because the shape has measured counterexamples:
     *   - "A good egg" (healed by raiders 36 times, "has died." four times) is a player's pet-rock, and
     *     summon names can be sentences.
     *   - spell names land in the name pool through the attacker field of dot/feedback lines, and calling one
     *     an NPC would hand the graph a friendly target to score against. Anything the spell DB answers for
     *     is therefore left alone (it stays Unknown, exactly as it was before this rule).
     * Medium also means every piece of line evidence - Targeted (NPC/Player), /who, joins, guild speech,
     * ownership words - still outranks it, which is the whole point of a shape rule.
     */
    private static void ApplyNameShape(IFactTable facts, EntityTimeline timeline)
    {
      var store = EQDataStore.Instance;
      foreach (var name in facts.InternedNames)
      {
        if (!HasIndefiniteArticle(name)) continue;
        if (OwnerInName(name) is not null) continue;                    // somebody's summon: R5 owns it
        if (store.GetDamagingSpellByName(name) is not null) continue;   // a spell is not a combatant

        // Same kind, better reason: npcs.txt already naming this creature says WHY it is an NPC, and the
        // report should carry that. Tie-breaking at equal strength is "last writer wins", so the shape rule
        // yields to whatever already spoke at Medium or above instead of overwriting its provenance.
        timeline.IdentityAt(name, double.PositiveInfinity, out var held, out _);
        if (held >= RuleStrength.Medium) continue;

        timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Medium, "R14-shape", double.NegativeInfinity);
      }
    }

    // The client writes the article in front of the name, lower-case at line start and mid-sentence alike.
    internal static bool HasIndefiniteArticle(string name)
      => !string.IsNullOrEmpty(name)
         && (name.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
             || name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
             || name.StartsWith("the ", StringComparison.OrdinalIgnoreCase));

    /*
     * R16 - a comma inside a name field is the client writing "Name, Title", and characters have no titles:
     * an EQ character name is ONE token, so `Teknaz, Bringer of Flames` cannot be a raid member. Where R14
     * reads the article in front of a name, this reads the title after it - the two markers do not overlap
     * (`Kratakel, Lord Misery` takes neither an article nor a registry entry until somebody adds one).
     *
     * Measured over the six captures (2022-2026): exactly 11 name fields carry a comma. Seven of them also
     * arrive with a `Targeted (NPC)` line, which is Certain (R1), and one is an owned summon
     * (`Akini, Xanathan`s Warder`, R5). The remaining three rest on npcs.txt alone - `Glarubaran, the Great
     * Storm`, plus `Kratakel, Lord Misery` (71M of attack damage) and `Ogna, Artisan of War` (12M), which
     * only entered the registry in Sep 2026. Before that edit those two had no identity claim at all beyond
     * the graph's guess, and that is the hole this rule fills: content newer than any list we ship, in a
     * capture where nobody parks the target frame on it. Four years of logs never put a comma name under
     * `Targeted (Player)`, and npcs.txt itself holds 238 of them (`Atathus, the Red Lord`,
     * `Ma`Maie, the Nest Mother`), so the shape is ordinary, not exotic - 0.6 % of the registry, against 8 % that
     * is a single bare word, which is a shape any character could wear (see the R6 tiers above).
     *
     * The one counterexample class is SUMMONS, not players: a pet name is free text, so `Feroun, come back` is
     * legal and prints with no ownership word for R5 to read. What keeps that honest is ordering - this rule runs
     * after R15, which only reads names still Unknown, so a name half the raid keeps topping up stays our side.
     *
     * Medium and not Strong, even though this invariant has no counterexample: what makes a shape rule safe
     * is that every piece of line evidence outranks it, and that property is worth more than a notch of
     * confidence here. The guards are R14's - an owned summon belongs to R5 (whose own owner cut already
     * refuses a comma owner, see UsableOwnerInName), and seven spell rows carry commas in their names
     * (`First Create, Then Destroy`, `Teleport: Doomfire, the Burning Lands`), so the attacker field of a dot
     * line must not be read as a combatant. The database keeps its reason too: R6 already spoke at Medium for
     * every name it knows, and at equal strength the earlier writer's provenance stands.
     */
    private static void ApplyCommaTitle(IFactTable facts, EntityTimeline timeline)
    {
      var store = EQDataStore.Instance;
      foreach (var name in facts.InternedNames)
      {
        if (!HasTitleComma(name)) continue;
        if (OwnerInName(name) is not null) continue;                    // somebody's summon: R5 owns it
        if (store.GetDamagingSpellByName(name) is not null) continue;   // a spell is not a combatant

        timeline.IdentityAt(name, double.PositiveInfinity, out var held, out _);
        if (held >= RuleStrength.Medium) continue;

        timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Medium, "R16-comma", double.NegativeInfinity);
      }
    }

    // "Name, Title" - a comma with text behind it. A stray trailing comma is not a title.
    internal static bool HasTitleComma(string name)
      => !string.IsNullOrEmpty(name) && name.Contains(", ", StringComparison.Ordinal);

    /*
     * R15 - a verified member of our side healing an unclassified name says more about the name than the
     * name says about itself. This is how a mercenary or a pet with a custom name and no owner line shows up:
     * it never speaks, never joins, owns nothing and casts nothing the DB can name - but half the raid keeps
     * topping it up ("Trelania healed Triumph for 47587 … by Symbol of Sharosh"). Measured over six captures
     * (docs/combat-mirror-design.md → "Fourth audit"): the unfiltered heal graph offers 38 names in Kizant 2026
     * and 217 in Incogitable; the gates below keep 23 and 65 of them, and what they take is 19-23 names per raid
     * day carrying 6-14 % of that capture's damage facts. None of the claimed names on any of the six files is a
     * known NPC or ever carried the target frame's `Targeted (NPC)` verdict.
     *
     * What they ARE, checked against the operator's own registry: CUSTOM-NAMED PETS far more often than
     * mercenaries. Sep-2026 has 25 petmapping.txt names in the log and 20 of them read `R15-healed`
     * (Bigboned=Goruuk, Heisenberg=Coas, Triumph=Jazrakhan, Xena=Rahoul, Dangle=Strangle); Incogitable gives
     * 16 of 59, and Squirticus is filed under `Unknown Pet Owner`. `Player` is therefore the least wrong label
     * a name-keyed timeline can write - "raid-side, never verified as a person" - and the owner stays unknown
     * unless the registry says it.
     *
     * The mirror image of that, and the reason heal volume alone must not move an NPC verdict: `Targeted (NPC)`
     * FIRES ON PETS. `Useless` reads `Npc:R1-target` in Incogitable with 303,554 attack edges while the raid
     * healed it 47,752 times from 52 casters; `Dragon`, `Bark`, `Dangle`, `Speedbump`, `Cutie`, `Funky` are the
     * same story (0.4-18.8 % of each capture's facts sit on the enemy column this way). What separates a pet
     * from a mob that merely gets raid AoE is WHO heals it: pets take heals from 19-52 distinct casters, while
     * every genuine hostile in the same lists - `Zelnithak`, `Captain Kar the Unmovable`, `Rufus Invictus`,
     * `Tallongast, The Egg`, `an echo` - tops out at 10. Two statements are true at once ("not a player",
     * "ours"), which is what R9's time-scoped `Friendly` interval exists to carry.
     *
     * The healer must be ours by EVIDENCE, not by guess: strength >= Strong (line-intrinsic or behaviour -
     * roster, joins, guild/group/raid speech, ownership, target frame). A Medium name healing a Medium name
     * would be an inference reasoning from itself, and that is how a charmed raid turns into an army.
     */
    /*
     * Incremental form: the heal walk and the swing-back veto resume at their cursors while the rule's CLOCK
     * still matches the timeline. The clock is load-bearing here in two directions the cursors cannot see: a
     * healer that reaches Strong retroactively qualifies every heal it ever cast (identity intervals span the
     * whole log, so "healer was not ours" is a verdict-dependent answer), and the veto colours each edge by its
     * DEFENDER's side. Any moved verdict rebuilds both aggregates from zero, which reproduces the original walk
     * term for term.
     *
     * The veto is counted over ALL attackers, not just current candidates: a name can become a candidate long
     * after its attacks streamed past the cursor, and today's full pass would count those older swings. The eval
     * below re-checks the admission filter (a name classified since it was admitted must not keep being claimed,
     * exactly as the from-zero walk excludes it) and runs over the carried candidates every pass - SetIdentity
     * dedupe makes that free on the timeline.
     */
    private static void ApplyHealedByRaidSide(IFactTable facts, IHealFactTable heals, EntityTimeline timeline,
                                              ClassificationState state)
    {
      var boundary = timeline.StateStamp();
      var frozen = state.DigestR15 == boundary;
      state.DigestR15 = boundary;
      if (!frozen)
      {
        state.R15Candidates.Clear();
        state.R15Vetos.Clear();
        state.R15HealCursor = 0;
        state.R15VetoCursor = 0;
      }

      var candidates = state.R15Candidates;
      var healList = heals.Heals;
      for (var i = state.R15HealCursor; i < healList.Length; i++)
      {
        var h = healList[i];
        var healer = heals.NameOf(h.HealerIdx);
        var healed = heals.NameOf(h.HealedIdx);
        if (healer == healed) continue;                       // self-heal proves nothing about anybody else

        var hk = timeline.IdentityAt(healer, h.TimeS, out var hs, out _);
        if (hs < RuleStrength.Strong || !IsRaidSideKind(hk)) continue;

        if (!candidates.TryGetValue(healed, out var agg))
        {
          // Only names nothing has classified yet; stronger evidence elsewhere wins by strength anyway.
          if (timeline.IdentityAt(healed, h.TimeS) is not IdentityKind.Unknown) continue;
          agg = candidates[healed] = new HealEdgeAgg();
        }
        agg.Lines++;
        agg.AddHealer(healer);
      }
      state.R15HealCursor = healList.Length;

      // Veto: a name that hits our side is not ours, however gently it was healed. A boss rained on by raid
      // AoE heals is the exact trap, and it answers for itself by swinging back.
      var vetos = state.R15Vetos;
      var factsList = facts.Facts;
      for (var i = state.R15VetoCursor; i < factsList.Length; i++)
      {
        var f = factsList[i];
        var atk = facts.NameOf(f.AtkIdx);
        if (!vetos.TryGetValue(atk, out var veto)) veto = default;
        veto.Edges++;
        if (IsRaidSideKind(timeline.IdentityAt(facts.NameOf(f.DefIdx), f.TimeS))) veto.RaidSideEdges++;
        vetos[atk] = veto;   // AttackVeto is a struct - the dictionary holds the copy, not the local
      }
      state.R15VetoCursor = factsList.Length;

      // Every carried candidate is re-evaluated each pass - today's from-zero walk asserts over its whole
      // candidate dict too, and SetIdentity dedupe makes the re-assertion free. No admission re-check is
      // needed here: a frozen gate proves this store still says "Unknown" for every admitted name, and an
      // unfrozen pass rebuilt the candidates from scratch.
      foreach (var (name, agg) in candidates)
      {
        if (agg.Lines < HealEdgeMinLines || agg.Healers.Count < HealEdgeMinHealers) continue;
        if (vetos.TryGetValue(name, out var veto) && veto.Edges > 0 &&
            (double)veto.RaidSideEdges / veto.Edges > HealEdgeMaxRaidAttackShare) continue;

        timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Medium, "R15-healed", double.NegativeInfinity);
      }
    }

    // Pet/Player/Merc = our side of the board (the projection's own reading; a Friendly interval is a
    // separate question and callers that care ask AffiliationAt themselves).
    private static bool IsRaidSideKind(IdentityKind kind)
      => kind is IdentityKind.Player or IdentityKind.Merc or IdentityKind.Pet;

    internal sealed class HealEdgeAgg
    {
      public int Lines;
      public int Edges;
      public int RaidSideEdges;
      public double LastS;   // latest heal seen (R18 ends its ownership interval from this)
      public readonly HashSet<string> Healers = new(StringComparer.Ordinal);

      public void AddHealer(string healer) => Healers.Add(healer);
    }

    /*
     * R18 - an NPC-verdict name that the whole raid keeps healing is OURS, and its damage belongs on our column.
     *
     * Why a rule can say this at all: `Targeted (NPC)` means "not a player", which is true of a pet and does not
     * mean "the enemy's" (design doc §"Targeted (NPC) means not a player"). Identity is the wrong place to fix it -
     * Certain evidence outranks inference by design, and it should - so this writes an AffiliationKind.PetOfPlayer
     * interval over the heal span instead of relabelling the name. The projection reads the interval, sides the
     * name with the raid, and its damage stops keying a mob row: that is the ~4% of Incogitable-style captures and
     * roughly a third of the cross-check mismatch mass this branch carries.
     *
     * Gates, in order, each one a measured false positive:
     *   - IDENTITY MUST READ NPC. Names still Unknown are R15's job; raid-side names need no interval.
     *   - NO CHARM WINDOW for the name (charmExplained). A charmed boss is healed by half the raid inside its
     *     window, and minting a whole-span ownership interval from those heals would keep a hostile mob on our
     *     side before and after the charm. R9 owns those names.
     *   - HEAL BREADTH >= OurPetMinCasters (see the tunables above), over OurPetMinHealLines lines from casters
     *     whose OWN identity is raid-side at Strong or better - the same rule R15 refuses to break: a Medium
     *     healer is a guess, and a guess reasoning about a guess is how a charmed raid becomes an army.
     *   - ITS OWN SWINGS AT OUR SIDE stay under OurPetMaxRaidAttackShare. A pet hits mobs; a boss answers for
     *     itself by hitting the raid back, and that veto survives however much healing it received.
     *
     * Measured over eqlog_Incogitable_xegony (1.89 M damage facts, 420 k heals) on the first run of this rule:
     * ONE claim, `Useless`, at 34 distinct casters and 3,637 heal lines, moving 7,254,740,918 damage across
     * 303,554 attack edges off the enemy column - the exact name the audit named, and nothing else. In the whole
     * 6-14 caster neighbourhood of the gate there was not one NPC-verdict name: every near-miss was a raid member
     * (Odin 12, Kizant 10) or a possessive-named pet (`Virul`s pet` 14), neither of which this rule is allowed to
     * speak for. The charm gate below fired on nothing there, because these captures hold no hostile-side charm
     * text at all - "is under the influence of" appears ZERO times across the 2022, 2024 and 2026 captures, which
     * is why no rule parses one: R9's one-sidedness is what the log actually writes.
     *
     * The interval is retroactive to the start of the log and ends at the last qualifying heal plus a tail: the
     * breadth gate means the raid never fought this thing, so "it was ours the whole time" is the honest reading
     * (R15 is retroactive for the same reason), while capping T1 means a pet that gets dismissed and later turns
     * up hostile again opens its own row from that moment. No owner is written: which raider owns `Dangle` is
     * history the rules cannot see, and RegistrySeed supplies it in warm runs from petmapping.txt - an ownerless interval
     * leaves AttackerOwner null rather than inventing a raider.
     */
    /*
     * Incremental form, same shape as R15: the stage gates on its OWN boundary digest (the NPC verdicts, healer
     * strengths and charm names it reads are all upstream of it, and anything upstream that moved re-coloured
     * this pass's store), then the heal walk and the swing-back veto resume at their cursors. Evaluation runs
     * over the FULL carried candidates every pass - as the from-zero walk did over its whole dict - so a minted
     * interval re-extends as top-ups stream in, and an unchanged re-add hits AddAffiliation's dedupe and moves
     * no digest.
     */
    private static List<string> ApplyHealedPetIntervals(IFactTable facts, IHealFactTable heals, EntityTimeline timeline,
                                                        IReadOnlyCollection<string> charmExplained,
                                                        ClassificationState state)
    {
      var minted = new List<string>();

      var boundary = timeline.StateStamp();
      var frozen = state.DigestR18 == boundary;
      state.DigestR18 = boundary;
      if (!frozen)
      {
        state.R18Candidates.Clear();
        state.R18Vetos.Clear();
        state.R18HealCursor = 0;
        state.R18VetoCursor = 0;
      }

      var candidates = state.R18Candidates;
      var healList = heals.Heals;
      for (var i = state.R18HealCursor; i < healList.Length; i++)
      {
        var h = healList[i];
        var healer = heals.NameOf(h.HealerIdx);
        var healed = heals.NameOf(h.HealedIdx);
        if (healer == healed) continue;                          // self-heal proves nothing about anybody else

        var hk = timeline.IdentityAt(healer, h.TimeS, out var hs, out _);
        if (hs < RuleStrength.Strong || !IsRaidSideKind(hk)) continue;

        if (!candidates.TryGetValue(healed, out var agg))
        {
          // Only names an NPC verdict is holding on the wrong side; anything else is explained already.
          if (timeline.IdentityWithSource(healed, out _) is not IdentityKind.Npc) continue;
          agg = candidates[healed] = new HealEdgeAgg();
        }
        agg.Lines++;
        agg.AddHealer(healer);
        if (h.TimeS > agg.LastS) agg.LastS = h.TimeS;
      }
      state.R18HealCursor = healList.Length;

      // The swing-back veto, counted over ALL attackers (a name can become a candidate long after its attacks
      // streamed past the cursor, and a from-zero walk counts those older swings), gated below by share.
      var vetos = state.R18Vetos;
      var factsList = facts.Facts;
      for (var i = state.R18VetoCursor; i < factsList.Length; i++)
      {
        var f = factsList[i];
        var atk = facts.NameOf(f.AtkIdx);
        if (!vetos.TryGetValue(atk, out var veto)) veto = default;
        veto.Edges++;
        if (IsRaidSideKind(timeline.IdentityAt(facts.NameOf(f.DefIdx), f.TimeS))) veto.RaidSideEdges++;
        vetos[atk] = veto;   // AttackVeto is a struct - the dictionary holds the copy, not the local
      }
      state.R18VetoCursor = factsList.Length;

      foreach (var (name, agg) in candidates)
      {
        if (charmExplained.Contains(name)) continue;
        if (agg.Lines < OurPetMinHealLines || agg.Healers.Count < OurPetMinCasters) continue;
        if (vetos.TryGetValue(name, out var veto) && veto.Edges > 0 &&
            (double)veto.RaidSideEdges / veto.Edges > OurPetMaxRaidAttackShare) continue;

        timeline.AddAffiliation(AffiliationKind.PetOfPlayer, name, double.NegativeInfinity, agg.LastS + OurPetTailS,
                               RuleStrength.Strong, "R18-healedpet");
        minted.Add(name);
      }
      return minted;
    }

    // R6: every log name that is an exact NPC-database entry (engine's own npcs.txt load).
    // Multi-word names are distinctive enough to assert Npc at Medium; single-token names (an NPC
    // called "Vex" collides with a player named "Vex") only corroborate at Weak.
    private static void ApplyNpcDatabase(IFactTable facts, EntityTimeline timeline)
    {
      var store = EQDataStore.Instance;
      foreach (var name in facts.InternedNames)
      {
        if (!store.IsKnownNpc(name)) continue;
        var strength = name.Contains(' ') ? RuleStrength.Medium : RuleStrength.Weak;
        timeline.SetIdentity(name, IdentityKind.Npc, strength, "R6-npcdb", double.NegativeInfinity);
      }
    }

    // R7: fight-graph inference for names nothing else could classify (melee mains with no joins,
    // chat or spire casts; named custom pets like `Useless` that never show an owner line).
    // Snapshot semantics: subjects must be Unknown and the defenders they face must already be
    // classified ONE side by everything prior (including R6/R14/R15). Opposition from the other side
    // is still an absolute veto - that is the guard keeping a charmed-player raid from inventing NPCs
    // out of players. Unclassified defenders are allowed up to MaxUnknownEdgeShare of the attacker's
    // edges: a handful of unnamed trash in a night of named ones used to be enough to leave a real
    // mercenary unclassified for the whole capture.
    /*
     * Incremental form: gated on this stage's boundary digest. The store at this point holds seed + prior
     * stages ONLY - a fresh timeline per pass means R7's walk never colours edges with its OWN earlier
     * verdicts (that would let the vote bootstrap itself), exactly as before; what it now skips is re-walking
     * edges whose endpoints' verdicts provably have not moved, since any upstream change IS the digest.
     * Evaluation runs over every carried attacker each pass, which is what the from-zero dict rebuild did.
     */
    private static void ApplyGraphInference(IFactTable facts, EntityTimeline timeline,
                                            List<(string Name, double T0, double T1)> friendlyWindows,
                                            ClassificationState state)
    {
      var boundary = timeline.StateStamp();
      var frozen = state.DigestR7 == boundary;
      state.DigestR7 = boundary;
      if (!frozen)
      {
        // Same law as R15/R18: a rebuilt aggregate must be rebuilt over EVERY edge, cursor included.
        state.GraphAggs.Clear();
        state.R7FactCursor = 0;
      }

      var kinds = new Dictionary<string, IdentityKind>(StringComparer.Ordinal);
      foreach (var name in facts.InternedNames)
      {
        kinds[name] = timeline.IdentityWithSource(name, out _);
      }

      var windowsByName = new Dictionary<string, List<(double T0, double T1)>>(StringComparer.Ordinal);
      foreach (var (name, t0, t1) in friendlyWindows)
      {
        if (!windowsByName.TryGetValue(name, out var list)) windowsByName[name] = list = [];
        list.Add((t0, t1));
      }

      var aggByAttacker = state.GraphAggs;
      var factList = facts.Facts;
      for (var fi = state.R7FactCursor; fi < factList.Length; fi++)
      {
        var f = factList[fi];
        var atk = facts.NameOf(f.AtkIdx);
        if (kinds[atk] != IdentityKind.Unknown) continue;

        // A spell name is not a combatant. "You have taken N damage from X." leaves the attacker
        // field holding a spell, and when that spell targets Self in the spell DB ("Cloudburst
        // Strike Feedback XII") the fact is the local player hitting themselves with their own
        // feedback — side evidence from it would fabricate an NPC out of the operator's own cast.
        // Mob dots share the line shape and keep their evidence: their DB entries are not
        // Self-target, and something beating you all night still earns the hostile verdict.
        if (IsSelfTargetDamageSpell(atk)) continue;

        // edges laid down while the attacker is charmed prove nothing about its real side
        if (windowsByName.TryGetValue(atk, out var wins))
        {
          double t = f.TimeS;
          bool charmed = false;
          foreach (var (t0, t1) in wins)
          {
            if (t >= t0 && t < t1) { charmed = true; break; }
          }
          if (charmed) continue;
        }

        var def = facts.NameOf(f.DefIdx);
        var dk = kinds[def];
        if (!aggByAttacker.TryGetValue(atk, out var agg)) aggByAttacker[atk] = agg = new SideAgg();
        switch (dk)
        {
          case IdentityKind.Npc: agg.AddNpc(def, f.TimeS); break;
          case IdentityKind.Unknown: agg.UnknownEdges++; break;
          default: agg.AddPlayerSide(def, f.TimeS); break;  // Player/Pet/Merc
        }
      }
      state.R7FactCursor = factList.Length;

      foreach (var (atk, agg) in aggByAttacker)
      {
        if (agg.UnknownShare > MaxUnknownEdgeShare) continue;

        if (agg.NpcInstances >= OpponentInstances && agg.NpcSpan >= MinSpanS && !agg.SawPlayer)
        {
          timeline.SetIdentity(atk, IdentityKind.Player, RuleStrength.Medium, "R7-graph");
          continue;
        }
        if (agg.PlayerInstances >= OpponentInstances && agg.PlayerSpan >= MinSpanS && !agg.SawNpc)
        {
          timeline.SetIdentity(atk, IdentityKind.Npc, RuleStrength.Medium, "R7-side");
        }
      }
    }

    internal sealed class SideAgg
    {
      // Unclassified defenders, counted rather than flagged: see MaxUnknownEdgeShare.
      public int UnknownEdges;
      public int KnownEdges { get; set; }
      public double UnknownShare => UnknownEdges + KnownEdges == 0 ? 1 : (double)UnknownEdges / (UnknownEdges + KnownEdges);
      public bool SawNpc => _npcTimes.Count > 0;
      public bool SawPlayer => _playerTimes.Count > 0;

      private readonly Dictionary<string, List<long>> _npcTimes = new(StringComparer.Ordinal);
      private readonly Dictionary<string, List<long>> _playerTimes = new(StringComparer.Ordinal);

      // Facts arrive in sequence order, so each per-defender list is sorted: an instance is a
      // combat-burst (gap <= BurstGapS stays the same instance of "a skeleton").
      public int NpcInstances => CountInstances(_npcTimes);
      public int PlayerInstances => CountInstances(_playerTimes);
      public double NpcSpan => Span(_npcTimes);
      public double PlayerSpan => Span(_playerTimes);

      public void AddNpc(string def, long timeS) { KnownEdges++; AddTime(_npcTimes, def, timeS); }
      public void AddPlayerSide(string def, long timeS) { KnownEdges++; AddTime(_playerTimes, def, timeS); }

      private static int CountInstances(Dictionary<string, List<long>> times)
      {
        var count = 0;
        foreach (var list in times.Values)
        {
          count++;
          for (var i = 1; i < list.Count; i++)
          {
            if (list[i] - list[i - 1] > BurstGapS) count++;
          }
        }
        return count;
      }

      private static double Span(Dictionary<string, List<long>> times)
      {
        long min = long.MaxValue, max = long.MinValue;
        foreach (var list in times.Values)
        {
          if (list.Count == 0) continue;
          min = Math.Min(min, list[0]);
          max = Math.Max(max, list[^1]);
        }
        return max > min ? max - min : 0;
      }
    }

    private static void AddTime(Dictionary<string, List<long>> map, string key, long timeS)
    {
      if (!map.TryGetValue(key, out var list)) map[key] = list = [];
      list.Add(timeS);
    }

    // R4 gate: the family pattern must match AND the data files must know this exact rank. The
    // data "knows" it two ways: a single-class rank resolves to a class label (which also rides
    // CastLineParser's registry write), while a multi-bit rank (`Battle Leap Warcry II` = War|Ber)
    // answers no class at all - the identity claim fires, nobody coin-flips a class. An unseen
    // future version claims nothing either way.
    internal static bool IsClassSafeCast(string spell)
      => EQDataStore.IsClassSafeSpellName(spell)
        && (EQDataStore.Instance.GetSpellClass(spell) is not null || EQDataStore.Instance.IsClassAmbiguousFamilyRank(spell));

    // R20 gate, symmetric to R4's: the family pattern must match AND the data files must know this
    // exact rank - an unseen future version claims nothing.
    internal static bool IsPetCastSpell(string spell)
      => EQDataStore.IsPetCastSpellName(spell) && EQDataStore.Instance.GetSpellClass(spell) is not null;

    // Damaging spell the spell DB says only hits its caster (SpellTarget.Self): spell feedback.
    // Cheap dict lookup, reached only for facts whose attacker has no identity yet.
    internal static bool IsSelfTargetDamageSpell(string name)
      => EQDataStore.Instance.GetDamagingSpellByName(name) is { Target: (byte) SpellTarget.Self };
  }
}

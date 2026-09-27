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
  //    (EQDataStore.IsClassSafeSpellName) assert identity here.
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

    // heals is optional: the damage stream alone classifies exactly as it did before this table existed.
    public static MirrorRuleOutcome Apply(IFactTable facts, EntityTimeline timeline, IHealFactTable heals = null)
    {
      var outcome = new MirrorRuleOutcome();
      ApplyLocalPlayer(timeline);
      ApplyEvidence(facts, timeline, outcome);
      ApplyOwnershipFlags(facts, timeline);
      ApplyNpcDatabase(facts, timeline);

      // Name shape before the graph, because the graph reads it: an article-shaped defender is no longer an
      // unknown edge, so attackers that were starved by "a skeleton" being unclassified get their evidence.
      ApplyNameShape(facts, timeline);

      // Before R7 for the same reason - our-side defenders are what R7-side needs to call an attacker hostile.
      if (heals is not null) ApplyHealedByRaidSide(facts, heals, timeline);

      // The other marker the client writes inside a name. After R15 on purpose: R15 only considers names still
      // Unknown, so a summon whose name happens to carry a comma would lose the heal evidence to punctuation.
      ApplyCommaTitle(facts, timeline);

      // Charm after everything that decides sides, before the graph: a window ends when the charmed name
      // swings at somebody these rules put on our side, so it has to see settled identities.
      var charms = CharmWindowPolicy.Apply(facts, timeline);
      outcome.Charms.AddRange(charms);

      ApplyGraphInference(facts, timeline, [.. charms.Select(w => (w.Name, w.T0, w.T1))]);
      return outcome;
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

    // R10 seed: the Phase 3 UI ("set as player / merc / npc from t0") calls through here.
    // Manual strength outranks every rule, retroactively.
    public static void ApplyManualOverride(EntityTimeline timeline, string name, IdentityKind kind)
      => timeline.SetIdentity(name, kind, RuleStrength.Manual, "R10-manual");

    // R12: operator history files (players.txt / petmapping.txt of a zone registry) as corroboration.
    // Not part of Apply - cold-mode rebuild must not see them; warm runs opt in explicitly.
    // Strong (not Certain) on purpose: the accumulated registry demonstrably contains false
    // players (mercs auto-verified by legacy join-line handling), and a run's own Targeted (NPC)
    // Certain evidence must still win - those names surface as "registry suspects" in the report.
    public static void ApplyHistory(EntityTimeline timeline, IEnumerable<string> knownPlayers, IEnumerable<(string Pet, string Owner)> knownPets)
    {
      if (knownPlayers != null)
      {
        foreach (var name in knownPlayers)
        {
          timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Strong, "R12-player");
        }
      }

      if (knownPets != null)
      {
        foreach (var (pet, owner) in knownPets)
        {
          timeline.SetIdentity(pet, IdentityKind.Pet, RuleStrength.Strong, "R12-pet");
          timeline.AddAffiliation(AffiliationKind.PetOfPlayer, pet, double.NegativeInfinity, double.PositiveInfinity, RuleStrength.Strong, $"R12-pet:{owner}");
        }
      }
    }

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

    // Identity evidence only. Charm WINDOWS are not built here: they need settled sides to know when a
    // charm broke, so they come later in Apply through CharmWindowPolicy. What this pass does is stamp the
    // fact that a name was charmed at all (identity stays Npc — a charmed mob is an NPC on our side for a
    // while, never a player).
    private static void ApplyEvidence(IFactTable facts, EntityTimeline timeline, MirrorRuleOutcome outcome)
    {

      // Target-frame verdicts and player-side behavior are collected per name and applied AFTER
      // the sweep as a deterministic ladder (order of evidence arrival must not matter).
      var targetNpc = new HashSet<string>(StringComparer.Ordinal);
      var targetPlayer = new HashSet<string>(StringComparer.Ordinal);
      var playerBehavior = new HashSet<string>(StringComparer.Ordinal);

      foreach (var e in facts.Evidence)
      {
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
            timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Strong, "R3-presence", double.NegativeInfinity);
            break;

          case EvidenceFact.EvMercJoinedGroup:
            // legacy isPossiblePlayerName said no - an oddly-named hireling, authoritative enough.
            timeline.SetIdentity(name, IdentityKind.Merc, RuleStrength.Strong, "R3-merc", double.NegativeInfinity);
            break;

          case EvidenceFact.EvWhoRoster:
            playerBehavior.Add(name);
            timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Certain, "R2-who", double.NegativeInfinity);
            break;

          case EvidenceFact.EvChat:
            // "You" is the local player slot, already verified through the registry path.
            if (PlayerChannels.Contains(facts.AuxOf(e.AuxIdx) ?? string.Empty) && name != ChatType.You)
            {
              playerBehavior.Add(name);
              timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Strong, "R3-chat", double.NegativeInfinity);
            }
            break;

          case EvidenceFact.EvCalledToOwner:
            timeline.SetIdentity(name, IdentityKind.Pet, RuleStrength.Certain, "R5-called", double.NegativeInfinity);
            timeline.AddAffiliation(AffiliationKind.Friendly, name, e.TimeS, double.PositiveInfinity, RuleStrength.Certain, "R5-called");
            break;

          case EvidenceFact.EvCharmStart:
            // A charmed mob is an NPC that is temporarily on your side (identity Npc stays the
            // stable answer; the Friendly interval is time-scoped, per D3).
            timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Strong, "R9-charm", double.NegativeInfinity);
            break;

          case EvidenceFact.EvCharmEnd:
            // Consumed by CharmWindowPolicy, which pairs it with the sighting it belongs to.
            break;

          case EvidenceFact.EvCast:
            // R4 tier 1 only until tier 2 gets its corroboration rule.
            if (IsClassSafeCast(facts.AuxOf(e.AuxIdx)))
            {
              timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Certain, "R4-spell", double.NegativeInfinity);
            }
            break;
        }
      }

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

    private static void ApplyOwnershipFlags(IFactTable facts, EntityTimeline timeline)
    {
      // The verdict depends only on the NAME, so it is taken from the name pool: one pass per distinct name.
      // Sweeping names rather than facts is what lets a summon that never swung a weapon be owned too -
      // "Tuona`s ward" appears in these captures only as something a raid member heals, and a ward nobody
      // owns is a stray row in the display list forever.
      var claimed = new HashSet<string>(StringComparer.Ordinal);
      foreach (var name in facts.InternedNames)
      {
        if (OwnerInName(name) is null) continue;
        ClaimOwnedSummon(timeline, name, claimed);
      }

      // Explicit "Owner: X" annotations: the line says ownership without saying it in the name, and only the
      // flag knows. (Heal lines have no raw text on the fact, so they carry the name-shape case above.)
      foreach (var f in facts.Facts)
      {
        if ((f.Flags & DamageFact.FlagOwnerInLine) == 0) continue;
        ClaimOwnedSummon(timeline, facts.NameOf(f.AtkIdx), claimed);
      }
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
    private static void ApplyHealedByRaidSide(IFactTable facts, IHealFactTable heals, EntityTimeline timeline)
    {
      var candidates = new Dictionary<string, HealEdgeAgg>(StringComparer.Ordinal);
      foreach (var h in heals.Heals)
      {
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

      if (candidates.Count == 0) return;

      // Veto: a name that hits our side is not ours, however gently it was healed. A boss rained on by raid
      // AoE heals is the exact trap, and it answers for itself by swinging back.
      foreach (var f in facts.Facts)
      {
        var atk = facts.NameOf(f.AtkIdx);
        if (!candidates.TryGetValue(atk, out var agg)) continue;
        agg.Edges++;
        if (IsRaidSideKind(timeline.IdentityAt(facts.NameOf(f.DefIdx), f.TimeS))) agg.RaidSideEdges++;
      }

      foreach (var (name, agg) in candidates)
      {
        if (agg.Lines < HealEdgeMinLines || agg.Healers.Count < HealEdgeMinHealers) continue;
        if (agg.Edges > 0 && (double)agg.RaidSideEdges / agg.Edges > HealEdgeMaxRaidAttackShare) continue;

        timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Medium, "R15-healed", double.NegativeInfinity);
      }
    }

    // Pet/Player/Merc = our side of the board (the projection's own reading; a Friendly interval is a
    // separate question and callers that care ask AffiliationAt themselves).
    private static bool IsRaidSideKind(IdentityKind kind)
      => kind is IdentityKind.Player or IdentityKind.Merc or IdentityKind.Pet;

    private sealed class HealEdgeAgg
    {
      public int Lines;
      public int Edges;
      public int RaidSideEdges;
      public readonly HashSet<string> Healers = new(StringComparer.Ordinal);

      public void AddHealer(string healer) => Healers.Add(healer);
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
    private static void ApplyGraphInference(IFactTable facts, EntityTimeline timeline, List<(string Name, double T0, double T1)> friendlyWindows)
    {
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

      var aggByAttacker = new Dictionary<string, SideAgg>(StringComparer.Ordinal);
      foreach (var f in facts.Facts)
      {
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

    private sealed class SideAgg
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

    private static bool IsClassSafeCast(string spell)
      => EQDataStore.IsClassSafeSpellName(spell) && EQDataStore.Instance.GetSpellClass(spell) is not null;

    // Damaging spell the spell DB says only hits its caster (SpellTarget.Self): spell feedback.
    // Cheap dict lookup, reached only for facts whose attacker has no identity yet.
    internal static bool IsSelfTargetDamageSpell(string name)
      => EQDataStore.Instance.GetDamagingSpellByName(name) is { Target: (byte) SpellTarget.Self };
  }
}

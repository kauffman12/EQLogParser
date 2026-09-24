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

    // Attacker name suffixes that carry ownership inside the line (R5). Matches the shapes
    // CombatMirror.HasOwnershipInLine flags on damage facts.
    private static readonly string[] OwnerSuffixes = ["`s pet", "`s warder"];

    // R7 tunables (increment 3): opponent breadth counts INSTANCES, not distinct names — raid
    // pulls reuse names constantly ("a skeleton" dies, the next "a skeleton" is a new instance,
    // separated by a combat-gap). Charmed-player raids are why inference only ever fires for
    // names with NO other identity evidence and requires every defender already classified on
    // one side: a charmed raider attacking allies is never ambiguous because the allies carry
    // their own Certain/Strong assignments.
    private const int OpponentInstances = 3;   // three waves of "a skeleton" count as three
    private const double BurstGapS = 30;       // gap that splits one name into separate instances
    private const double MinSpanS = 60;        // evidence must spread over a real engagement

    public static MirrorRuleOutcome Apply(IFactTable facts, EntityTimeline timeline)
    {
      var outcome = new MirrorRuleOutcome();
      var charmWindows = ApplyEvidence(facts, timeline, outcome);
      ApplyOwnershipFlags(facts, timeline);
      ApplyNpcDatabase(facts, timeline);
      ApplyGraphInference(facts, timeline, charmWindows);
      return outcome;
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

    // Returns the charm (Friendly) intervals for the graph pass; windows close on wear-off or death.
    private static List<(string Name, double T0, double T1)> ApplyEvidence(IFactTable facts, EntityTimeline timeline, MirrorRuleOutcome outcome)
    {
      // charm windows: Edict-style starts open a Friendly interval, the wear-off line closes it.
      // Re-charm without a close leaves the earlier window open until the next close (last-open wins).
      var charmStarts = new Dictionary<string, double>(StringComparer.Ordinal);
      var charmWindows = new List<(string, double, double)>();

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
            charmStarts[name] = e.TimeS;
            timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Strong, "R9-charm", double.NegativeInfinity);
            break;

          case EvidenceFact.EvCharmEnd:
            if (charmStarts.TryGetValue(name, out var charmT0))
            {
              charmStarts.Remove(name);
              charmWindows.Add((name, charmT0, e.TimeS));
              timeline.AddAffiliation(AffiliationKind.Friendly, name, charmT0, e.TimeS, RuleStrength.Certain, "R9-charm");
            }
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

      // A charm that never wore off still ends - the charmed mob died (measured: raid kills end
      // charms by killing). First death at/after the start closes the window; otherwise it stays
      // open through the log's end (owner dismissed it off-screen; nothing contradicts it).
      // Case-insensitive on purpose: charm lines and slain lines can disagree on the leading
      // article's case ("a X has been charmed." vs "A X was slain by …") while identity keys stay ordinal.
      Dictionary<string, List<long>> deathsByName = new(StringComparer.OrdinalIgnoreCase);
      foreach (var d in facts.Deaths)
      {
        AddTime(deathsByName, facts.NameOf(d.KilledIdx), d.TimeS);
      }

      foreach (var (name, t0) in charmStarts)
      {
        var end = double.PositiveInfinity;
        if (deathsByName.TryGetValue(name, out var deaths))
        {
          foreach (var dt in deaths)
          {
            if (dt >= t0 && dt < end) end = dt;
          }
        }
        charmWindows.Add((name, t0, end));
        timeline.AddAffiliation(AffiliationKind.Friendly, name, t0, end, RuleStrength.Certain, "R9-charm");
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

      return charmWindows;
    }

    private static void ApplyOwnershipFlags(IFactTable facts, EntityTimeline timeline)
    {
      foreach (var f in facts.Facts)
      {
        if ((f.Flags & DamageFact.FlagOwnerInLine) == 0) continue;

        var attacker = facts.NameOf(f.AtkIdx);
        var source = "R5-owner";
        string owner = null;
        foreach (var suffix in OwnerSuffixes)
        {
          if (attacker.EndsWith(suffix, StringComparison.Ordinal))
          {
            owner = attacker[..^suffix.Length];
            break;
          }
        }

        timeline.SetIdentity(attacker, IdentityKind.Pet, RuleStrength.Certain, owner is null ? source : $"{source}:{owner}", double.NegativeInfinity);
        if (owner != null)
        {
          // The named owner gets corroboration only: the line proves the pet, not the person.
          timeline.SetIdentity(owner, IdentityKind.Player, RuleStrength.Medium, source);

          // Owner-keyed pet claim: registry petmapping keys are pet NAMES, but log evidence is
          // often only "X`s pet" - scoring pairs through the owner side is what makes the pet
          // metric measurable (docs increment 3).
          timeline.AddAffiliation(AffiliationKind.PetOfPlayer, attacker, double.NegativeInfinity, double.PositiveInfinity, RuleStrength.Certain, $"{source}:{owner}");
        }
      }
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
    // Snapshot semantics: subjects must be Unknown and defenders already classified ONE side by
    // everything prior (including R6). Ambiguous defenders (Unknown) poison the aggregation -
    // a melee pile of unclassified names proves nothing either way, which is exactly the guard
    // that keeps charmed-player raids from inventing NPCs out of players.
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
          case IdentityKind.Unknown: agg.SawUnknown = true; break;
          default: agg.AddPlayerSide(def, f.TimeS); break;  // Player/Pet/Merc
        }
      }

      foreach (var (atk, agg) in aggByAttacker)
      {
        if (!agg.SawUnknown && agg.NpcInstances >= OpponentInstances && agg.NpcSpan >= MinSpanS && !agg.SawPlayer)
        {
          timeline.SetIdentity(atk, IdentityKind.Player, RuleStrength.Medium, "R7-graph");
          continue;
        }
        if (!agg.SawUnknown && agg.PlayerInstances >= OpponentInstances && agg.PlayerSpan >= MinSpanS && !agg.SawNpc)
        {
          timeline.SetIdentity(atk, IdentityKind.Npc, RuleStrength.Medium, "R7-side");
        }
      }
    }

    private sealed class SideAgg
    {
      public bool SawUnknown;
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

      public void AddNpc(string def, long timeS) => AddTime(_npcTimes, def, timeS);
      public void AddPlayerSide(string def, long timeS) => AddTime(_playerTimes, def, timeS);

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
  }
}

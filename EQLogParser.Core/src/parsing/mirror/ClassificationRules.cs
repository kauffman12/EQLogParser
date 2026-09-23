namespace EQLogParser.Mirror
{
  // Strength scale for the rule catalog (docs/batch-parsing-plan.md): higher wins on conflict and
  // a weaker rule never retracts a stronger assignment (EntityTimeline read-time resolution).
  internal static class RuleStrength
  {
    public const int Manual = 1000;  // R10 operator override
    public const int Certain = 100;  // line-intrinsic truth: targeted verdicts, who roster, ownership in line
    public const int Strong = 60;    // behavior only your side performs: join/leave, guild/group/raid speech
    public const int Medium = 30;    // strong corroboration: multi-word NPC name hit, future graph inference
    public const int Weak = 10;      // single-source hints kept for context only
  }

  // Phase 2 rules (identity slice): turn the captured evidence facts into retroactive identity
  // assignments and time-scoped affiliations. One pass per completed run - the fact table is the
  // input, so this stays replay-safe and never touches ingest-time state (D3).
  //
  // Deliberate exclusions, all catalog-mandated:
  //  - Legacy registry verification events are NOT applied here. Cold-mode rebuild measures what the
  //    rules alone can recover; the fidelity path (harness SeedIdentity) still exercises them.
  //  - R4 tier 2 (other single-class spells) waits for its corroboration rule; only tier-1 names
  //    (EQDataStore.IsClassSafeSpellName) assert identity here.
  //  - R7/R8/R11 graph inference, R9 resist-negatives, and R10 manual overrides land in the next
  //    increment; strengths below already leave the ordering room for them.
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

    public static void Apply(IFactTable facts, EntityTimeline timeline)
    {
      ApplyEvidence(facts, timeline);
      ApplyOwnershipFlags(facts, timeline);
      ApplyNpcDatabase(facts, timeline);
    }

    // R12: operator history files (players.txt / petmapping.txt of a zone registry) as corroboration.
    // Not part of Apply - cold-mode rebuild must not see them; warm runs opt in explicitly.
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

    private static void ApplyEvidence(IFactTable facts, EntityTimeline timeline)
    {
      // charm windows: Edict-style starts open a Friendly interval, the wear-off line closes it.
      // Re-charm without a close leaves the earlier window open until the next close (last-open wins).
      var charmStarts = new Dictionary<string, double>(StringComparer.Ordinal);

      foreach (var e in facts.Evidence)
      {
        var name = facts.NameOf(e.NameIdx);
        switch (e.Kind)
        {
          case EvidenceFact.EvTargetedPlayer:
            timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Certain, "R1-target", double.NegativeInfinity);
            break;

          case EvidenceFact.EvTargetedNpc:
            timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Certain, "R1-target", double.NegativeInfinity);
            break;

          case EvidenceFact.EvJoinedRaid:
          case EvidenceFact.EvLeftRaid:
          case EvidenceFact.EvJoinedGroup:
          case EvidenceFact.EvLeftGroup:
          case EvidenceFact.EvRaidLeader:
            timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Strong, "R3-presence", double.NegativeInfinity);
            break;

          case EvidenceFact.EvMercJoinedGroup:
            timeline.SetIdentity(name, IdentityKind.Merc, RuleStrength.Strong, "R3-merc", double.NegativeInfinity);
            break;

          case EvidenceFact.EvWhoRoster:
            timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Certain, "R2-who", double.NegativeInfinity);
            break;

          case EvidenceFact.EvChat:
            // "You" is the local player slot, already verified through the registry path.
            if (PlayerChannels.Contains(facts.AuxOf(e.AuxIdx) ?? string.Empty) && name != ChatType.You)
            {
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

      // charms that never wore off inside the log stay friendly through the end of time (the owner
      // dismissed them off-screen at worst; nothing in the log contradicts it)
      foreach (var (name, t0) in charmStarts)
      {
        timeline.AddAffiliation(AffiliationKind.Friendly, name, t0, double.PositiveInfinity, RuleStrength.Certain, "R9-charm");
      }
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

    private static bool IsClassSafeCast(string spell)
      => EQDataStore.IsClassSafeSpellName(spell) && EQDataStore.Instance.GetSpellClass(spell) is not null;
  }
}

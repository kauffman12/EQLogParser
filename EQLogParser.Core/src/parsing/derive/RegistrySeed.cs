namespace EQLogParser;

// Registry end-state + evidence times as manual identity assignments (D2 warm-registry seeding).
// Strengths stay below the Phase 2 rule tiers so rule output overrides this seed when both are
// present (R10 > R2 > …). Shared by the test harness and the app's derive engine so cold/warm
// semantics can never drift between them.
internal static class RegistrySeed
{
  // Below ClassificationRules tiers, above nothing else that writes identity.
  private const int SeedStrengthVerified = 8;
  private const int SeedStrengthYou = 10;

  public static void Apply(EntityTimeline timeline, IFactTable facts, double logStartS, double logEndS)
  {
    var registry = PlayerRegistry.Instance;

    foreach (var kv in registry.GetVerifiedPlayerTimes())
    {
      // evidence time inside this log -> ingest-replay; outside (persisted warm state or
      // user-set "now") -> retroactive over the whole log, same as Contains at ingest time
      var eff = !double.IsNaN(logStartS) && kv.Value >= logStartS && kv.Value <= logEndS ? kv.Value : double.NegativeInfinity;
      timeline.SetIdentity(kv.Key, IdentityKind.Player, SeedStrengthVerified, "RegistrySeed", eff);
    }

    // pets carry no evidence time — retroactive (documented approximation: a pet verified
    // mid-log is slightly earlier here than at ingest time in the current pipeline).
    // Pet, not Player: both read raid-side, but the label feeds the readers that separate a person from a
    // summon (+Pets folding, the pet rows the fight list hides), and seeding them as people made every one of
    // these names look like an unverified raider.
    foreach (var pet in registry.GetVerifiedPets())
    {
      timeline.SetIdentity(pet, IdentityKind.Pet, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);
    }

    ApplyPetMappings(timeline, registry);

    var player = ConfigUtil.PlayerName;
    if (!string.IsNullOrEmpty(player))
    {
      timeline.SetIdentity(player, IdentityKind.Player, SeedStrengthYou, "You");
    }

    // mercs have no enumeration or times — check every name the facts touched, end-state only
    foreach (var name in facts.InternedNames)
    {
      if (registry.IsMerc(name))
      {
        timeline.SetIdentity(name, IdentityKind.Merc, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);
      }
    }
  }

  /*
   * petmapping.txt as an OWNERSHIP interval, not just a grid row.
   *
   * Until now GetPetMappings() was read by one surface - the Pet Owners tab (MainActions) - and by nothing that
   * classifies, so the operator's own knowledge that `Dangle` belongs to Strangle never reached a board: derived
   * damage credits a pet to its person through EntityTimeline.OwnerOf, and OwnerOf reads affiliation intervals,
   * which nothing in this file used to write. Same argument for `Bigboned`/Goruuk, `Triumph`/Jazrakhan and the
   * other 95 pairs the file holds while the log's own possessive lines prove only 18 of them.
   *
   * Strength: RuleStrength.Strong, NOT a seed strength. The seed tiers (8/10) exist so that a run's rules can
   * outvote an old registry entry, and that stays true for IDENTITY - who a name IS. Whether a named summon
   * belongs to a named raider is not something any rule contradicts: the log either prints the owner (`Sancus`s
   * pet` writes the same interval at Certain, which outranks this) or prints nothing at all. Strong also keeps
   * the pair under R9-charm's Certain, so a window - which is time-scoped evidence about this very run - still
   * wins where both speak.
   *
   * The pet claim itself is unconditional; the OWNER name is only registered as a raider when it is shaped like
   * one. A mapping whose owner text carries a comma (`Akini, Xanathan`s Warder` = one summon, two masters) still
   * tells us who the pet belongs to, but must not invent a raid member named "Akini, Xanathan".
   */
  private static void ApplyPetMappings(EntityTimeline timeline, PlayerRegistry registry)
  {
    foreach (var mapping in registry.GetPetMappings())
    {
      var pet = mapping.Pet;
      var owner = mapping.Owner;
      if (string.IsNullOrWhiteSpace(pet) || string.IsNullOrWhiteSpace(owner)) continue;

      // An unowned row of the grid is the operator saying "this is a summon" without saying whose. That half is
      // still worth what R18 buys - the name stops being the enemy's - but writing "Unassigned" into Owner would
      // hand its damage to a raider who does not exist.
      var knownOwner = !string.Equals(owner, Labels.Unassigned, StringComparison.OrdinalIgnoreCase);
      timeline.AddAffiliation(AffiliationKind.PetOfPlayer, pet, double.NegativeInfinity, double.PositiveInfinity,
                              RuleStrength.Strong, knownOwner ? $"RegistrySeed:{owner}" : "RegistrySeed",
                              knownOwner ? owner : null);

      // The pet itself, when the log never verified it: a custom-named summon with no owner line in this capture
      // is still somebody's summon.
      if (timeline.IdentityWithSource(pet, out _) is not (IdentityKind.Pet or IdentityKind.Player))
        timeline.SetIdentity(pet, IdentityKind.Pet, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);

      if (!knownOwner || owner.Contains(',', StringComparison.Ordinal)) continue;   // `Akini, Xanathan`s Warder` = one summon, two masters

      if (timeline.IdentityWithSource(owner, out _) is not IdentityKind.Player)
        timeline.SetIdentity(owner, IdentityKind.Player, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);
    }
  }
}

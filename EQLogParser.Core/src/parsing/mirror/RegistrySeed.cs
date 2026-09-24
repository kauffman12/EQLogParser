namespace EQLogParser.Mirror;

// Registry end-state + evidence times as manual identity assignments (D2 warm-registry seeding).
// Strengths stay below the Phase 2 rule tiers so rule output overrides this seed when both are
// present (R10 > R2 > …). Shared by the test harness and the app's mirror session so cold/warm
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
    // mid-log is slightly earlier here than at ingest time in the current pipeline)
    foreach (var pet in registry.GetVerifiedPets())
    {
      timeline.SetIdentity(pet, IdentityKind.Player, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);
    }

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
}

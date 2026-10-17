namespace EQLogParser;

[TestClass]
public sealed class PetLeaderEvidenceGuardTest
{
  private static readonly long T0 = (long)FixtureTime.Base;

  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  [TestMethod]
  public void AWeakPlayerSeedCannotVetoAConfessionAgainstAnNpcFrame()
  {
    var facts = Confession();
    var cold = new EntityTimeline();
    ClassificationRules.Apply(facts, cold);
    PlayerRegistry.Instance.AddVerifiedPlayer("Probefoo", T0 - 100);
    var warm = new EntityTimeline();
    RegistrySeed.Apply(warm, facts, T0, T0 + 1);
    ClassificationRules.Apply(facts, warm);
    Assert.AreEqual(IdentityKind.Pet, cold.Identity("Probefoo"));
    Assert.AreEqual(cold.Identity("Probefoo"), warm.Identity("Probefoo"));
    Assert.AreEqual("Probeowner", warm.OwnerOf("Probefoo", T0));
  }

  [TestMethod]
  [DataRow(nameof(IdentityKind.Player))]
  [DataRow(nameof(IdentityKind.Merc))]
  public void OnlyWeakMemoryIsIgnoredNotPositivePersonClaims(string kindWord)
  {
    var kind = Enum.Parse<IdentityKind>(kindWord);
    var facts = Confession();
    var weak = new EntityTimeline();
    weak.SetIdentity("Probefoo", kind, RuleStrength.Weak, "RegistrySeed");
    ClassificationRules.Apply(facts, weak);
    Assert.AreEqual(IdentityKind.Pet, weak.Identity("Probefoo"));

    var proved = new EntityTimeline();
    proved.SetIdentity("Probefoo", kind, RuleStrength.Medium, "person evidence");
    ClassificationRules.Apply(facts, proved);
    Assert.IsFalse(proved.ClaimsOf("Probefoo").Any(claim => claim.Source.StartsWith("R5-leader")));
    Assert.IsNull(proved.OwnerOf("Probefoo", T0));
  }

  private static DamageFactTable Confession()
  {
    var facts = new DamageFactTable(16);
    var speaker = facts.InternName("Probefoo");
    facts.AddEvidence(new EvidenceFact(0, T0, speaker, EvidenceFact.EvPetLeader, facts.InternAux("Probeowner")));
    facts.AddEvidence(new EvidenceFact(1, T0 + 1, speaker, EvidenceFact.EvTargetedNpc, -1));
    return facts;
  }
}

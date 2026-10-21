namespace EQLogParser;

[TestClass]
public class OverlayPendingActivityTest
{
  private const long T0 = (long)FixtureTime.Base;

  [TestMethod]
  public void SelfDamageFriendlyFireAndNpcNoiseAreNotPendingCombat()
  {
    var facts = new DamageFactTable(8);
    var player = facts.InternName("Illuminai");
    var other = facts.InternName("Kizant");
    var guard = facts.InternName("a guard");
    var bat = facts.InternName("a bat");
    facts.AddFact(Hit(0, player, player));
    facts.AddFact(Hit(1, player, other));
    facts.AddFact(Hit(2, guard, bat));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity("Kizant", IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity("a guard", IdentityKind.Npc, RuleStrength.Manual, "manual");
    timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "manual");
    ClassificationRules.Apply(facts, timeline);
    var pending = new OverlayPendingActivity(0);
    pending.Update(facts, timeline);
    Assert.IsFalse(double.IsFinite(pending.Latest));
    pending.Update(facts, timeline);
    Assert.IsFalse(double.IsFinite(pending.Latest), "polling is not activity");
  }

  [TestMethod]
  public void TankingAndTheSpellCasterExceptionRemainEligible()
  {
    var facts = new DamageFactTable(8);
    var guard = facts.InternName("a guard");
    var player = facts.InternName("Illuminai");
    var effect = facts.InternName("a test spell effect");
    facts.AddFact(Hit(0, guard, player));
    facts.AddFact(new DamageFact(1, T0 + 1, effect, guard, 100, LabelTypes.Melee,
      DamageFact.FlagAttackerIsSpell, 0, ushort.MaxValue));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity("a guard", IdentityKind.Npc, RuleStrength.Manual, "manual");
    timeline.SetIdentity("a test spell effect", IdentityKind.Spell, RuleStrength.Manual, "manual");
    ClassificationRules.Apply(facts, timeline);
    var incoming = new OverlayPendingActivity(0);
    incoming.Update(facts, timeline);
    Assert.AreEqual(T0 + 1, incoming.ContinuedThrough("a guard", T0));
    var spellOnly = new OverlayPendingActivity(1);
    spellOnly.Update(facts, timeline);
    Assert.AreEqual(T0 + 1, spellOnly.Latest, "an unnamed caster's damage is not NPC-on-NPC noise");
  }

  [TestMethod]
  public void ChangedVerdictsRecheckThePendingSuffix()
  {
    var facts = new DamageFactTable(8);
    var attacker = facts.InternName("Stranger");
    var defender = facts.InternName("a bat");
    facts.AddFact(Hit(0, attacker, defender));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Stranger", IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "manual");
    ClassificationRules.Apply(facts, timeline);
    var pending = new OverlayPendingActivity(0);
    pending.Update(facts, timeline);
    Assert.AreEqual(T0, pending.Latest);
    timeline.SetIdentity("Stranger", IdentityKind.Npc, RuleStrength.Manual, "manual");
    pending.Update(facts, timeline);
    Assert.IsFalse(double.IsFinite(pending.Latest), "a cached hint must not survive a verdict that excludes it");
  }

  private static DamageFact Hit(int sequence, int attacker, int defender) =>
    new(sequence, T0 + sequence, attacker, defender, 100, LabelTypes.Melee, 0, 0, ushort.MaxValue);
}

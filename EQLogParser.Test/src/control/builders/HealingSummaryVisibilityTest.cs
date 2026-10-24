namespace EQLogParser;

[TestClass]
public sealed class HealingSummaryVisibilityTest
{
  private Func<string, double, IdentityKind>? _savedLiveVerdict;
  private bool _aoe;
  private bool _swarm;

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    _savedLiveVerdict = IdentityLookup.LiveVerdict;
    IdentityLookup.LiveVerdict = null;
    IdentityOverrideStore.Instance.Init(string.Empty);
    IdentityPriorStore.Instance.Init(string.Empty);
    PlayerRegistry.Instance.Clear();
    _aoe = AppSettings.IsAoEHealingEnabled;
    _swarm = AppSettings.IsHealingSwarmPetsEnabled;
    AppSettings.IsAoEHealingEnabled = true;
    AppSettings.IsHealingSwarmPetsEnabled = true;
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityLookup.LiveVerdict = _savedLiveVerdict;
    IdentityOverrideStore.Instance.Init(string.Empty);
    IdentityPriorStore.Instance.Init(string.Empty);
    PlayerRegistry.Instance.Clear();
    AppSettings.IsAoEHealingEnabled = _aoe;
    AppSettings.IsHealingSwarmPetsEnabled = _swarm;
  }

  [TestMethod]
  public void ClassifiedHealersAreListedWithoutDiscardingTheirCapturedHealing()
  {
    const long time = FixtureTime.BaseL;
    var facts = new DamageFactTable(16);
    var heals = new HealFactTable(facts);
    var names = new[] { "Reviewhealer", "Reviewmerc", "Reviewpet", "Reviewnpc", "Reviewspell", "Reviewunknown" };
    var kinds = new[] { IdentityKind.Player, IdentityKind.Merc, IdentityKind.Pet, IdentityKind.Npc,
      IdentityKind.Spell, IdentityKind.Unknown };
    var player = facts.InternName(names[0]);
    facts.AddEvidence(new EvidenceFact(0, time, player, EvidenceFact.EvJoinedRaid, -1));
    facts.AddEvidence(new EvidenceFact(1, time, facts.InternName(names[1]), EvidenceFact.EvMercJoinedGroup, -1));
    facts.AddEvidence(new EvidenceFact(2, time, facts.InternName(names[2]), EvidenceFact.EvPetLeader,
      facts.InternAux(names[0])));
    facts.AddEvidence(new EvidenceFact(3, time, facts.InternName(names[3]), EvidenceFact.EvTargetedNpc, -1));
    facts.AddFact(new DamageFact(4, time, facts.InternName(names[4]), player, 1, LabelTypes.Dd,
      DamageFact.FlagAttackerIsSpell, LineModifiersParser.None, DamageFactTable.NoSubtype));
    for (var i = 0; i < names.Length; i++)
      heals.AddHeal(new HealFact(5 + i, time, facts.InternName(names[i]), player, 100, 0,
        LabelTypes.Heal, 0, LineModifiersParser.None, HealFact.NoSpell));

    var timeline = new EntityTimeline();
    // Healing is a combatant role, so an effect-name collision cannot automatically type this healer as Spell.
    // An explicit operator verdict still can; the board must hide that row without deleting its captured heals.
    ClassificationRules.ApplyManualOverride(timeline, names[4], IdentityKind.Spell);
    ClassificationRules.Apply(facts, timeline, heals);
    IdentityLookup.LiveVerdict = timeline.IdentityAt;
    for (var i = 0; i < names.Length; i++)
      Assert.AreEqual(kinds[i], timeline.IdentityAt(names[i], time), names[i]);

    var range = new TimeRange();
    range.Add(new TimeSegment(time, time + 1));
    var builder = new HealingStatsBuilder();
    builder.BuildTotalStats(new GenerateStatsOptions
    {
      Source = "healing summary visibility test",
      AllRanges = range,
      Heals = HealSummarySource.Materialize(heals, range)
    });
    var combined = builder.GetLastStats()?.CombinedStats;
    Assert.IsNotNull(combined);
    var listed = combined.StatsList.Where(row => HealingStatsBuilder.IsHealerListed(row.OrigName))
      .Select(row => row.OrigName).ToArray();
    CollectionAssert.AreEquivalent(new[] { names[0], names[1], names[5] }, listed);
    Assert.AreEqual(600L, combined.RaidStats.Total, "visibility must not change the retained healing total");
    Assert.AreEqual(6, heals.HealCount);

    builder.PopulateHealing(combined);
    var healed = combined.StatsList.Single(row => row.OrigName == names[0]);
    Assert.AreEqual(600L, healed.Extra, "hidden healers still count in healing received");
    Assert.IsNotNull(healed.MoreStats);
    Assert.HasCount(6, healed.MoreStats.SubStats2);
  }

  [TestMethod]
  [DataRow((int)IdentityKind.Player, true)]
  [DataRow((int)IdentityKind.Merc, true)]
  [DataRow((int)IdentityKind.Pet, false)]
  [DataRow((int)IdentityKind.Npc, false)]
  [DataRow((int)IdentityKind.Spell, false)]
  public void ManualVerdictsControlVisibilityInsteadOfRegistryOrNameShape(int kind, bool listed)
  {
    const string name = "Reviewpick";
    PlayerRegistry.Instance.AddVerifiedPlayer(name, FixtureTime.Base);
    IdentityLookup.LiveVerdict = (_, _) => IdentityKind.Npc;
    Assert.IsFalse(HealingStatsBuilder.IsHealerListed(name));
    IdentityOverrideStore.Instance.Set(name, (IdentityKind)kind);
    Assert.AreEqual(listed, HealingStatsBuilder.IsHealerListed(name));
  }

  [TestMethod]
  public void AnUnknownHealerReturnsWhenItsExcludingVerdictIsTakenBack()
  {
    const string name = "Reviewunplaced";
    IdentityLookup.LiveVerdict = (_, _) => IdentityKind.Unknown;
    Assert.IsTrue(HealingStatsBuilder.IsHealerListed(name));
    IdentityOverrideStore.Instance.Set(name, IdentityKind.Pet);
    Assert.IsFalse(HealingStatsBuilder.IsHealerListed(name));
    IdentityOverrideStore.Instance.Remove(name);
    Assert.IsTrue(HealingStatsBuilder.IsHealerListed(name));
  }
}

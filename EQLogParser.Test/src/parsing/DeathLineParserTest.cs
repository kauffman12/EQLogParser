namespace EQLogParser;

[TestClass]
public class DeathLineParserTest
{
  private const double Time = 1_700_000_000d;
  private const string Previous = "A distant bell rings.";
  private string _priorPlayerName = string.Empty;

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.SharedDataStore();
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear(false);
    _priorPlayerName = ConfigUtil.PlayerName;
    ConfigUtil.PlayerName = "Kizant";
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear(false);
    ConfigUtil.PlayerName = _priorPlayerName;
  }

  [TestMethod]
  [DataRow("Tooke`s pet has died.", "Tooke`s pet", "")]
  [DataRow("Rahoul`s pet has died.", "Rahoul`s pet", "")]
  [DataRow("Tooke`s pet died.", "Tooke`s pet", "")]
  [DataRow("A restless ghoul died.", "A restless ghoul", "")]
  [DataRow("Ammeren died.", "Ammeren", "")]
  [DataRow("The gods have died.", "The gods", "")]
  [DataRow("You have died.", "Kizant", "")]
  [DataRow("You died.", "Kizant", "")]
  [DataRow("A creature that has wings died.", "A creature that has wings", "")]
  [DataRow("Has died.", "Has", "")]
  [DataRow("Tooke`s pet was slain by Echo of Tunat`Muram Cuu Vauax!", "Tooke`s pet", "Echo of Tunat`Muram Cuu Vauax")]
  [DataRow("Tooke`s pet has been slain by Echo of Tunat`Muram Cuu Vauax!", "Tooke`s pet", "Echo of Tunat`Muram Cuu Vauax")]
  [DataRow("You have been slain by an armed flyer!", "Kizant", "An armed flyer")]
  [DataRow("You have slain a failed bodyguard!", "A failed bodyguard", "Kizant")]
  public void DeathNamesExcludeTheVerbPhraseInNewAndLegacyFormats(string action, string killed, string killer)
  {
    var (events, facts) = Parse(action);
    Assert.HasCount(1, events);
    var death = RecordsStore.Instance.GetAllDeaths().Single();
    Assert.AreSame(events[0].Record, death.Item2);
    Assert.AreEqual(Time, death.Item1);
    Assert.AreEqual(killed, death.Item2.Killed);
    Assert.AreEqual(killer, death.Item2.Killer);
    Assert.AreEqual(action, death.Item2.Message);
    Assert.AreEqual(Previous, death.Item2.Previous);

    Assert.AreEqual(1, facts.DeathCount);
    var fact = facts.Deaths[0];
    Assert.AreEqual(killed, facts.NameOf(fact.KilledIdx));
    Assert.AreEqual((long)Time, fact.TimeS);
    if (killer.Length == 0)
    {
      Assert.AreEqual(-1, fact.KillerIdx);
    }
    else
    {
      Assert.AreEqual(killer, facts.NameOf(fact.KillerIdx));
    }
  }

  [TestMethod]
  [DataRow("Tooke`s pet died.")]
  [DataRow("Tooke`s pet has died.")]
  [DataRow("Tooke`s pet was slain by a ghoul!")]
  [DataRow("Tooke`s pet has been slain by a ghoul!")]
  public void CapturedPetDeathKeepsItsIdentityAndOwnerName(string action)
  {
    var (_, facts) = Parse(action);
    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, facts, Time, Time);
    ClassificationRules.Apply(facts, timeline);

    var killed = facts.NameOf(facts.Deaths[0].KilledIdx);
    Assert.AreEqual("Tooke`s pet", killed);
    Assert.AreEqual(IdentityKind.Pet, timeline.Identity(killed));
    Assert.AreEqual("Tooke", ClassificationRules.OwnerInName(killed));
    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Tooke"));
    Assert.IsFalse(facts.InternedNames.Contains("Tooke`s pet has"));
  }

  [TestMethod]
  [DataRow("has died.")]
  [DataRow("have died.")]
  [DataRow("died.")]
  public void MissingSubjectPublishesNoDeath(string action)
  {
    var (events, facts) = Parse(action);
    Assert.HasCount(0, events);
    Assert.AreEqual(0, RecordsStore.Instance.GetAllDeaths().Count());
    Assert.AreEqual(0, facts.DeathCount);
    Assert.HasCount(0, facts.InternedNames);
  }

  [TestMethod]
  [DataRow("Eye of Tester has died.")]
  [DataRow("Tester`s mount has died.")]
  [DataRow("an old chest has died.")]
  public void AuxiliaryDoesNotBypassIgnoredDeathSubjects(string action)
  {
    var (events, facts) = Parse(action);
    Assert.HasCount(0, events);
    Assert.AreEqual(0, RecordsStore.Instance.GetAllDeaths().Count());
    Assert.AreEqual(0, facts.DeathCount);
  }

  private static (List<DeathEvent> Events, DamageFactTable Facts) Parse(string action)
  {
    var lane = new ParseLane();
    var events = new List<DeathEvent>();
    lane.NewDeath += events.Add;
    var facts = new DamageFactTable(16);
    var capture = new CombatCapture(facts, lane: lane);
    var priorLane = ParseLane.Current;
    ParseLane.BindCurrentThread(lane);
    capture.Start();
    try
    {
      DamageLineParser.Process(new LineData { Action = Previous, BeginTime = Time - 1 });
      DamageLineParser.Process(new LineData { Action = action, BeginTime = Time });
    }
    finally
    {
      capture.Stop();
      ParseLane.BindCurrentThread(priorLane);
    }

    return (events, facts);
  }
}

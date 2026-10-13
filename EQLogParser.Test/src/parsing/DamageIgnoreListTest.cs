namespace EQLogParser;

[TestClass]
public class DamageIgnoreListTest
{
  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.SharedDataStore();
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear(false);
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear(false);
  }

  [TestMethod]
  [DataRow("an old chest", true)]
  [DataRow("an old CHEST", true)]
  [DataRow("an old cache", true)]
  [DataRow("an old satchel", true)]
  [DataRow("an old treasure box", true)]
  [DataRow("an old lost treasure", true)]
  [DataRow("Tester`s Mount", true)]
  [DataRow("Tester`s mOuNt", true)]
  [DataRow("Eye of Tester", true)]
  [DataRow("Eye of Veeshan", false)]
  [DataRow("Eye of Despair", false)]
  [DataRow("Eye of Mother", false)]
  [DataRow("an old chest guardian", false)]
  [DataRow("Chest", false)]
  [DataRow("a lost treasure hunter", false)]
  [DataRow("Tester`s pet", false)]
  [DataRow("a restless ghoul", false)]
  public void IgnoredTargetsSuppressDamageAndDeathPublication(string target, bool ignored)
  {
    var lane = new ParseLane();
    var damages = new List<DamageProcessedEvent>();
    var deaths = new List<DeathEvent>();
    lane.DamageProcessed += damages.Add;
    lane.NewDeath += deaths.Add;
    var priorLane = ParseLane.Current;
    ParseLane.BindCurrentThread(lane);
    try
    {
      var hit = $"Tester hits {target} for 3 points of damage.";
      Assert.IsTrue(DamageLineParser.Process(new LineData { Action = hit, BeginTime = 5 }));
      Assert.IsTrue(DamageLineParser.Process(new LineData { Action = $"{target} died.", BeginTime = 6 }));

      var expected = ignored ? 0 : 1;
      Assert.HasCount(expected, damages);
      Assert.HasCount(expected, deaths);
      Assert.AreEqual(expected, RecordsStore.Instance.GetAllDeaths().Count());
      if (!ignored)
      {
        Assert.AreEqual(3u, damages[0].Record.Total);
        Assert.AreEqual(hit, deaths[0].Record.Previous);
      }
    }
    finally
    {
      ParseLane.BindCurrentThread(priorLane);
    }
  }
}

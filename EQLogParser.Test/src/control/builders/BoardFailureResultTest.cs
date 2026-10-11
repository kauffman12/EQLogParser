namespace EQLogParser;

[TestClass]
public sealed class BoardFailureResultTest
{
  [TestMethod]
  [DataRow("damage")]
  [DataRow("tanking")]
  [DataRow("healing")]
  public void ACaughtBuilderFailureIsNotAnAnswerAndACleanRetryResetsIt(string board)
  {
    Action<GenerateStatsOptions> build = board switch
    {
      "damage" => new DamageStatsBuilder().BuildTotalStats,
      "tanking" => new TankingStatsBuilder().BuildTotalStats,
      _ => new HealingStatsBuilder().BuildTotalStats
    };
    var failFast = StatsBuildTrace.FailFast;
    try
    {
      StatsBuildTrace.FailFast = false; // Exercise production's catch, not the suite's usual fail-fast door.
      var options = new GenerateStatsOptions { Source = nameof(BoardFailureResultTest), AllRanges = new TimeRange() };
      options.Npcs.Add(null);
      var gate = new SummaryBuildGate(work => work());
      Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, Run));
      Assert.IsTrue(options.BuildFailed);
      Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, Run), "a swallowed exception must remain retryable");
      options.Npcs.Clear();
      Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, Run));
      Assert.IsFalse(options.BuildFailed);
      Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(1, Run));
      bool Run() { build(options); return !options.BuildFailed; }
    }
    finally
    {
      StatsBuildTrace.FailFast = failFast;
    }
  }

  [TestMethod]
  public void FanoutReportsAThrowOrAnAbandonedWorkerWithoutLosingItsSiblings()
  {
    var ran = 0;
    var answered = BoardFanout.RunParallel([
      new("damage", "test", () => true, () => throw new InvalidOperationException()),
      new("tanking", "test", () => false, () => Assert.Fail("abandoned")),
      new("healing", "test", () => true, () => Interlocked.Increment(ref ran))
    ]);
    Assert.IsFalse(answered);
    Assert.AreEqual(1, ran);
  }
}

using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{

  /*
   * A derive stage that throws is a bug in ONE check, not in the capture: RunStage retires that stage after repeated
   * consecutive failures and every other verdict of the pass survives. These tests drive the guard directly (stages are
   * production-named lambdas inside Apply, which cannot be poisoned from outside) and pin its laws: swallow-but-report,
   * a clean run pays off the streak, retirement is self-only and announced once, and a new session gets the full rule
   * book back. The exception text must ride on the outcome because Core owns no logger — a swallowed throw that nobody
   * can find in eqlogparser.log is how a silently degraded board ships.
   */
  [TestClass]
  public class ClassificationRuleHealthTest
  {
    // This class is the sanctioned exception to the assembly-wide FailFastStages: it pins the SWALLOW-on-purpose
    // semantics (count, report, retire-after-five), which need RunStage to survive a poisoned stage.
    [TestInitialize]
    public void Setup()
    {
      ClassificationRules.FailFastStages = false;
      ClassificationRules.ResetRuleHealth();
    }

    [TestCleanup]
    public void Cleanup()
    {
      ClassificationRules.FailFastStages = true;
      ClassificationRules.ResetRuleHealth();
    }

    [TestMethod]
    public void AFailingStageIsReportedAndThePassContinues()
    {
      var outcome = new ClassificationOutcome();
      var laterRan = false;

      ClassificationRules.RunStage("poison stage", outcome, () => throw new InvalidOperationException("one bad fact"));
      ClassificationRules.RunStage("healthy stage", outcome, () => laterRan = true);

      Assert.AreEqual(1, outcome.FailedRules.Count, "the failure must be reported to the caller (which logs it)");
      StringAssert.Contains(outcome.FailedRules[0], "poison stage");
      StringAssert.Contains(outcome.FailedRules[0], "one bad fact");
      Assert.IsTrue(laterRan, "a failed stage must not cost the pass the stages after it");
      Assert.AreEqual(0, outcome.RetiredRules.Count, "one failure is a hiccup, not a retirement");
    }

    [TestMethod]
    public void ACleanRunPaysOffTheStreak_RetirementNeedsFiveInARow()
    {
      var outcome = new ClassificationOutcome();
      Action poison = () => throw new InvalidOperationException("poison");

      for (var i = 0; i < 4; i++)
        ClassificationRules.RunStage("streaky", outcome, poison);
      Assert.AreEqual(0, outcome.RetiredRules.Count, "four in a row is still short of the line");

      ClassificationRules.RunStage("streaky", outcome, () => { });   // one clean pass wipes the slate

      for (var i = 0; i < 4; i++)
        ClassificationRules.RunStage("streaky", outcome, poison);
      Assert.AreEqual(0, outcome.RetiredRules.Count, "the earlier four must not count toward this five");

      ClassificationRules.RunStage("streaky", outcome, poison);
      CollectionAssert.Contains(outcome.RetiredRules, "streaky", "five IN A ROW retires the stage");
    }

    [TestMethod]
    public void ARetiredStageStopsRunningAndAnnouncesOnce()
    {
      var outcome = new ClassificationOutcome();
      Action poison = () => throw new InvalidOperationException("poison");
      var calls = 0;

      for (var i = 0; i < 5; i++)
        ClassificationRules.RunStage("dying", outcome, poison);
      Assert.AreEqual(1, outcome.RetiredRules.Count);

      ClassificationRules.RunStage("dying", outcome, () => calls++);
      Assert.AreEqual(0, calls, "a retired stage is SKIPPED - that is the whole point: no more exceptions, no more cost");
      Assert.AreEqual(1, outcome.RetiredRules.Count, "retirement is announced once, not per later pass");

      // The other stages keep running after a neighbour retired.
      var ran = false;
      ClassificationRules.RunStage("neighbour", outcome, () => ran = true);
      Assert.IsTrue(ran);
    }

    [TestMethod]
    public void ANewSessionGetsTheFullRuleBook()
    {
      var outcome = new ClassificationOutcome();
      Action poison = () => throw new InvalidOperationException("poison");
      for (var i = 0; i < 5; i++)
        ClassificationRules.RunStage("dying", outcome, poison);

      ClassificationRules.ResetRuleHealth();   // what DeriveEngine's constructor calls

      var ran = false;
      ClassificationRules.RunStage("dying", outcome, () => ran = true);
      Assert.IsTrue(ran, "retirement protects one session's loop; the NEXT capture gets the rule again - it may hold " +
                        "entirely different data that never poisons it");
    }
  }
}

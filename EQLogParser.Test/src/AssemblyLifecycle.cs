using EQLogParser.Mirror;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * Mirror classification stage deaths fail the run. A derive stage that throws is swallowed by design in production
   * (one bad fact must not cost a raid night every line), which in a test reads exactly like "the rules found
   * nothing" — every parity board would go on agreeing while one rule contributed nothing at all. With the flag on,
   * RunStage rethrows and the failure carries its real stack to whoever runs the suite.
   *
   * ClassificationRuleHealthTest pins the swallow-on-purpose semantics and turns the flag off for its own methods;
   * that pairing is part of the contract.
   */
  [TestClass]
  public static class AssemblyLifecycle
  {
    [AssemblyInitialize]
    public static void Initialize(TestContext context) => ClassificationRules.FailFastStages = true;
  }
}

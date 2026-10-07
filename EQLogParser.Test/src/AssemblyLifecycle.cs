using EQLogParser;
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
    public static void Initialize(TestContext context)
    {
      ClassificationRules.FailFastStages = true;

      /*
       * The heal parser's output is observed through its own event for the whole run (see HealRecordTap): heals are no
       * longer duplicated into RecordsStore as live objects, so "what did the parse produce" needs a listener rather
       * than a store read. Assembly-wide on purpose — a per-class attach fails open, i.e. a forgotten attach reads as
       * an empty list and passes.
       */
      HealTap.Attach();
    }
  }
}

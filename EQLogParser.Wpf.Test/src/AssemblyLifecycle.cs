using EQLogParser.Mirror;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * Same guarantee as the other assembly's AssemblyLifecycle: a mirror classification stage that throws fails the
   * run instead of reading as "the rules found nothing". See EQLogParser.Test/src/AssemblyLifecycle.cs for the
   * reasoning and the pairing with ClassificationRuleHealthTest (which turns the flag off for its own methods).
   */
  [TestClass]
  public static class AssemblyLifecycle
  {
    [AssemblyInitialize]
    public static void Initialize(TestContext context) => ClassificationRules.FailFastStages = true;
  }
}

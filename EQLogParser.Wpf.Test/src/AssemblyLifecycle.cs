using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Syncfusion.Licensing;

namespace EQLogParser
{
  /*
   * Same guarantee as the other assembly's AssemblyLifecycle: the classification stage that throws fails the
   * run instead of reading as "the rules found nothing". See EQLogParser.Test/src/AssemblyLifecycle.cs for the
   * reasoning and the pairing with ClassificationRuleHealthTest (which turns the flag off for its own methods).
   */
  [TestClass]
  public static class AssemblyLifecycle
  {
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
      ClassificationRules.FailFastStages = true;

      // A headless test host must not inherit Syncfusion's unlicensed popup. Whatever key the app registers never runs
      // here (MSTest does not construct App), so this process is always keyless, and on a machine whose keyless validation
      // produces a message the FIRST SfDataGrid construction in the process
      // (SfDataGrid.ctor -> LicenseHelper.ValidateLicense, decompiled from Syncfusion.Shared.WPF 34.2.8) builds the
      // "Syncfusion(R) License" message and calls LicenseMessage.DisplayMessage, which does a SYNCHRONOUS
      // Application.Current.Dispatcher.Invoke. The test-host's Application is created by EnsureAppResources on a
      // disposable STA thread that dies without pumping, so that Invoke never returns: the body wedges inside
      // SfDataGrid.ctor for the full 60 s Sta budget, and at the sixth construction (a process counter past five)
      // the over-limit shouldQuit branch re-enters the same display (measured: exactly those two shapes hung).
      // The flag's own meaning is "the host showed/handles the message itself"; set BEFORE any control constructs
      // and every later GetLicenseType reads null
      // instead of a message, so nothing ever displays. (Registering a key here would also silence it, but a test must not
      // depend on a live license string, and this flag is what an app that shows its own notice sets.)
      SyncfusionLicenseProvider.IsLicenseExceptionShown = true;
    }
  }
}

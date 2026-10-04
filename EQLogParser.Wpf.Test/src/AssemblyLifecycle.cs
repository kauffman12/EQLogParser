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

      // Same registration as the app, from the same one line: with a real key in SyncFusionUtil.LicenseKey the test
      // process runs licensed exactly like the app; with the committed empty key it is a vendor no-op and the guard
      // below is what protects a keyless machine.
      SyncFusionUtil.LoadLicense();

      // Guard for every unlicensed state - the committed empty key, or a real key that fails to validate on this
      // machine (machine-side validation can flip with no code change; that is how the hang appeared overnight). On
      // such a run the FIRST SfDataGrid construction in the process (SfDataGrid.ctor -> LicenseHelper.ValidateLicense,
      // decompiled from Syncfusion.Shared.WPF 34.2.8) builds the "Syncfusion(R) License" message and calls
      // LicenseMessage.DisplayMessage, which does a SYNCHRONOUS Application.Current.Dispatcher.Invoke. The test-host's
      // Application is created by EnsureAppResources on a disposable STA thread that dies without pumping, so that
      // Invoke never returns: the body wedges inside SfDataGrid.ctor for the full 60 s Sta budget, and at the sixth
      // construction (a process counter past five) the over-limit shouldQuit branch re-enters the same display
      // (measured: exactly those two shapes hung). The flag's own meaning is "the host showed/handles the message
      // itself"; set BEFORE any control constructs and every later GetLicenseType reads null instead of a message, so
      // nothing ever displays. A valid key makes this line redundant but harmless - it never expires, so it stays.
      SyncfusionLicenseProvider.IsLicenseExceptionShown = true;
    }
  }
}

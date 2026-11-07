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
      StatsBuildTrace.FailFast = true;   // a swallowed builder throw is an empty board: same law, one line (see StatsBuildersTest)

      // Same registration as the app, from the same one line: with a real key in SyncFusionUtil.LicenseKey the test
      // process runs licensed exactly like the app; with the committed empty key it is a vendor no-op and the guard
      // below is what protects a keyless machine.
      SyncFusionUtil.LoadLicense();

      // Set before any control constructs: a keyless or invalid-key run must never enter the vendor's modal notice.
      // The shared Application pumps its dispatcher, so a queued license dialog would really display and block the suite.
      // docs/DesignNotes.md → "A test host is unlicensed, and Syncfusion says so at construction".
      SyncfusionLicenseProvider.IsLicenseExceptionShown = true;

      /*
       * The class door is a HOST HOOK, not a table. `CombatRecordLookup.IsValidClassName` is a public static Func that
       * defaults to `_ => false`, and App.OnStartup installs the real one over EQDataStore's parsed class list. A test
       * process runs no App, so every class write was refused: PlayerRegistry.SetDefaultPlayerClass answers false before
       * it touches anything, which is why the blank-pick take-back test died on its OWN precondition ("the saved class
       * exists before the gesture") rather than on the gesture. This installs a stand-in with the real shape - a closed
       * set of player classes, so a test that asks whether a bogus word is accepted still gets a refusal and the door
       * stays a door.
       */
      CombatRecordLookup.IsValidClassName = name => !string.IsNullOrEmpty(name) && PlayerClassNames.Contains(name);
    }

    // The 21 player classes as EQDataStore's `classes.txt` spells them, minus the pet-only and NPC-only rows.
    private static readonly HashSet<string> PlayerClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
      "Berserker", "Beastlord", "Cavalier", "Champion", "Cleric", "Druid", "Enchanter", "Explorer", "Hero",
      "Magician", "Monk", "Necromancer", "Paladin", "Ranger", "Rogue", "Shadow Knight", "Shaman", "Warrior",
      "Wizard", "Bard", "Ninja",
    };
  }
}

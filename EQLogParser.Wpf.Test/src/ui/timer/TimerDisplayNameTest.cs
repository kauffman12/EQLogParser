using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class TimerDisplayNameTest
  {
    private TimerData CreateTimerData(
      string displayName,
      string? displayNameTemplate = null,
      ConcurrentDictionary<string, string>? variables = null,
      int repeatedCount = -1,
      int counterCount = -1,
      string? logTime = null)
    {
      return new TimerData
      {
        DisplayName = displayName,
        DisplayNameTemplate = displayNameTemplate,
        Variables = variables,
        RepeatedCount = repeatedCount,
        CounterCount = counterCount,
        LogTime = logTime,
        TimerOverlayIds = new ReadOnlyCollection<string>(Array.Empty<string>()),
      };
    }

    #region No Template — returns DisplayName directly

    [TestMethod]
    public void GetDisplayName_NoTemplate_ReturnsDisplayName()
    {
      var timerData = CreateTimerData("My Spell");
      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("My Spell", result);
    }

    [TestMethod]
    public void GetDisplayName_TemplateNoBraces_ReturnsDisplayName()
    {
      var timerData = CreateTimerData("My Spell", "Plain Text No Tokens");
      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("My Spell", result);
    }

    [TestMethod]
    public void GetDisplayName_NullTemplate_ReturnsDisplayName()
    {
      var timerData = CreateTimerData("My Spell", null, new ConcurrentDictionary<string, string>());
      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("My Spell", result);
    }

    #endregion

    #region Variable resolution on every call (no rate-limiting)

    [TestMethod]
    public void GetDisplayName_WithTemplate_ResolvesVariables()
    {
      var variables = new ConcurrentDictionary<string, string> { ["hp"] = "100" };
      var timerData = CreateTimerData(
        displayName: "Shield 100",
        displayNameTemplate: "Shield {hp}",
        variables: variables);

      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("Shield 100", result);
    }

    [TestMethod]
    public void GetDisplayName_ChangedVariable_ReflectsImmediately()
    {
      var variables = new ConcurrentDictionary<string, string> { ["hp"] = "100" };
      var timerData = CreateTimerData(
        displayName: "Shield 100",
        displayNameTemplate: "Shield {hp}",
        variables: variables);

      // First call resolves to "Shield 100"
      Assert.AreEqual("Shield 100", TimerOverlayWindow.GetDisplayName(timerData));

      // Change variable — next call sees it immediately (no rate-limiting)
      variables["hp"] = "25";
      Assert.AreEqual("Shield 25", TimerOverlayWindow.GetDisplayName(timerData));
    }

    #endregion

    #region Built-in codes take precedence over custom variables

    [TestMethod]
    public void GetDisplayName_BuiltInCounter_TakesPrecedenceOverVariable()
    {
      // If a custom variable is named "counter", the built-in {counter} code wins
      var variables = new ConcurrentDictionary<string, string> { ["counter"] = "999" };
      var timerData = CreateTimerData(
        displayName: "Cast 5",
        displayNameTemplate: "Cast {counter}",
        variables: variables,
        counterCount: 5);

      var result = TimerOverlayWindow.GetDisplayName(timerData);
      // Built-in {counter} = 5 takes precedence over variable counter = 999
      Assert.AreEqual("Cast 5", result);
    }

    [TestMethod]
    public void GetDisplayName_BuiltInRepeated_TakesPrecedenceOverVariable()
    {
      var variables = new ConcurrentDictionary<string, string> { ["repeated"] = "999" };
      var timerData = CreateTimerData(
        displayName: "Tick 3",
        displayNameTemplate: "Tick {repeated}",
        variables: variables,
        repeatedCount: 3);

      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("Tick 3", result);
    }

    [TestMethod]
    public void GetDisplayName_BuiltInCodes_ResolvedBeforeVariables()
    {
      // Built-in codes are replaced first, then custom variables resolve
      var variables = new ConcurrentDictionary<string, string> { ["target"] = "Heal" };
      var timerData = CreateTimerData(
        displayName: "Heal 7",
        displayNameTemplate: "{target} {counter}",
        variables: variables,
        counterCount: 7);

      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("Heal 7", result);
    }

    #endregion

    #region Multiple variable resolution

    [TestMethod]
    public void GetDisplayName_MultipleVariables_AllResolved()
    {
      var variables = new ConcurrentDictionary<string, string>
        {
          ["spell"] = "Fireball",
          ["stacks"] = "3",
        };
      var timerData = CreateTimerData(
        displayName: "Fireball 3",
        displayNameTemplate: "{spell} x{stacks}",
        variables: variables);

      var result = TimerOverlayWindow.GetDisplayName(timerData);
      Assert.AreEqual("Fireball x3", result);
    }

    [TestMethod]
    public void GetDisplayName_UnsetVariable_ReplacedWithNull()
    {
      var variables = new ConcurrentDictionary<string, string> { ["spell"] = "Ice" };
      var timerData = CreateTimerData(
        displayName: "Ice ",
        displayNameTemplate: "{spell} {missing}",
        variables: variables);

      var result = TimerOverlayWindow.GetDisplayName(timerData);
      // Unset variables are replaced with null (empty string in ProcessMatchesText)
      Assert.AreEqual("Ice ", result);
    }

    #endregion

    #region Dynamic updates from shared variables

    [TestMethod]
    public void GetDisplayName_SharedVariables_ReflectsExternalChanges()
    {
      // Simulates the real scenario: multiple timers share the same _variables dict,
      // and a trigger action changes a variable while timers are running.
      var sharedVars = new ConcurrentDictionary<string, string> { ["hp"] = "100" };
      var timerData = CreateTimerData(
        displayName: "Shield 100",
        displayNameTemplate: "Shield {hp}",
        variables: sharedVars);

      // First resolution
      Assert.AreEqual("Shield 100", TimerOverlayWindow.GetDisplayName(timerData));

      // Simulate another trigger changing the variable
      sharedVars["hp"] = "25";

      // Next call sees the change immediately (throttled by render cycle in practice)
      Assert.AreEqual("Shield 25", TimerOverlayWindow.GetDisplayName(timerData));
    }

    #endregion
  }
}

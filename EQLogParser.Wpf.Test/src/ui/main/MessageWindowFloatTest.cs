using EQLogParser;
using FontAwesome5;
using System.Windows;

namespace EQLogParser.Wpf.Test;

/*
 * Pin the opt-in window policy before the native show path runs; default callers must retain their existing policy.
 * These Windows-only tests do not show a window or prove focus behavior. Native styles and button-click activation
 * need a live desktop and are untested.
 */
[TestClass]
public class MessageWindowFloatTest
{
  [TestMethod]
  public void TheIncomingPromptFloatsAndNeverTakesTheForeground()
  {
    var topmost = false;
    var showActivated = true;
    var location = WindowStartupLocation.Manual;
    WithWindow(
      () => new MessageWindow("Merge Triggers from Bob or Import to New Folder?", "Quick Share Received",
        MessageWindow.IconType.Question, "New Folder", "Merge", extra: true, floatAboveOtherApps: true),
      window =>
      {
        topmost = window.Topmost;
        showActivated = window.ShowActivated;
        location = window.WindowStartupLocation;
      });

    Assert.IsTrue(topmost, "a prompt nobody summoned has to sit in the topmost band, or it lands under the game");
    Assert.IsFalse(showActivated, "showing the prompt must not request activation away from EQ");
    Assert.AreEqual(WindowStartupLocation.CenterScreen, location, "floating prompts use screen centering");
  }

  [TestMethod]
  public void APromptTheOperatorSummonedKeepsTheOldWindowState()
  {
    var topmost = true;
    var showActivated = false;
    var location = WindowStartupLocation.CenterScreen;
    WithWindow(() => new MessageWindow("Saved.", "Title"), window =>
    {
      topmost = window.Topmost;
      showActivated = window.ShowActivated;
      location = window.WindowStartupLocation;
    });

    Assert.IsFalse(topmost, "only the Quick Share path floats; the other prompts stay where they have always been");
    Assert.IsTrue(showActivated, "a prompt the operator asked for from EQLP may take focus like any dialog");
    Assert.AreEqual(WindowStartupLocation.CenterOwner, location, "the summoned prompt is centred on the window it was asked from");
  }

  private static void WithWindow(Func<MessageWindow> build, Action<MessageWindow> body)
  {
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();

      // The app's own dictionary holds this key; a bare test Application has to answer it or the template load fails.
      app.Resources["EQIconStyle"] ??= new Style(typeof(ImageAwesome));

      var window = build();
      try
      {
        body(window);
      }
      finally
      {
        window.Close();
      }
    });
  }
}

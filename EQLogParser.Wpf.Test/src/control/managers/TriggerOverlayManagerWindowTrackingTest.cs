// <file>
//   Name:        TriggerOverlayManagerWindowTrackingTest.cs
//   Author:      John S
//   Created:     2026-09-24
//   Purpose:     Tests the two seams behind "sound plays but the text overlay never shows": untracking a window the manager did not close,
//                and the cooldown that lets a dropped-text warning be seen without being heard.
//   Notes:       A corpse in _textWindows swallows every later AddText at the window's own _isClosed guard, so an unplanned close (the OS
//                tearing a layered window down, the overlay's own close button) is permanent unless the slot goes with it — while a planned
//                close untracks first and must not resurrect anything through the recovery path. The identity rule below is what separates
//                the two. These run on Windows only, like the rest of this assembly, though neither seam touches WPF at runtime.
// </file>

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using System.Collections.Generic;

// The application's sources live in one flat namespace regardless of folder.
namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class TriggerOverlayManagerWindowTrackingTest
  {
    private const string OverlayId = "fddc65c7-0000-4000-8000-000000000000";

    [TestMethod]
    public void TheWindowsOwnSlotIsUntrackedAndTheCloseCounts()
    {
      var data = new OverlayWindowData();
      var windows = new Dictionary<string, OverlayWindowData> { [OverlayId] = data };

      Assert.IsTrue(TriggerOverlayManager.TryUntrackClosed(windows, OverlayId, data),
        "a close of the window the slot still holds has to empty the slot, or the corpse swallows every later line");
      Assert.IsFalse(windows.ContainsKey(OverlayId), "the slot must be gone so the recovery path can rebuild under it");
    }

    /* The id gets reused: RemoveWindowAsync untracks before Close, and a restart installs a newer window under the same id. A late Closed
       event from the old window must leave the replacement tracked — that is the difference between self-healing and a resurrection race. */
    [TestMethod]
    public void ALateCloseOfAReplacedWindowLeavesTheReplacementAlone()
    {
      var oldData = new OverlayWindowData();
      var newData = new OverlayWindowData();
      var windows = new Dictionary<string, OverlayWindowData> { [OverlayId] = newData };

      Assert.IsFalse(TriggerOverlayManager.TryUntrackClosed(windows, OverlayId, oldData),
        "an event from a window that no longer owns the slot must report itself as nothing to do");
      Assert.IsTrue(ReferenceEquals(windows[OverlayId], newData), "the live window's slot must survive the stale event untouched");
    }

    [TestMethod]
    public void AnUntrackedIdClosesNothing()
    {
      var windows = new Dictionary<string, OverlayWindowData>();

      Assert.IsFalse(TriggerOverlayManager.TryUntrackClosed(windows, OverlayId, new OverlayWindowData()),
        "a planned close already took the entry out; the late event is not a second removal");
    }

    /* A dropped-text warning is the only trace of the failure, so it must survive once and never more: per matched line, a stream of
       tracking messages would otherwise write the log into a roll-over inside a fight. */
    [TestMethod]
    public void AWarningArrivesOncePerCooldownNotOncePerLine()
    {
      var stamps = new ConcurrentDictionary<string, long>();
      const long cooldown = 300;

      Assert.IsTrue(TriggerOverlayManager.ShouldWarnNow(stamps, "text:p1", 1_000, cooldown), "the first sighting must be reported");
      Assert.IsFalse(TriggerOverlayManager.ShouldWarnNow(stamps, "text:p1", 1_100, cooldown), "a second line inside the cooldown is silence");
      Assert.IsFalse(TriggerOverlayManager.ShouldWarnNow(stamps, "text:p1", 1_299, cooldown), "still one tick short of due");

      // Fixed window: the clock runs from the first warning, so a busy trigger cannot postpone the next one by staying busy.
      Assert.IsTrue(TriggerOverlayManager.ShouldWarnNow(stamps, "text:p1", 1_300, cooldown), "due means a warning again");
      Assert.IsTrue(TriggerOverlayManager.ShouldWarnNow(stamps, "timer:p2", 1_310, cooldown), "a timer warning is not silenced by the text one");
      Assert.IsTrue(TriggerOverlayManager.ShouldWarnNow(stamps, "text:p3", 1_310, cooldown), "one trigger's silence does not cover another's");
    }

    /* The stamps live in one shared dictionary keyed by trigger pattern; a clear keeps it bounded against imported packs. The cost of a
       clear is early warnings, never lost text, so the seam only has to promise the dictionary cannot grow without end. */
    [TestMethod]
    public void TheStampDictionaryStaysBounded()
    {
      var stamps = new ConcurrentDictionary<string, long>();

      for (var i = 0; i < 5_000; i++)
      {
        TriggerOverlayManager.ShouldWarnNow(stamps, $"text:p{i}", i, 300);
      }

      Assert.IsTrue(stamps.Count <= 4_096, $"the stamp dictionary grew past its bound ({stamps.Count} keys)");
    }
  }
}

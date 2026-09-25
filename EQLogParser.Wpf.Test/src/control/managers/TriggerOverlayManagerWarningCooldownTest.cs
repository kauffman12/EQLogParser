// <file>
//   Name:        TriggerOverlayManagerWarningCooldownTest.cs
//   Author:      John S
//   Created:     2026-09-24
//   Purpose:     Tests the cooldown seam behind "sound plays but the text overlay never shows": it lets a dropped-text warning be seen
//                once without being heard per line.
//   Notes:       A trigger whose selected overlays resolved to no window (and with no usable default) drops every line it emits, and the
//                throttled Warn is the only trace of that state. The seam must therefore report the first sighting and stay quiet for the
//                cooldown — a stream of tracking messages would otherwise write the log into a roll-over inside a fight. These run on
//                Windows only, like the rest of this assembly, though the seam itself touches no WPF at runtime.
// </file>

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;

// The application's sources live in one flat namespace regardless of folder.
namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class TriggerOverlayManagerWarningCooldownTest
  {
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

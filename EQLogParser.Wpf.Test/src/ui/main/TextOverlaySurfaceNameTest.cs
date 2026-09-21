// <file>
//   Name:        TextOverlaySurfaceNameTest.cs
//   Author:      John S
//   Created:     2026-09-21
//   Purpose:     Tests that a text overlay names itself on the perf report with something a player recognises.
//   Notes:       The name lands inside a line that joins surfaces with "+" and fields with "|", so a title carrying either of those would
//                split one surface into two for whoever reads the stall line. It also has to stay short: the line already carries a dozen
//                counters and is read during a raid, not afterwards at a desk.
//                These run on Windows only, like the rest of this assembly.
// </file>

using Microsoft.VisualStudio.TestTools.UnitTesting;

// The application's sources live in one flat namespace regardless of folder, which is what TextOverlayWindow is reached through here.
namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class TextOverlaySurfaceNameTest
  {
    /* A real id from a measured session, where the report line read "open meter+fct+text29d9e8ff-ac7b-…". */
    private const string NodeId = "29d9e8ff-ac7b-41ef-bf68-ec14f4b442e1";

    [TestMethod]
    public void ATitledOverlayNamesItselfByItsTitle()
    {
      var name = TextOverlayWindow.SurfaceName(new TriggerNode { Name = "Tank Damage", Id = NodeId });

      StringAssert.StartsWith(name, "text:TankDamage", $"the title a player chose should be readable (actual: {name})");
      Assert.IsFalse(name.Contains("ac7b"), $"a whole id should not be what the line carries (actual: {name})");
    }

    /* A stall line is parsed by eye against its separators; a title must not be able to write one of them. */
    [TestMethod]
    public void TheSeparatorsOfTheReportLineCannotComeFromTheTitle()
    {
      var name = TextOverlayWindow.SurfaceName(new TriggerNode { Name = "hate|list+top", Id = NodeId });

      Assert.IsFalse(name.Contains('|'), $"a field separator came out of the title (actual: {name})");
      Assert.IsFalse(name.Contains('+'), $"a surface separator came out of the title (actual: {name})");
      Assert.AreEqual("text:hatelisttop-29d9", name);
    }

    /* Several overlays can be open at once, and the report joins them with "+": two that read alike would be one entry. */
    [TestMethod]
    public void TwoOverlaysSharingATitleAreStillTwoNames()
    {
      var first = TextOverlayWindow.SurfaceName(new TriggerNode { Name = "Hate", Id = NodeId });
      var second = TextOverlayWindow.SurfaceName(new TriggerNode { Name = "Hate", Id = "b7c1d2e3-0000-1111-2222-333344445555" });

      Assert.AreNotEqual(first, second, "two overlays must not collapse into one surface name");
    }

    [TestMethod]
    public void AnOverlayWithoutATitleFallsBackToItsId()
    {
      Assert.AreEqual("text:29d9", TextOverlayWindow.SurfaceName(new TriggerNode { Name = "  ", Id = NodeId }),
        "an unreadable title should still leave a name, not an empty one");
    }

    /*
     * The timer overlay labels itself by this same rule under its own prefix (TimerOverlayWindow), so the stall line says which kind of
     * overlay was on screen while one rule keeps both lists shaped alike.
     */
    [TestMethod]
    public void APrefixNamesTheKindWithoutChangingTheRule()
    {
      var name = TextOverlayWindow.SurfaceName(new TriggerNode { Name = "Nuke Ready", Id = NodeId }, "tmr");

      Assert.AreEqual("tmr:NukeReady-29d9", name);
      Assert.AreEqual("tmr:none", TextOverlayWindow.SurfaceName(null, "tmr"), "the prefix survives the fallback too");
    }

    [TestMethod]
    public void ANullNodeIsNamedRatherThanThrowing()
    {
      Assert.AreEqual("text:none", TextOverlayWindow.SurfaceName(null),
        "the report has to print something even when the node is gone; it runs while a freeze is on screen");
    }
  }
}

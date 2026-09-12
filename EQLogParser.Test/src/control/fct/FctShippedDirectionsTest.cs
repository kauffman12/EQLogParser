using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The direction dials' never-set answers are a shipped decision, not an accident of which mode happened to load first:
   * Save writes every dial out explicitly, so one first-run save in fountain would otherwise bake that mode's default into
   * somebody's file and make the split answer unreachable for them. Pinning the table where it is written
   * (FctOverlaySettings.ShippedDirections) is what keeps a future "simplification" from quietly re-pointing a fresh install.
   */
  [TestClass]
  public sealed class FctShippedDirectionsTest
  {
    /* A file that carries no direction at all gets the spread's own look: my numbers and the heals rise, what lands on the
       player sinks - in whichever mode the file loads as, because neither dial knows about modes. */
    [TestMethod]
    public void NeverSetDirectionsAreTheShippedSpread()
    {
      var (healUp, takenUp, dealtUp) = FctOverlaySettings.ShippedDirections(null, null, null);

      Assert.IsTrue(healUp, "a fresh install's heals rise - the classic look, in either mode");
      Assert.IsFalse(takenUp, "what lands on the player sinks");
      Assert.IsTrue(dealtUp, "my numbers rise away from it");
    }

    /* A saved word wins for each dial independently, so choosing one direction never disturbs the other two's shipped answers. */
    [TestMethod]
    public void ASavedDirectionWins()
    {
      var (healUp, takenUp, dealtUp) = FctOverlaySettings.ShippedDirections("down", "up", null);

      Assert.IsFalse(healUp, "a saved down stays a chosen down, not the shipped up");
      Assert.IsTrue(takenUp, "a saved up stays a chosen up, not the shipped down");
      Assert.IsTrue(dealtUp, "the dial nobody touched still gets its shipped answer");
    }
  }
}

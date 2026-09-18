using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * "No labels" is a shipped decision rather than the absence of one, so it is pinned in the same terms as the direction dials
   * (FctShippedDirectionsTest): the never-set answer is a pure function of the word settings.ini carries, which makes both halves of the
   * rule checkable without a file. What makes this worth pinning is what used to be true — "below" WAS the fall-through of that switch, so
   * absent and chosen were one line of code, and moving the shipped answer would have quietly rewritten every file that never mentioned
   * labels: anybody who had asked for names under their numbers would have woken to amounts alone and no idea they had been overruled.
   */
  [TestClass]
  public sealed class FctShippedLabelTest
  {

    /* A file with no seat in it gets numbers alone, and so does a file with nonsense in it — junk lands on the shipped arrangement instead
       of reaching the geometry as garbage. */
    [TestMethod]
    public void NeverSetLabelsAreNoLabels()
    {
      Assert.AreEqual(FctLabelSide.None, FctOverlaySettings.ShippedLabelSide(null), "a fresh install draws amounts only");
      Assert.AreEqual(FctLabelSide.None, FctOverlaySettings.ShippedLabelSide(""), "an empty value is the same as no value");
      Assert.AreEqual(FctLabelSide.None, FctOverlaySettings.ShippedLabelSide("sideways"), "an unreadable word gets the shipped seat");
    }

    /* A saved word still wins, and every seat is reachable by its own word — below included, which is the one that no longer arrives by
       falling through anything. Spelling is case-free, like every other word in the file. */
    [TestMethod]
    public void ASavedSeatWins()
    {
      Assert.AreEqual(FctLabelSide.Below, FctOverlaySettings.ShippedLabelSide("below"), "names under the numbers survive a change of default");
      Assert.AreEqual(FctLabelSide.Left, FctOverlaySettings.ShippedLabelSide("left"), "the inline seats arrive at themselves");
      Assert.AreEqual(FctLabelSide.Right, FctOverlaySettings.ShippedLabelSide("RIGHT"), "and spelling is case-free, as everywhere else in the file");
      Assert.AreEqual(FctLabelSide.None, FctOverlaySettings.ShippedLabelSide("none"), "asking for none by word is asking for none");
    }

    /* Writer and reader speak one vocabulary: whatever word a seat is saved as, it reads back as that seat — which also pins that no two
       seats claim the same word. */
    [TestMethod]
    public void EverySeatRoundTripsThroughItsWord()
    {
      foreach (var side in new[] { FctLabelSide.Left, FctLabelSide.Below, FctLabelSide.Right, FctLabelSide.None })
      {
        var word = FctOverlaySettings.WordForLabelSide(side);
        Assert.AreEqual(side, FctOverlaySettings.ShippedLabelSide(word), $"{side} has to read back from \"{word}\"");
      }
    }
  }
}

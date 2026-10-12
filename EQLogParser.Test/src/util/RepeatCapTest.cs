using EQLogParser;

namespace EQLogParserTest;

/*
 * The voice-cap behind every per-line `catch` in the parsers (docs/DesignNotes → "Say it once, then count").
 *
 * What matters here is not the counting arithmetic but what it protects: a fault that repeats per log line must cost ONE line of the
 * operator's file, while still being countable and still telling the truth about scale. Each test class resets the cap because the keys are
 * process-wide on purpose (one bug, one line for the whole session), which would otherwise leak between tests.
 */
[TestClass]
public class RepeatCapTest
{
  [TestInitialize]
  public void Reset() => RepeatCap.Reset();

  [TestMethod]
  public void TheFirstSightingSpeaksAndEveryLaterOneIsSilent()
  {
    Assert.IsTrue(RepeatCap.SpeaksOnce("damage.Process NullReferenceException"), "the fault that opens a key must be reported");

    for (var i = 0; i < 5_000; i++)
      Assert.IsFalse(RepeatCap.SpeaksOnce("damage.Process NullReferenceException"), "a per-line fault may not print per line");

    Assert.AreEqual(5_001, RepeatCap.SightingsOf("damage.Process NullReferenceException"),
      "silence is counted, not lost - the scale of a flood has to stay answerable");
    Assert.AreEqual(5_000, RepeatCap.Silenced, "the speaking sighting is not itself silenced");
  }

  [TestMethod]
  public void TwoDifferentFailuresEachGetTheirOwnLine()
  {
    Assert.IsTrue(RepeatCap.SpeaksOnce("heal.Process ArgumentNullException"));
    Assert.IsTrue(RepeatCap.SpeaksOnce("cast.Process InvalidCastException"),
      "keying on the failure rather than the occurrence keeps a second, unrelated bug audible");

    Assert.AreEqual(0, RepeatCap.Silenced);
  }

  [TestMethod]
  public void AnUnknownKeyHasNeverBeenSeen()
  {
    Assert.AreEqual(0, RepeatCap.SightingsOf("nobody.said.this"));
  }

  [TestMethod]
  public void ResetLetsTheNextSightingSpeakAgain()
  {
    Assert.IsTrue(RepeatCap.SpeaksOnce("misc.Process FormatException"));
    Assert.IsFalse(RepeatCap.SpeaksOnce("misc.Process FormatException"));

    RepeatCap.Reset();

    Assert.AreEqual(0, RepeatCap.SightingsOf("misc.Process FormatException"));
    Assert.IsTrue(RepeatCap.SpeaksOnce("misc.Process FormatException"), "after a reset the fault is news again");
  }

  [TestMethod]
  public void ClipPrintsOneLineOfWhateverItIsGiven()
  {
    Assert.AreEqual("<empty>", RepeatCap.Clip(null));
    Assert.AreEqual("<empty>", RepeatCap.Clip(string.Empty));

    Assert.AreEqual("clean line", RepeatCap.Clip("clean line"), "ordinary text passes through untouched");

    var folded = RepeatCap.Clip("[Wed Nov 12 04:13:22 2025] Kizant hits Grimgoh.\r\n--");
    Assert.IsFalse(folded.Contains('\n') || folded.Contains('\r'), "a log line must not split the log line that quotes it");

    var cut = RepeatCap.Clip(new string('x', 900), 240);
    Assert.AreEqual(241, cut.Length, "an unbounded message is an unbounded log line");
    Assert.IsTrue(cut.EndsWith("…", StringComparison.Ordinal), "a clipped message says so");
  }
}

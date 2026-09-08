using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The damage threshold (FctIngest.Threshold, the configure row's "hide under"): a display filter for the white noise
   * of small numbers — MSBT's damageThreshold, off by default like theirs. What these pin down: below is hidden and
   * counted, equal shows (the ladder value itself is still somebody's damage), heals and the zero-damage words are
   * exempt because they are information rather than volume, crits are not exempt (a small crit is still small noise),
   * and off really is off.
   */
  [TestClass]
  public sealed class FctThresholdTest
  {
    private const double Width = 980;
    private const double Height = 640;

    private static FctIngest Ingest(double threshold) => new(new Random(3))
    {
      Style = FctMotionStyle.Hold,
      Layout = FctLayoutChoice.Bands,
      Threshold = threshold,
    };

    /* The way FctManager actually sends a crit: its own lane plus the flag, with pooling to Crit done inside Accept. */
    private static FctHitState AcceptDamage(FctIngest ingest, List<FctHitState> hits, double value, bool crit = false)
      => ingest.Accept(hits, FctLane.DamageDealt, value, "Slash", crit, false, false, null, Width, Height, 0);

    [TestMethod]
    public void DamageBelowTheThresholdIsHiddenAndCounted()
    {
      var ingest = Ingest(1000);
      var hits = new List<FctHitState>();

      Assert.IsNull(AcceptDamage(ingest, hits, 999), "a number under the threshold must not be drawn");
      Assert.AreEqual(1, ingest.HiddenCount, "hiding must be counted — a filter that eats silently is a bug with a UI");

      Assert.IsNotNull(AcceptDamage(ingest, hits, 1000), "the threshold value itself is damage like any other");
      Assert.IsNotNull(AcceptDamage(ingest, hits, 1001));
      Assert.AreEqual(1, ingest.HiddenCount, "visible numbers do not add to the hidden count");
    }

    [TestMethod]
    public void HealsAndWordsAreNeverHidden()
    {
      var ingest = Ingest(5000);
      var hits = new List<FctHitState>();

      Assert.IsNotNull(ingest.Accept(hits, FctLane.HealingDealt, 300, "Flash of Health", false, false, false, null, Width, Height, 0),
        "a heal is information, not volume — even a small one");
      Assert.IsNotNull(ingest.Accept(hits, FctLane.Missed, 0, null, false, false, false, Labels.Miss, Width, Height, 0));
      Assert.IsNotNull(ingest.Accept(hits, FctLane.Defensive, 0, null, false, false, false, Labels.Resist, Width, Height, 0),
        "hiding that I am being resisted because the number beside it is small is exactly the surprise this dial must not produce");

      Assert.AreEqual(0, ingest.HiddenCount);
    }

    /* A crit under the threshold is still small damage; the exemption list is heals and words, nothing else. */
    [TestMethod]
    public void ACritUnderTheThresholdIsHiddenToo()
    {
      var ingest = Ingest(1000);
      var hits = new List<FctHitState>();

      Assert.IsNull(AcceptDamage(ingest, hits, 400, crit: true));
      Assert.AreEqual(1, ingest.HiddenCount);
    }

    [TestMethod]
    public void TheFilterIsOffUntilSomebodyAsks()
    {
      var ingest = Ingest(0);
      var hits = new List<FctHitState>();

      for (var i = 1; i <= 5; i++)
      {
        Assert.IsNotNull(AcceptDamage(ingest, hits, i), "zero means off: nothing may hide at the shipped default");
      }

      Assert.AreEqual(0, ingest.HiddenCount);
    }
  }
}
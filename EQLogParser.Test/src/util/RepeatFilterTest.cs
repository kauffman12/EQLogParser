using EQLogParser;

namespace EQLogParserTest
{
  /*
   * RepeatFilter is the thing that decides whether a record cache spends an entry on a value, so the two
   * properties worth pinning are the ones the cache's correctness rests on: it never claims a value is new
   * once it has marked it (a false "new" only costs a shared instance, but the class promises not to do it
   * while its bits stand), and it never claims more than a sliver of genuinely new values are old (a false
   * "old" is the only way this file can waste memory). What it must never do is grow without bound: when it
   * fills it starts over, which is the accepted wart.
   */
  [TestClass]
  public sealed class RepeatFilterTest
  {
    /* Spread across the whole int range. Real keys come out of HashCode.Combine and are this well mixed;
     * feeding the filter sequential hashes would test nothing about its false-positive rate. */
    private static int Spread(long i)
    {
      var z = (ulong)i + 0x9E3779B97F4A7C15UL;
      z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
      z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
      return (int)(z ^ (z >> 31));
    }

    [TestMethod]
    public void ProbablySeen_FirstCallIsAlwaysNo()
    {
      var filter = new RepeatFilter(100_000);
      Assert.IsFalse(filter.ProbablySeen(Spread(1)));
    }

    [TestMethod]
    public void ProbablySeen_SecondCallForTheSameHashIsYes()
    {
      var filter = new RepeatFilter(100_000);
      var hash = Spread(4242);
      Assert.IsFalse(filter.ProbablySeen(hash));
      Assert.IsTrue(filter.ProbablySeen(hash), "a restated value has to be recognized, or no entry is ever taken");
      Assert.IsTrue(filter.ProbablySeen(hash));
    }

    [TestMethod]
    public void ProbablySeen_BoundaryHashesAreHandled()
    {
      var filter = new RepeatFilter(100_000);
      foreach (var hash in new[] { 0, 1, -1, int.MinValue, int.MaxValue })
      {
        Assert.IsFalse(filter.ProbablySeen(hash));
        Assert.IsTrue(filter.ProbablySeen(hash));
      }
    }

    [TestMethod]
    public void ProbablySeen_NeverForgetsWhatItMarked()
    {
      // 10k marks into a filter sized for a million: nowhere near saturation, so every one of them has to
      // still read as seen when asked again. A miss here means the probes collide in a way that drops bits.
      var filter = new RepeatFilter(1_000_000);
      const int Count = 10_000;
      for (var i = 0; i < Count; i++)
      {
        filter.ProbablySeen(Spread(i));
      }

      var forgotten = 0;
      for (var i = 0; i < Count; i++)
      {
        if (!filter.ProbablySeen(Spread(i)))
        {
          forgotten++;
        }
      }

      Assert.AreEqual(0, forgotten);
    }

    [TestMethod]
    public void ProbablySeen_KeepsFalsePositivesToASmallShare()
    {
      // Sized for 200k with 50k marked: about a tenth density, which is a hundredth of a percent expected.
      // The bound below is loose on purpose — it fails if the filter stops being a bloom filter, not if the
      // hashing has an off day.
      var filter = new RepeatFilter(200_000);
      const int Marked = 50_000;
      for (var i = 0; i < Marked; i++)
      {
        filter.ProbablySeen(Spread(i));
      }

      var wrong = 0;
      const int Fresh = 50_000;
      for (var i = Marked; i < Marked + Fresh; i++)
      {
        if (filter.ProbablySeen(Spread(i)))
        {
          wrong++;
        }
      }

      Assert.IsTrue(wrong < Fresh / 100, $"expected a small false-positive rate, got {wrong} of {Fresh}");
    }

    [TestMethod]
    public void Reset_LeavesEverythingLookingNew()
    {
      var filter = new RepeatFilter(100_000);
      var hash = Spread(99);
      Assert.IsFalse(filter.ProbablySeen(hash));
      Assert.IsTrue(filter.ProbablySeen(hash));

      filter.Reset();

      Assert.IsFalse(filter.ProbablySeen(hash), "clearing the cache clears the sighting history with it");
    }

    [TestMethod]
    public void ProbablySeen_StartsOverWhenFullInsteadOfGrowing()
    {
      // The whole reason this class is allowed to be fixed size: past its window the filter may not keep
      // getting bigger, because memory is what it exists to protect. Starting over costs shared instances,
      // never correctness — RepeatStore treats "new" as "keep your own record".
      var filter = new RepeatFilter(1_000);
      var bytes = filter.Bytes;

      var marker = Spread(7);
      Assert.IsFalse(filter.ProbablySeen(marker));
      Assert.IsTrue(filter.ProbablySeen(marker));

      for (var i = 0; i < 900; i++)
      {
        filter.ProbablySeen(Spread(i + 20_000));
      }

      Assert.IsTrue(filter.ProbablySeen(marker), "a sighting inside the window is still remembered");

      var forgottenAt = -1;
      for (var i = 900; i < 50_000 && forgottenAt < 0; i++)
      {
        if (!filter.ProbablySeen(marker))
        {
          forgottenAt = i;
        }
      }

      Assert.IsTrue(forgottenAt > 0, "a filter past its window is expected to begin again");
      Assert.AreEqual(bytes, filter.Bytes, "beginning again must not allocate");
    }
  }
}

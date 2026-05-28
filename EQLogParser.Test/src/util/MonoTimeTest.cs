using EQLogParser;
using System.Diagnostics;

namespace EQLogParserTest
{
  [TestClass]
  public class MonoTimeTest
  {
    [TestMethod]
    public void Freq_EqualsStopwatchFrequency()
    {
      Assert.AreEqual(Stopwatch.Frequency, MonoTime.Freq);
    }

    [TestMethod]
    public void NowStamp_ReturnsPositiveValue()
    {
      var stamp = MonoTime.NowStamp();
      Assert.IsTrue(stamp > 0, "Stopwatch timestamp should be positive");
    }

    [TestMethod]
    public void NowStamp_IsMonotonicallyIncreasing()
    {
      var first = MonoTime.NowStamp();
      System.Threading.Thread.Sleep(1);
      var second = MonoTime.NowStamp();
      Assert.IsTrue(second >= first, "Timestamps should not decrease");
    }

    [TestMethod]
    public void SecondsToTicks_ConvertsCorrectly()
    {
      var ticks = MonoTime.SecondsToTicks(1.0);
      Assert.AreEqual(MonoTime.Freq, ticks);
    }

    [TestMethod]
    public void SecondsToTicks_HalfSecond()
    {
      var ticks = MonoTime.SecondsToTicks(0.5);
      Assert.AreEqual(MonoTime.Freq / 2, ticks);
    }

    [TestMethod]
    public void SecondsToTicks_Zero()
    {
      var ticks = MonoTime.SecondsToTicks(0.0);
      Assert.AreEqual(0, ticks);
    }

    [TestMethod]
    public void SecondsToTicks_LargeValue()
    {
      var ticks = MonoTime.SecondsToTicks(3600.0); // 1 hour
      Assert.AreEqual(MonoTime.Freq * 3600, ticks);
    }

    [TestMethod]
    public void SecondsToTicks_RoundsCorrectly()
    {
      // 1.5 seconds should round to nearest tick
      var ticks = MonoTime.SecondsToTicks(1.5);
      Assert.AreEqual(MonoTime.Freq * 3 / 2, ticks);
    }
  }
}

using EQLogParser;
using System;
using System.Threading;

namespace EQLogParserTest
{
  [TestClass]
  public class FastTimeTest
  {
    [TestMethod]
    public void Now_ReturnsPositiveSeconds()
    {
      var (seconds, ticks) = FastTime.Now();
      Assert.IsTrue(seconds > 0, "Seconds since epoch should be positive");
    }

    [TestMethod]
    public void Now_ReturnsPositiveTicks()
    {
      var (seconds, ticks) = FastTime.Now();
      Assert.IsTrue(ticks > 0, "Ticks should be positive");
    }

    [TestMethod]
    public void Now_SecondsMatchesTicks()
    {
      var (seconds, ticks) = FastTime.Now();
      var expectedSeconds = ticks / TimeSpan.TicksPerSecond;
      Assert.AreEqual(expectedSeconds, (long)seconds);
    }

    [TestMethod]
    public void Now_ConsecutiveCallsAreNonDecreasing()
    {
      var (s1, t1) = FastTime.Now();
      Thread.Sleep(10);
      var (s2, t2) = FastTime.Now();
      Assert.IsTrue(s2 >= s1, "Seconds should not decrease");
      Assert.IsTrue(t2 >= t1, "Ticks should not decrease");
    }

    [TestMethod]
    public void Now_WithinOneSecond_ReturnsSameWholeSecond()
    {
      var (s1, _) = FastTime.Now();
      var (s2, _) = FastTime.Now();
      // Both calls within same second should return same value
      Assert.AreEqual(s1, s2);
    }

    [TestMethod]
    public void Now_SecondsApproximatelyMatchesDateTime()
    {
      var (fastSeconds, _) = FastTime.Now();
      var dtSeconds = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
      // Should be within 1 second of each other
      Assert.IsTrue(Math.Abs(fastSeconds - dtSeconds) <= 1,
        $"FastTime ({fastSeconds}) should be within 1s of DateTime ({dtSeconds})");
    }

    [TestMethod]
    public void Now_TicksFlooredToWholeSecond()
    {
      var (_, ticks) = FastTime.Now();
      var remainder = ticks % TimeSpan.TicksPerSecond;
      Assert.AreEqual(0, remainder, "Ticks should be floored to whole seconds");
    }

    [TestMethod]
    public void Now_MultipleCallsConsistent()
    {
      double[] seconds = new double[10];
      long[] ticks = new long[10];
      for (var i = 0; i < 10; i++)
      {
        var result = FastTime.Now();
        seconds[i] = result.Seconds;
        ticks[i] = result.Ticks;
      }

      for (var i = 1; i < 10; i++)
      {
        Assert.IsTrue(seconds[i] >= seconds[i - 1],
          $"Call {i} should be >= call {i - 1}");
        Assert.IsTrue(ticks[i] >= ticks[i - 1],
          $"Call {i} ticks should be >= call {i - 1}");
      }
    }
  }
}

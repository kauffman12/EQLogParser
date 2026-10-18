using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class FastTimeTest
  {
    [TestMethod]
    public void OneSampleCarriesTheSameWholeSecondInBothUnits()
    {
      var (seconds, ticks) = FastTime.Now();
      Assert.IsTrue(seconds > 0);
      Assert.AreEqual(0L, ticks % TimeSpan.TicksPerSecond);
      Assert.AreEqual(ticks / TimeSpan.TicksPerSecond, (long)seconds);
    }
  }
}

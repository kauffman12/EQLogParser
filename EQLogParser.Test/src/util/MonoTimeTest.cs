using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class MonoTimeTest
  {
    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.5)]
    [DataRow(1.0)]
    [DataRow(1.5)]
    [DataRow(3600.0)]
    public void SecondsConvertToMonotonicClockTicks(double seconds)
    {
      Assert.AreEqual((long)(seconds * MonoTime.Freq), MonoTime.SecondsToTicks(seconds));
    }
  }
}

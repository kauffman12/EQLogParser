namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class LoadTidyTriggerTest
{
  [TestMethod]
  [DataRow(-1L, false)]
  [DataRow(0L, false)]
  [DataRow(1L, true)]
  public void OnlyAReadThatHandedLinesToTheParserOwesLoadCleanup(long lines, bool expected)
  {
    Assert.AreEqual(expected, LogReader.LoadAllocatedGarbage(lines));
  }
}

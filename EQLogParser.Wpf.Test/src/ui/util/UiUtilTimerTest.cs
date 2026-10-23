namespace EQLogParser;

[TestClass]
public class UiUtilTimerTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void CreateTimerHonoursTheStartFlag(bool start)
  {
    var enabled = !start;
    Sta.Run(() =>
    {
      var timer = UiUtil.CreateTimer((_, _) => { }, 30_000, start);
      try
      {
        enabled = timer.IsEnabled;
      }
      finally
      {
        timer.Stop();
      }
    });

    // Autosave asks for start=true and has no later startup call to repair an ignored flag.
    Assert.AreEqual(start, enabled);
  }
}

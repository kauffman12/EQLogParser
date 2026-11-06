using System.Windows;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class StaLifecycleTest
{
  [TestMethod]
  public void ClosingATestWindowDoesNotPreventTheNextWindowFromShowing()
  {
    var firstConnected = false;
    var secondConnected = false;
    Sta.Run(() =>
    {
      _ = Application.Current ?? new Application();
      var first = new Window { Width = 200, Height = 100 };
      var second = new Window { Width = 200, Height = 100 };
      try
      {
        first.Show();
        Sta.FlushDispatcher();
        firstConnected = first.IsVisible && PresentationSource.FromVisual(first) != null;
        first.Close();
        Sta.FlushDispatcher();
        second.Show();
        Sta.FlushDispatcher();
        secondConnected = second.IsVisible && PresentationSource.FromVisual(second) != null;
      }
      finally
      {
        second.Close();
        first.Close();
      }
    });
    Assert.IsTrue(firstConnected, "The first test window must actually show.");
    Assert.IsTrue(secondConnected, "Closing a fixture window must not shut down the suite's resource-host Application.");
  }
}

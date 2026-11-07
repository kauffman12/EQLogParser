using System.Windows;

#nullable enable annotations

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class StaLifecycleTest
{
  [TestMethod]
  public void ClosingATestWindowDoesNotPreventTheNextWindowFromShowing()
  {
    var firstConnected = false;
    var secondConnected = false;
    Sta.RunOnApplication(() =>
    {
      var app = Application.Current ?? throw new InvalidOperationException("The test Application must exist.");
      Assert.IsTrue(app.Dispatcher.CheckAccess(), "Only the Application's owner thread exercises last-window shutdown.");
      Assert.AreEqual(0, app.Windows.Count, "The lifetime check must start without another registered window.");
      var first = new Window { Width = 200, Height = 100 };
      Window? second = null;
      try
      {
        first.Show();
        Sta.FlushDispatcher();
        firstConnected = first.IsVisible && PresentationSource.FromVisual(first) != null;
        Assert.AreEqual(1, app.Windows.Count, "The first window must really be the Application's last window.");
        first.Close();
        Sta.FlushDispatcher();
        Assert.AreEqual(0, app.Windows.Count);

        // Construction registers a window even before Show: do not construct this one until the first is closed.
        second = new Window { Width = 200, Height = 100 };
        second.Show();
        Sta.FlushDispatcher();
        secondConnected = second.IsVisible && PresentationSource.FromVisual(second) != null;
      }
      finally
      {
        second?.Close();
        first.Close();
      }
    });
    Assert.IsTrue(firstConnected, "The first test window must actually show.");
    Assert.IsTrue(secondConnected, "Closing a fixture window must not shut down the suite's resource-host Application.");
  }
}

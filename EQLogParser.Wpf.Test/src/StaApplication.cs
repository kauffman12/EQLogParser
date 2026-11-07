using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  // Resource state is process-wide, but its dispatcher must outlive the disposable STAs used by individual test bodies.
  internal static class StaApplication
  {
    private static readonly Lazy<Dispatcher> _dispatcher = new(CreateDispatcher);

    internal static Dispatcher Dispatcher => _dispatcher.Value;

    private static Dispatcher CreateDispatcher()
    {
      if (Application.Current is { } existing)
      {
        existing.Dispatcher.Invoke(() => existing.ShutdownMode = ShutdownMode.OnExplicitShutdown,
          DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(30));
        return existing.Dispatcher;
      }

      var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
      var thread = new Thread(() =>
      {
        try
        {
          var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
          ready.SetResult(app.Dispatcher);
          System.Windows.Threading.Dispatcher.Run();
        }
        catch (Exception ex)
        {
          ready.TrySetException(ex);
          ThreadBoundary.Note("WPF test application host", ex);
        }
      }) { IsBackground = true, Name = "EQLogParser test Application" };

      if (!thread.TrySetApartmentState(ApartmentState.STA))
        throw new InvalidOperationException("The test Application requires an STA dispatcher.");
      thread.Start();
      return ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }
  }
}

using System.Windows.Threading;

namespace EQLogParser;

[TestClass]
public sealed class UiUtilAsyncTest
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task InvocationStaysPendingUntilTheAsyncBodyFinishes(bool fromDispatcher)
  {
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var onDispatcher = false;
    var finished = false;
    var invocation = Invoke(fromDispatcher, async () =>
    {
      onDispatcher = StaApplication.Dispatcher.CheckAccess();
      started.SetResult();
      await release.Task;
      finished = true;
    });

    var pending = false;
    try
    {
      await started.Task.WaitAsync(Timeout);
      // Let the dispatcher's outer operation finish while the async body remains suspended.
      await StaApplication.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.WaitAsync(Timeout);
      pending = !invocation.IsCompleted;
    }
    finally
    {
      release.TrySetResult();
      await invocation.WaitAsync(Timeout);
    }

    Assert.IsTrue(onDispatcher);
    Assert.IsTrue(pending, "A caller must not release its import queue when only the dispatcher post has completed.");
    Assert.IsTrue(finished);
  }

  [TestMethod]
  [DataRow(false, false)]
  [DataRow(false, true)]
  [DataRow(true, false)]
  [DataRow(true, true)]
  public async Task AsyncBodyFailuresReachTheAwaitingCaller(bool fromDispatcher, bool afterYield)
  {
    var expected = new InvalidOperationException("Import failed.");
    var invocation = Invoke(fromDispatcher, async () =>
    {
      if (afterYield)
      {
        await Task.Yield();
      }

      throw expected;
    });

    var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => invocation.WaitAsync(Timeout));
    Assert.AreSame(expected, actual, "The worker's catch must observe failures before and after the body's first yield.");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task AsyncBodyCancellationReachesTheAwaitingCaller(bool fromDispatcher)
  {
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var invocation = Invoke(fromDispatcher, async () =>
    {
      started.SetResult();
      await release.Task;
    });

    try
    {
      await started.Task.WaitAsync(Timeout);
    }
    finally
    {
      release.TrySetCanceled();
    }

    await Assert.ThrowsAsync<OperationCanceledException>(() => invocation.WaitAsync(Timeout));
    Assert.IsTrue(invocation.IsCanceled);
  }

  private static Task Invoke(bool fromDispatcher, Func<Task> body)
  {
    var dispatcher = StaApplication.Dispatcher;
    return fromDispatcher
      ? dispatcher.Invoke(() => UiUtil.InvokeAsync(body), DispatcherPriority.Normal)
      : UiUtil.InvokeAsync(body);
  }
}

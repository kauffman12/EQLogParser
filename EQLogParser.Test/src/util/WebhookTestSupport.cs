using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace EQLogParserTest
{
  internal sealed class WebhookTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    internal static HttpResponseMessage Response(HttpStatusCode status, string body = "{}") => new(status) { Content = new StringContent(body) };
    internal static async Task<string> ContentAsync(HttpRequestMessage request, CancellationToken token)
    {
      Assert.IsNotNull(request.Content);
      using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
      return json.RootElement.GetProperty("content").GetString() ?? "";
    }
  }

  internal sealed class WebhookTestClock
  {
    private readonly object _sync = new();
    private DateTimeOffset _now = new(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
    private readonly List<(DateTimeOffset At, TaskCompletionSource Completion)> _waiters = [];
    internal DateTimeOffset Now() { lock (_sync) return _now; }
    internal int Waiting { get { lock (_sync) return _waiters.Count(wait => !wait.Completion.Task.IsCompleted); } }

    internal async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
      token.ThrowIfCancellationRequested();
      var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_sync) _waiters.Add((_now + delay, completion));
      using var registration = token.Register(() => completion.TrySetCanceled(token));
      await completion.Task;
    }

    internal async Task AdvanceAsync(double seconds)
    {
      lock (_sync)
      {
        _now = _now.AddSeconds(seconds);
        foreach (var waiter in _waiters.Where(waiter => waiter.At <= _now)) waiter.Completion.TrySetResult();
        _waiters.RemoveAll(waiter => waiter.At <= _now);
      }
      await Task.Delay(15);
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
      var timer = Stopwatch.StartNew();
      while (!condition())
      {
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), "Timed out waiting for webhook state.");
        await Task.Delay(5);
      }
    }
  }
}
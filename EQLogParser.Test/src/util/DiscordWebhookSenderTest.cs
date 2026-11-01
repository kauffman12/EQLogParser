using EQLogParser;
using System.Net;
using System.Text.Json;
using static EQLogParserTest.WebhookTestHandler;

namespace EQLogParserTest
{
  [TestClass]
  public class DiscordWebhookSenderTest
  {
    private const string Url = "https://discord.com/api/webhooks/123/test";

    [TestMethod]
    [DataRow("{\"retry_after\":0.25}", "1.5", "1", 1500)]
    [DataRow("<html>limited</html>", "1.25", "", 1250)]
    [DataRow("{}", "", "3.5", 3500)]
    [DataRow("{\"retry_after\":\"NaN\"}", "", "", 1000)]
    [DataRow("{\"retry_after\":-1}", "", "", 1000)]
    public async Task RateLimitsRetryIdenticalPayloadAfterTheLongestDeadline(string body, string retryAfter,
      string resetAfter, int milliseconds)
    {
      var clock = new WebhookTestClock();
      var attempts = new List<string>();
      using var handler = new WebhookTestHandler(async (request, token) =>
      {
        Assert.IsNotNull(request.Content);
        attempts.Add(await request.Content.ReadAsStringAsync(token));
        Assert.IsTrue(request.RequestUri?.Query.Contains("wait=true") == true);
        Assert.IsTrue(request.RequestUri?.Query.Contains("thread_id=7") == true);
        var response = Response(attempts.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, body);
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", resetAfter);
        return response;
      });
      using var client = new HttpClient(handler);
      var sender = new DiscordWebhookSender(client, clock.Now, clock.DelayAsync);
      var sending = sender.SendAsync(WebhookMessage.Create("line", false)[0], Url + "?wait=false&thread_id=7");
      await WebhookTestClock.UntilAsync(() => clock.Waiting == 1);
      await clock.AdvanceAsync((milliseconds - 1) / 1000d);
      Assert.HasCount(1, attempts);
      await clock.AdvanceAsync(.001);
      await sending.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.HasCount(2, attempts);
      Assert.AreEqual(attempts[0], attempts[1]);
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    public async Task PermanentRejectionsAreNotRetried(int status)
    {
      var attempts = 0;
      using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(++attempts == 1
        ? (HttpStatusCode)status : HttpStatusCode.OK)));
      using var client = new HttpClient(handler);
      var sender = new DiscordWebhookSender(client);
      await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(WebhookMessage.Create("bad", false)[0], Url));
      await sender.SendAsync(WebhookMessage.Create("next", false)[0], Url);
      Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(408)]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task TemporaryAndUncertainFailuresRetryWithTheSameIdentity(int status)
    {
      var clock = new WebhookTestClock();
      var payloads = new List<string>();
      using var handler = new WebhookTestHandler(async (request, token) =>
      {
        Assert.IsNotNull(request.Content);
        payloads.Add(await request.Content.ReadAsStringAsync(token));
        if (payloads.Count == 1)
        {
          if (status == 0) throw new HttpRequestException("connection lost");
          if (status == -1) throw new TaskCanceledException("request timed out");
          return Response((HttpStatusCode)status);
        }
        return Response(HttpStatusCode.OK);
      });
      using var client = new HttpClient(handler);
      var sender = new DiscordWebhookSender(client, clock.Now, clock.DelayAsync);
      var sending = sender.SendAsync(WebhookMessage.Create("loot", true)[0], Url);
      await WebhookTestClock.UntilAsync(() => clock.Waiting == 1);
      await clock.AdvanceAsync(5);
      await sending.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.HasCount(2, payloads);
      Assert.AreEqual(payloads[0], payloads[1]);
    }

    [TestMethod]
    public async Task RepeatedTemporaryFailuresBackOffToSixtySecondsAndKeepRetrying()
    {
      var clock = new WebhookTestClock();
      var attempts = 0;
      using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(++attempts <= 6
        ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
      using var client = new HttpClient(handler);
      var sender = new DiscordWebhookSender(client, clock.Now, clock.DelayAsync);
      var sending = sender.SendAsync(WebhookMessage.Create("retry", false)[0], Url);
      foreach (var seconds in new[] { 5, 10, 20, 40, 60, 60 })
      {
        await WebhookTestClock.UntilAsync(() => clock.Waiting == 1);
        var before = attempts;
        await clock.AdvanceAsync(seconds - 1);
        Assert.AreEqual(before, attempts);
        await clock.AdvanceAsync(1);
        await WebhookTestClock.UntilAsync(() => attempts == before + 1);
      }
      await sending.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.AreEqual(7, attempts);
    }

    [TestMethod]
    public async Task CancelingARateLimitedSourceReleasesOtherQueuedSends()
    {
      var clock = new WebhookTestClock();
      var calls = new List<string>();
      using var handler = new WebhookTestHandler(async (request, token) =>
      {
        calls.Add(await ContentAsync(request, token));
        return Response(calls.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "{\"retry_after\":5}");
      });
      using var client = new HttpClient(handler);
      using var canceled = new CancellationTokenSource();
      var sender = new DiscordWebhookSender(client, clock.Now, clock.DelayAsync);
      var first = sender.SendAsync(WebhookMessage.Create("first", false)[0], Url, canceled.Token);
      var second = sender.SendAsync(WebhookMessage.Create("second", false)[0], Url);
      await WebhookTestClock.UntilAsync(() => clock.Waiting == 1);
      canceled.Cancel();
      await Assert.ThrowsAsync<OperationCanceledException>(() => first);
      await clock.AdvanceAsync(5);
      await second.WaitAsync(TimeSpan.FromSeconds(5));
      CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
    }

    [TestMethod]
    public void LinePackedPartsPreserveTheLegacyWireFixturesLootAndMetadata()
    {
      var data = Path.Combine(AppContext.BaseDirectory, "mini-data");
      var lines = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(data, "discord-webhook-burst.json")));
      Assert.IsNotNull(lines);
      var content = string.Join("\n", lines.Concat(lines));
      var parts = WebhookMessage.Create(content, true, "0123456789abcdef0123456789abcdef");
      using var wire = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "discord-webhook-aggregate.json")));
      Assert.AreEqual(content, string.Concat(wire.RootElement.EnumerateArray()
        .Select(part => part.GetProperty("content").GetString())));
      Assert.AreEqual(content, string.Concat(parts.Select(part => part.Content)));
      Assert.AreEqual(parts.Count, wire.RootElement.GetArrayLength());
      for (var i = 0; i < parts.Count; i++)
      {
        using var payload = JsonDocument.Parse(parts[i].Json);
        Assert.AreEqual(wire.RootElement[i].GetProperty("embeds").GetRawText(),
          payload.RootElement.GetProperty("embeds").GetRawText());
      }
    }

    [TestMethod]
    public async Task GlobalRateLimitAlsoPausesAnotherWebhook()
    {
      var clock = new WebhookTestClock();
      var calls = 0;
      using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(Interlocked.Increment(ref calls) == 1
        ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "{\"retry_after\":2,\"global\":true}")));
      using var client = new HttpClient(handler);
      var sender = new DiscordWebhookSender(client, clock.Now, clock.DelayAsync);
      var first = sender.SendAsync(WebhookMessage.Create("one", false)[0], Url);
      await WebhookTestClock.UntilAsync(() => clock.Waiting == 1);
      var second = sender.SendAsync(WebhookMessage.Create("two", false)[0], Url + "-other");
      await WebhookTestClock.UntilAsync(() => clock.Waiting == 2);
      Assert.AreEqual(1, calls);
      await clock.AdvanceAsync(2);
      await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
      Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task SuccessfulExhaustedBucketDelaysTheNextPart()
    {
      var clock = new WebhookTestClock();
      var calls = 0;
      using var handler = new WebhookTestHandler((_, _) =>
      {
        calls++;
        var response = Response(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", "1.5");
        return Task.FromResult(response);
      });
      using var client = new HttpClient(handler);
      var sender = new DiscordWebhookSender(client, clock.Now, clock.DelayAsync);
      var parts = WebhookMessage.Create(new string('x', 2500), true);
      await sender.SendAsync(parts[0], Url);
      var second = sender.SendAsync(parts[1], Url + "?thread_id=7");
      Assert.AreEqual(1, calls);
      await clock.AdvanceAsync(1.5);
      await second.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void SplitPartsPreserveUnicodeAndMetadataWithinDiscordLimit()
    {
      var content = new string('x', 1999) + "🌳\n" + new string('y', 4100);
      var parts = WebhookMessage.Create(content, true);
      Assert.HasCount(5, parts);
      Assert.IsTrue(parts.All(part => part.Content.Length <= 2000));
      Assert.AreEqual(content, string.Concat(parts.Select(part => part.Content)));
      Assert.HasCount(1, parts.Select(part => part.BatchId).Distinct().ToArray());
      foreach (var part in parts)
      {
        using var payload = JsonDocument.Parse(part.Json);
        var footer = payload.RootElement.GetProperty("embeds")[0].GetProperty("footer").GetProperty("text").GetString();
        Assert.AreEqual($"EQLP1|{part.BatchId}|{part.Part}|5|A|{part.Digest}", footer);
        Assert.IsFalse(char.IsHighSurrogate(part.Content[^1]));
      }
      Assert.AreNotEqual(parts[0].BatchId, WebhookMessage.Create(content, true)[0].BatchId);
    }
  }
}
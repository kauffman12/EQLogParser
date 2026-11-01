using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal sealed class DiscordWebhookSender
  {
    private readonly HttpClient _client;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly object _sync = new();
    private readonly Dictionary<string, WebhookState> _webhooks = new(StringComparer.Ordinal);
    private DateTimeOffset _globalRetryAt;

    internal DiscordWebhookSender(HttpClient client, Func<DateTimeOffset> utcNow = null,
      Func<TimeSpan, CancellationToken, Task> delayAsync = null)
    {
      _client = client;
      _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
      _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
    }

    internal async Task SendAsync(WebhookMessage message, string webhookUrl,
      CancellationToken cancellationToken = default, Action retrying = null)
    {
      ArgumentNullException.ThrowIfNull(message);
      if (!Uri.TryCreate(webhookUrl?.Trim(), UriKind.Absolute, out var url)
        || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
      {
        throw new ArgumentException("Invalid Discord webhook URL.", nameof(webhookUrl));
      }

      WebhookState queue;
      lock (_sync)
      {
        /* Query options such as wait/thread_id share the webhook's rate limit. */
        var key = url.GetLeftPart(UriPartial.Path);
        if (!_webhooks.TryGetValue(key, out queue))
        {
          queue = new();
          _webhooks.Add(key, queue);
        }
      }

      /* wait=true requests confirmation that Discord actually saved the message. */
      var builder = new UriBuilder(url);
      var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Where(value => !string.Equals(value.Split('=')[0], "wait", StringComparison.OrdinalIgnoreCase));
      builder.Query = string.Join("&", query.Append("wait=true"));
      await queue.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        await SendQueuedAsync(queue, builder.Uri, message.Json, cancellationToken, retrying).ConfigureAwait(false);
      }
      finally
      {
        queue.Gate.Release();
      }
    }

    internal static string EndpointKey(string url) => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
      && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
      ? uri.GetLeftPart(UriPartial.Path) : url?.Trim() ?? "";

    private async Task SendQueuedAsync(WebhookState queue, Uri url, string json,
      CancellationToken cancellationToken, Action retrying)
    {
      var retrySeconds = 5;
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        await WaitForLimitAsync(queue, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response;
        try
        {
          using var body = new StringContent(json, Encoding.UTF8, "application/json");
          response = await _client.PostAsync(url, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
          && ex is HttpRequestException or OperationCanceledException)
        {
          retrying?.Invoke();
          UpdateRetryAt(queue, TimeSpan.FromSeconds(retrySeconds), false);
          retrySeconds = Math.Min(60, retrySeconds * 2);
          continue;
        }

        using (response)
        {
          if (response.StatusCode == HttpStatusCode.TooManyRequests)
          {
            var (delay, global) = await ReadRateLimitAsync(response, cancellationToken).ConfigureAwait(false);
            UpdateRetryAt(queue, delay, global);
            retrying?.Invoke();
            continue;
          }

          if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
          {
            retrying?.Invoke();
            UpdateRetryAt(queue, TimeSpan.FromSeconds(retrySeconds), false);
            retrySeconds = Math.Min(60, retrySeconds * 2);
            continue;
          }

          /* Permanent 4xx errors are reported by the dispatch queue, never retried. */
          response.EnsureSuccessStatusCode();
          if (GetHeader(response, "X-RateLimit-Remaining") == "0")
          {
            UpdateRetryAt(queue, ResetDelay(response), false);
          }

          return;
        }
      }
    }

    private void UpdateRetryAt(WebhookState queue, TimeSpan delay, bool global)
    {
      lock (_sync)
      {
        var now = _utcNow();
        var retryAt = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
        if (retryAt > queue.RetryAt)
        {
          queue.RetryAt = retryAt;
        }

        if (global && retryAt > _globalRetryAt)
        {
          _globalRetryAt = retryAt;
        }
      }
    }

    private async Task WaitForLimitAsync(WebhookState queue, CancellationToken cancellationToken)
    {
      while (true)
      {
        DateTimeOffset retryAt;
        lock (_sync)
        {
          retryAt = queue.RetryAt > _globalRetryAt ? queue.RetryAt : _globalRetryAt;
        }

        var delay = retryAt - _utcNow();
        if (delay <= TimeSpan.Zero)
        {
          return;
        }

        /* Recheck global limits after sleeping; another webhook can extend them. */
        await _delayAsync(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, cancellationToken)
          .ConfigureAwait(false);
      }
    }

    private async Task<(TimeSpan Delay, bool Global)> ReadRateLimitAsync(HttpResponseMessage response,
      CancellationToken cancellationToken)
    {
      var delay = ResetDelay(response);
      var retryHeader = GetHeader(response, "Retry-After");
      var headerDelay = Seconds(retryHeader);
      if (headerDelay == TimeSpan.Zero && DateTimeOffset.TryParse(retryHeader, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var retryDate))
      {
        headerDelay = retryDate - _utcNow();
      }

      if (headerDelay > delay)
      {
        delay = headerDelay;
      }

      var global = string.Equals(GetHeader(response, "X-RateLimit-Global"), "true", StringComparison.OrdinalIgnoreCase);
      var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind == JsonValueKind.Object)
        {
          if (json.RootElement.TryGetProperty("retry_after", out var retry))
          {
            var bodyDelay = Seconds(retry.ToString());
            if (bodyDelay > delay)
            {
              delay = bodyDelay;
            }
          }

          global |= json.RootElement.TryGetProperty("global", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
      }
      catch (JsonException)
      {
        /* Some proxies return HTML with their 429. Retain the header deadline or a safe fallback. */
      }

      return (delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), global);
    }

    private TimeSpan ResetDelay(HttpResponseMessage response)
    {
      var delay = Seconds(GetHeader(response, "X-RateLimit-Reset-After"));
      if (delay == TimeSpan.Zero && double.TryParse(GetHeader(response, "X-RateLimit-Reset"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var reset))
      {
        delay = Seconds((reset - (_utcNow() - DateTimeOffset.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture));
      }

      return delay;
    }

    private static TimeSpan Seconds(string value)
    {
      return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds / 2
        ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    private static string GetHeader(HttpResponseMessage response, string name)
    {
      return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    private sealed class WebhookState
    {
      internal readonly SemaphoreSlim Gate = new(1, 1);
      internal DateTimeOffset RetryAt;
    }
  }
}
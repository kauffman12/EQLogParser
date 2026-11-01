using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser;

// Reference identity prevents cached wrappers from sending after a trigger is disabled and re-enabled.
internal sealed class WebhookSource(string owner, string characterId, string triggerId, string name)
{
  internal string Owner { get; } = owner;
  internal string CharacterId { get; } = characterId;
  internal string TriggerId { get; } = triggerId;
  internal string Name { get; set; } = name;
}

internal sealed record WebhookError(WebhookSource Source, string Url, string Message);
internal sealed record WebhookQueueStatus(int Pending, int Aggregating, int Retrying, WebhookError[] Errors);

internal sealed class WebhookDispatchQueue
{
  private readonly object _sync = new();
  private readonly DiscordWebhookSender _sender;
  private readonly Func<DateTimeOffset> _utcNow;
  private readonly Func<TimeSpan, CancellationToken, Task> _delay;
  private readonly Dictionary<(string Owner, string Trigger), WebhookSource> _sources = new();
  private readonly HashSet<(string Character, string Trigger)> _disabled = new();
  private readonly Dictionary<(WebhookSource Source, string Url, int Seconds), Batch> _collecting = new();
  private readonly HashSet<Batch> _pending = new();
  private readonly Dictionary<string, ErrorState> _errors = new(StringComparer.Ordinal);

  internal event Action EventsStatusChanged;
  internal event Action<WebhookError> EventsSendFailed;

  internal WebhookDispatchQueue(DiscordWebhookSender sender, Func<DateTimeOffset> utcNow = null,
    Func<TimeSpan, CancellationToken, Task> delay = null)
  {
    _sender = sender;
    _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
  }

  internal WebhookQueueStatus Status
  {
    get
    {
      lock (_sync)
      {
        return new(_pending.Count, _collecting.Count, _pending.Count(batch => batch.Retrying),
          [.. _errors.Values.Select(state => state.Error)]);
      }
    }
  }

  internal string ErrorFor(string url, string characterId = null, string triggerId = null)
  {
    lock (_sync)
    {
      if (url != null && _errors.TryGetValue(DiscordWebhookSender.EndpointKey(url), out var state)) return state.Error.Message;
      return triggerId == null ? null : _errors.Values.SelectMany(item => item.Affected.Values).FirstOrDefault(item =>
        item.Source.CharacterId == characterId && item.Source.TriggerId == triggerId)?.Message;
    }
  }

  internal Dictionary<string, WebhookSource> UpdateSources(string owner, string characterId,
    IEnumerable<(string Id, string Name)> triggers)
  {
    var result = new Dictionary<string, WebhookSource>();
    bool changed;
    lock (_sync)
    {
      var current = triggers.Where(trigger => !_disabled.Contains((characterId, trigger.Id))).ToArray();
      var ids = current.Select(trigger => trigger.Id).ToHashSet();
      var removed = _sources.Values.Where(source => source.Owner == owner && !ids.Contains(source.TriggerId)).ToHashSet();
      changed = RemoveSources(removed);
      foreach (var (id, name) in current)
      {
        var key = (owner, id);
        if (!_sources.TryGetValue(key, out var source))
        {
          source = new(owner, characterId, id, name);
          _sources.Add(key, source);
        }

        source.Name = name;
        result.Add(id, source);
      }
    }

    if (changed) PublishStatus();
    return result;
  }

  internal void SetEnabled(string characterId, string triggerId, bool enabled)
  {
    var changed = false;
    lock (_sync)
    {
      if (enabled) _disabled.Remove((characterId, triggerId));
      else _disabled.Add((characterId, triggerId));

      if (!enabled)
      {
        changed = RemoveSources(_sources.Values
          .Where(source => source.CharacterId == characterId && source.TriggerId == triggerId).ToHashSet());
      }
    }

    if (changed) PublishStatus();
  }

  internal void ClearOwner(string owner, bool removeSources = false)
  {
    bool changed;
    lock (_sync)
    {
      var sources = _sources.Values.Where(source => source.Owner == owner).ToHashSet();
      changed = removeSources ? RemoveSources(sources) : RemoveBatches(sources);
    }

    if (changed) PublishStatus();
  }

  internal Task EnqueueAsync(WebhookSource source, string url, string content, int seconds)
  {
    if (source == null || string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(content)) return Task.CompletedTask;
    seconds = seconds == 1 || (seconds is >= 5 and <= 30 && seconds % 5 == 0) ? seconds : 0;
    url = url.Trim();
    Batch batch;
    lock (_sync)
    {
      if (!_sources.TryGetValue((source.Owner, source.TriggerId), out var active) || !ReferenceEquals(source, active))
        return Task.CompletedTask;

      var key = (source, url, seconds);
      if (seconds > 0 && _collecting.TryGetValue(key, out batch) && _utcNow() < batch.Deadline)
      {
        batch.Lines.Add(content);
        return batch.Completion.Task;
      }

      batch = new(source, url, seconds, _utcNow().AddSeconds(seconds));
      batch.Lines.Add(content);
      _pending.Add(batch);
      if (seconds > 0) _collecting[key] = batch;
    }

    PublishStatus();
    _ = ProcessAsync(batch);
    return batch.Completion.Task;
  }

  private async Task ProcessAsync(Batch batch)
  {
    var token = batch.Cancellation.Token;
    try
    {
      while (batch.Deadline - _utcNow() is var wait && wait > TimeSpan.Zero)
      {
        await _delay(wait, token).ConfigureAwait(false);
      }

      string content;
      lock (_sync)
      {
        token.ThrowIfCancellationRequested();
        StopCollecting(batch);
        content = string.Join("\n", batch.Lines);
      }

      PublishStatus();
      foreach (var part in WebhookMessage.Create(content, batch.Seconds > 0))
      {
        await _sender.SendAsync(part, batch.Url, token, () => MarkRetrying(batch)).ConfigureAwait(false);
        lock (_sync)
        {
          batch.Retrying = false;
          var endpoint = DiscordWebhookSender.EndpointKey(batch.Url);
          _errors.Remove(endpoint);
          RemoveErrors(source => source.TriggerId == batch.Source.TriggerId
            && source.CharacterId == batch.Source.CharacterId);
        }

        PublishStatus();
      }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      // Disabling a trigger cancels its pending work without reporting a failure.
    }
    catch (Exception ex)
    {
      ReportFailure(batch, ex);
    }
    finally
    {
      lock (_sync)
      {
        StopCollecting(batch);
        _pending.Remove(batch);
        // Serialize disposal with cancellation in RemoveBatches.
        batch.Dispose();
      }

      batch.Completion.TrySetResult();
      PublishStatus();
    }
  }

  private void ReportFailure(Batch batch, Exception exception)
  {
    WebhookError error;
    bool report;
    lock (_sync)
    {
      if (batch.Cancellation.IsCancellationRequested) return;
      var status = exception is HttpRequestException http ? http.StatusCode : null;
      var message = status is { } code
        ? $"Discord rejected this webhook (HTTP {(int)code}). Check the URL and permissions."
        : $"Webhook failed ({exception.GetType().Name}). Check the URL and Error Log.";
      error = new(batch.Source, batch.Url, message);
      var endpoint = DiscordWebhookSender.EndpointKey(batch.Url);
      if (!_errors.TryGetValue(endpoint, out var state))
      {
        state = new();
        _errors.Add(endpoint, state);
      }

      state.Error = error;
      state.Affected[batch.Source] = error;
      // Keep each distinct cause silent until this endpoint succeeds or its last affected source is retired.
      report = state.Reported.Add((exception.GetType(), status));
    }

    if (report)
    {
      foreach (Action<WebhookError> subscriber in EventsSendFailed?.GetInvocationList() ?? [])
      {
        ThreadBoundary.Guard("webhook failure subscriber", () => subscriber(error));
      }
    }
  }

  private void MarkRetrying(Batch batch)
  {
    var changed = false;
    lock (_sync)
    {
      if (_pending.Contains(batch) && !batch.Retrying)
      {
        batch.Retrying = true;
        changed = true;
      }
    }

    if (changed) PublishStatus();
  }

  private bool RemoveSources(HashSet<WebhookSource> sources)
  {
    foreach (var source in sources) _sources.Remove((source.Owner, source.TriggerId));
    var changed = RemoveBatches(sources);
    changed |= RemoveErrors(sources.Contains);
    return changed;
  }

  private bool RemoveErrors(Func<WebhookSource, bool> matches)
  {
    var changed = false;
    foreach (var (endpoint, state) in _errors.ToArray())
    {
      foreach (var source in state.Affected.Keys.Where(matches).ToArray())
      {
        state.Affected.Remove(source);
        changed = true;
      }

      if (state.Affected.Count == 0) _errors.Remove(endpoint);
      else if (!state.Affected.ContainsKey(state.Error.Source)) state.Error = state.Affected.Values.First();
    }

    return changed;
  }

  private bool RemoveBatches(HashSet<WebhookSource> sources)
  {
    var batches = _pending.Where(batch => sources.Contains(batch.Source)).ToArray();
    foreach (var batch in batches)
    {
      _pending.Remove(batch);
      StopCollecting(batch);
      batch.Cancellation.Cancel();
    }

    return batches.Length > 0;
  }

  private void StopCollecting(Batch batch)
  {
    var key = (batch.Source, batch.Url, batch.Seconds);
    if (_collecting.TryGetValue(key, out var current) && ReferenceEquals(current, batch)) _collecting.Remove(key);
  }

  private void PublishStatus()
  {
    foreach (Action subscriber in EventsStatusChanged?.GetInvocationList() ?? [])
    {
      ThreadBoundary.Guard("webhook status subscriber", subscriber);
    }
  }

  private sealed class ErrorState
  {
    internal WebhookError Error;
    internal readonly Dictionary<WebhookSource, WebhookError> Affected = [];
    internal readonly HashSet<(Type Kind, System.Net.HttpStatusCode? Status)> Reported = [];
  }

  private sealed class Batch(WebhookSource source, string url, int seconds, DateTimeOffset deadline)
    : IDisposable
  {
    internal readonly WebhookSource Source = source;
    internal readonly string Url = url;
    internal readonly int Seconds = seconds;
    internal readonly DateTimeOffset Deadline = deadline;
    internal readonly List<string> Lines = [];
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Retrying;

    public void Dispose() => Cancellation.Dispose();
  }
}
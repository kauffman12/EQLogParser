using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal partial class TriggerProcessor
  {
    // Pending resets outlive _timerLists membership. Never retain a countdown just to keep its reset cancellable.
    // docs/DesignNotes.md → "A Timer's reset point: what the bar paints and what speaks at it".
    private void CancelTimerResets(string triggerId = null, string displayName = null)
    {
      lock (_pendingTimerResets)
      {
        for (var i = _pendingTimerResets.Count - 1; i >= 0; i--)
        {
          var data = _pendingTimerResets[i];
          if ((triggerId == null || data.TriggerId == triggerId)
            && (displayName == null || string.Equals(data.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)))
          {
            TimerDataUtil.CancelTimerReset(data);
            _pendingTimerResets.RemoveAt(i);
          }
        }
      }
    }

    private void ScheduleTimerReset(TriggerWrapper wrapper, TimerData data, LineData lineData,
      Dictionary<string, string> matches)
    {
      var trigger = wrapper.TriggerData;
      if (!(trigger.ResetDurationSeconds > 0)
        || (string.IsNullOrEmpty(trigger.ResetSoundToPlay)
          && string.IsNullOrEmpty(wrapper.ModifiedResetSpeak)
          && string.IsNullOrEmpty(wrapper.ModifiedResetDisplay))) return;

      CancellationToken token;
      lock (_pendingTimerResets)
      {
        if (_isDisposed) return;
        data.ResetSource = new CancellationTokenSource();
        token = data.ResetSource.Token;
        _pendingTimerResets.Add(data);
      }

      _ = Task.Run(() => RunTimerResetAsync(wrapper, data, lineData, matches, token));
    }

    private async Task RunTimerResetAsync(TriggerWrapper wrapper, TimerData data, LineData lineData,
      Dictionary<string, string> matches, CancellationToken token)
    {
      try
      {
        // Ask after the worker starts, so time already spent starting the timer or queuing this task is not charged twice.
        await Task.Delay(TimerLifecycle.RemainingDelayMs(data.ResetTicks, DateTime.UtcNow.Ticks), token);
        await _activeTriggerSemaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
          lock (_pendingTimerResets)
          {
            if (_isDisposed || data.ResetNotified) return;
            data.ResetNotified = true;
          }

          // Serialize delivery with restart/Stop/disable, not just the delayed wake-up.
          var trigger = wrapper.TriggerData;
          var tts = TriggerUtil.GetFromDecodedSoundOrText(trigger.ResetSoundToPlay, wrapper.ModifiedResetSpeak, out var isSound);
          if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase)
            && !_isDisposed && !_speakCollection.IsCompleted)
          {
            try
            {
              _speakCollection.Add(new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = tts,
                IsSound = isSound,
                Matches = matches,
                Previous = data.PreviousMatches,
                Original = data.OriginalMatches,
                Action = lineData.Action
              });
            }
            catch (InvalidOperationException)
            {
              // The processor closed its collection during disposal.
            }
          }

          ExpireVariablesIfNeeded();
          if (!_isDisposed && ProcessDisplayText(wrapper.ModifiedResetDisplay, lineData.Action, matches,
            data.OriginalMatches, data.PreviousMatches, _variables) is { } text)
          {
            await AddTextAsync(trigger, text);
          }

          if (!_isDisposed && !_triggerLogCollection.IsCompleted)
          {
            try
            {
              _triggerLogCollection.Add(new(lineData, wrapper, "Timer Reset", 0));
            }
            catch (InvalidOperationException)
            {
              // The processor closed its collection during disposal.
            }
          }
        }
        finally
        {
          _activeTriggerSemaphore.Release();
        }
      }
      catch (OperationCanceledException) when (token.IsCancellationRequested)
      {
        // Restart, Stop, disable or disposal took back this run's announcement.
      }
      catch (Exception ex)
      {
        ThreadBoundary.Note("trigger timer reset", ex);
      }
      finally
      {
        lock (_pendingTimerResets)
        {
          TimerDataUtil.CancelTimerReset(data);
          _pendingTimerResets.Remove(data);
        }
      }
    }
  }
}

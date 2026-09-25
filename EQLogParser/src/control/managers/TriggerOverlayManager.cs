using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  internal class TriggerOverlayManager
  {
    internal enum TimerStateChange
    {
      Start, Stop
    }

    internal static TriggerOverlayManager Instance => Lazy.Value;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerOverlayManager> Lazy = new(() => new TriggerOverlayManager());
    private const string TEXT_OVERLAY = "text-overlay";
    private const string TIMER_OVERLAY = "timer-overlay";
    private readonly Dictionary<string, OverlayWindowData> _textWindows = [];
    private readonly Dictionary<string, OverlayWindowData> _timerWindows = [];
    private readonly Dictionary<string, TriggerNode> _defaultOverlays = [];
    private readonly ConcurrentDictionary<string, RegexData> _closeRegex = [];

    /*
     * Cooldown stamps for the "text/timer had nowhere to go" warnings. AddText and timer Start are called per matched line, and an overlay
     * that stays missing must be reported once per trigger every few minutes — not once per raid event.
     */
    private static readonly ConcurrentDictionary<string, long> _noWindowWarnStamps = [];
    private static readonly long NoWindowWarnCooldownTicks = MonoTime.SecondsToTicks(300);

    private TriggerOverlayManager()
    {
      TriggerStateDB.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
      TriggerStateDB.Instance.TriggerUpdateEvent += TriggerOverlayUpdateEvent;
    }

    internal void CheckLine(string action)
    {
      if (!_closeRegex.IsEmpty && !string.IsNullOrEmpty(action))
      {
        List<string> closeIds = null;
        foreach (var kv in _closeRegex)
        {
          var doClose = false;
          if (kv.Value.UseRegex)
          {
            try
            {
              doClose = kv.Value.Regex?.IsMatch(action) is true;
            }
            catch (Exception)
            {
              // ignore timeout
            }
          }
          else if (!string.IsNullOrEmpty(kv.Value.ClosePattern))
          {
            doClose = action.Contains(kv.Value.ClosePattern, StringComparison.OrdinalIgnoreCase);
          }

          if (doClose && !string.IsNullOrEmpty(kv.Value.Id))
          {
            closeIds ??= new List<string>(2);
            closeIds.Add(kv.Value.Id);
          }
        }

        if (closeIds?.Count > 0)
        {
          _ = UiUtil.InvokeAsync(() =>
          {
            foreach (var id in closeIds)
            {
              if (_textWindows.TryGetValue(id, out var windowData) && windowData.TheWindow is TextOverlayWindow { } window)
              {
                // stop and clear but don't remove
                window.StopOverlay();
              }
            }
          });
        }
      }
    }

    internal void HideOverlays()
    {
      _ = UiUtil.InvokeAsync(async () =>
      {
        foreach (var kv in _textWindows)
        {
          if (kv.Value is OverlayWindowData windowData && windowData.TheWindow is TextOverlayWindow { } textWindow)
          {
            textWindow.HideOverlay();
          }
        }

        foreach (var kv in _timerWindows)
        {
          if (kv.Value is OverlayWindowData windowData && windowData.TheWindow is TimerOverlayWindow { } timerWindow)
          {
            await timerWindow.HideOverlayAsync();
          }
        }
      });
    }

    internal void StopOverlays()
    {
      _ = UiUtil.InvokeAsync(async () =>
      {
        foreach (var kv in _textWindows)
        {
          if (kv.Value is OverlayWindowData windowData && windowData.TheWindow is TextOverlayWindow { } textWindow)
          {
            textWindow.StopOverlay();
          }
        }

        foreach (var kv in _timerWindows)
        {
          if (kv.Value is OverlayWindowData windowData && windowData.TheWindow is TimerOverlayWindow { } timerWindow)
          {
            await timerWindow.StopOverlayAsync();
          }
        }
      });
    }

    internal async Task RemoveAllAsync()
    {
      await UiUtil.InvokeAsync(async () =>
      {
        foreach (var key in _textWindows.Keys.ToArray())
        {
          await RemoveWindowAsync(key);
        }

        foreach (var key in _timerWindows.Keys.ToArray())
        {
          await RemoveWindowAsync(key);
        }
      });
    }

    internal async Task RestartOverlayAsync(string overlayId)
    {
      if (!string.IsNullOrEmpty(overlayId))
      {
        var overlay = await TriggerStateDB.Instance.GetOverlayById(overlayId);
        if (overlay != null)
        {
          await UiUtil.InvokeAsync(async () =>
          {
            await RemoveWindowAsync(overlayId);

            if (overlay.OverlayData.IsTextOverlay && !_textWindows.ContainsKey(overlayId))
            {
              AddWindow(_textWindows, overlay);
            }
            else if (overlay.OverlayData.IsTimerOverlay && !_timerWindows.ContainsKey(overlayId))
            {
              AddWindow(_timerWindows, overlay);
            }
          });
        }
      }
    }

    // Started from Task.Run
    internal async Task UpdateOverlayInfoAsync(HashSet<string> overlayIds, HashSet<string> enabledTriggers)
    {
      await UpdateDefaultOverlaysAsync();

      var ids = overlayIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
      var overlays = await Task.WhenAll(ids.Select(TriggerStateDB.Instance.GetOverlayById));

      Dictionary<string, TriggerNode> needRegexUpdate = null;
      TriggerNode defaultTextOverlay = null;
      TriggerNode defaultTimerOverlay = null;

      await UiUtil.InvokeAsync(async () =>
      {
        for (var i = 0; i < ids.Count; i++)
        {
          var overlayId = ids[i];
          var overlay = overlays[i];

          // if not found make sure there's no window
          if (overlay == null)
          {
            /*
             * Overlays are written as whole documents inside the serialized queue and planned deletes reach the manager first through the
             * delete event, so a miss here means the id really is gone from the database rather than caught mid-write. It still gets a line
             * at the default log level: this is the one path that closes a window the player can see, and "my overlay vanished" should say
             * so in the log without Debug switched on.
             */
            var hadWindow = _textWindows.ContainsKey(overlayId) || _timerWindows.ContainsKey(overlayId);
            await RemoveWindowAsync(overlayId);
            _closeRegex.TryRemove(overlayId, out _);
            if (hadWindow)
            {
              Log.Warn($"Overlay '{overlayId}' no longer exists in the trigger database; closed its window.");
            }
            continue;
          }

          // update text overlay regex
          if (overlay.OverlayData.IsTextOverlay)
          {
            needRegexUpdate ??= [];
            needRegexUpdate[overlayId] = overlay;
          }

          if (overlay.OverlayData.IsTextOverlay && !_textWindows.ContainsKey(overlayId))
          {
            AddWindow(_textWindows, overlay);
          }
          else if (overlay.OverlayData.IsTimerOverlay && !_timerWindows.ContainsKey(overlayId))
          {
            AddWindow(_timerWindows, overlay);
          }
        }

        defaultTextOverlay = _defaultOverlays.GetValueOrDefault(TEXT_OVERLAY);
        defaultTimerOverlay = _defaultOverlays.GetValueOrDefault(TIMER_OVERLAY);

        if (!string.IsNullOrEmpty(defaultTextOverlay?.Id))
        {
          needRegexUpdate ??= [];
          needRegexUpdate[defaultTextOverlay.Id] = defaultTextOverlay;
        }

        // validate timers
        foreach (var kv in _timerWindows)
        {
          if (kv.Value.TheWindow is TimerOverlayWindow { } timerWindow)
          {
            timerWindow.ValidateTimers(enabledTriggers);
          }
        }

        foreach (var id in ids)
        {
          if (!_textWindows.ContainsKey(id) && !_timerWindows.ContainsKey(id) &&
            !string.Equals(id, defaultTextOverlay?.Id, StringComparison.Ordinal) &&
            !string.Equals(id, defaultTimerOverlay?.Id, StringComparison.Ordinal))
          {
            // remove any windows not in use
            await RemoveWindowAsync(id);
          }
        }
      });

      if (needRegexUpdate != null)
      {
        foreach (var kv in needRegexUpdate)
        {
          _closeRegex[kv.Key] = CreateCloseRegex(kv.Key, kv.Value.OverlayData);
        }
      }
    }

    // Not called on UI thread
    internal async Task AddTextAsync(Trigger trigger, string text, string customFontColor)
    {
      var now = MonoTime.NowStamp();

      var windowsToAdd = new List<TextOverlayWindow>(1);
      await UiUtil.InvokeAsync(() =>
      {
        var added = false;
        foreach (var overlayId in trigger.SelectedOverlays)
        {
          if (_textWindows.TryGetValue(overlayId, out var windowData) && windowData.TheWindow is TextOverlayWindow { } window)
          {
            windowsToAdd.Add(window);
            added = true;
          }
        }

        if (!added && _defaultOverlays.TryGetValue(TEXT_OVERLAY, out var overlay) && !string.IsNullOrEmpty(overlay?.Id) &&
          _textWindows.TryGetValue(overlay.Id, out var defaultWindowData) && defaultWindowData.TheWindow is TextOverlayWindow { } defaultWindow)
        {
          windowsToAdd.Add(defaultWindow);
        }
      });

      // Throttled: this is a per-line path, and silence here is what made "sound plays but the overlay never shows" untraceable.
      if (windowsToAdd.Count == 0 &&
        ShouldWarnNow(_noWindowWarnStamps, WarnKey(trigger, "text"), MonoTime.NowStamp(), NoWindowWarnCooldownTicks))
      {
        Log.Warn($"Text from trigger '{PatternOf(trigger)}' was dropped: no text overlay window is open for"
          + $" [{string.Join(", ", trigger?.SelectedOverlays ?? [])}] and the default text overlay window is unavailable.");
      }

      foreach (var window in windowsToAdd)
      {
        try
        {
          window.AddText(text, now, customFontColor);
        }
        catch (Exception ex)
        {
          Log.Debug("Error Adding Text", ex);
        }
      }
    }

    internal async Task UpdateTimerAsync(Trigger trigger, TimerData timerData, TimerStateChange state)
    {
      var windowsToStart = new List<TimerOverlayWindow>(2);
      var windowsToStop = new List<TimerOverlayWindow>(2);

      await UiUtil.InvokeAsync(() =>
      {
        var started = false;
        var stopped = false;

        /*
         * A row comes off the overlays it went onto, not off whatever is ticked right now. Which windows a countdown was painted on is a fact
         * about the past, and ticking a different overlay (or deleting and rebuilding one) between the countdown starting and its removal timer
         * firing used to send the Stop to windows that never held the row while the one that did kept it - the window's own reaper collects that
         * about two seconds later, so the symptom was a bar outstaying its spell rather than staying forever. Start therefore records the ids it
         * actually dispatched to on the TimerData, and Stop follows that record. An empty record means the row came from somewhere that never
         * showed it, which keeps the older behaviour of trying the current selection.
         */
        var overlayIds = state == TimerStateChange.Stop && timerData.TimerOverlayIds is { Count: > 0 } shownOn
          ? (IEnumerable<string>)shownOn
          : trigger.SelectedOverlays;
        var dispatchedTo = new List<string>(2);

        foreach (var overlayId in overlayIds)
        {
          if (_timerWindows.TryGetValue(overlayId, out var windowData) && windowData.TheWindow is TimerOverlayWindow { } window)
          {
            if (state == TimerStateChange.Start)
            {
              windowsToStart.Add(window);
              dispatchedTo.Add(overlayId);
              started = true;
            }
            else if (state == TimerStateChange.Stop)
            {
              windowsToStop.Add(window);
              stopped = true;
            }
          }
        }

        if (state == TimerStateChange.Start && !started &&
          _defaultOverlays.TryGetValue(TIMER_OVERLAY, out var overlay) && !string.IsNullOrEmpty(overlay?.Id) &&
          _timerWindows.TryGetValue(overlay.Id, out var defaultWindowData) && defaultWindowData.TheWindow is TimerOverlayWindow { } defaultWindow)
        {
          windowsToStart.Add(defaultWindow);
          dispatchedTo.Add(overlay.Id);
        }
        else if (state == TimerStateChange.Stop && !stopped &&
          _defaultOverlays.TryGetValue(TIMER_OVERLAY, out var overlay2) && !string.IsNullOrEmpty(overlay2?.Id) &&
          _timerWindows.TryGetValue(overlay2.Id, out var defaultWindowData2) && defaultWindowData2.TheWindow is TimerOverlayWindow { } defaultWindow2)
        {
          windowsToStop.Add(defaultWindow2);
        }

        // Recorded even when empty: "this countdown was shown on nothing" is the truth a later Stop needs, and it keeps that Stop from
        // picking a fight with whatever happens to be ticked now.
        if (state == TimerStateChange.Start)
        {
          timerData.TimerOverlayIds = new ReadOnlyCollection<string>(dispatchedTo);
        }
      });

      // Start only: a Stop with no window is normal after the window was recreated mid-row, while a Start that lands nowhere is the silent
      // loss the text overlay warned about above. Throttled for the same reason.
      if (state == TimerStateChange.Start && windowsToStart.Count == 0 &&
        ShouldWarnNow(_noWindowWarnStamps, WarnKey(trigger, "timer"), MonoTime.NowStamp(), NoWindowWarnCooldownTicks))
      {
        Log.Warn($"Timer from trigger '{PatternOf(trigger)}' was dropped: no timer overlay window is open for"
          + $" [{string.Join(", ", trigger?.SelectedOverlays ?? [])}] and the default timer overlay window is unavailable.");
      }

      foreach (var startWindow in windowsToStart)
      {
        try
        {
          _ = startWindow.StartTimerAsync(timerData);
        }
        catch (Exception ex)
        {
          Log.Debug("Error Starting Timer", ex);
        }
      }

      foreach (var stopWindow in windowsToStop)
      {
        try
        {
          _ = stopWindow.StopTimerAsync(timerData);
        }
        catch (Exception ex)
        {
          Log.Debug("Error Stopping Timer", ex);
        }
      }
    }

    /*
     * Not a lock and not meant to be exact at the boundary — two threads deciding "due" at once cost one duplicate line. What it must never
     * do is let a per-line call turn a missing overlay into a per-line warning, so callers hand it a key and a fixed cooldown window.
     */
    internal static bool ShouldWarnNow(ConcurrentDictionary<string, long> stamps, string key, long nowStamp, long cooldownTicks)
    {
      if (stamps.Count > 4096)
      {
        // Bounded: keys come from trigger patterns, which imported packs can make numerous. Cooldown restarts for everything; a slow leak
        // of stale keys is the lesser evil against growing without limit.
        stamps.Clear();
      }

      var due = false;
      stamps.AddOrUpdate(key,
        _ => { due = true; return nowStamp; },
        (_, previous) =>
        {
          if (nowStamp - previous >= cooldownTicks)
          {
            due = true;
            return nowStamp;
          }

          return previous;
        });

      return due;
    }

    private async void TriggerOverlayDeleteEvent(string id)
    {
      if (!string.IsNullOrEmpty(id))
      {
        await UiUtil.InvokeAsync(() => RemoveWindowAsync(id));
        await UpdateDefaultOverlaysAsync();
      }
    }

    private async void TriggerOverlayUpdateEvent(TriggerNode node)
    {
      // if overlay was modified
      if (node.OverlayData != null)
      {
        await UpdateDefaultOverlaysAsync();
      }
    }

    private async Task UpdateDefaultOverlaysAsync()
    {
      var defaultTextOverlay = await TriggerStateDB.Instance.GetDefaultTextOverlay();
      var defaultTimerOverlay = await TriggerStateDB.Instance.GetDefaultTimerOverlay();

      await UiUtil.InvokeAsync(() =>
      {
        _defaultOverlays[TEXT_OVERLAY] = defaultTextOverlay;
        _defaultOverlays[TIMER_OVERLAY] = defaultTimerOverlay;
        AddWindow(_textWindows, defaultTextOverlay);
        AddWindow(_timerWindows, defaultTimerOverlay);
      });
    }

    // only run this from UI thread
    private void AddWindow(Dictionary<string, OverlayWindowData> theWindows, TriggerNode overlay)
    {
      if (overlay != null && !theWindows.ContainsKey(overlay.Id))
      {
        var windowData = new OverlayWindowData
        {
          TheWindow = (theWindows == _textWindows) ? new TextOverlayWindow(overlay) : new TimerOverlayWindow(overlay)
        };

        // workaround for running under wine so input isn't captured
        // during startup/creation
        windowData.TheWindow.Visibility = Visibility.Visible;
        windowData.TheWindow.Opacity = 0;
        windowData.TheWindow.Show();
        windowData.TheWindow.UpdateLayout();
        windowData.TheWindow.Visibility = Visibility.Collapsed;
        windowData.TheWindow.Opacity = 1.0;
        theWindows[overlay.Id] = windowData;
      }
    }

    private static string WarnKey(Trigger trigger, string kind) => $"{kind}:{PatternOf(trigger)}";

    private static string PatternOf(Trigger trigger)
    {
      var pattern = trigger?.Pattern ?? string.Empty;
      return pattern.Length > 80 ? pattern[..80] + "…" : pattern;
    }

    // only run this from UI thread
    private async Task RemoveWindowAsync(string id)
    {
      if (_textWindows.Remove(id, out var textWindow))
      {
        Log.Debug($"Closing text overlay window '{id}'.");
        if (textWindow.TheWindow is TextOverlayWindow { } window)
        {
          window.StopOverlay();
        }

        textWindow.TheWindow.Close();
        textWindow.TheWindow = null;
      }

      if (_timerWindows.Remove(id, out var timerWindow))
      {
        Log.Debug($"Closing timer overlay window '{id}'.");
        if (timerWindow.TheWindow is TimerOverlayWindow { } window)
        {
          await window.StopOverlayAsync();
        }

        timerWindow.TheWindow.Close();
        timerWindow.TheWindow = null;
      }
    }

    private static RegexData CreateCloseRegex(string id, Overlay overlay)
    {
      if (!string.IsNullOrEmpty(id) && overlay != null)
      {
        var regex = overlay.UseCloseRegex ? new Regex(overlay.ClosePattern, RegexOptions.IgnoreCase |
          RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)) : null;
        regex?.Match(""); // warm up the regex

        return new RegexData
        {
          Id = id,
          ClosePattern = overlay.ClosePattern,
          UseRegex = overlay.UseCloseRegex,
          Regex = regex
        };
      }

      return null;
    }

    private class RegexData
    {
      public string ClosePattern { get; set; }
      public Regex Regex { get; set; }
      public bool UseRegex { get; set; }
      public string Id { get; set; }
    }
  }
}

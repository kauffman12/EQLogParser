using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

    private TriggerOverlayManager()
    {
      TriggerStateManager.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerOverlayUpdateEvent;
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
              doClose = kv.Value.Regex?.IsMatch(action) == true;
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
        foreach (var kv in _textWindows)
        {
          await RemoveWindowAsync(kv.Key);
        }

        foreach (var kv in _timerWindows)
        {
          await RemoveWindowAsync(kv.Key);
        }
      });
    }

    internal async Task RestartOverlayAsync(string overlayId)
    {
      if (!string.IsNullOrEmpty(overlayId))
      {
        var overlay = await TriggerStateManager.Instance.GetOverlayById(overlayId);
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
      var overlays = await Task.WhenAll(ids.Select(TriggerStateManager.Instance.GetOverlayById));

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
            await RemoveWindowAsync(overlayId);
            _closeRegex.TryRemove(overlayId, out _);
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
    internal async Task AddTextAsync(Trigger trigger, string text, string fontColor)
    {
      fontColor ??= trigger.FontColor;
      var beginTicks = DateTime.UtcNow.Ticks;

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

      foreach (var window in windowsToAdd)
      {
        try
        {
          window.AddText(text, beginTicks, fontColor);
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
        foreach (var overlayId in trigger.SelectedOverlays)
        {
          if (_timerWindows.TryGetValue(overlayId, out var windowData) && windowData.TheWindow is TimerOverlayWindow { } window)
          {
            if (state == TimerStateChange.Start)
            {
              windowsToStart.Add(window);
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
        }
        else if (state == TimerStateChange.Stop && !stopped &&
          _defaultOverlays.TryGetValue(TIMER_OVERLAY, out var overlay2) && !string.IsNullOrEmpty(overlay2?.Id) &&
          _timerWindows.TryGetValue(overlay2.Id, out var defaultWindowData2) && defaultWindowData2.TheWindow is TimerOverlayWindow { } defaultWindow2)
        {
          windowsToStop.Add(defaultWindow2);
        }
      });

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
      var defaultTextOverlay = await TriggerStateManager.Instance.GetDefaultTextOverlay();
      var defaultTimerOverlay = await TriggerStateManager.Instance.GetDefaultTimerOverlay();

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

        windowData.TheWindow.Visibility = System.Windows.Visibility.Collapsed;
        windowData.TheWindow.Show();
        theWindows[overlay.Id] = windowData;
      }
    }

    // only run this from UI thread
    private async Task RemoveWindowAsync(string id)
    {
      if (_textWindows.Remove(id, out var textWindow))
      {
        if (textWindow.TheWindow is TextOverlayWindow { } window)
        {
          window.StopOverlay();
        }

        textWindow.TheWindow.Close();
        textWindow.TheWindow = null;
      }

      if (_timerWindows.Remove(id, out var timerWindow))
      {
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

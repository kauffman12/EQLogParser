using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    private readonly ConcurrentDictionary<string, OverlayWindowData> _textWindows = [];
    private readonly ConcurrentDictionary<string, OverlayWindowData> _timerWindows = [];
    private readonly ConcurrentDictionary<string, TriggerNode> _defaultOverlays = [];

    private TriggerOverlayManager()
    {
      TriggerStateManager.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerOverlayUpdateEvent;
    }

    internal void HideOverlays()
    {
      _ = UiUtil.InvokeAsync(() =>
      {
        foreach (var value in _textWindows.Values.ToArray())
        {
          if (value is OverlayWindowData windowData && windowData.TheWindow is TextOverlayWindow { } textWindow)
          {
            textWindow.Visibility = System.Windows.Visibility.Collapsed;
          }
        }

        foreach (var value in _timerWindows.Values.ToArray())
        {
          if (value is OverlayWindowData windowData && windowData.TheWindow is TimerOverlayWindow { } timerWindow)
          {
            timerWindow.HideOverlay();
            timerWindow.Visibility = System.Windows.Visibility.Collapsed;
          }
        }
      });
    }

    internal async Task StopAsync()
    {
      await UiUtil.InvokeAsync(() =>
      {
        foreach (var key in _textWindows.Keys.ToArray())
        {
          RemoveWindow(key);
        }

        foreach (var key in _timerWindows.Keys.ToArray())
        {
          RemoveWindow(key);
        }
      });
    }

    internal async Task UpdateOverlayInfoAsync(HashSet<string> overlayIds, HashSet<string> enabledTriggers)
    {
      await UpdateDefaultOverlaysAsync();

      await UiUtil.InvokeAsync(async () =>
      {
        foreach (var overlayId in overlayIds)
        {
          if (!string.IsNullOrEmpty(overlayId))
          {
            var overlay = await TriggerStateManager.Instance.GetOverlayById(overlayId);

            // if not found make sure there's no window
            if (overlay == null)
            {
              RemoveWindow(overlayId);
              continue;
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
        }
      });

      // remove windows that aren't needed anymore
      var allOverlayIds = _textWindows.Keys.ToList();
      allOverlayIds.AddRange([.. _timerWindows.Keys]);

      var defaultTextOverlay = _defaultOverlays.GetValueOrDefault(TEXT_OVERLAY);
      var defaultTimerOverlay = _defaultOverlays.GetValueOrDefault(TIMER_OVERLAY);

      await UiUtil.InvokeAsync(() =>
      {
        foreach (var allId in allOverlayIds)
        {
          if (!overlayIds.Contains(allId) && allId != defaultTextOverlay?.Id && allId != defaultTimerOverlay.Id)
          {
            RemoveWindow(allId);
          }
        }
      });

      // validate timers
      foreach (var windowData in _timerWindows.Values.ToArray())
      {
        if (windowData.TheWindow is TimerOverlayWindow { } timerWindow)
        {
          timerWindow.ValidateTimers(enabledTriggers);
        }
      }
    }

    internal void AddText(Trigger trigger, string text, string fontColor)
    {
      fontColor ??= trigger.FontColor;
      var beginTicks = DateTime.UtcNow.Ticks;

      var added = false;
      foreach (var overlayId in trigger.SelectedOverlays)
      {
        if (AddToWindow(beginTicks, overlayId, text, fontColor))
        {
          added = true;
        }
      }

      // try default overlay if not added
      if (!added && _defaultOverlays.TryGetValue(TEXT_OVERLAY, out var overlay) && overlay?.Id != null)
      {
        AddToWindow(beginTicks, overlay.Id, text, fontColor);
      }

      bool AddToWindow(long beginTicks, string id, string theText, string theFontColor)
      {
        if (_textWindows.TryGetValue(id, out var windowData) && windowData.TheWindow is TextOverlayWindow { } window)
        {
          var brush = UiUtil.GetBrush(theFontColor);

          try
          {
            window.AddTextAsync(theText, beginTicks, brush);
          }
          catch (Exception ex)
          {
            Log.Debug("Error Adding Text", ex);
          }

          return true;
        }

        return false;
      }
    }

    internal void UpdateTimer(Trigger trigger, TimerData timerData, TimerStateChange state)
    {
      var added = false;
      foreach (var overlayId in trigger.SelectedOverlays)
      {
        if (AddToWindow(overlayId, timerData, state))
        {
          added = true;
        }
      }

      // try default overlay if not added
      if (!added && _defaultOverlays.TryGetValue(TIMER_OVERLAY, out var overlay) && overlay?.Id != null)
      {
        AddToWindow(overlay.Id, timerData, state);
      }

      bool AddToWindow(string id, TimerData theTimerData, TimerStateChange newState)
      {
        if (_timerWindows.TryGetValue(id, out var windowData) && windowData.TheWindow is TimerOverlayWindow { } window)
        {
          try
          {
            if (newState == TimerStateChange.Start)
            {
              window.StartTimerAsync(theTimerData);
            }
            else if (newState == TimerStateChange.Stop)
            {
              window.StopTimer(theTimerData);
            }
            return true;
          }
          catch (Exception ex)
          {
            Log.Debug("Error Updating Timer", ex);
          }
        }

        return false;
      }
    }

    private async void TriggerOverlayDeleteEvent(string id)
    {
      if (!string.IsNullOrEmpty(id))
      {
        await UiUtil.InvokeAsync(() => RemoveWindow(id));
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
      var defTextOverlay = await TriggerStateManager.Instance.GetDefaultTextOverlay();
      var defTimerOverlay = await TriggerStateManager.Instance.GetDefaultTimerOverlay();
      _defaultOverlays[TEXT_OVERLAY] = defTextOverlay;
      _defaultOverlays[TIMER_OVERLAY] = defTimerOverlay;

      await UiUtil.InvokeAsync(() =>
      {
        AddWindow(_textWindows, defTextOverlay);
        AddWindow(_timerWindows, defTimerOverlay);
      });
    }

    // only run this from UI thread
    private void AddWindow(ConcurrentDictionary<string, OverlayWindowData> theWindows, TriggerNode overlay)
    {
      if (overlay != null)
      {
        var windowData = new OverlayWindowData
        {
          TheWindow = (theWindows == _textWindows) ? new TextOverlayWindow(overlay) : new TimerOverlayWindow(overlay)
        };

        windowData.TheWindow.Show();
        windowData.TheWindow.Visibility = System.Windows.Visibility.Collapsed;
        theWindows[overlay.Id] = windowData;
      }
    }

    // only run this from UI thread
    private void RemoveWindow(string id)
    {
      if (_textWindows.Remove(id, out var textWindow))
      {
        textWindow.TheWindow.Close();
        textWindow.TheWindow = null;
      }

      if (_timerWindows.Remove(id, out var timerWindow))
      {
        timerWindow.TheWindow.Close();
        timerWindow.TheWindow = null;
      }
    }
  }
}

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
      Start, Stop, Delete
    }

    internal static TriggerOverlayManager Instance => Lazy.Value;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerOverlayManager> Lazy = new(() => new TriggerOverlayManager());
    private readonly ConcurrentDictionary<string, OverlayWindowData> _textWindows = [];
    private readonly ConcurrentDictionary<string, OverlayWindowData> _timerWindows = [];
    private TriggerNode _defaultTextOverlay;
    private TriggerNode _defaultTimerOverlay;

    private TriggerOverlayManager()
    {
      TriggerStateManager.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
    }

    internal void HideOverlays()
    {
      _ = UiUtil.InvokeAsync(() =>
      {
        foreach (var value in _textWindows.Values.ToArray())
        {
          if (value is OverlayWindowData windowData && windowData.TheWindow != null)
          {
            windowData.TheWindow.Visibility = System.Windows.Visibility.Collapsed;
          }
        }

        foreach (var value in _timerWindows.Values.ToArray())
        {
          if (value is OverlayWindowData windowData && windowData.TheWindow != null)
          {
            windowData.TheWindow.Visibility = System.Windows.Visibility.Collapsed;
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

    internal async Task UpdateOverlayWindowsAsync(List<string> overlayIds)
    {
      await UiUtil.InvokeAsync(async () =>
      {
        await UpdateDefaultOverlaysAsync();

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

        // remove windows that aren't needed anymore
        var allOverlayIds = _textWindows.Keys.ToList();
        allOverlayIds.AddRange([.. _timerWindows.Keys]);

        foreach (var allId in allOverlayIds)
        {
          if (!overlayIds.Contains(allId) && allId != _defaultTextOverlay?.Id && allId != _defaultTimerOverlay.Id)
          {
            RemoveWindow(allId);
          }
        }
      });
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
      if (!added && _defaultTextOverlay != null)
      {
        AddToWindow(beginTicks, _defaultTextOverlay.Id, text, fontColor);
      }

      bool AddToWindow(long beginTicks, string id, string theText, string theFontColor)
      {
        if (!string.IsNullOrEmpty(id) && _textWindows.TryGetValue(id, out var windowData) &&
          windowData.TheWindow is TextOverlayWindow { } window)
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
      if (!added && _defaultTimerOverlay != null)
      {
        AddToWindow(_defaultTimerOverlay.Id, timerData, state);
      }

      bool AddToWindow(string id, TimerData theTimerData, TimerStateChange newState)
      {
        if (!string.IsNullOrEmpty(id) && _timerWindows.TryGetValue(id, out var windowData) &&
          windowData.TheWindow is TimerOverlayWindow { } window)
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
            else if (newState == TimerStateChange.Delete)
            {
              window.DeleteTimerAsync(theTimerData);
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

    // only run this on the UI thread
    private async void TriggerOverlayDeleteEvent(string id)
    {
      if (!string.IsNullOrEmpty(id))
      {
        if (_defaultTextOverlay?.Id == id)
        {
          _defaultTextOverlay = null;
          await UpdateDefaultOverlaysAsync();
        }
        else if (_defaultTimerOverlay?.Id == id)
        {
          _defaultTimerOverlay = null;
          await UpdateDefaultOverlaysAsync();
        }

        RemoveWindow(id);
      }
    }

    // only run this on the UI thread
    private async Task UpdateDefaultOverlaysAsync()
    {
      var defTextOverlay = await TriggerStateManager.Instance.GetDefaultTextOverlay();
      var defTimerOverlay = await TriggerStateManager.Instance.GetDefaultTimerOverlay();

      if (defTextOverlay != null && !string.IsNullOrEmpty(defTextOverlay.Id) && _defaultTextOverlay?.Id != defTextOverlay.Id)
      {
        _defaultTextOverlay = defTextOverlay;
        AddWindow(_textWindows, defTextOverlay);
      }

      if (defTimerOverlay != null && !string.IsNullOrEmpty(defTimerOverlay.Id) && _defaultTimerOverlay?.Id != defTimerOverlay.Id)
      {
        _defaultTimerOverlay = defTimerOverlay;
        AddWindow(_timerWindows, defTimerOverlay);
      }
    }

    // only run this from UI thread
    private void AddWindow(ConcurrentDictionary<string, OverlayWindowData> theWindows, TriggerNode overlay)
    {
      var windowData = new OverlayWindowData
      {
        TheWindow = (theWindows == _textWindows) ? new TextOverlayWindow(overlay) : new TimerOverlayWindow(overlay)
      };

      windowData.TheWindow.Show();
      windowData.TheWindow.Visibility = System.Windows.Visibility.Collapsed;
      theWindows[overlay.Id] = windowData;
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

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
    private readonly ConcurrentDictionary<string, OverlayWindowData> _textWindows = [];
    private readonly ConcurrentDictionary<string, OverlayWindowData> _timerWindows = [];
    private readonly ConcurrentDictionary<string, TriggerNode> _defaultOverlays = [];
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
        foreach (var data in _closeRegex.Values.ToArray())
        {
          var doClose = false;
          if (data.UseRegex)
          {
            doClose = data.Regex?.IsMatch(action) == true;
          }
          else if (!string.IsNullOrEmpty(data.ClosePattern))
          {
            doClose = action.Contains(data.ClosePattern, StringComparison.OrdinalIgnoreCase);
          }

          if (doClose)
          {
            _ = UiUtil.InvokeAsync(() =>
            {
              if (_textWindows.TryGetValue(data.Id, out var windowData) && windowData.TheWindow is TextOverlayWindow { } window)
              {
                window.Clear();
              }
            });
          }
        }
      }
    }

    internal void HideOverlays()
    {
      _ = UiUtil.InvokeAsync(() =>
      {
        foreach (var value in _textWindows.Values.ToArray())
        {
          if (value is OverlayWindowData windowData && windowData.TheWindow is TextOverlayWindow { } textWindow)
          {
            textWindow.Clear();
          }
        }

        foreach (var value in _timerWindows.Values.ToArray())
        {
          if (value is OverlayWindowData windowData && windowData.TheWindow is TimerOverlayWindow { } timerWindow)
          {
            timerWindow.HideOverlay();
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
              _closeRegex.TryRemove(overlayId, out _);
              continue;
            }

            // update text overlay regex
            if (overlay.OverlayData.IsTextOverlay)
            {
              UpdateCloseRegex(overlayId, overlay.OverlayData);
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

      if (defaultTextOverlay != null)
      {
        UpdateCloseRegex(defaultTextOverlay.Id, defaultTextOverlay.OverlayData);
      }

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
      var defaultTextOverlay = await TriggerStateManager.Instance.GetDefaultTextOverlay();
      var defaultTimerOverlay = await TriggerStateManager.Instance.GetDefaultTimerOverlay();
      _defaultOverlays[TEXT_OVERLAY] = defaultTextOverlay;
      _defaultOverlays[TIMER_OVERLAY] = defaultTimerOverlay;

      await UiUtil.InvokeAsync(() =>
      {
        AddWindow(_textWindows, defaultTextOverlay);
        AddWindow(_timerWindows, defaultTimerOverlay);
      });
    }

    // only run this from UI thread
    private void AddWindow(ConcurrentDictionary<string, OverlayWindowData> theWindows, TriggerNode overlay)
    {
      if (overlay != null && !theWindows.ContainsKey(overlay.Id))
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

    private void UpdateCloseRegex(string id, Overlay overlay)
    {
      if (!string.IsNullOrEmpty(id) && overlay != null)
      {
        if (!_closeRegex.TryGetValue(id, out var data))
        {
          data = new RegexData();
          _closeRegex[id] = data;
        }

        data.Id = id;
        data.ClosePattern = overlay.ClosePattern;
        data.UseRegex = overlay.UseCloseRegex;

        if (overlay.UseCloseRegex)
        {
          data.Regex = new Regex(overlay.ClosePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
          data.Regex.Match(""); // warm up the regex
        }
        else
        {
          data.Regex = null;
        }
      }
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

using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQLogParser
{
  public partial class TimerOverlayWindow : IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly bool _preview;
    private readonly Dictionary<string, TimerData> _cooldownTimerData = [];
    private readonly List<ShortDurationData> _shortDurationBars = [];
    private readonly SemaphoreSlim _renderSemaphore = new(1, 1);
    private readonly SynchronizedCollection<TimerData> _timerList = [];
    private TriggerNode _node;
    private long _savedHeight;
    private long _savedWidth;
    private long _savedTop = long.MaxValue;
    private long _savedLeft = long.MaxValue;
    private Dictionary<string, Window> _previewWindows;
    private volatile bool _isClosed;
    private volatile bool _isRendering;
    private volatile bool _newData;
    private bool _disposed;

    internal TimerOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();
      _node = node;
      _preview = previews != null;
      _previewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + _node.Id);
      mainPanel.SetResourceReference(VerticalAlignmentProperty, "OverlayVerticalAlignment-" + _node.Id);

      Height = _node.OverlayData.Height;
      Width = _node.OverlayData.Width;
      Top = _node.OverlayData.Top;
      Left = _node.OverlayData.Left;

      if (_preview)
      {
        MainActions.SetCurrentTheme(this);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;
        CreatePreviewTimer("Example Trigger Name", "03:00", 90.0);
        CreatePreviewTimer("Example Trigger Name #2", "01:00", 30.0);
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + _node.Id);
      }

      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
    }

    internal async void StopTimer(TimerData timerData)
    {
      _timerList.Remove(timerData);

      if (timerData.TimerType == 2)
      {
        await UiUtil.InvokeAsync(() => RemoveShortDurationBar(timerData));
      }
    }

    internal async void DeleteTimerAsync(TimerData timerData)
    {
      StopTimer(timerData);

      var key = timerData.Key;
      if (!string.IsNullOrEmpty(timerData.Key))
      {
        await UiUtil.InvokeAsync(() =>
        {
          _cooldownTimerData.Remove(timerData.Key);
          RemoveShortDurationBar(timerData);
        });
      }
    }

    internal async void StartTimerAsync(TimerData timerData)
    {
      if (_isClosed)
      {
        return;
      }

      await _renderSemaphore.WaitAsync();

      try
      {
        _timerList.Add(timerData);
        _newData = true;

        if (!_isRendering)
        {
          _isRendering = true;
          _ = StartRenderingAsync();
        }
      }
      finally
      {
        _renderSemaphore.Release();
      }
    }

    private async Task StartRenderingAsync()
    {
      var timerIncrement = 0;

      while (_isRendering)
      {
        var isComplete = false;
        await UiUtil.InvokeAsync(() =>
        {
          if (_newData)
          {
            Visibility = Visibility.Visible;
            _newData = false;
          }

          if (Visibility != Visibility.Visible)
          {
            return;
          }

          // true if complete
          if (timerIncrement == 0)
          {
            isComplete = Tick();
          }
          else
          {
            if (_timerList.Count(timerData => timerData.TimerType == 2) != _shortDurationBars.Count)
            {
              isComplete = Tick();
              timerIncrement = 0;
            }
            else
            {
              ShortTick();
            }
          }
        });

        await Task.Delay(50);

        if (_isClosed)
        {
          return;
        }

        if (isComplete)
        {
          await _renderSemaphore.WaitAsync();

          try
          {
            if (_timerList.Count == 0)
            {
              _isRendering = false;
              await UiUtil.InvokeAsync(() =>
              {
                Visibility = Visibility.Collapsed;
              });
            }
          }
          finally
          {
            _renderSemaphore.Release();
          }
        }

        if (timerIncrement++ == 10)
        {
          timerIncrement = 0;
        }
      }
    }

    internal void CreatePreviewTimer(string displayName, string timeText, double progress)
    {
      var timerBar = new TimerBar();
      timerBar.Init(_node.Id);
      timerBar.Update(displayName, timeText, progress, new TimerData());
      timerBar.Visibility = Visibility.Visible;
      content.Children.Add(timerBar);
    }

    private void ShortTick()
    {
      var currentTicks = DateTime.UtcNow.Ticks;
      foreach (var value in _shortDurationBars.ToArray())
      {
        var remainingTicks = value.TheTimerData.EndTicks - currentTicks;
        UpdateTimerBar(remainingTicks, value.TheTimerBar, value.TheTimerData, value.MaxDuration, true);
      }
    }

    private bool Tick()
    {
      var currentTicks = DateTime.UtcNow.Ticks;
      var maxDurationTicks = double.NaN;
      TimerData[] orderedList = null;

      lock (_timerList.SyncRoot)
      {
        if (_node.OverlayData.SortBy == 0)
        {
          // create order
          orderedList = [.. _timerList.OrderBy(timerData => timerData.BeginTicks)];
        }
        else if (_node.OverlayData.SortBy == 1)
        {
          // remaining order
          orderedList = [.. _timerList.OrderBy(timerData => timerData.EndTicks - currentTicks)];
        }
        else if (_node.OverlayData.SortBy == 2)
        {
          // alpha
          orderedList = [.. _timerList.OrderBy(timerData => timerData.DisplayName)];
        }

        if (orderedList?.Length > 0 && _node.OverlayData.UseStandardTime && orderedList.Length > 0)
        {
          maxDurationTicks = orderedList.Select(timerData => timerData.DurationTicks).Max();
        }
      }

      var count = 0;
      var childCount = content.Children.Count;
      var handledKeys = new Dictionary<string, bool>();

      if (orderedList != null)
      {
        foreach (var timerData in orderedList)
        {
          var remainingTicks = timerData.EndTicks - currentTicks;
          remainingTicks = Math.Max(remainingTicks, 0);

          if (_node.OverlayData.TimerMode == 1 && timerData.ResetTicks > 0)
          {
            handledKeys[timerData.Key] = true;
            _cooldownTimerData[timerData.Key] = timerData;
          }

          TimerBar timerBar;
          if (count < childCount)
          {
            timerBar = content.Children[count] as TimerBar;
          }
          else
          {
            timerBar = new TimerBar();
            timerBar.Init(_node.Id);
            content.Children.Add(timerBar);
            childCount++;
          }

          UpdateTimerBar(remainingTicks, timerBar, timerData, maxDurationTicks);
          count++;
        }
      }

      var oldestIdleTicks = double.NaN;
      var idleList = new List<dynamic>();
      var resetList = new List<dynamic>();
      if (_node.OverlayData.TimerMode == 1)
      {
        foreach (var timerData in _cooldownTimerData.Values)
        {
          if (!handledKeys.ContainsKey(timerData.Key))
          {
            var remainingTicks = timerData.ResetTicks - currentTicks;
            var data = new { RemainingTicks = remainingTicks, TimerData = timerData };
            if (remainingTicks > 0)
            {
              resetList.Add(data);
            }
            else
            {
              if (double.IsNaN(oldestIdleTicks))
              {
                oldestIdleTicks = Math.Abs(data.RemainingTicks);
              }
              else
              {
                oldestIdleTicks = Math.Min(oldestIdleTicks, Math.Abs(data.RemainingTicks));
              }

              idleList.Add(data);
            }
          }
        }

        foreach (var data in idleList.OrderBy(data => data.TimerData.DurationTicks))
        {
          TimerBar timerBar;
          if (count >= childCount)
          {
            timerBar = new TimerBar();
            timerBar.Init(_node.Id);
            content.Children.Add(timerBar);
            childCount++;
          }
          else
          {
            timerBar = content.Children[count] as TimerBar;
          }

          UpdateCooldownTimerBar(data.RemainingTicks, timerBar, data.TimerData);
          count++;
        }

        foreach (var data in resetList.OrderBy(data => data.RemainingTicks))
        {
          TimerBar timerBar;
          if (count >= childCount)
          {
            timerBar = new TimerBar();
            timerBar.Init(_node.Id);
            content.Children.Add(timerBar);
            childCount++;
          }
          else
          {
            timerBar = content.Children[count] as TimerBar;
          }

          UpdateCooldownTimerBar(data.RemainingTicks, timerBar, data.TimerData);
          count++;
        }
      }

      var complete = false;
      if (_node.OverlayData.TimerMode == 0)
      {
        complete = count == 0;
      }
      else if (_node.OverlayData.IdleTimeoutSeconds > 0)
      {
        complete = (count == idleList.Count) &&
                   (double.IsNaN(oldestIdleTicks) || (oldestIdleTicks > (_node.OverlayData.IdleTimeoutSeconds * TimeSpan.TicksPerSecond)));
      }

      while (count < childCount)
      {
        if (content.Children[count].Visibility == Visibility.Collapsed)
        {
          break;
        }

        content.Children[count].Visibility = Visibility.Collapsed;
        count++;
      }

      return complete;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateTimerBar(double remainingTicks, TimerBar timerBar, TimerData timerData, double maxDurationTicks, bool shortTick = false)
    {
      var endTicks = double.IsNaN(maxDurationTicks) ? timerData.DurationTicks : maxDurationTicks;
      var progress = remainingTicks / endTicks * 100.0;

      var timeText = timerData.TimerType switch
      {
        2 => DateUtil.FormatSimpleMillis((long)remainingTicks),
        3 => DateUtil.FormatSimpleMs((long)(endTicks - remainingTicks)),
        _ => DateUtil.FormatSimpleMs((long)remainingTicks)
      };

      timerBar.SetActive(timerData);
      timerBar.Update(GetDisplayName(timerData), timeText, progress, timerData);

      if (!shortTick)
      {
        if (timerData.TimerType == 2)
        {
          ShortDurationData value = _shortDurationBars.Find(value => value.TheTimerData == timerData);
          if (value == null)
          {
            value = new ShortDurationData
            {
              TheTimerData = timerData,
              TheTimerBar = timerBar
            };

            _shortDurationBars.Add(value);
          }

          value.MaxDuration = maxDurationTicks;
        }
      }

      if (remainingTicks <= (TimeSpan.TicksPerMillisecond * 10))
      {
        RemoveShortDurationBar(timerData);
      }
      else
      {
        UpdateTimerBarVisibility(timerBar);
      }
    }

    private void UpdateCooldownTimerBar(double remainingTicks, TimerBar timerBar, TimerData timerData)
    {
      if (remainingTicks > 0)
      {
        var progress = 100.0 - (remainingTicks / timerData.ResetDurationTicks * 100.0);
        var timeText = DateUtil.FormatSimpleMs((long)remainingTicks);
        timerBar.SetReset();
        timerBar.Update(GetDisplayName(timerData), timeText, progress, timerData);
      }
      else
      {
        var timeText = DateUtil.FormatSimpleMs(timerData.DurationTicks);
        timerBar.SetIdle();
        timerBar.Update(GetDisplayName(timerData), timeText, 100.0, timerData);
      }

      UpdateTimerBarVisibility(timerBar);
    }

    private void UpdateTimerBarVisibility(TimerBar timerBar)
    {
      var state = timerBar.GetState();
      if (_node.OverlayData.TimerMode == 1 &&
          ((state is TimerBar.State.Active or TimerBar.State.None && _node.OverlayData.ShowActive == false) ||
          (state == TimerBar.State.None && _node.OverlayData.ShowActive == false) ||
          (state == TimerBar.State.Idle && _node.OverlayData.ShowIdle == false) ||
          (state == TimerBar.State.Reset && _node.OverlayData.ShowReset == false)))
      {
        if (timerBar.Visibility != Visibility.Collapsed)
        {
          timerBar.Visibility = Visibility.Collapsed;
        }
      }
      else if (timerBar.Visibility != Visibility.Visible)
      {
        timerBar.Visibility = Visibility.Visible;
      }
    }

    private void RemoveShortDurationBar(TimerData timerData)
    {
      if (_shortDurationBars.FindIndex(value => value.TheTimerData == timerData) is var index and >= 0)
      {
        _shortDurationBars[index].TheTimerBar.Visibility = Visibility.Collapsed;
        _shortDurationBars.RemoveAt(index);
      }
    }

    private static string GetDisplayName(TimerData timerData)
    {
      if (timerData.RepeatedCount > -1)
      {
        return timerData.DisplayName.Replace("{repeated}", $"{timerData.RepeatedCount}", StringComparison.OrdinalIgnoreCase);
      }

      return timerData.DisplayName;
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
      _node.OverlayData.Height = _savedHeight = (long)Height;
      _node.OverlayData.Width = _savedWidth = (long)Width;
      _node.OverlayData.Top = _savedTop = (long)Top;
      _node.OverlayData.Left = _savedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      await TriggerStateManager.Instance.Update(_node);
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      Height = _savedHeight;
      Width = _savedWidth;
      Top = _savedTop;
      Left = _savedLeft;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (_node != null && _node.Id == node.Id)
      {
        if (!_node.Equals(node))
        {
          _node = node;
        }

        Height = _node.OverlayData.Height;
        Width = _node.OverlayData.Width;
        Top = _node.OverlayData.Top;
        Left = _node.OverlayData.Left;

        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        closeButton.IsEnabled = true;
      }
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      DragMove();

      if (!saveButton.IsEnabled)
      {
        saveButton.IsEnabled = true;
        closeButton.IsEnabled = false;
      }

      if (!cancelButton.IsEnabled)
      {
        cancelButton.IsEnabled = true;
        closeButton.IsEnabled = false;
      }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      _savedHeight = (long)Height;
      _savedWidth = (long)Width;
      _savedTop = (long)Top;
      _savedLeft = (long)Left;
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (_savedTop != long.MaxValue)
      {
        if (!saveButton.IsEnabled)
        {
          saveButton.IsEnabled = true;
          closeButton.IsEnabled = false;
        }

        if (!cancelButton.IsEnabled)
        {
          cancelButton.IsEnabled = true;
          closeButton.IsEnabled = false;
        }
      }
    }

    private async void WindowClosing(object sender, CancelEventArgs e)
    {
      try
      {
        _isClosed = true;
        _isRendering = false;
        TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        content.Children.Clear();
        _cooldownTimerData.Clear();
        _shortDurationBars.Clear();
        _previewWindows?.Remove(_node.Id);
        _previewWindows = null;
        _timerList.Clear();
        await Task.Delay(200);
        Dispose();
      }
      catch (Exception)
      {
        // do nothing
      }
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (_disposed) return;

      if (disposing)
      {
        _renderSemaphore?.Dispose();
      }

      _disposed = true;
    }

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);
      var source = (HwndSource)PresentationSource.FromVisual(this)!;
      if (source != null)
      {
        source.AddHook(NativeMethods.BandAidHook); // Make sure this is hooked first. That ensures it runs last
        source.AddHook(NativeMethods.ProblemHook);

        if (!_preview)
        {
          // set to layered and topmost by xaml
          var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;
          NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, exStyle);
        }
      }
    }

    private class ShortDurationData
    {
      public TimerData TheTimerData { get; set; }
      public TimerBar TheTimerBar { get; set; }
      public double MaxDuration { get; set; }
    }
  }
}

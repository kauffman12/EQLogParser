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
  public partial class TimerOverlayWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const long TopTimeout = TimeSpan.TicksPerSecond * 2;
    private readonly bool _preview;
    private readonly SemaphoreSlim _renderSemaphore = new(1, 1);
    private readonly List<TimerData> _timerList = [];
    private readonly List<TimerData> _idleTimerList = [];
    private Dictionary<string, Window> _previewWindows;
    private TriggerNode _node;
    private long _savedHeight;
    private long _savedWidth;
    private long _savedTop = long.MaxValue;
    private long _savedLeft = long.MaxValue;
    private long _lastActiveTicks = long.MinValue;
    private long _lastTopTicks = long.MinValue;
    private int _tickCounter;
    private nint _windowHndl;
    private volatile bool _isClosed;
    private volatile bool _isRendering;
    private volatile bool _newData;
    private volatile bool _newShortTickData;
    private volatile bool _useStandardTime;
    private volatile bool _hideDupes;
    private volatile int _sortBy;
    private volatile int _timerMode;
    private volatile int _idleTimeoutSeconds;
    private volatile bool _showActive;
    private volatile bool _showIdle;
    private volatile bool _showReset;
    private readonly bool _streamerMode;

    internal TimerOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();

      _node = node;
      _preview = previews != null;
      _previewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + _node.Id);
      mainPanel.SetResourceReference(VerticalAlignmentProperty, "OverlayVerticalAlignment-" + _node.Id);

      _streamerMode = _node.OverlayData.StreamerMode;
      UpdateFields();

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

    internal async Task StartTimerAsync(TimerData timerData)
    {
      if (_isClosed) return;

      var startLoop = false;
      await _renderSemaphore.WaitAsync().ConfigureAwait(false);
      try
      {
        _timerList.Add(timerData);
        _newData = true;

        if (timerData.TimerType == 2)
        {
          _newShortTickData = true;
        }

        if (!_isRendering)
        {
          _isRendering = true;
          startLoop = true; // decide under the lock
        }
      }
      finally
      {
        _renderSemaphore.Release();
      }

      if (startLoop)
      {
        // start the loop *after* releasing the semaphore
        _ = StartRenderingAsync();
      }
    }

    internal async Task StopTimerAsync(TimerData timerData)
    {
      await _renderSemaphore.WaitAsync();

      try
      {
        // if cooldown timer don't lose the data
        if (_timerList.Remove(timerData) && _timerMode == 1 && timerData.ResetTicks > 0)
        {
          _idleTimerList.Add(timerData);
        }
      }
      finally
      {
        _renderSemaphore.Release();
      }
    }

    // Keep on UI thread
    internal async Task HideOverlayAsync()
    {
      await Task.Run(async () =>
      {
        await _renderSemaphore.WaitAsync();

        try
        {
          _idleTimerList.Clear();
        }
        finally
        {
          _renderSemaphore.Release();
        }
      });

      HideContent();
    }

    // Keep on UI thread
    internal async Task StopOverlayAsync()
    {
      await Task.Run(async () =>
      {
        await _renderSemaphore.WaitAsync();

        try
        {
          _idleTimerList.Clear();
          _timerList.Clear();
          _newData = false;
          _newShortTickData = false;
        }
        finally
        {
          _renderSemaphore.Release();
        }
      });

      HideContent();
    }

    internal void ValidateTimers(HashSet<string> enabledTriggers)
    {
      _ = Task.Run(async () =>
      {
        await _renderSemaphore.WaitAsync();

        try
        {
          foreach (var idle in _idleTimerList.ToArray())
          {
            if (!enabledTriggers.Contains(idle.TriggerId))
            {
              _idleTimerList.Remove(idle);
            }
          }

          foreach (var timerData in _timerList.ToArray())
          {
            if (!enabledTriggers.Contains(timerData.TriggerId))
            {
              _timerList.Remove(timerData);
            }
          }
        }
        finally
        {
          _renderSemaphore.Release();
        }
      });
    }

    internal void CreatePreviewTimer(string displayName, string timeText, double progress)
    {
      var timerBar = new TimerBar();
      timerBar.Init(_node.Id);
      timerBar.Update(displayName, timeText, progress, new TimerData());
      timerBar.Visibility = Visibility.Visible;
      content.Children.Add(timerBar);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task StartRenderingAsync()
    {
      while (_isRendering)
      {
        if (_tickCounter++ == 0 || _newShortTickData)
        {
          if (_newShortTickData)
          {
            _newShortTickData = false;
            _tickCounter = 1;
          }

          var models = await GenerateTimerBarModelsAsync();
          await RenderTimerBarsAsync(models);

          var removeList = models.Where(m => m.IsRemoved && !m.IsCooldown).ToList();
          if (removeList.Count > 0)
          {
            await _renderSemaphore.WaitAsync();

            try
            {
              // Remove expired non-cooldown timers
              foreach (var model in removeList)
              {
                _timerList.Remove(model.TimerData);
              }
            }
            finally
            {
              _renderSemaphore.Release();
            }
          }
        }
        else
        {
          await ShortTickAsync();

          if (_tickCounter > 5)
          {
            _tickCounter = 0;
          }
        }

        if (_isClosed)
        {
          return;
        }

        var needHide = false;
        await _renderSemaphore.WaitAsync();

        try
        {
          if (_timerList.Count == 0)
          {
            if (_idleTimerList.Count > 0 && _timerMode == 1)
            {
              // if not set then just let it idle forever
              if (_idleTimeoutSeconds > 0)
              {
                var lastUpdateTicks = Interlocked.Read(ref _lastActiveTicks);
                var secondsSince = (DateTime.UtcNow.Ticks - lastUpdateTicks) / (double)TimeSpan.TicksPerSecond;
                if (secondsSince >= _idleTimeoutSeconds)
                {
                  // remove data
                  _isRendering = false;
                  _tickCounter = 0;
                  _idleTimerList.Clear();
                  needHide = true;
                }
              }
            }
            else
            {
              _isRendering = false;
              _tickCounter = 0;
              needHide = true;
            }
          }
        }
        finally
        {
          _renderSemaphore.Release();
        }

        if (needHide)
        {
          HideContent();
        }

        await Task.Delay(75); // Adjust delay as needed
      }
    }

    private async Task<List<TimerBarModel>> GenerateTimerBarModelsAsync()
    {
      var currentTicks = DateTime.UtcNow.Ticks;
      List<TimerData> tempTimerList;
      TimerData[] tempIdleList;

      await _renderSemaphore.WaitAsync();

      try
      {
        // Lock the timer list to copy
        tempTimerList = [.. _timerList];

        // add idle timers
        if (_idleTimerList.Count > 0)
        {
          foreach (var idle in _idleTimerList.ToArray())
          {
            // if latest timerData contains the previously idle timer then remove it
            if (tempTimerList.Find(timerData => timerData.Key == idle.Key) != null)
            {
              _idleTimerList.Remove(idle);
            }
            else
            {
              tempTimerList.Add(idle);
            }
          }
        }

        tempIdleList = [.. _idleTimerList];
      }
      finally
      {
        _renderSemaphore.Release();
      }

      // Determine maxDurationTicks based on the current state of timers
      var maxDurationTicks = long.MinValue;

      if (_useStandardTime && tempTimerList.Count > 0)
      {
        maxDurationTicks = tempTimerList.Select(timer => timer.DurationTicks).Max();
      }

      var models = new List<TimerBarModel>();
      var idleModels = new List<TimerBarModel>();
      var resetModels = new List<TimerBarModel>();

      // Process timers
      foreach (var timerData in tempTimerList)
      {
        var type = timerData.TimerType;
        var remainingTicks = timerData.EndTicks - currentTicks;

        if (_timerMode == 1 && timerData.ResetTicks > 0)
        {
          var isInIdleList = false;
          if (tempIdleList.Length > 0)
          {
            isInIdleList = tempIdleList.Contains(timerData);
          }

          if (remainingTicks > 0 && !isInIdleList)
          {
            // Normal countdown phase
            models.Add(new TimerBarModel
            {
              DisplayName = GetDisplayName(timerData),
              TimeText = DateUtil.FormatSimpleMs(remainingTicks),
              Progress = CalcProgress(type, timerData.DurationTicks, remainingTicks, maxDurationTicks),
              TimerData = timerData,
              State = TimerBar.State.Active,
              IsCooldown = true,
              IsRemoved = false,
              MaxDurationTicks = maxDurationTicks,
              RemainingTicks = remainingTicks
            });

            Interlocked.Exchange(ref _lastActiveTicks, currentTicks);
          }
          else
          {
            // Reset phase
            var remainingResetTicks = timerData.ResetTicks - currentTicks;
            var state = remainingResetTicks > 0 ? TimerBar.State.Reset : TimerBar.State.Idle;

            var model = new TimerBarModel
            {
              DisplayName = GetDisplayName(timerData),
              TimeText = remainingResetTicks > 0
                    ? DateUtil.FormatSimpleMs(remainingResetTicks)
                    : DateUtil.FormatSimpleMs(timerData.DurationTicks),
              Progress = remainingResetTicks > 0 ? 100.0 - CalcProgress(type, timerData.ResetDurationTicks, remainingResetTicks, long.MinValue) : 100.0,
              TimerData = timerData,
              State = state,
              IsCooldown = true,
              IsRemoved = false,
              MaxDurationTicks = maxDurationTicks,
              RemainingTicks = remainingResetTicks > 0 ? remainingResetTicks : timerData.DurationTicks,
            };

            if (model.State == TimerBar.State.Idle)
            {
              idleModels.Add(model);
            }
            else if (model.State == TimerBar.State.Reset)
            {
              resetModels.Add(model);
              Interlocked.Exchange(ref _lastActiveTicks, currentTicks);
            }
          }
        }
        else if (remainingTicks >= 0)
        {
          // Regular timers
          models.Add(new TimerBarModel
          {
            DisplayName = GetDisplayName(timerData),
            TimeText = timerData.TimerType switch
            {
              2 => DateUtil.FormatSimpleMillis(remainingTicks),
              3 => DateUtil.FormatSimpleMs(timerData.DurationTicks - remainingTicks),
              _ => DateUtil.FormatSimpleMs(remainingTicks)
            },
            Progress = CalcProgress(type, timerData.DurationTicks, remainingTicks, maxDurationTicks),
            TimerData = timerData,
            State = TimerBar.State.Active,
            IsCooldown = false,
            IsRemoved = remainingTicks <= 0,
            MaxDurationTicks = maxDurationTicks,
            RemainingTicks = remainingTicks
          });

          Interlocked.Exchange(ref _lastActiveTicks, currentTicks);
        }
      }

      // Sort the timers based on the sort criteria
      models.Sort(TimerBarModelSort);
      idleModels.Sort(TimerBarModelSort);
      resetModels.Sort(TimerBarModelSort);

      // order things by idle -> active -> reset
      models.AddRange(idleModels);
      models.AddRange(resetModels);

      if (_hideDupes)
      {
        var dont = false;
        var collapsed = new List<TimerBarModel>(models.Count);
        foreach (var model in models)
        {
          dont = false;
          foreach (var col in collapsed)
          {
            if (model.TimerData.TriggerId == col.TimerData.TriggerId && model.TimerData.CharacterId != col.TimerData.CharacterId &&
              model.State == col.State && string.Equals(model.DisplayName, col.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
              if (!double.IsNaN(model.Progress) && !double.IsNaN(col.Progress) && Math.Abs(model.Progress - col.Progress) < 5.0)
              {
                dont = true;
                break;
              }
            }
          }

          if (!dont)
          {
            collapsed.Add(model);
          }
        }
        return collapsed;
      }

      return models;
    }

    private int TimerBarModelSort(TimerBarModel x, TimerBarModel y)
    {
      return _sortBy switch
      {
        0 => x.TimerData.BeginTicks.CompareTo(y.TimerData.BeginTicks),
        1 => x.RemainingTicks.CompareTo(y.RemainingTicks),
        2 => string.Compare(x.DisplayName, y.DisplayName, StringComparison.Ordinal),
        _ => 0
      };
    }

    private async Task RenderTimerBarsAsync(List<TimerBarModel> models)
    {
      await Dispatcher.InvokeAsync(() =>
      {
        if (_newData)
        {
          Visibility = Visibility.Visible;
          _newData = false;
        }
        else if (Visibility != Visibility.Visible)
        {
          return;
        }

        var childCount = content.Children.Count;
        var count = 0;

        foreach (var model in models)
        {
          if (model.IsRemoved)
          {
            // Skip rendering removed timers
            continue;
          }

          // dont render if turned off for cooldown overlays
          if (_timerMode == 1 && ((model.State == TimerBar.State.Active && !_showActive) || (model.State == TimerBar.State.Idle && !_showIdle) ||
            (model.State == TimerBar.State.Reset && !_showReset)))
          {
            continue;
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
          }

          // Update the TimerBar based on its state
          UpdateTimerBarState(model.State, model.TimerData, timerBar);
          timerBar.Update(model.DisplayName, model.TimeText, model.Progress, model.TimerData);

          if (timerBar.Visibility != Visibility.Visible)
          {
            timerBar.Visibility = Visibility.Visible;
            if (content.Visibility != Visibility.Visible)
            {
              content.Visibility = Visibility.Visible;
            }
          }

          // Store the associated TimerBarModel in the Tag field
          timerBar.Tag = model;
          count++;
        }

        // Collapse unused bars
        var extraCount = 0;
        while (count < childCount)
        {
          // remove some when we get too many
          if (extraCount > 5)
          {
            content.Children.RemoveAt(count);
            childCount--;
            continue;
          }
          else if (content.Children[count] is TimerBar bar)
          {
            bar.Visibility = Visibility.Collapsed;
            bar.Tag = null;
            extraCount++;
          }
          count++;
        }
      });
    }

    private async Task ShortTickAsync()
    {
      var currentTicks = DateTime.UtcNow.Ticks;
      TimerData[] tempIdleList;

      await _renderSemaphore.WaitAsync();

      try
      {
        tempIdleList = [.. _idleTimerList];
      }
      finally
      {
        _renderSemaphore.Release();
      }

      await Dispatcher.InvokeAsync(() =>
      {
        if (_windowHndl != 0 && (_lastTopTicks == long.MinValue || (currentTicks - _lastTopTicks) > TopTimeout))
        {
          NativeMethods.SetWindowTopMost(_windowHndl);
          _lastTopTicks = currentTicks;
        }

        foreach (var child in content.Children)
        {
          if (child is TimerBar timerBar && timerBar.Visibility == Visibility.Visible)
          {
            if (timerBar.Tag is not TimerBarModel model) continue;

            var timerData = model.TimerData;
            var type = timerData.TimerType;
            var remainingTicks = timerData.EndTicks - currentTicks;
            var maxDurationTicks = model.MaxDurationTicks;

            if (_timerMode == 1 && timerData.ResetTicks > 0)
            {
              if (remainingTicks > 0 && !tempIdleList.Contains(timerData))
              {
                // Update the TimerBar based on its state
                UpdateTimerBarState(TimerBar.State.Active, timerData, timerBar);

                timerBar.Update(
                  model.DisplayName,
                  DateUtil.FormatSimpleMs(remainingTicks),
                  CalcProgress(type, timerData.DurationTicks, remainingTicks, maxDurationTicks),
                  timerData
                );
              }
              else
              {
                // Reset phase
                var remainingResetTicks = timerData.ResetTicks - currentTicks;
                var state = remainingResetTicks > 0 ? TimerBar.State.Reset : TimerBar.State.Idle;

                // Update the TimerBar based on its state
                UpdateTimerBarState(state, timerData, timerBar);

                timerBar.Update(
                  model.DisplayName,
                  remainingResetTicks > 0 ? DateUtil.FormatSimpleMs(remainingResetTicks) : DateUtil.FormatSimpleMs(timerData.DurationTicks),
                  remainingResetTicks > 0 ? 100.0 - CalcProgress(type, timerData.ResetDurationTicks, remainingResetTicks, long.MinValue) : 100.0,
                  timerData
                );
              }
            }
            else if (remainingTicks >= 0)
            {
              // Update progress and time text for active timers
              timerBar.Update(
                model.DisplayName,
                timerData.TimerType switch
                {
                  2 => DateUtil.FormatSimpleMillis(remainingTicks),
                  3 => DateUtil.FormatSimpleMs(timerData.DurationTicks - remainingTicks),
                  _ => DateUtil.FormatSimpleMs(remainingTicks)
                },
                CalcProgress(type, timerData.DurationTicks, remainingTicks, maxDurationTicks),
                timerData
              );
            }
          }
        }
      });
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

        UpdateFields();
        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        closeButton.IsEnabled = true;
      }
    }

    // Keep on UI thread
    private void HideContent()
    {
      // sometimes called from main render thread
      if (!Dispatcher.CheckAccess())
      {
        Dispatcher.Invoke(HideContent);
        return;
      }

      foreach (var child in content.Children)
      {
        if (child is TimerBar { } bar && bar.Visibility != Visibility.Collapsed)
        {
          bar.Visibility = Visibility.Collapsed;
        }
      }

      Visibility = Visibility.Collapsed;
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

    private void UpdateFields()
    {
      Height = _node.OverlayData.Height;
      Width = _node.OverlayData.Width;
      Top = _node.OverlayData.Top;
      Left = _node.OverlayData.Left;

      Title = _node.Name;
      _hideDupes = _node.OverlayData.HideDuplicates;
      _useStandardTime = _node.OverlayData.UseStandardTime;
      _sortBy = _node.OverlayData.SortBy;
      _timerMode = _node.OverlayData.TimerMode;
      _idleTimeoutSeconds = (int)_node.OverlayData.IdleTimeoutSeconds;
      _showActive = _node.OverlayData.ShowActive;
      _showIdle = _node.OverlayData.ShowIdle;
      _showReset = _node.OverlayData.ShowReset;

      if (_streamerMode != _node.OverlayData.StreamerMode && !_preview)
      {
        _ = Task.Run(async () =>
        {
          await TriggerOverlayManager.Instance.RestartOverlayAsync(_node.Id);
        });
      }
    }

    private async void WindowClosing(object sender, CancelEventArgs e)
    {
      try
      {
        _isClosed = true;
        _isRendering = false;
        _newData = false;
        _newShortTickData = false;
        TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        _previewWindows?.Remove(_node.Id);
        _previewWindows = null;
        await Task.Delay(750);
      }
      catch (Exception)
      {
        // do nothing
      }
    }

    private static double CalcProgress(int type, long duration, long remaining, long max)
    {
      var denom = max == long.MinValue ? duration : max;
      if (denom <= 0) return 0.0;

      var p = (double)remaining / denom * 100.0;
      if (type == 3 && max != long.MinValue && duration > 0)
        p += (1 - ((double)duration / max)) * 100.0;

      if (double.IsNaN(p) || double.IsInfinity(p)) return 0.0;
      return Math.Clamp(p, 0.0, 100.0);
    }

    private static string GetDisplayName(TimerData timerData)
    {
      var result = timerData.DisplayName;
      if (timerData.RepeatedCount > -1)
      {
        result = result.Replace(TriggerProcessor.RepeatedCode, $"{timerData.RepeatedCount}", StringComparison.OrdinalIgnoreCase);
      }

      if (timerData.CounterCount > -1)
      {
        result = result.Replace(TriggerProcessor.CounterCode, $"{timerData.CounterCount}", StringComparison.OrdinalIgnoreCase);
      }

      if (!string.IsNullOrEmpty(timerData.LogTime))
      {
        result = result.Replace(TriggerProcessor.LogTimeCode, timerData.LogTime, StringComparison.OrdinalIgnoreCase);
      }

      return result;
    }

    private static void UpdateTimerBarState(TimerBar.State state, TimerData timerData, TimerBar timerBar)
    {
      // Update the TimerBar based on its state
      switch (state)
      {
        case TimerBar.State.Active:
          timerBar.SetActive(timerData);
          break;
        case TimerBar.State.Reset:
          timerBar.SetReset();
          break;
        case TimerBar.State.Idle:
          timerBar.SetIdle();
          break;
      }
    }

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      try
      {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        if (source != null)
        {
          source.AddHook(NativeMethods.BandAidHook); // Make sure this is hooked first. That ensures it runs last
          source.AddHook(NativeMethods.ProblemHook);
          NativeMethods.SetWindowTopMost(source.Handle);
          _windowHndl = source.Handle;

          if (!_preview)
          {
            // Get current extended styles
            var exStyle = (int)NativeMethods.GetWindowLongPtr(_windowHndl, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

            // Add transparency and layered styles
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExLayered | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;

            if (!_streamerMode)
            {
              // tool window to not show up in alt-tab
              exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;
            }

            // Apply the new extended styles
            NativeMethods.SetWindowLong(_windowHndl, (int)NativeMethods.GetWindowLongFields.GwlExstyle, new IntPtr(exStyle));
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Problem in OnSourceInitialized", ex);
      }
    }

    internal class TimerBarModel
    {
      public string DisplayName { get; set; }
      public string TimeText { get; set; }
      public double Progress { get; set; }
      public TimerData TimerData { get; set; }
      public TimerBar.State State { get; set; } // Active, Reset, Idle
      public bool IsCooldown { get; set; }     // Indicates cooldown behavior
      public bool IsRemoved { get; set; }      // Indicates whether it should be hidden/removed
      public long MaxDurationTicks { get; set; }
      public long RemainingTicks { get; set; }
    }
  }
}
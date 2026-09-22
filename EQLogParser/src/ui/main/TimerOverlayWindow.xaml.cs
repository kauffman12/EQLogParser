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

    /*
     * This overlay's spans on the heartbeat (PerfCounters, UiBeatMonitor). The panel redraws from a 75 ms loop for as long as any timer is
     * live, rewriting the text and progress of every bar in an AllowsTransparency window, and a second pass rebuilds the visual tree when
     * what is firing changes. Both are UI-thread work of exactly the kind that can take the combat numbers down with it, so both belong on
     * the log line where those numbers are reported — until now this window was the largest thing a player can open during a raid with no
     * number against its name.
     */
    private static readonly int TimerBarId = PerfCounters.Register("trig.timerBar");
    private static readonly int TimerTickId = PerfCounters.Register("trig.timerTick");
    private static readonly int TimerBarsId = PerfCounters.Register("trig.timerBars");

    /*
     * Bars the overlay had to reap for itself, i.e. rows whose owner never sent the stop that was supposed to take them away. Zero is the
     * healthy number: every removal then came from the timer's own scheduled end or an "end early" line, which is the design. Anything else
     * says a lifecycle hole is open — a saturated delay (clamped now), a stop lost against a closing dispatcher, a stop routed to a window the
     * trigger stopped using — and each one is also named in the log by ReapForgottenRows. Reported as trig.timerStale.
     */
    private static readonly int TimerStaleId = PerfCounters.Register("trig.timerStale", uiThread: false);

    /*
     * Rows refused at the door because their stop had already been spent before they arrived — the same lost-removal class as trig.timerStale, caught one
     * step earlier so the bar never appears. Under a burst this is the common one: three lines matching one trigger, and the second message's "restart"
     * cancels the first row while that row's insert is still waiting on the render semaphore.
     */
    private static readonly int TimerLateAddId = PerfCounters.Register("trig.timerLateAdd", uiThread: false);

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

    /* When each trigger last had a stale bar named in the log, so a broken trigger says once a minute and not thirteen times a second. */
    private readonly Dictionary<string, long> _staleLogMs = [];

    /* When this overlay last complained about a row that arrived after its own stop — same throttle, one per window. */
    private long _lateLogMs;
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
    private volatile bool _showMillis;
    private volatile bool _showReset;
    private readonly bool _streamerMode;

    internal TimerOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();

      _node = node;

      /* Reported as open while on screen, so a stall line saying "open fct+meter+tmr:…" is a different investigation from one where the
         timers were never up. Named per trigger node because several of these can be open at once. */
      var surface = TextOverlayWindow.SurfaceName(_node, "tmr");
      IsVisibleChanged += (_, e) => UiBeatMonitor.NoteSurface(surface, (bool)e.NewValue);

      _preview = previews != null;
      _previewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + _node.Id);
      mainPanel.SetResourceReference(VerticalAlignmentProperty, "OverlayVerticalAlignment-" + _node.Id);

      _streamerMode = _node.OverlayData.StreamerMode;
      UpdateFields();

      if (_preview)
      {
        ThemeConfig.SetCurrentTheme(this);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        titleViewbox.Visibility = Visibility.Visible;
        buttonContent.Visibility = Visibility.Visible;
        mainPanel.Visibility = Visibility.Visible;
        contentBorder.Visibility = Visibility.Visible;
        mainPanel.IsHitTestVisible = true;
        buttonContent.IsHitTestVisible = true;
        DoPreview();
      }
      else
      {
        contentBorder.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        IsHitTestVisible = false;
      }

      TriggerStateDB.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
    }

    internal void DoPreview()
    {
      content.Children.Clear();
      CreatePreviewTimer("Example Trigger Name", _showMillis ? "03:00.140" : "03:00", 90.0);
      CreatePreviewTimer("Example Trigger Name #2", _showMillis ? "01:00.344" : "01:00", 30.0);
    }

    internal async Task StartTimerAsync(TimerData timerData)
    {
      if (_isClosed)
        return;

      var startLoop = false;
      var armedLoop = false;
      var accepted = false;

      await _renderSemaphore.WaitAsync().ConfigureAwait(false);

      try
      {
        /*
         * Do not insert a row its owner has already given up on. Start is fire-and-forget and Stop waits on this same semaphore, so nothing anywhere
         * keeps Add before Stop, and a Stop only removes what is already in the list: one that arrives first vanishes. Two ways that happens — a short
         * countdown whose removal task wakes while its own row is still queued (a quarter-second timer is enough on a busy machine), and the "restart
         * timer" option, where the second of three messages in one batch cancels the first row and stops it before that first insert has landed. What
         * used to arrive either way was a row past its end, which produces no model (the display guards on remaining >= 0), so nothing could ever take it
         * away, and whatever the bar last showed — for these, "0:00" — stayed up for the rest of the session. Canceled is set before any Stop is
         * dispatched in every cancellation path, which is what makes it safe to believe here.
         */
        if (TimerLifecycle.AcceptsRow(timerData.EndTicks, timerData.Canceled, DateTime.UtcNow.Ticks))
        {
          accepted = true;
          _timerList.Add(timerData);
          _newData = true;

          if (timerData.TimerType == 2)
          {
            _newShortTickData = true;
          }

          if (!_isRendering)
          {
            _isRendering = true;
            armedLoop = true;
            startLoop = true; // decide under the lock
          }
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Error starting timer", ex);

        // Take back only what this call may have put in. _isRendering is left alone unless this call was the one that set it: clearing a loop that other
        // rows are still using is how a failed add used to freeze the whole overlay.
        _timerList.Remove(timerData);

        if (armedLoop)
        {
          _isRendering = false;
          startLoop = false;
        }

        accepted = false;
      }
      finally
      {
        _renderSemaphore.Release();
      }

      if (!accepted)
      {
        NoteLateTimer(timerData);
        return;
      }

      if (startLoop)
      {
        // start the loop *after* releasing the semaphore
        _ = StartRenderingAsync();
      }
    }

    internal async Task StopTimerAsync(TimerData timerData)
    {
      if (_isClosed)
        return;

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

    /* Resolve the display name for a timer bar.
     *
     * Variable resolution order (highest → lowest precedence):
     *   1. Built-in codes: {counter}, {repeated}, {logtime}
     *   2. Custom variables from timerData.Variables
     *
     * The line code {l} is resolved into the template at timer-creation time and
     * always references the log action that started the timer — it does not update
     * dynamically during the timer's lifetime.
     *
     * Timers without a dynamic display name skip the template and keep the name resolved when the
     * timer started. The built-in codes still resolve from per-timer fields captured at that same
     * moment, so those names do not move either.
     *
     * This method is called only during full renders (~450ms interval). Short ticks
     * reuse the display name from the cached TimerBarModel. No additional rate-limiting
     * is needed — the render cycle provides natural throttling.
     */
    internal static string GetDisplayName(TimerData timerData)
    {
      var result = timerData.DisplayName;
      var hasTemplateVars = timerData.DynamicDisplayName &&
          !string.IsNullOrEmpty(timerData.DisplayNameTemplate) &&
          timerData.Variables is not null &&
          timerData.DisplayNameTemplate.Contains('{');

      // If the timer has a template with custom variables, re-resolve from live values
      if (hasTemplateVars)
      {
        result = timerData.DisplayNameTemplate;
      }

      // Built-in codes first (highest priority)
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

      // Custom variables last (lowest priority)
      if (hasTemplateVars)
      {
        result = TriggerProcessor.ProcessMatchesText(result, timerData.Variables);
      }

      return result;
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

    /*
     * A row that turned up after its own stop was already spent, and so was dropped at the door rather than added as a bar nothing would remove.
     * Counted always (trig.timerLateAdd: zero is healthy, see ReapForgottenRows for the case that arrives too late to be refused), named once a
     * minute per window because a trigger that does this does it on every burst.
     */
    private void NoteLateTimer(TimerData timerData)
    {
      PerfCounters.Note(TimerLateAddId);

      var nowMs = Environment.TickCount64;
      if (nowMs - _lateLogMs < 60_000)
      {
        return;
      }

      _lateLogMs = nowMs;
      Log.Warn($"Timer row for '{GetDisplayName(timerData)}' (trigger {timerData.TriggerId}) arrived after its own " +
        $"{(timerData.Canceled ? "cancel" : "expiry")}; dropped instead of adding a bar nothing could remove");
    }

    /* The configured idle timeout in ticks, or IdleNever when none is set (the shipped "leave it up" default). */
    private long IdleTimeoutTicks =>
      _idleTimeoutSeconds > 0 ? _idleTimeoutSeconds * TimeSpan.TicksPerSecond : TimerLifecycle.IdleNever;

    /*
     * Drop the rows whose owner never came for them, and say so once a minute per trigger. This is the only reaper that can see them: their
     * scheduled removal is what usually takes a bar away (TriggerProcessor's detached delay), and when that sleep saturated, when the stop was
     * posted against a closing dispatcher, or when it went to a window the trigger no longer uses, nothing else in the program would ever offer
     * the row for deletion again. Called under the render lock from the long tick; see the note there for why the rules live in Core.
     */
    private void ReapForgottenRows(long nowTicks)
    {
      if (_timerList.Count == 0)
      {
        return;
      }

      foreach (var timerData in _timerList.ToArray())
      {
        if (TimerLifecycle.RetainRow(timerData.EndTicks, nowTicks))
        {
          continue;
        }

        _timerList.Remove(timerData);
        PerfCounters.Note(TimerStaleId);

        var lastLog = _staleLogMs.TryGetValue(timerData.TriggerId ?? "", out var ms) ? ms : 0L;
        if (nowTicks / TimeSpan.TicksPerMillisecond - lastLog < 60_000)
        {
          continue;
        }

        _staleLogMs[timerData.TriggerId ?? ""] = nowTicks / TimeSpan.TicksPerMillisecond;
        Log.Warn($"Timer overlay removed a bar its trigger never stopped: '{GetDisplayName(timerData)}' " +
          $"(trigger {timerData.TriggerId}) ended {TimerLifecycle.StaleSeconds(timerData.EndTicks, nowTicks):0}s ago, " +
          $"type {timerData.TimerType}, mode {_timerMode}");
      }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    /*
     * The loop is wrapped rather than run directly, because the wrapper is what keeps a single fault from being permanent. If this method
     * faults anywhere below — inside a render, or against a dispatcher that is being torn down — _isRendering is left true while no loop is
     * running, and that combination never recovers: StartTimerAsync starts a loop only `if (!_isRendering)`, so every timer added afterwards
     * goes into the list and is never painted, never ticked and never removed, and the overlay sits on screen holding whatever its last frame
     * showed. For a bar caught at its end that frame reads 0:00, indefinitely. Clearing the flag in a finally makes the next timer re-arm the
     * loop instead of joining a dead one, and logging says which render died rather than leaving it to the player to notice.
     */
    private async Task StartRenderingAsync()
    {
      try
      {
        await RenderTimerLoopAsync();
      }
      catch (Exception ex)
      {
        Log.Warn("timer overlay render loop failed; the next timer restarts it", ex);
      }
      finally
      {
        _isRendering = false;
        _tickCounter = 0;
      }
    }

    private async Task RenderTimerLoopAsync()
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

          /*
           * Reaping, in one place and on the row's own clock rather than on what its owner chose to do about it. Two holes close here.
           *
           * The removal above can only see rows that produced a model, and rows stop producing models as soon as their remaining time goes
           * negative — so a bar which was hidden by "hide duplicates" at the moment it expired, or whose end frame fell between two long
           * ticks, is never offered for removal again. It also keeps the loop alive: the render loop stops only when the list is empty, so one
           * forgotten row means an overlay that never idles and never hides. Cooldown rows are worse still, because they deliberately report
           * IsRemoved = false forever — anything their owner loses is on screen until the process ends.
           *
           * So every long tick asks TimerLifecycle whether each row should still exist (TimerLifecycle.RetainRow, which is where the grace and
           * the idle-timeout rules live) and drops the ones whose answer is no, naming them in the log. The grace keeps the design intact: an
           * owner that stops its own timers still always wins, because nothing is reaped until well past its end.
           */
          await _renderSemaphore.WaitAsync();

          try
          {
            // Remove expired non-cooldown timers
            foreach (var model in removeList)
            {
              _timerList.Remove(model.TimerData);
            }

            ReapForgottenRows(DateTime.UtcNow.Ticks);
          }
          finally
          {
            _renderSemaphore.Release();
          }
        }

        /*
         * Idle rows age per row, not per overlay. The shipped rule (below, at the foot of this loop) clears the idle list only once every
         * live timer has gone AND an idle timeout is configured, which in a raid — where something is always counting down — means greyed
         * bars pile up for the whole night and none of them ever leave. Each row now dies idleTimeout after it stopped being live; with no
         * timeout configured the shipped "idle forever" still stands, so nobody's configured behaviour changes except that lost rows go.
         */
        if (_idleTimerList.Count > 0)
        {
          var idleNow = DateTime.UtcNow.Ticks;
          var idleTimeout = IdleTimeoutTicks;
          await _renderSemaphore.WaitAsync();

          try
          {
            foreach (var idle in _idleTimerList.ToArray())
            {
              // "Stopped being live" is whichever stamp came later: the countdown, or the reset that follows it.
              var idleSince = idle.ResetTicks > 0 ? Math.Max(idle.ResetTicks, idle.EndTicks) : idle.EndTicks;
              if (!TimerLifecycle.RetainIdleRow(idleSince, idleNow, idleTimeout))
              {
                _idleTimerList.Remove(idle);
              }
            }
          }
          finally
          {
            _renderSemaphore.Release();
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

        await Task.Delay(75);
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
              TimeText = FormatTime(remainingTicks),
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
                    ? FormatTime(remainingResetTicks)
                    : FormatTime(timerData.DurationTicks),
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
              2 => DateUtil.FormatTicks(remainingTicks, DateUtil.TimeFormat.SecondsMs),
              3 => FormatTime(timerData.DurationTicks - remainingTicks),
              _ => FormatTime(remainingTicks)
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
        3 => CompareNatural(x.DisplayName, y.DisplayName),
        _ => 0
      };
    }

    // Add the natural sorting method
    private static int CompareNatural(string x, string y)
    {
      if (x == null && y == null) return 0;
      if (x == null) return 1;
      if (y == null) return -1;

      var xIndex = 0;
      var yIndex = 0;

      while (xIndex < x.Length && yIndex < y.Length)
      {
        var xChar = x[xIndex];
        var yChar = y[yIndex];

        if (char.IsDigit(xChar) && char.IsDigit(yChar))
        {
          // Extract complete numbers
          var xNumStart = xIndex;
          while (xIndex < x.Length && char.IsDigit(x[xIndex]))
            xIndex++;

          var yNumStart = yIndex;
          while (yIndex < y.Length && char.IsDigit(y[yIndex]))
            yIndex++;

          var xNum = x.Substring(xNumStart, xIndex - xNumStart);
          var yNum = y.Substring(yNumStart, yIndex - yNumStart);

          // Compare numerically
          var numCompare = xNum.Length.CompareTo(yNum.Length);
          if (numCompare != 0)
            return numCompare;

          // If same length, compare digit by digit
          for (var i = 0; i < xNum.Length; i++)
          {
            if (xNum[i] != yNum[i])
              return xNum[i].CompareTo(yNum[i]);
          }
        }
        else
        {
          // Compare characters
          var charCompare = xChar.CompareTo(yChar);
          if (charCompare != 0)
            return charCompare;

          xIndex++;
          yIndex++;
        }
      }

      // If we've reached the end of one string, the shorter one comes first
      return x.Length.CompareTo(y.Length);
    }

    private async Task RenderTimerBarsAsync(List<TimerBarModel> models)
    {
      await Dispatcher.InvokeAsync(() => PerfCounters.Run(TimerBarId, () => DrawTimerBars(models)));
    }

    /*
     * The visual-tree pass, named so a span can wrap it: reuses, adds and collapses TimerBars until the panel matches what is firing.
     * Reported as trig.timerBar, apart from trig.timerTick (which only moves bars that already exist), because this is the pass that
     * changes the tree — and an AllowsTransparency window re-composites its whole surface when the tree changes. The gauge is the pool
     * size including collapsed spares, so cost can be read per bar instead of in the abstract.
     */
    private void DrawTimerBars(List<TimerBarModel> models)
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
        }

        if (contentBorder.Visibility != Visibility.Visible)
        {
          contentBorder.Visibility = Visibility.Visible;
        }

        if (mainPanel.Visibility != Visibility.Visible)
        {
          mainPanel.Visibility = Visibility.Visible;
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

      /* The pool this overlay is carrying, so the two costs above can be read per bar rather than in the abstract. */
      PerfCounters.Gauge(TimerBarsId, content.Children.Count);
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

      await Dispatcher.InvokeAsync(() => PerfCounters.Run(TimerTickId, () => DrawTimerTick(currentTicks, tempIdleList)));
    }

    /*
     * The steady cost of the overlay, named so a span can wrap it: one pass over every visible bar rewriting its text and its progress,
     * driven from a 75 ms loop for as long as any timer is live. Reported as trig.timerTick — this is the number that says what an open
     * timer overlay costs a frame in a full raid.
     */
    private void DrawTimerTick(long currentTicks, TimerData[] tempIdleList)
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
                FormatTime(remainingTicks),
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
                remainingResetTicks > 0 ? FormatTime(remainingResetTicks) : FormatTime(timerData.DurationTicks),
                remainingResetTicks > 0 ? 100.0 - CalcProgress(type, timerData.ResetDurationTicks, remainingResetTicks, long.MinValue) :
                100.0, timerData
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
                2 => DateUtil.FormatTicks(remainingTicks, DateUtil.TimeFormat.SecondsMs),
                3 => FormatTime(timerData.DurationTicks - remainingTicks),
                _ => FormatTime(remainingTicks)
              },
              CalcProgress(type, timerData.DurationTicks, remainingTicks, maxDurationTicks),
              timerData
            );
          }
        }
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

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
      _node.OverlayData.Height = _savedHeight = (long)Height;
      _node.OverlayData.Width = _savedWidth = (long)Width;
      _node.OverlayData.Top = _savedTop = (long)Top;
      _node.OverlayData.Left = _savedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      await TriggerStateDB.Instance.Update(_node);
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

        if (_preview)
        {
          DoPreview();
        }
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
      contentBorder.Visibility = Visibility.Collapsed;
      mainPanel.Visibility = Visibility.Collapsed;
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
      _showMillis = _node.OverlayData.ShowMillis;
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
        TriggerStateDB.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        _previewWindows?.Remove(_node.Id);
        _previewWindows = null;
        await Task.Delay(750);
      }
      catch (Exception)
      {
        // do nothing
      }
    }

    private string FormatTime(long ticks)
    {
      var showDays = ticks >= TimeSpan.TicksPerDay;
      var format = (_showMillis, showDays) switch
      {
        (true, true) => DateUtil.TimeFormat.DHMSMsCompact,
        (true, false) => DateUtil.TimeFormat.HMSMsCompact,
        (false, true) => DateUtil.TimeFormat.DHMSCompact,
        (false, false) => DateUtil.TimeFormat.HMSCompact
      };
      return DateUtil.FormatTicks(ticks, format);
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
          timerBar.SetReset(timerData);
          break;
        case TimerBar.State.Idle:
          timerBar.SetIdle(timerData);
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

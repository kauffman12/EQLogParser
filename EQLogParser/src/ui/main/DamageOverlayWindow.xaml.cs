using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class DamageOverlayWindow
  {
    private const long TopTimeout = TimeSpan.TicksPerSecond * 2;
    private static readonly ILog Log = LogManager.GetLogger(typeof(DamageOverlayWindow));
    private DamageOverlayStats _stats;
    private readonly DispatcherTimer _updateTimer;
    private readonly bool _preview;
    private readonly MeterRefreshGate _refreshGate = new();
    private readonly MeterHeaderReserve _toolbarHeaderReserve = new();
    private readonly MeterPresentationState _presentation = new();
    private readonly Stopwatch _refreshClock = Stopwatch.StartNew();
    private readonly DamageMeterCalculation _calculation = new();
    private CancellationTokenSource _buildCancellation;
    private int _calculationSession;
    private readonly long _lifecycleOwner;
    private long _lastTopTicks = long.MinValue;
    private int _savedFontSize;
    private int _savedMaxRows;
    private int _currentDamageMode;
    private int _savedDamageMode;

    private bool _meterWarned;
    private int _meterFailures;
    private bool _closed;
    private int _displayVersion;

    // X-close/reopen preserves explicit reset state, but the lifecycle retains no engine, snapshot or row references.
    private static readonly MeterLifecycleCheckpoint MeterLifecycle = new();
    private int _currentShowCritRate;
    private int _savedShowCritRate;
    private bool _currentHideOthers;
    private bool _savedHideOthers;
    private bool _savedMiniBars;
    private bool _savedStreamerMode;
    private bool _currentShowDps;

    // Which panels this window has at all, and therefore which activity may open it or keep it up. DamageMeterConfigState judged
    // the key; this is that answer carried for the life of the window, replaced only by a committed Setup change.
    private MeterPanels _panels = MeterPanels.Both;

    private string _currentSelectedClass;
    private string _savedSelectedClass;
    private string _savedProgressColor;
    private string _savedHighlightColor;
    private DamageOverlayToolbarWindow _toolbarWindow;
    private MeterColumnLayout _columnLayout;

    internal DamageOverlayWindow(bool preview = false, bool reset = false)
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();

      _preview = preview;
      _lifecycleOwner = MeterLifecycle.Open();

      if (reset)
      {
        _stats = null;
      }

      // dimensions
      var width = ConfigUtil.GetSettingAsDouble("OverlayWidth", 400);
      var height = ConfigUtil.GetSettingAsDouble("OverlayHeight", int.MaxValue);
      var top = ConfigUtil.GetSettingAsDouble("OverlayTop", 20);
      var left = ConfigUtil.GetSettingAsDouble("OverlayLeft", 100);
      // Keep the saved row budget intact when adding the header-to-row gap.
      SetWindowSizes(height + controlPanel.Margin.Bottom, width, top, left);

      /*
       * Every staged value comes from DamageMeterConfigState.Load() rather than from its own parse of each key: that is the same guard set
       * the setup window opens on, so an unreadable or out-of-range settings line cannot give this meter one answer and configure another.
       * Crit rate is the reason the law exists — this file used to read OverlayEnableCritRate with no 0..3 clamp while Load() clamped it, so
       * a foreign settings.txt put the panel and the live meter out of step. Geometry stays read key by key on purpose: dragging and resizing
       * the stage is the geometry editor, not a staged setting.
       */
      var config = DamageMeterConfigState.Load();

      /* Which numeric columns are drawn, read before the first font asks for widths: the header and both bar pools lay out from
         the same answer. Elapsed seconds is not one of these switches (MeterColumns carries the list and its defaults). */
      _currentColumns = config.Columns;

      _savedFontSize = config.FontSize;
      UpdateFontSize(_savedFontSize);

      _savedProgressColor = config.ProgressColor;
      UpdateProgressBrush(_savedProgressColor);

      // Highlight falls back to the progress colour, which Load() already settled.
      _savedHighlightColor = config.HighlightColor;
      UpdateHighlightBrush(_savedHighlightColor);

      _savedMaxRows = config.MaxRows;

      _savedDamageMode = config.DamageResetMode;
      UpdateDamageMode(_savedDamageMode);

      var list = EQDataStore.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);

      /* The one staged value this window re-checks: "is that class still in the game" needs the data store, which the staged
         object deliberately does not touch. A name no longer offered reads as ANY_CLASS here and on the next Save. */
      var selectedClass = config.SelectedClass;
      if (!list.Contains(selectedClass))
      {
        selectedClass = Resource.ANY_CLASS;
      }

      UpdateSelectedClass(selectedClass);
      _savedSelectedClass = _currentSelectedClass;

      _savedHideOthers = config.HideOtherPlayers;
      UpdateHideOthers(_savedHideOthers);

      _savedShowCritRate = config.CritRateDisplay;
      UpdateShowCritRate(_savedShowCritRate);

      _savedMiniBars = config.MiniBars;
      UpdateMiniBars(_savedMiniBars);

      UpdateMaxRows(_savedMaxRows);

      _savedStreamerMode = config.StreamerMode;
      _currentMaxRows = _savedMaxRows;
      _currentStreamerMode = _savedStreamerMode;

      _panels = config.Panels;

      // A remembered tab that is not available yields to the surviving panel without writing a setting.
      _currentShowDps = ConfigUtil.IfSetOrElse("OverlayShowingDps", true);
      if (_currentShowDps && (config.Panels & MeterPanels.Damage) == 0) _currentShowDps = false;
      else if (!_currentShowDps && (config.Panels & MeterPanels.Tanking) == 0) _currentShowDps = true;

      _updateTimer = UiUtil.CreateTimer(UpdateTimerTick, 1000, false, DispatcherPriority.DataBind);

      if (preview)
      {
        _updateTimer.Stop();
        ResizeMode = ResizeMode.CanResizeWithGrip;
        lineGrid.Visibility = Visibility.Visible;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "PreviewBackgroundBrush");
        border.Background = null;
        damageContent.Visibility = Visibility.Visible;
        controlPanel.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        MinHeight = 40;
      }
      else
      {
        MinHeight = 0;
        ResizeMode = ResizeMode.NoResize;
        lineGrid.Visibility = Visibility.Collapsed;
        controlPanel.Visibility = Visibility.Collapsed;
        BorderBrush = null;
        Background = null;
        border.SetResourceReference(Border.BackgroundProperty, "DamageOverlayBackgroundBrush");
        _updateTimer.Start();
        Loaded += (_, _) => RequestMeterUpdate(true);

        /*
         * Ask for a pass the moment a derived meter is opened. The board reads a snapshot, not the live pipeline, so
         * without this the first thing a user sees after enabling it is an empty window until the cadence happens to
         * fire (up to a few seconds, longer if a bulk load is still running and quiescence has not arrived). One extra
         * pass on an open click is nothing; an empty board that looks broken is not nothing.
         */
        DeriveEngine.Active?.RederiveAsync();

        // Derive and timer asks share one gate; the timer still pays expiration when no new pass is published.
        // Follow captures rather than retaining the engine of a log that has closed.
        DeriveEngine.ActiveChanged += FollowActiveSession;
        FollowActiveSession();
      }
    }

    private DeriveEngine _derivedFrom;

    // Re/listens to whichever session is current. Detached in WindowClosing: ActiveChanged is static and outlives the window.
    private void FollowActiveSession()
    {
      if (!Dispatcher.CheckAccess())
      {
        Dispatcher.BeginInvoke(FollowActiveSession);
        return;
      }
      if (_closed) return;
      var session = DeriveEngine.Active;
      if (ReferenceEquals(session, _derivedFrom)) return;
      _displayVersion++;
      _buildCancellation?.Cancel();
      if (_buildCancellation is null) _calculation.Clear();
      HideMeterPresentation();

      if (_derivedFrom is not null) _derivedFrom.Derived -= OnDerived;
      _derivedFrom = session;

      // Subscribing is not Reset. The lifecycle distinguishes a reopened window from a new capture by SessionId.
      if (session is not null) session.Derived += OnDerived;
      if (IsLoaded) RequestMeterUpdate(true);
    }

    // Derived runs on a worker. Only the coalesced request is dispatched here; accounting stays off the UI thread.
    private void OnDerived(DerivedSnapshot snapshot)
    {
      // A pass from a capture that is no longer open must not wake this board (Dispose does not join a running pass);
      // the repaint below would then read whichever session is current and paint a beat later than it should.
      if (!snapshot.FromLiveSession) return;

      var dispatcher = Dispatcher;
      if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

      dispatcher.BeginInvoke(new Action(RepaintAfterDerive));
    }

    private void RepaintAfterDerive()
    {
      if (_preview || !IsLoaded || _closed) return;
      RequestMeterUpdate();
    }

    // staged-config working set (mirrors _saved* until the setup window restages it) and its companion window
    private DamageMeterSettingsWindow _meterSettings;
    private int _currentFontSize = 12;
    private bool _currentMiniBars;
    private MeterColumn _currentColumns = MeterColumns.Default;
    private bool _currentStreamerMode;
    private int _currentMaxRows = 5;
    private string _currentProgressColor = "#FF1D397E";
    private string _currentHighlightColor = "Gold";

    // Staged configuration API for DamageMeterSettingsWindow: preview applies a whole state without touching the ini,
    // commit previews then persists (the same keys the inline panel used to write), discard simply ends configure --
    // the next live meter reads the saved values, so discarding needs no undo writes.
    // Geometry is not staged: dragging and resizing the stage is the geometry editor, and commit captures whatever
    // rectangle the stage ended on.

    internal void PreviewMeterState(DamageMeterConfigState s)
    {
      // Only content geometry earns a refit; unrelated previews preserve a manually resized stage.
      var refit = s.FontSize != _currentFontSize || s.MiniBars != _currentMiniBars
        || s.MaxRows != _currentMaxRows || s.Panels != _panels;

      UpdateFontSize(s.FontSize);
      UpdateDamageMode(s.DamageResetMode);

      UpdatePanels(s.Panels);
      UpdateSelectedClass(s.SelectedClass);
      UpdateHideOthers(s.HideOtherPlayers);
      UpdateShowCritRate(s.CritRateDisplay);
      _currentStreamerMode = s.StreamerMode;
      UpdateColumns(s.Columns);
      UpdateMiniBars(s.MiniBars);
      UpdateProgressBrush(s.ProgressColor);
      UpdateHighlightBrush(s.HighlightColor);

      // The bar pool rebuilds only when the row count actually moved: a color-picker drag would otherwise recreate
      // and reseed every DamageBar on each intermediate stop.
      if (s.MaxRows != _currentMaxRows)
      {
        UpdateMaxRows(s.MaxRows);
      }

      if (refit)
      {
        AdjustHeight();
      }
    }

    // Refit after content geometry changes, behind layout so the newly visible panel has been measured.
    // Live meters preserve the saved row-space budget, with their toolbar reserve accounted for separately.
    private void AdjustHeight()
    {
      if (!_preview)
      {
        return;
      }

      Dispatcher.InvokeAsync(() =>
      {
        // Whichever panel is on screen decides how tall the stage is: a Tanking-only preview has no visible damage rows to measure.
        var content = tankContent.Visibility == Visibility.Visible ? tankContent : damageContent;
        var needed = content.ActualHeight + 8 + controlPanel.Margin.Bottom;
        if (!needed.Equals(Height))
        {
          Height = needed;
        }
      }, DispatcherPriority.Background);
    }

    internal void CommitMeterState(DamageMeterConfigState s)
    {
      PreviewMeterState(s);

      var calcHeight = GetOverlayHeight();
      ConfigUtil.SetSetting("OverlayHeight", calcHeight);
      ConfigUtil.SetSetting("OverlayWidth", Width);

      ConfigUtil.SetSetting("OverlayTop", Top);
      ConfigUtil.SetSetting("OverlayLeft", Left);

      ConfigUtil.SetSetting("OverlayFontSize", (double)s.FontSize);
      _savedFontSize = s.FontSize;

      ConfigUtil.SetSetting("OverlayDamageMode", s.DamageResetMode);
      _savedDamageMode = s.DamageResetMode;

      ConfigUtil.SetSetting("OverlaySelectedClass", s.SelectedClass);
      _savedSelectedClass = s.SelectedClass;

      ConfigUtil.SetSetting("OverlayHideOtherPlayers", s.HideOtherPlayers);
      _savedHideOthers = s.HideOtherPlayers;

      ConfigUtil.SetSetting("OverlayEnableCritRate", s.CritRateDisplay);
      _savedShowCritRate = s.CritRateDisplay;

      // Save the preview's surviving tab as well as its panel choice; Cancel writes neither.
      ConfigUtil.SetSetting("OverlayMeterPanels", (int)s.Panels);
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);

      ConfigUtil.SetSetting("OverlayMiniBars", s.MiniBars);
      _savedMiniBars = s.MiniBars;

      MeterColumns.Save(s.Columns);

      ConfigUtil.SetSetting("OverlayStreamerMode", s.StreamerMode);
      _savedStreamerMode = s.StreamerMode;

      ConfigUtil.SetSetting("OverlayMaxRows", s.MaxRows);
      _savedMaxRows = s.MaxRows;
      _currentMaxRows = s.MaxRows;

      ConfigUtil.SetSetting("OverlayRankColor", s.ProgressColor);
      _savedProgressColor = s.ProgressColor;

      ConfigUtil.SetSetting("OverlayHighlightColor", s.HighlightColor);
      _savedHighlightColor = s.HighlightColor;

      MainActions.CloseDamageOverlay(false, this);
    }

    internal void DiscardMeterSettings() => MainActions.CloseDamageOverlay(false, this);

    private MeterBuildResult BuildMeterUpdate(DeriveEngine session, DamageOverlayLifecycle lifecycle,
      int damageMode, MeterPanels panels, CancellationToken cancellation, Action<double> publishExpiry)
    {
      if (session is null)
      {
        if (!_meterWarned)
        {
          _meterWarned = true;
          Log.Warn("Damage overlay: no capture is running; waiting for a log to open.");
        }
        return MeterBuildResult.Completed();
      }
      if (session.SessionId != (DeriveEngine.Active?.SessionId ?? 0)) return MeterBuildResult.Completed();
      _meterWarned = false;

      try
      {
        var now = MeterArrivals.Now;
        return session.BuildOverlayStats(lifecycle, _calculation, damageMode, panels, now, cancellation, publishExpiry);
      }
      catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        /*
         * No legacy substitute here either: a derived board that quietly became a legacy board is the one outcome worse
         * than an empty one, because it is indistinguishable from correct numbers. So the board blanks and the failure
         * says so — first time immediately, then every 30th tick with the running count so a fault that repeats at timer
         * rate cannot bury the log while still being visible as a growing number.
         */
        _meterFailures++;
        if (_meterFailures == 1 || _meterFailures % 30 == 0)
        {
          Log.Error($"Damage meter: derived build failed ({_meterFailures} time(s)); the board is empty until it works",
            ex);
        }

        return MeterBuildResult.Completed();
      }
    }

    private void UpdateTimerTick(object sender, EventArgs e) => RequestMeterUpdate();

    private void RequestMeterUpdate(bool force = false)
    {
      if (_closed || _preview) return;
      if (!ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Close();
        return;
      }
      // This tick still runs when the refresh gate is occupied by a blocked worker.
      if (_stats is not null && !_presentation.IsFresh(DeriveEngine.Active?.SessionId ?? 0, _displayVersion,
        MeterArrivals.Now)) HideMeterPresentation();
      if (_refreshGate.Request(_refreshClock.Elapsed.TotalSeconds, force)) _ = RunMeterUpdatesAsync();
    }

    private async Task RunMeterUpdatesAsync()
    {
      var deferred = false;
      do
      {
        deferred = false;
        try
        {
          var session = DeriveEngine.Active;
          var sessionId = session?.SessionId ?? 0;
          if (_calculationSession != sessionId)
          {
            _calculation.Clear();
            _calculationSession = sessionId;
          }
          var displayVersion = _displayVersion;
          var damageMode = _currentDamageMode;

          // Carried into the worker as plain data, like every other UI-side value a build reads.
          var panels = _panels;
          var checkpoint = MeterLifecycle.Read(_lifecycleOwner);
          _buildCancellation = new CancellationTokenSource();
          var cancellation = _buildCancellation.Token;
          var result = await Task.Run(() => BuildMeterUpdate(session, checkpoint.State, damageMode, panels, cancellation,
            expiry => _presentation.Publish(sessionId, displayVersion, expiry)));
          deferred = result.IsDeferred;
          if (_closed || displayVersion != _displayVersion
            || sessionId != (DeriveEngine.Active?.SessionId ?? 0)) continue;
          if (!MeterLifecycle.TryCommit(checkpoint, result)) continue;
          _stats = result.Stats;
          ApplyMeterUpdate(result.Stats);
        }
        catch (OperationCanceledException)
        {
          // Reset, capture change, and close supersede work without blocking the dispatcher.
        }
        catch (Exception ex)
        {
          if (!_closed) HideMeterPresentation();
          Log.Error("Damage overlay update failed", ex);
        }
        finally
        {
          _buildCancellation?.Dispose();
          _buildCancellation = null;
        }
      } while (!_closed && _refreshGate.Complete(_refreshClock.Elapsed.TotalSeconds, deferred));
      if (_closed || _calculationSession != (DeriveEngine.Active?.SessionId ?? 0)) _calculation.Clear();
    }

    /*
     * Hand the reveal to the paint: a meter that opens itself (after a load, on resume, on fresh combat) starts transparent and raises its
     * own opacity on the first paint that actually drew rows, so an empty frame is never what the player sees. An answer with no rows hides
     * the window rather than leaving it ghost-visible, and this request stays armed, so the meter appears by itself on the next real pull;
     * a meter the operator opened from the menu is revealed immediately by its caller instead.
     */
    internal void RevealOnFirstContent() => _revealOnPaint = true;

    private bool _revealOnPaint;

    // Pulse immediately, then alternate 1 s of colour with 4.5 s at rest; board answers retire the reason, hides retire it immediately.
    // docs/DesignNotes.md → "Which panels the meter shows decides what may wake it and what is worth displaying".
    private DispatcherTimer _attentionTimer;
    private bool _attentionIsOn;

    private void SetTabAttention(bool needed)
    {
      if (_preview) return;

      if (!needed)
      {
        _attentionTimer?.Stop();
        if (!_attentionIsOn) return;
        _attentionIsOn = false;
        _toolbarWindow?.SetTabAttention(false);
        return;
      }

      _attentionTimer ??= UiUtil.CreateTimer((_, _) => AttentionTick(), AttentionWaitMs, false);
      if (_attentionTimer.IsEnabled) return;
      _attentionTimer.Start();
      AttentionTick(); // The first pulse does not wait for the next timer tick.
    }

    private void AttentionTick()
    {
      _attentionIsOn = !_attentionIsOn;
      _toolbarWindow?.SetTabAttention(_attentionIsOn);
      if (_attentionTimer is not null)
        _attentionTimer.Interval = TimeSpan.FromMilliseconds(_attentionIsOn ? AttentionHoldMs : AttentionWaitMs);
    }

    private const int AttentionWaitMs = 4500;
    private const int AttentionHoldMs = 1000;

    private void ApplyMeterUpdate(DamageOverlayStats damageOverlayStats)
    {
      try
      {
        _presentation.Paint(() => ApplyMeterUpdateCore(damageOverlayStats), HideMeterPresentation);
      }
      catch (Exception ex)
      {
        Log.Error("Damage overlay update failed", ex);
      }
    }

    private void ApplyMeterUpdateCore(DamageOverlayStats damageOverlayStats)
    {
      if (damageOverlayStats != null)
      {
        if (!_presentation.IsFresh(DeriveEngine.Active?.SessionId ?? 0, _displayVersion,
          MeterArrivals.Now))
        {
          HideMeterPresentation();
          return;
        }
        var currentTicks = DateTime.UtcNow.Ticks;
        if (_lastTopTicks == long.MinValue || (currentTicks - _lastTopTicks) > TopTimeout)
        {
          Topmost = true;
          _lastTopTicks = currentTicks;
        }

        /*
         * Three separate things, and none of them moves the operator to another half -- they said plainly that tabs do not auto switch; you
         * clicked Tanking if you wanted tanking. WHICH TABS EXIST is the setting's business alone. WHAT IS DRAWN is the tab that was picked,
         * or the single half a one-sided meter has. And the WINDOW exists because at least one half the operator ENABLED has a row to list:
         * with Both on, an empty board in front of them while the other half is busy is legitimate (the tab is right there and clicking it is
         * their call, nudged by `SetTabAttention`), but with one half selected and nothing in it there is no reason for a meter at all, so
         * `HideAndReask` asks the activity receipts whether to close. What each half may show comes from `_panels`, so an activity lane that was
         * switched off cannot keep the window open either. A listed entry counts even without its own fight-list row. A missing half clears its
         * old bars: a slain tanking fight must not remain beside a new damage-only fight.
         */
        var damageRows = LoadStats(damageContent.Children, damageOverlayStats.DamageStats,
          damageOverlayStats.DamageRdps) > 0;
        var tankRows = LoadStats(tankContent.Children, damageOverlayStats.TankStats,
          damageOverlayStats.TankRdps) > 0;

        if (!AnyPanelHasEntries(HasRowEntries(damageOverlayStats.DamageStats), HasRowEntries(damageOverlayStats.TankStats), _panels))
        {
          HideAndReask();
          return;
        }

        if (Visibility != Visibility.Visible)
        {
          Visibility = Visibility.Visible;
        }

        var drawsDamage = DrawsDamagePanel(_panels, _currentShowDps);
        damageContent.Visibility = drawsDamage ? Visibility.Visible : Visibility.Collapsed;
        tankContent.Visibility = drawsDamage ? Visibility.Collapsed : Visibility.Visible;

        // The half NOT on screen lists somebody while the half on screen lists nobody: nudge that tab quietly every few seconds instead of
        // moving anyone. These counts obey the class filter, which is the whole point -- it is a nudge about what the operator would SEE.
        SetTabAttention(NeedsTabAttention(_panels, _currentShowDps, damageRows, tankRows));

        if (_revealOnPaint)
        {
          _revealOnPaint = false;
          Opacity = 1.0;
        }

      }
      else
      {
        HideAndReask();
      }
    }

    /*
     * The window's right to exist: at least one of the halves the operator ENABLED has a board that lists anyone. An entry is any listed row --
     * a player, an ``X +Pets`` group, a mercenary, or the source-less Reverse DS bucket; what matters is that something would be listed here, not
     * that it is a person or that it opened a fight-list row. A half that was switched off earns the meter nothing (that was the ask: a
     * Damage-only meter is neither popped open nor held up by tanking), and both halves empty means no meter anywhere -- which is where the
     * receipts get asked (HideAndReask), because rows do not age and only the quiet-period clock can time a close.
     * docs/DesignNotes.md -> "Which panels the meter shows decides what may wake it and what is worth displaying".
     */
    internal static bool AnyPanelHasEntries(bool damageHasEntries, bool tankHasEntries, MeterPanels panels) =>
      (damageHasEntries && (panels & MeterPanels.Damage) != 0) || (tankHasEntries && (panels & MeterPanels.Tanking) != 0);

    /*
     * Whether this lane's board lists anyone, read BEFORE the class filter and the row cap. The filter is a lens on the fight, not a fact about
     * it: filtering to a class that has done nothing yet must leave the meter standing with an empty board (what it did for years) rather than
     * deleting the window out from under somebody who forgot the lens was on -- and an empty-looking meter they can fix by clearing the filter
     * tells them something, while no meter at all reads as a broken overlay. The bars and the tab nudge still obey the filter, because those
     * are about what the operator sees; only EXISTENCE ignores it.
     */
    private static bool HasRowEntries(CombinedStats stats) => stats is { StatsList.Count: > 0 };

    /*
     * Which half is drawn: the operator's pick when both halves exist, otherwise the only half there is. Nothing here ever moves them to the
     * other tab because that one has data -- `ShowDamage` and `ShowTank` are the only thing that changes what is on screen.
     */
    internal static bool DrawsDamagePanel(MeterPanels panels, bool showingDps) =>
      (panels & MeterPanels.Tanking) == 0 || ((panels & MeterPanels.Damage) != 0 && showingDps);

    // Counts describe the filtered bars; a disabled lane never has a tab to nudge.
    internal static bool NeedsTabAttention(MeterPanels panels, bool showingDps, bool damageHasRows, bool tankHasRows)
    {
      var drawsDamage = DrawsDamagePanel(panels, showingDps);
      var hiddenHalfEnabled = drawsDamage ? (panels & MeterPanels.Tanking) != 0 : (panels & MeterPanels.Damage) != 0;
      return hiddenHalfEnabled && (drawsDamage ? !damageHasRows && tankHasRows : !tankHasRows && damageHasRows);
    }

    // Hide now; whether this window still has a reason to exist is the receipts' answer, asked with THIS window's panels. An explicit
    // Reset can hide a still-active fight and must keep polling it, which is why this asks rather than closes -- and a lane the operator
    // switched off must stop holding the window open right then, not after the next derive.
    private void HideAndReask()
    {
      HideMeterPresentation();
      if (!DerivedMeter.HasActivity(_currentDamageMode, _panels)) MainActions.CloseDamageOverlay(false, this);
    }

    private void HideMeterPresentation()
    {
      // Hide both HWNDs before touching individual bars: even a failed paint must not strand the toolbar.
      controlPanel.Visibility = Visibility.Collapsed;
      HideToolbarWindow();
      Visibility = Visibility.Collapsed;
      _stats = null;
      _presentation.Clear();
      SetTabAttention(false);
      damageContent.Visibility = Visibility.Collapsed;
      tankContent.Visibility = Visibility.Collapsed;
      ClearStats(damageContent.Children);
      ClearStats(tankContent.Children);
    }

    private static void ClearStats(UIElementCollection children)
    {
      foreach (var child in children)
      {
        if (child is DamageBar bar) bar.Visibility = Visibility.Collapsed;
      }
    }

    /*
     * `rates` is the detached six-second answer for THIS build's rows, keyed by the same row names as `localStats`
     * (pet folding included) and formatted here and nowhere else. The window rides log time end to end, so the UI holds no
     * clock over these numbers: a value stands still while no new lines arrive, exactly like the DPS beside it.
     */
    // Returns how many PLAYER rows this panel actually lists under the class filter and row cap (the title bar is not one): the
    // nudge reads this, while whether the window exists at all ignores the lens and asks `HasRowEntries`.
    private int LoadStats(UIElementCollection children, CombinedStats localStats, MeterDirectionRates rates)
    {
      if (localStats is null)
      {
        ClearStats(children);
        return 0;
      }

      rates ??= MeterDirectionRates.None;

      /*
       * The display step the legacy overlay did inside its tally loop (class filter + the local player's slot), restored over the
       * derived board. `children.Count - 1` is how many PLAYER bars exist — the title bar is not one. It runs over both panels,
       * exactly as the legacy build applied it to damage and tanking alike.
       */
      var shown = MeterDisplaySelection.SelectRows(localStats.StatsList, _currentSelectedClass, children.Count - 1, ConfigUtil.PlayerName);

      for (var i = 0; i < children.Count; i++)
      {
        var statIndex = i;
        var damageBar = children[i] as DamageBar;
        if (shown.Count > statIndex)
        {
          var stat = shown[statIndex];
          var barPercent = MeterDisplaySelection.Percentage(stat.Total, shown[0].Total);

          var isMe = MeterDisplaySelection.IsPlayerName(stat.Name, ConfigUtil.PlayerName);

          string name;
          string className;
          if (_currentHideOthers && !isMe)
          {
            name = $"{stat.Rank}. Hidden Player";
            className = "";
          }
          else
          {
            name = $"{stat.Rank}. {stat.Name}";
            className = stat.ClassName;
          }

          // A resource KEY, not a colour: "my own row" is drawn with the meter's highlight brush, whichever value it currently publishes.
          var overrideColorKey = isMe ? "DamageOverlayHighlightBrush" : null;

          if (_currentShowCritRate > 0)
          {
            var critMods = new List<string>();
            if (_currentShowCritRate is 1 or 3 && isMe && AdpsTracker.Instance.MyDoTCritRateMod is var doTCritRate and > 0)
            {
              critMods.Add($"DoT +{doTCritRate}");
            }

            if (_currentShowCritRate is 2 or 3 && isMe && AdpsTracker.Instance.MyNukeCritRateMod is var nukeCritRate and > 0)
            {
              critMods.Add($"Nuke +{nukeCritRate}");
            }

            if (critMods.Count > 0)
            {
              name = $"{name}  [{string.Join(", ", critMods)}]";
            }
          }

          var percent = Math.Round(MeterDisplaySelection.Percentage(stat.Total, localStats.RaidStats.Total), 1);
          damageBar?.Update(name, className, $"{percent}%", StatsUtil.FormatTotals(stat.Total),
            StatsUtil.FormatTotals(stat.Dps, 1), rdpsValue: StatsUtil.FormatTotals(rates.RateFor(stat.Name), 1),
            sdpsValue: StatsUtil.FormatTotals(stat.Sdps, 1),
            timeValue: stat.TotalSeconds.ToString(CultureInfo.InvariantCulture), barPercent: barPercent,
            colorOverrideKey: overrideColorKey);

          if (damageBar?.Visibility == Visibility.Collapsed)
          {
            damageBar.Visibility = Visibility.Visible;
          }
        }
        else
        {
          if (damageBar?.Visibility == Visibility.Visible)
          {
            damageBar.Update("", "", "", "", "", "", "", "", 0);
            damageBar.Visibility = Visibility.Collapsed;
          }
        }
      }

      var titleBar = children[^1] as DamageBar;
      titleBar?.Update(localStats.TargetTitle, "", "", StatsUtil.FormatTotals(localStats.RaidStats.Total),
        StatsUtil.FormatTotals(localStats.RaidStats.Dps, 1), rdpsValue: StatsUtil.FormatTotals(rates.Total, 1),

        // The raid's own sustained rate is its average — the raid divides by nothing but its own span — so this cell repeats DPS by definition
        // rather than printing the zero RaidStats.Sdps never gets assigned. An empty cell would read as a column that failed to fill.
        sdpsValue: StatsUtil.FormatTotals(localStats.RaidStats.Dps, 1),
        timeValue: localStats.RaidStats.TotalSeconds.ToString(CultureInfo.InvariantCulture), barPercent: 0);

      if (titleBar?.Visibility == Visibility.Collapsed)
      {
        titleBar.Visibility = Visibility.Visible;
      }

      if (controlPanel.Visibility != Visibility.Visible)
      {
        controlPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(ShowToolbarWindow, DispatcherPriority.Loaded);
      }

      return shown.Count;
    }

    private void LoadTestData()
    {
      FillTestData(damageContent.Children);

      // Seed hidden bars too: changing panels can reveal this pool without rebuilding it.
      FillTestData(tankContent.Children);
    }

    private static void FillTestData(UIElementCollection children)
    {
      var classList = EQDataStore.Instance.GetClassList();
      for (var i = 0; i < children.Count - 1; i++)
      {
        if (children[i] is DamageBar { } bar)
        {
          if (i == 0)
          {
            bar.Update("Your Player Name", classList[i], "5.2%", "120.5M", "100.1K", rdpsValue: "96.4K",
              sdpsValue: "92.3K", timeValue: "123", barPercent: 120 - (i * 10), colorOverrideKey: "DamageOverlayHighlightBrush");
          }
          else
          {
            bar.Update(i + 1 + ". Example Player " + i, classList[i], "3.1%", "120.5M", "100.1K", rdpsValue: "72.8K",
              sdpsValue: "58.6K", timeValue: "123", barPercent: 120 - (i * 10));
          }
        }
      }

      (children[^1] as DamageBar)?.Update("Example NPC", "", "", "500.2M", "490.5K",
        rdpsValue: "487.1K", sdpsValue: "465.2K", timeValue: "456", barPercent: 0);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => MainActions.CloseDamageOverlay(false, this);

    private double GetOverlayHeight()
    {
      var pos = heightRectangle.TransformToAncestor(this).Transform(new Point(0, 0));
      return pos.Y - 2 - controlPanel.Margin.Bottom;
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      DragMove();
    }

    private void WindowContentRendered(object sender, EventArgs e)
    {
      // the stage is laid out and parked where it will stay: only now does its companion panel exist, because docking
      // reads the measured rectangle
      if (_preview && _meterSettings is null)
      {
        _meterSettings = new DamageMeterSettingsWindow(this) { Owner = this };
        _meterSettings.Show();
      }
    }

    private void SetWindowSizes(double height, double width, double top, double left)
    {
      // Size guards (keep your existing rules)
      if (width > 0 && width <= SystemParameters.VirtualScreenWidth)
        Width = width;

      if (height > 0 && height <= SystemParameters.VirtualScreenHeight)
        Height = height;

      // Virtual desktop bounds (multi-monitor aware)
      var vLeft = SystemParameters.VirtualScreenLeft;
      var vTop = SystemParameters.VirtualScreenTop;
      var overlapsH = Overlaps(left, width, vLeft, SystemParameters.VirtualScreenWidth);
      var overlapsV = Overlaps(top, height, vTop, SystemParameters.VirtualScreenHeight);

      // Apply positions:
      // - Allow negative (or > right/bottom) if there's any overlap.
      // - If completely offscreen on that axis, snap to 0 (your preference).
      Left = overlapsH ? left : 0;
      Top = overlapsV ? top : 0;

      // Helper: does the proposed rect overlap the virtual screen at all?
      static bool Overlaps(double aStart, double aLen, double bStart, double bLen)
      {
        var aEnd = aStart + aLen;
        var bEnd = bStart + bLen;
        return aLen > 0 && bLen > 0 && aStart < bEnd && aEnd > bStart; // strict overlap
      }
    }

    private void UpdateSelectedClass(string selectedClass)
    {
      _currentSelectedClass = selectedClass;
    }

    /*
     * Which numeric columns the meter draws: rows, totals and their header in one gesture. Columns are presentation only —
     * RDPS keeps being calculated while its column is off, so showing it again asks for no warm-up and no replay — and a column
     * gesture never re-fits the window, rebuilds the bar pool or resets the calculation.
     */
    private void UpdateColumns(MeterColumn columns)
    {
      _currentColumns = columns;
      ApplyColumnLayout();
    }

    // One layout per font/column change feeds the header and both bar pools. MinWidth is the only geometry it moves: hiding a
    // column never narrows a wide window, and one saved under the new floor is coerced up by the floor itself.
    private void ApplyColumnLayout()
    {
      var layout = _columnLayout = new MeterColumnLayout(_currentFontSize, _currentColumns);
      UpdateMinimumWidth();

      SetHeaderCell(headPercent, titlePercent, layout, MeterSlot.Percent);
      SetHeaderCell(headDamage, titleDamage, layout, MeterSlot.Damage);
      SetHeaderCell(headDPS, titleDPS, layout, MeterSlot.Dps);
      SetHeaderCell(headRDPS, titleRDPS, layout, MeterSlot.Rdps);
      SetHeaderCell(headSdps, titleSDPS, layout, MeterSlot.Sdps);
      SetHeaderCell(headTime, titleTime, layout, MeterSlot.Seconds);

      foreach (var child in damageContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.ApplyColumns(layout);
        }
      }

      foreach (var child in tankContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.ApplyColumns(layout);
        }
      }
    }

    /*
     * The window's floor belongs to this method alone — the markup carries no MinWidth, deliberately. A number in XAML is either
     * overridden by the table or contradicts it: this window used to declare 300 while the shipped default columns need 261 at 12 pt
     * and every column switched off needs 141 (MeterColumnLayoutTest pins the latter), so the attribute was a floor nobody ever got to.
     * Lowering it never shrinks a window the operator dragged wide, which is the behaviour the column switches rely on.
     */
    private void UpdateMinimumWidth()
    {
      if (_columnLayout is null) return;

      var toolbarWidth = _toolbarWindow?.ContentWidth ?? 0;
      if (_toolbarWindow is not null
        && PresentationSource.FromVisual(_toolbarWindow)?.CompositionTarget is { } toolbarTarget
        && PresentationSource.FromVisual(this)?.CompositionTarget is { } meterTarget)
      {
        var pixels = toolbarTarget.TransformToDevice.Transform(new Vector(toolbarWidth, 0));
        toolbarWidth = meterTarget.TransformFromDevice.Transform(pixels).X;
      }

      var chrome = BorderThickness.Left + BorderThickness.Right + headerRow.Margin.Left + headerRow.Margin.Right;
      MinWidth = _columnLayout.MinimumWindowWidthFor(toolbarWidth, chrome);
    }

    /*
     * A header word shares its column's width and its digits' right inset — zero when the column is off, which is why hiding takes
     * the word with it. That sharing is what keeps the header true at every font and over any set of columns without a table of
     * hand-tuned margins behind it. The WORD comes from MeterColumns rather than the markup, so each one exists exactly once and the
     * picker row, the ini key and the cell above the digits cannot be renamed separately.
     */
    private static void SetHeaderCell(ColumnDefinition cell, TextBlock title, MeterColumnLayout layout, MeterSlot slot)
    {
      cell.Width = new GridLength(layout.WidthOf(slot));
      title.Text = MeterColumns.HeaderOf(slot);
      title.Visibility = layout.Shows(slot) ? Visibility.Visible : Visibility.Collapsed;
      title.FontSize = layout.HeaderFontPt;
      title.Margin = new Thickness(0, 0, layout.InsetOf(slot), 0);
    }

    private void UpdateMiniBars(bool isChecked)
    {
      var newHeight = 0.0;
      if (isChecked)
      {
        newHeight = 3.0;
      }
      else
      {
        newHeight = _currentFontSize switch
        {
          10 => 19.0,
          12 => 21.0,
          14 => 22.0,
          _ => 24.0
        };
      }

      Application.Current.Resources["DamageOverlayBarHeight"] = newHeight;

      foreach (var child in damageContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetMiniBars(isChecked);
        }
      }

      foreach (var child in tankContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetMiniBars(isChecked);
        }
      }

      _currentMiniBars = isChecked;
    }

    private void UpdateHideOthers(bool isHideOthers)
    {
      _currentHideOthers = isHideOthers;
    }

    private void UpdateShowCritRate(int show)
    {
      _currentShowCritRate = show;
    }

    private void UpdateDamageMode(int damageMode)
    {
      _currentDamageMode = damageMode;
    }

    private void UpdateProgressBrush(string colorString)
    {
      _currentProgressColor = colorString;
      Application.Current.Resources["DamageOverlayProgressBrush"] = UiUtil.GetBrush(colorString);
    }

    private void UpdateHighlightBrush(string colorString)
    {
      _currentHighlightColor = colorString;
      Application.Current.Resources["DamageOverlayHighlightBrush"] = UiUtil.GetBrush(colorString);
    }

    private void UpdateMaxRows(int maxRows)
    {
      List<UIElement> damage = [];
      List<UIElement> tank = [];

      // damage bars
      for (var i = 0; i < maxRows; i++)
      {
        damage.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
        tank.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
      }

      // title bar
      damage.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", false));
      tank.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", false));

      using (Dispatcher.CurrentDispatcher.DisableProcessing())
      {
        damageContent.Children.Clear();
        tankContent.Children.Clear();

        foreach (var element in damage)
        {
          damageContent.Children.Add(element);
        }

        foreach (var element in tank)
        {
          tankContent.Children.Add(element);
        }
      }

      _currentMaxRows = maxRows;

      ApplyColumnLayout();
      UpdateMiniBars(_currentMiniBars);

      if (_preview)
      {
        LoadTestData();
      }
    }

    private void UpdateFontSize(int fontSize)
    {
      var selectedIndex = -1;
      switch (fontSize)
      {
        case 10:
          selectedIndex = 0;
          break;
        case 12:
          selectedIndex = 1;
          break;
        case 14:
          selectedIndex = 2;
          break;
        case 16:
          selectedIndex = 3;
          break;
      }

      if (selectedIndex != -1)
      {
        _currentFontSize = fontSize;
        Application.Current.Resources["DamageOverlayFontSize"] = (double)fontSize;
        UpdateColumnSizes(fontSize);
      }

      UpdateMiniBars(_currentMiniBars);

      _toolbarWindow?.UpdateFontSize(fontSize, controlPanel.Height);
      ScheduleToolbarPosition();
    }

    /*
     * The column widths and insets come from this window's own layout, so a staged preview cannot restyle another meter's columns. Font size, bar height and the
     * class icon stay application resource keys (`DamageOverlayFontSize`, `BarHeight`, `ImageSize`) that `DamageBar` resolves through DynamicResource — that is the app's
     * mechanism for style state, and it is safe here only because one overlay is alive at a time and every constructor republishes these values; the day a second meter
     * can open, they move to this window's own Resources and DamageBar never notices. See docs/DesignNotes.md →
     * "Style state lives in the application resource dictionary".
     */
    private void UpdateColumnSizes(int fontSize)
    {
      controlPanel.Height = fontSize + 6;
      Application.Current.Resources["DamageOverlayImageSize"] = fontSize switch
      {
        10 => 13.0,
        12 => 14.0,
        14 => 15.0,
        _ => 16.0,
      };

      ApplyColumnLayout();
    }

    private void ConfigureClick(object sender, RoutedEventArgs e)
    {
      _updateTimer.Stop();

      HideToolbarWindow();
      Hide();
      MainActions.CloseDamageOverlay(true, this);
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
      var stats = _currentShowDps ? _stats?.DamageStats : _stats?.TankStats;
      if (stats is null) return;
      if (_currentShowDps) MainActions.AddAndCopyDamageParse(stats, stats.StatsList);
      else MainActions.AddAndCopyTankParse(stats, stats.StatsList);
    }

    private void DpsClick(object sender, RoutedEventArgs e) => ShowDamage();

    private void TankClick(object sender, RoutedEventArgs e) => ShowTank();

    private void ShowDamage()
    {
      _currentShowDps = true;
      _toolbarWindow?.SetShowingDps(true);
      if (!_preview) ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      if (_stats is not null) ApplyMeterUpdate(_stats);
    }

    private void ShowTank()
    {
      _currentShowDps = false;
      _toolbarWindow?.SetShowingDps(false);
      if (!_preview) ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      if (_stats is not null) ApplyMeterUpdate(_stats);
    }

    // Narrowing selects the survivor in the staged state; widening keeps that selection.
    private void UpdatePanels(MeterPanels panels)
    {
      _panels = panels;
      _toolbarWindow?.SetAvailableViews(panels);

      // Never none: if the panel being looked at is the one going away, the survivor takes the selection.
      if (_currentShowDps && (panels & MeterPanels.Damage) == 0) ShowTank();
      else if (!_currentShowDps && (panels & MeterPanels.Tanking) == 0) ShowDamage();

      if (_preview)
      {
        /*
         * The configuration stage paints no live board, so it shows its sample rows in whichever panel is on screen; a live meter
         * gets this pair flipped by ApplyMeterUpdateCore off the real answer.
         */
        damageContent.Visibility = _currentShowDps ? Visibility.Visible : Visibility.Collapsed;
        tankContent.Visibility = _currentShowDps ? Visibility.Collapsed : Visibility.Visible;
      }

      // PreviewMeterState owns refitting and only asks when the content geometry changed.
      RequestMeterUpdate(true);
    }

    private void FullResetClick(object sender, RoutedEventArgs e)
    {
      _displayVersion++;
      _buildCancellation?.Cancel();
      var session = DeriveEngine.Active;
      MeterLifecycle.Reset(session?.SessionId ?? 0, session?.MeterCapturedCount ?? 0);
      HideMeterPresentation();
      RequestMeterUpdate(true);
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      ScheduleToolbarPosition();
    }

    private void WindowClosing(object sender, CancelEventArgs e)
    {
      _closed = true;
      _presentation.Clear();
      MainActions.DamageOverlayClosed(this);
      _buildCancellation?.Cancel();
      if (_buildCancellation is null) _calculation.Clear();
      if (_derivedFrom is not null) _derivedFrom.Derived -= OnDerived;
      DeriveEngine.ActiveChanged -= FollowActiveSession;
      _derivedFrom = null;

      _updateTimer?.Stop();
      _attentionTimer?.Stop();
      damageContent.Children.Clear();
      tankContent.Children.Clear();

      if (_toolbarWindow is not null)
      {
        _toolbarWindow.Owner = null;
        _toolbarWindow.Close();
        _toolbarWindow = null;
      }
    }

    private void EnsureToolbarWindow()
    {
      if (_closed || _preview || _toolbarWindow is not null)
      {
        return;
      }

      _toolbarWindow = new DamageOverlayToolbarWindow { Owner = this };
      _toolbarWindow.SizeChanged += WindowSizeChanged;
      _toolbarWindow.ContentSizeChanged += ToolbarContentSizeChanged;

      _toolbarWindow.ConfigureRequested += (_, _) => ConfigureClick(null, null);
      _toolbarWindow.CopyRequested += (_, _) => CopyClick(null, null);
      _toolbarWindow.ResetRequested += (_, _) => FullResetClick(null, null);
      _toolbarWindow.CloseRequested += (_, _) => CloseClick(null, null);
      _toolbarWindow.DpsRequested += (_, _) => DpsClick(null, null);
      _toolbarWindow.TankRequested += (_, _) => TankClick(null, null);

      _toolbarWindow.SetAvailableViews(_panels);
      _toolbarWindow.SetShowingDps(_currentShowDps);
      // The first paint can arm attention before the deferred toolbar creation runs.
      _toolbarWindow.SetTabAttention(_attentionIsOn);
      _toolbarWindow.UpdateFontSize(_savedFontSize, controlPanel.Height);
    }

    private void ToolbarContentSizeChanged(object sender, EventArgs e)
    {
      if (_closed || _preview || _toolbarWindow is null) return;
      UpdateMinimumWidth();
      // Reserve the controls and their inset, not unused space imposed on the companion HWND by Windows.
      var contentHeight = _toolbarWindow.ContentHeight;
      if (!double.IsFinite(contentHeight) || contentHeight <= 0) return;

      var currentHeight = double.IsFinite(Height) ? Height : ActualHeight;
      var adjusted = _toolbarHeaderReserve.Update(currentHeight, controlPanel.Height, contentHeight);
      controlPanel.MinHeight = contentHeight;
      // The header and the window grow together; the excess must not consume the last row or enter OverlayHeight.
      if (!adjusted.Equals(currentHeight)) Height = adjusted;
      ScheduleToolbarPosition();
    }

    private void ShowToolbarWindow()
    {
      if (_closed || _preview || !IsVisible || controlPanel.Visibility != Visibility.Visible)
      {
        return;
      }

      EnsureToolbarWindow();

      if (_toolbarWindow is null)
      {
        return;
      }

      if (!_toolbarWindow.IsVisible)
      {
        _toolbarWindow.Show();
      }

      ScheduleToolbarPosition();
    }

    private void HideToolbarWindow()
    {
      if (_toolbarWindow?.IsVisible == true)
      {
        _toolbarWindow.Hide();
      }
    }

    private void PositionToolbarWindow()
    {
      if (_closed || _preview ||
          _toolbarWindow is null ||
          controlPanel.Visibility != Visibility.Visible ||
          !IsVisible)
      {
        return;
      }

      if (PresentationSource.FromVisual(_toolbarWindow) is not HwndSource toolbarSource ||
          toolbarSource.CompositionTarget == null)
      {
        return;
      }

      var screenPixels = controlPanel.PointToScreen(new Point(0, 0));

      var screenDips =
          toolbarSource.CompositionTarget.TransformFromDevice.Transform(screenPixels);

      _toolbarWindow.Left = screenDips.X;
      _toolbarWindow.Top = screenDips.Y;
      // Moving between DPI scales can change the toolbar's width in meter DIPs without changing its content width.
      UpdateMinimumWidth();
    }

    private void ScheduleToolbarPosition()
    {
      if (_closed || _preview)
      {
        return;
      }

      // Background fires after layout + render are complete,
      // ensuring both windows have settled to their final positions.
      Dispatcher.BeginInvoke(PositionToolbarWindow, DispatcherPriority.Background);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
      base.OnLocationChanged(e);
      ScheduleToolbarPosition();
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

        // set to layered and topmost by xaml
        var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

        if (!_preview)
        {
          // Add transparency and layered styles
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExLayered | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;

          if (!_savedStreamerMode)
          {
            // tool window to not show up in alt-tab
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow;
          }
        }
        else
        {
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;
        }

        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, exStyle);
      }
    }
  }
}

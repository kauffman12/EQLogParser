using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using EQLogParser.Mirror;

namespace EQLogParser
{
  public partial class DamageOverlayWindow
  {
    private const double DamageModeZeroTimeout = TimeSpan.TicksPerSecond * 7; // with 3 second slain queue delay
    private const long TopTimeout = TimeSpan.TicksPerSecond * 2;
    private static readonly object StatsLock = new();
    private static readonly ILog Log = LogManager.GetLogger(typeof(DamageOverlayWindow));
    private static DamageOverlayStatsBuilder _statsBuilder = new();
    private static DamageOverlayStats _stats;
    private readonly DispatcherTimer _updateTimer;
    private readonly bool _preview;
    private Task _lastUpdateTask = Task.CompletedTask;
    private long _lastTopTicks = long.MinValue;
    private int _savedFontSize;
    private int _savedMaxRows;
    private int _currentDamageMode;
    private int _savedDamageMode;

    /*
     * Which engine this window paints comes from ONE dial (`EnableCombatMirror`, read live through MirrorMeter), the same
     * word the fight list docks on - so a mid-run toggle of that icon moves the list and this window together, and an open
     * overlay can never sit on the other engine. Off by default: the legacy tally below stays the shipped path until the
     * derived one has been watched on live pulls, and when the legacy path goes the dial itself goes with it.
     *
     * What moves over is only WHERE the numbers come from: one calculation over the mirrored facts inside a window
     * (MirrorStats), instead of the overlay's own running totals. The meter's policy stays here, because it always was
     * the meter's — `OverlayDamageMode` deciding when a quiet board zeroes itself (0 = on kill, i.e. the engagement gap,
     * otherwise N seconds), and the window starting at the reset. That is why the mirror holds no "current fight": the
     * seconds a board covers is this component's business.
     */
    private bool _mirrorMeterWarned;
    private int _mirrorFailures;

    /*
     * The first second this board adds up, STATIC on purpose. Legacy got "the meter closed with the X and came back where it
     * left off" for free: its running totals lived in `_stats`/`_statsBuilder` (also static) and in FightManager's fights,
     * so a reopened window kept painting an accumulation it had not thrown away. A derived board holds nothing between ticks —
     * it is recomputed from the facts inside these seconds — so if this start lived on the window instance it would be reborn
     * at "now" and every second already spent on the pull would disappear from the numbers. Closing a window is not a reset;
     * only two things legitimately move this: the clear button (explicit) and quiet longer than `OverlayDamageMode` (the
     * meter's own expiry, applied by LiveFights.WindowStartFor below).
     *
     * Nothing runs while no window exists, so an X does not age the start either. A reopen inside the quiet range therefore
     * shows the whole pull, and a reopen after it gets one fresh board — which is the dial deciding, not a special case here.
     */
    private static double _mirrorWindowT = -1;
    private int _currentShowCritRate;
    private int _savedShowCritRate;
    private bool _currentHideOthers;
    private bool _savedHideOthers;
    private bool _savedMiniBars;
    private bool _savedShowDamagePercent;
    private bool _savedStreamerMode;
    private bool _currentShowDps;
    private string _currentSelectedClass;
    private string _savedSelectedClass;
    private string _savedProgressColor;
    private string _savedHighlightColor;
    private DamageOverlayToolbarWindow _toolbarWindow;

    /*
     * This window's two spans on the heartbeat (PerfCounters, UiBeatMonitor). They are reported separately because they run on different
     * threads and fail for different reasons: "build" is the stats rebuild under StatsLock on a pool thread, which goes long when the log
     * reader is holding that lock, while "loadstats" is this window rewriting every bar on the UI thread — the one of the two that can
     * stop the combat numbers, and the reason the meter is instrumented at all.
     */
    private static readonly int BuildId = PerfCounters.Register("meter.build", uiThread: false);
    private static readonly int LoadStatsId = PerfCounters.Register("meter.loadstats");

    internal DamageOverlayWindow(bool preview = false, bool reset = false)
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();

      /* Every stall line says which windows were open, because the meter's once-a-second rebuild is a standing suspect for an overlay
         that froze and recovered by itself — and it keeps rebuilding whether or not anyone can see it. */
      IsVisibleChanged += (_, e) => UiBeatMonitor.NoteSurface("meter", (bool)e.NewValue);

      _preview = preview;

      if (reset)
      {
        _stats = null;
        _statsBuilder = new();
      }

      // dimensions
      var width = ConfigUtil.GetSettingAsDouble("OverlayWidth", 400);
      var height = ConfigUtil.GetSettingAsDouble("OverlayHeight", int.MaxValue);
      var top = ConfigUtil.GetSettingAsDouble("OverlayTop", 20);
      var left = ConfigUtil.GetSettingAsDouble("OverlayLeft", 100);
      SetWindowSizes(height, width, top, left);

      // fonts
      var fontSizeString = ConfigUtil.GetSetting("OverlayFontSize");
      if (fontSizeString == null || !int.TryParse(fontSizeString, out _savedFontSize) || (_savedFontSize != 10 &&
        _savedFontSize != 12 && _savedFontSize != 14 && _savedFontSize != 16))
      {
        _savedFontSize = 12;
      }

      UpdateFontSize(_savedFontSize);

      // color
      _savedProgressColor = ConfigUtil.GetSetting("OverlayRankColor");
      if (_savedProgressColor == null || ColorConverter.ConvertFromString(_savedProgressColor) == null)
      {
        _savedProgressColor = "#FF1D397E";
      }

      UpdateProgressBrush(_savedProgressColor);

      // highlight color
      _savedHighlightColor = ConfigUtil.GetSetting("OverlayHighlightColor");
      if (_savedHighlightColor == null || ColorConverter.ConvertFromString(_savedHighlightColor) == null)
      {
        _savedHighlightColor = _savedProgressColor;
      }

      UpdateHighlightBrush(_savedHighlightColor);

      // Max Rows
      var maxRowsString = ConfigUtil.GetSetting("OverlayMaxRows");
      if (maxRowsString == null || !int.TryParse(maxRowsString, out _savedMaxRows) || _savedMaxRows < 1 || _savedMaxRows > 10)
      {
        _savedMaxRows = 5;
      }

      // damage mode
      _savedDamageMode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");
      if (_savedDamageMode < 0 || _savedDamageMode > 100)
      {
        _savedDamageMode = 0;
      }

      UpdateDamageMode(_savedDamageMode);

      var list = EQDataStore.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);

      // selected class
      var selectedClass = ConfigUtil.GetSetting("OverlaySelectedClass");
      if (selectedClass == null || !list.Contains(selectedClass))
      {
        selectedClass = Resource.ANY_CLASS;
      }

      UpdateSelectedClass(selectedClass);
      _savedSelectedClass = _currentSelectedClass;

      // Hide other player names on overlay
      _savedHideOthers = ConfigUtil.IfSet("OverlayHideOtherPlayers");
      UpdateHideOthers(_savedHideOthers);

      // Hide/Show crit rate
      _savedShowCritRate = ConfigUtil.GetSettingAsInteger("OverlayEnableCritRate");
      UpdateShowCritRate(_savedShowCritRate);

      // Mini bars
      _savedMiniBars = ConfigUtil.IfSet("OverlayMiniBars");
      UpdateMiniBars(_savedMiniBars);

      // Damage Percent
      _savedShowDamagePercent = ConfigUtil.IfSet("OverlayShowDamagePercent");
      UpdateShowDamagePercent(_savedShowDamagePercent);

      UpdateMaxRows(_savedMaxRows);

      // Streamer Mode
      _savedStreamerMode = ConfigUtil.IfSet("OverlayStreamerMode");
      _currentMaxRows = _savedMaxRows;
      _currentStreamerMode = _savedStreamerMode;

      _currentShowDps = ConfigUtil.IfSetOrElse("OverlayShowingDps", true);

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

        /*
         * Ask for a pass the moment a derived meter is opened. The board reads a snapshot, not the live pipeline, so
         * without this the first thing a user sees after enabling it is an empty window until the cadence happens to
         * fire (up to a few seconds, longer if a bulk load is still running and quiescence has not arrived). One extra
         * pass on an open click is nothing; an empty board that looks broken is not nothing.
         */
        if (MirrorMeter.Enabled)
        {
          MirrorSession.Active?.RederiveAsync();

          /*
           * Paint when the pump publishes, not on the next poll. The 1 s tick above is a repaint interval that happens to
           * share its number with the capture's cadence, and stacking it on a pass costs up to a second of visible delay for
           * no reason: `Derived` says exactly when the board changed. The poll stays as the path back for a window that was
           * hidden through a derive, and for the legacy meter, which has nothing to announce.
           *
           * A session belongs to one log-open, so it is listened to through ActiveChanged rather than grabbed once: the
           * overlay can be opened before a session exists, and survives one being swapped under it.
           */
          MirrorSession.ActiveChanged += FollowActiveSession;
          FollowActiveSession();
        }
      }
    }

    private MirrorSession _derivedFrom;

    // Re/listens to whichever session is current. Detached in WindowClosing: ActiveChanged is static and outlives the window.
    private void FollowActiveSession()
    {
      var session = MirrorSession.Active;
      if (ReferenceEquals(session, _derivedFrom)) return;

      if (_derivedFrom is not null) _derivedFrom.Derived -= OnMirrorDerived;
      _derivedFrom = session;

      /*
       * A new capture is a new board: the stored start second describes a log that is gone (legacy got the same reset from
       * ResetOverlayFights on every log open). Reset to null as well, so a later re-enable cannot inherit the previous
       * capture's window start.
       */
      _mirrorWindowT = -1;
      if (session is not null) session.Derived += OnMirrorDerived;
    }

    /*
     * `Derived` is raised by the pump on its own thread, inside the session's gate, and this window belongs to the UI
     * thread — so the repaint is queued there rather than run inline. No priority is specified: default (Normal) lands it
     * behind whatever input is already queued but ahead of the Background poll, which is what a user-visible refresh wants.
     */
    private void OnMirrorDerived(MirrorSnapshot snapshot)
    {
      var dispatcher = Dispatcher;
      if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

      dispatcher.BeginInvoke(new Action(RepaintAfterDerive));
    }

    private void RepaintAfterDerive()
    {
      // A hidden window gets no reason to build a board it will not show; the poll picks it up when it comes back.
      // The tick's own `_lastUpdateTask` guard keeps a derive landing mid-repaint from starting a second one.
      if (_preview || !IsLoaded || !IsVisible) return;

      UpdateTimerTick(null, EventArgs.Empty);
    }

    // staged-config working set (mirrors _saved* until the setup window restages it) and its companion window
    private DamageMeterSettingsWindow _meterSettings;
    private int _currentFontSize = 12;
    private bool _currentMiniBars;
    private bool _currentShowDamagePercent;
    private bool _currentStreamerMode;
    private int _currentMaxRows = 5;
    private string _currentProgressColor = "#FF1D397E";
    private string _currentHighlightColor = "Gold";

    // Staged configuration API for DamageMeterSettingsWindow: preview applies a whole state without touching the ini,
    // commit previews then persists (the same keys the inline panel used to write), discard simply ends configure --
    // MainWindow reopens a live meter that reads the untouched values back off disk, so discarding restores itself.
    // Geometry is not staged: dragging and resizing the stage is the geometry editor, and commit captures whatever
    // rectangle the stage ended on.

    internal void PreviewMeterState(DamageMeterConfigState s)
    {
      // Only these three move the bottom edge; re-fitting on a color-picker stop would snap back a stage somebody
      // resized by hand in between.
      var refit = s.FontSize != _currentFontSize || s.MiniBars != _currentMiniBars || s.MaxRows != _currentMaxRows;

      UpdateFontSize(s.FontSize);
      UpdateDamageMode(s.DamageResetMode);
      UpdateSelectedClass(s.SelectedClass);
      UpdateHideOthers(s.HideOtherPlayers);
      UpdateShowCritRate(s.CritRateDisplay);
      _currentStreamerMode = s.StreamerMode;
      UpdateShowDamagePercent(s.ShowDamagePercent);
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

    // The stage hugs its content while configuring: rows, font size and thin bars all move the bottom edge, so each
    // preview re-fits the window (dispatched behind layout, so it measures the NEW bar heights). Live meters never
    // hear from it — their height is a saved setting and stays exactly where the player left it.
    private void AdjustHeight()
    {
      if (!_preview)
      {
        return;
      }

      Dispatcher.InvokeAsync(() =>
      {
        var needed = damageContent.ActualHeight + 8;
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

      ConfigUtil.SetSetting("OverlayMiniBars", s.MiniBars);
      _savedMiniBars = s.MiniBars;

      ConfigUtil.SetSetting("OverlayShowDamagePercent", s.ShowDamagePercent);
      _savedShowDamagePercent = s.ShowDamagePercent;

      ConfigUtil.SetSetting("OverlayStreamerMode", s.StreamerMode);
      _savedStreamerMode = s.StreamerMode;

      ConfigUtil.SetSetting("OverlayMaxRows", s.MaxRows);
      _savedMaxRows = s.MaxRows;
      _currentMaxRows = s.MaxRows;

      ConfigUtil.SetSetting("OverlayRankColor", s.ProgressColor);
      _savedProgressColor = s.ProgressColor;

      ConfigUtil.SetSetting("OverlayHighlightColor", s.HighlightColor);
      _savedHighlightColor = s.HighlightColor;

      MainActions.CloseDamageOverlay(false);
    }

    internal void DiscardMeterSettings() => MainActions.CloseDamageOverlay(false);

    /*
     * The same board, derived. Window = [reset moment, now]; a row that went quiet for longer than the meter's own
     * timeout expires the board (legacy zeroes it, and the next window starts here rather than at the old reset), which
     * is the display rule DamageMeterConfigState.DamageResetMode always encoded — kept on this side of the seam so the
     * mirror stays free of "what a meter is showing right now".
     *
     * There is no fallback to the legacy tally anywhere on this path. A derived board that quietly became a legacy one
     * is indistinguishable from correct numbers, which is the worst failure a meter can have — so failures blank the
     * board and say so in the log instead.
     */
    private DamageOverlayStats BuildMirrorUpdate()
    {
      var session = MirrorSession.Active;
      if (session is null)
      {
        // Loud, not legacy. The dial means "the derived numbers, or nothing": an empty board plus one line saying why.
        // Falling back to the old tally would put two engines' numbers on one screen with no hint of which one is being
        // read, and the moment the legacy path goes away that fallback becomes a silent blank.
        if (!_mirrorMeterWarned)
        {
          _mirrorMeterWarned = true;
          Log.Warn("Damage meter: EnableCombatMirror is set but no capture is being mirrored, so the board stays "
                   + "empty until a log is opened (clear the setting to use the legacy tally).");
        }

        return null;
      }

      _mirrorMeterWarned = false;

      try
      {
        // The capture's own newest event, never wall time (LiveFights' stated rule): a bulk load runs behind the wall,
        // and an old log must not read as "quiet for hours" against a clock its facts will never catch. NaN until the
        // first fact lands - there is no board to sum then anyway, so answer null without moving the window start.
        var nowT = session.LastEventTime;
        if (double.IsNaN(nowT)) return null;
        var timeout = MirrorMeter.TimeoutFor(_currentDamageMode);

        // Phase 1: the seconds this board covers, from what is stored and nothing else (no lastFactT known before building).
        var fromT = LiveFights.WindowStartFor(_mirrorWindowT, double.NaN, nowT, timeout);
        var update = session.BuildOverlayStats(fromT, nowT, out var lastFactT);

        /*
         * Phase 2: the same rule again, this time with what the capture turned out to hold. Quiet for longer than the board
         * allows (mode > 0 is that many seconds, mode == 0 is the engagement gap) and it zeroes — with the window reopening
         * here, so the next pull counts from now rather than from an old reset. Otherwise the start carries forward, including
         * across this window being closed and reopened.
         */
        _mirrorWindowT = LiveFights.WindowStartFor(_mirrorWindowT, lastFactT, nowT, timeout);
        if (_mirrorWindowT != fromT)
        {
          return null;
        }

        /*
         * Nothing in the window yet: hold what is on screen. A mirror pass runs when the log goes quiet, so a fight in
         * progress can be a few seconds from landing, and flickering an empty board between passes would read as "the
         * raid stopped doing damage". This is also the one place where the derived meter differs in FEEL from the
         * legacy one second-to-second: legacy accumulates per line as they parse, this updates per derive.
         */
        return update ?? _stats;
      }
      catch (Exception ex)
      {
        /*
         * No legacy substitute here either: a derived board that quietly became a legacy board is the one outcome worse
         * than an empty one, because it is indistinguishable from correct numbers. So the board blanks and the failure
         * says so — first time immediately, then every 30th tick with the running count so a fault that repeats at timer
         * rate cannot bury the log while still being visible as a growing number.
         */
        _mirrorFailures++;
        if (_mirrorFailures == 1 || _mirrorFailures % 30 == 0)
        {
          Log.Error($"Damage meter: derived build failed ({_mirrorFailures} time(s)); the board is empty until it works",
            ex);
        }

        return null;
      }
    }

    private async void UpdateTimerTick(object sender, EventArgs e)
    {
      // if turned off
      if (!ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Close();
        return;
      }

      if (!_lastUpdateTask.IsCompleted) return;

      var maxRows = _currentMaxRows;
      DamageOverlayStats damageOverlayStats = null;

      _lastUpdateTask = Task.Run(() =>
      {
        var buildMark = PerfCounters.Begin(BuildId);

        try
        {
          lock (StatsLock)
          {
            damageOverlayStats = _stats;
            var update = MirrorMeter.Enabled ? BuildMirrorUpdate() : _statsBuilder.Build(_stats == null, _currentDamageMode, maxRows, _currentSelectedClass);

            if (update == null)
            {
              if (_stats != null && (_currentDamageMode != 0 || (DateTime.Now.Ticks - _stats.LastUpdateTicks) >= DamageModeZeroTimeout))
              {
                damageOverlayStats = _stats = null;
              }
            }
            else
            {
              update.LastUpdateTicks = DateTime.Now.Ticks;
              damageOverlayStats = _stats = update;
            }
          }
        }
        catch (Exception)
        {
          // ignore for now
        }
        finally
        {
          PerfCounters.End(buildMark);
        }
      });

      await _lastUpdateTask;

      if (damageOverlayStats != null)
      {
        var currentTicks = DateTime.UtcNow.Ticks;
        if (_lastTopTicks == long.MinValue || (currentTicks - _lastTopTicks) > TopTimeout)
        {
          Topmost = true;
          _lastTopTicks = currentTicks;
        }

        /* One span over both lists: the player sees "the meter redrew", and the two calls are the same work on two panels. */
        var loadMark = PerfCounters.Begin(LoadStatsId);

        try
        {
          if (damageOverlayStats.DamageStats is not null)
          {
            LoadStats(damageContent.Children, damageOverlayStats.DamageStats);
          }

          if (damageOverlayStats.TankStats is not null)
          {
            LoadStats(tankContent.Children, damageOverlayStats.TankStats);
          }
        }
        finally
        {
          PerfCounters.End(loadMark);
        }

        if (Visibility != Visibility.Visible)
        {
          Visibility = Visibility.Visible;
        }

        if (_currentShowDps)
        {
          if (tankContent.Visibility != Visibility.Collapsed)
          {
            tankContent.Visibility = Visibility.Collapsed;
          }

          if (damageContent.Visibility != Visibility.Visible)
          {
            damageContent.Visibility = Visibility.Visible;
          }
        }
        else
        {
          if (tankContent.Visibility != Visibility.Visible)
          {
            tankContent.Visibility = Visibility.Visible;
          }

          if (damageContent.Visibility != Visibility.Collapsed)
          {
            damageContent.Visibility = Visibility.Collapsed;
          }
        }
      }
      else
      {
        foreach (var child in damageContent.Children)
        {
          if (child is DamageBar damageBar)
          {
            damageBar.Visibility = Visibility.Collapsed;
          }
        }

        foreach (var child in tankContent.Children)
        {
          if (child is DamageBar damageBar)
          {
            damageBar.Visibility = Visibility.Collapsed;
          }
        }

        damageContent.Visibility = Visibility.Collapsed;
        tankContent.Visibility = Visibility.Collapsed;
        controlPanel.Visibility = Visibility.Collapsed;
        HideToolbarWindow();
        Visibility = Visibility.Collapsed;

        /*
         * Hiding a meter with nothing behind it closes it for real. The two engines are asked the same question in their own
         * terms: legacy kept a set of overlay fights, the mirror asks whether any row is still going inside the window that
         * zeroes this board (LiveFights). One consequence of the second phrasing is worth knowing: on a derived meter a
         * window hidden between pulls closes instead of waiting out the log, and comes back on the next pull through
         * MirrorSession.NewFightObserved rather than lingering invisibly until the app restarts.
         */
        var stillSomethingToShow = MirrorMeter.Enabled ? MirrorMeter.HasLiveFight(_currentDamageMode)
                                                : FightManager.Instance.HasOverlayFights();
        if (!stillSomethingToShow)
        {
          MainActions.CloseDamageOverlay(false);
        }
      }
    }

    private void LoadStats(UIElementCollection children, CombinedStats localStats)
    {
      for (var i = 0; i < children.Count; i++)
      {
        var statIndex = i;
        var damageBar = children[i] as DamageBar;
        if (localStats.StatsList.Count > statIndex)
        {
          var stat = localStats.StatsList[statIndex];
          var barPercent = (statIndex == 0) ? 100.0 : stat.Total / (double)localStats.StatsList[0].Total * 100.0;

          var playerName = ConfigUtil.PlayerName;
          var isMe = !string.IsNullOrEmpty(playerName) && stat.Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
            (playerName.Length >= stat.Name.Length || stat.Name[playerName.Length] == ' ');

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

          var overrideColor = isMe ? "DamageOverlayHighlightBrush" : null;

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

          var percent = (float)Math.Round((float)stat.Total / localStats.RaidStats.Total * 100, 1);
          damageBar?.Update(name, className, $"{percent}%", StatsUtil.FormatTotals(stat.Total),
          StatsUtil.FormatTotals(stat.Dps, 1), stat.TotalSeconds.ToString(CultureInfo.InvariantCulture), barPercent, overrideColor);

          if (damageBar?.Visibility == Visibility.Collapsed)
          {
            damageBar.Visibility = Visibility.Visible;
          }
        }
        else
        {
          if (damageBar?.Visibility == Visibility.Visible)
          {
            damageBar.Update("", "", "", "", "", "", 0);
            damageBar.Visibility = Visibility.Collapsed;
          }
        }
      }

      var titleBar = children[^1] as DamageBar;
      titleBar?.Update(localStats.TargetTitle, "", "", StatsUtil.FormatTotals(localStats.RaidStats.Total),
        StatsUtil.FormatTotals(localStats.RaidStats.Dps, 1), localStats.RaidStats.TotalSeconds.ToString(CultureInfo.InvariantCulture), 0);

      if (titleBar?.Visibility == Visibility.Collapsed)
      {
        titleBar.Visibility = Visibility.Visible;
      }

      if (controlPanel.Visibility != Visibility.Visible)
      {
        controlPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(ShowToolbarWindow, DispatcherPriority.Loaded);
      }
    }

    private void LoadTestData()
    {
      var classList = EQDataStore.Instance.GetClassList();
      for (var i = 0; i < damageContent.Children.Count - 1; i++)
      {
        if (damageContent.Children[i] is DamageBar { } bar)
        {
          if (i == 0)
          {
            bar.Update("Your Player Name", classList[i], "5.2%", "120.5M", "100.1K", "123", 120 - (i * 10), "DamageOverlayHighlightBrush");
          }
          else
          {
            bar.Update(i + 1 + ". Example Player " + i, classList[i], "3.1%", "120.5M", "100.1K", "123", 120 - (i * 10));
          }
        }
      }

      (damageContent.Children[^1] as DamageBar)?.Update("Example NPC", "", "", "500.2M", "490.5K", "456", 0);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => MainActions.CloseDamageOverlay(false);

    private double GetOverlayHeight()
    {
      var pos = heightRectangle.TransformToAncestor(this).Transform(new Point(0, 0));
      return pos.Y - 2;
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

    private void UpdateShowDamagePercent(bool isChecked)
    {
      foreach (var child in damageContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetShowDamagePercent(isChecked);
        }
      }

      foreach (var child in tankContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetShowDamagePercent(isChecked);
        }
      }

      _currentShowDamagePercent = isChecked;
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

      UpdateShowDamagePercent(_currentShowDamagePercent);
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

      _toolbarWindow?.UpdateFontSize(fontSize);
      ScheduleToolbarPosition();
    }

    private void UpdateColumnSizes(int fontSize)
    {
      switch (fontSize)
      {
        case 10:
          Application.Current.Resources["DamageOverlayImageSize"] = 13.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(50.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(40.0);
          titlePercent.Margin = new Thickness(0, 5, 20, 0);
          titleDamage.Margin = new Thickness(0, 5, 18, 0);
          titleDPS.Margin = new Thickness(0, 5, 19, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 11;
          titleDamage.FontSize = 11;
          titleDPS.FontSize = 11;
          titleTime.FontSize = 11;
          controlPanel.Height = 27;
          break;
        case 12:
          Application.Current.Resources["DamageOverlayImageSize"] = 14.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(60.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(45.0);
          titlePercent.Margin = new Thickness(0, 5, 22, 0);
          titleDamage.Margin = new Thickness(0, 5, 25, 0);
          titleDPS.Margin = new Thickness(0, 5, 21, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 13;
          titleDamage.FontSize = 13;
          titleDPS.FontSize = 13;
          titleTime.FontSize = 13;
          controlPanel.Height = 27;
          break;
        case 14:
          Application.Current.Resources["DamageOverlayImageSize"] = 15.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(70.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(50.0);
          titlePercent.Margin = new Thickness(0, 5, 28, 0);
          titleDamage.Margin = new Thickness(0, 5, 33, 0);
          titleDPS.Margin = new Thickness(0, 5, 21, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 15;
          titleDamage.FontSize = 15;
          titleDPS.FontSize = 15;
          titleTime.FontSize = 15;
          controlPanel.Height = 29;
          break;
        case 16:
          Application.Current.Resources["DamageOverlayImageSize"] = 16.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(75.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(55.0);
          titlePercent.Margin = new Thickness(0, 5, 28, 0);
          titleDamage.Margin = new Thickness(0, 5, 32, 0);
          titleDPS.Margin = new Thickness(0, 5, 25, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 17;
          titleDamage.FontSize = 17;
          titleDPS.FontSize = 17;
          titleTime.FontSize = 17;
          controlPanel.Height = 31;
          break;
      }
    }

    private void ConfigureClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        _updateTimer.Stop();
      }

      HideToolbarWindow();
      Hide();
      MainActions.CloseDamageOverlay(true);
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        if (_currentShowDps)
        {
          if (_stats.DamageStats != null)
          {
            MainActions.AddAndCopyDamageParse(_stats.DamageStats, _stats.DamageStats.StatsList);
          }
        }
        else
        {
          if (_stats.TankStats != null)
          {
            MainActions.AddAndCopyTankParse(_stats.TankStats, _stats.TankStats.StatsList);
          }
        }
      }
    }

    private void DpsClick(object sender, RoutedEventArgs e)
    {
      _currentShowDps = true;
      _toolbarWindow?.SetShowingDps(true);
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      UpdateTimerTick(null, null);
    }

    private void TankClick(object sender, RoutedEventArgs e)
    {
      _currentShowDps = false;
      _toolbarWindow?.SetShowingDps(false);
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      UpdateTimerTick(null, null);
    }

    private void FullResetClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        _stats = null;

        /*
         * Each engine resets the way IT keeps a board. The legacy tally needs its builder and FightManager's overlay-fight
         * set thrown away, because those hold the running totals; a derived board holds nothing, so its reset is moving the
         * window's start to the next tick. Touching legacy state on the derived path would be harmless and misleading — and
         * the day the legacy builder goes, "harmless" becomes a NullReference in the reset button.
         */
        if (MirrorMeter.Enabled)
        {
          _mirrorWindowT = -1;
        }
        else
        {
          _statsBuilder = new();
          FightManager.Instance.ResetOverlayFights();
        }
      }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      ScheduleToolbarPosition();
    }

    private void WindowClosing(object sender, CancelEventArgs e)
    {
      if (_derivedFrom is not null) _derivedFrom.Derived -= OnMirrorDerived;
      MirrorSession.ActiveChanged -= FollowActiveSession;
      _derivedFrom = null;

      _updateTimer?.Stop();
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
      if (_preview || _toolbarWindow is not null)
      {
        return;
      }

      _toolbarWindow = new DamageOverlayToolbarWindow { Owner = this };

      _toolbarWindow.ConfigureRequested += (_, _) => ConfigureClick(null, null);
      _toolbarWindow.CopyRequested += (_, _) => CopyClick(null, null);
      _toolbarWindow.ResetRequested += (_, _) => FullResetClick(null, null);
      _toolbarWindow.CloseRequested += (_, _) => CloseClick(null, null);
      _toolbarWindow.DpsRequested += (_, _) => DpsClick(null, null);
      _toolbarWindow.TankRequested += (_, _) => TankClick(null, null);

      _toolbarWindow.SetShowingDps(_currentShowDps);
      _toolbarWindow.UpdateFontSize(_savedFontSize);
    }

    private void ShowToolbarWindow()
    {
      if (_preview || controlPanel.Visibility != Visibility.Visible)
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
      if (_preview ||
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
    }

    private void ScheduleToolbarPosition()
    {
      if (_preview)
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

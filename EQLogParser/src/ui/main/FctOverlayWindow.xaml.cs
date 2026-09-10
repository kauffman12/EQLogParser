using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EQLogParser
{
  /*
   * Production FCT host: topmost, non-activating overlay fed by FctManager's queue (live monitor lines
   * only — historical replay never reaches it). The queue is drained once per painted frame from the
   * canvas's EventsFrame, so a log burst becomes one cross-thread hop instead of one dispatcher item per
   * record. Position, motion style and the click-through lock persist like every other overlay window.
   *
   * Motion and shape are chosen in the settings window rather than on this window, because they are presentation and not
   * information: whichever style is picked, which region a number sits in and which way it travels still say who acted. A
   * change applies to hits spawned afterwards, so trying one during a pull is safe. What a scheme may move with is decided
   * in FctStage.DefaultMotion, which takes the shapes that need non-overlapping regions (parabola, straight) away under
   * bands instead of drawing them across the protected middle strip.
   *
   * It is resizable without being resizeable: Windows gives a transparent, chromeless window no frame to grab, so a band along each
   * edge drags the size and FctResize offers the sizes it settles on. Position and size persist together, and numbers already in
   * flight move with the window rather than staying where the old one was (FctResize.Rescale, called by the canvas).
   *
   * There are two states, and only one of them is a window. **Locked** is how it always opens and how it is played with: no header,
   * no panel, nothing drawn but the numbers, click-through and non-activating (WS_EX_TRANSPARENT + WS_EX_NOACTIVATE — the same recipe
   * TextOverlayWindow/TimerOverlayWindow use). **Configuring** is entered deliberately from the app menu and not from the overlay,
   * because this window sits in the middle of the screen where a permanent settings row would be fighting the player's view of the
   * game; the Damage Meter can carry a toolbar because you park it in a corner. Save finishes configuring and keeps the
   * settings; Cancel finishes without keeping them — the panel's two buttons, clicked like everybody else.
   *
   * Configure mode is deliberately not a stored setting: nothing good comes from a window that reopens having grabbed the mouse, and
   * a state that outlives the session it was meant for turns an overlay into a click-eating rectangle the next time the game starts.
   * Settings are staged while configuring and written only by the Save button; Cancel ends configuring with the previous ones back.
   * Placement is the exception, saved as soon as a drag or resize is released — where you left it is never ambiguous.
   *
   * There are two region schemes (FctStage): halves — one stream per side, the genre standard — and bands — top and bottom
   * around a clear middle strip. In bands, direction of travel is the "who" carrier (mine rise, hits on me sink), so it is not a
   * setting there; in halves, position carries who and each side's up/down is free to choose. Configure mode states whichever is on
   * screen with a four-character legend and nothing else — a sentence about it gets read once and then sits there being clutter.
   */
  public partial class FctOverlayWindow : Window
  {
    /* Which edges a resize drag pulls on. The XAML bands name themselves with Tag so the grip stays visible in the markup and
     * the arithmetic lives here once, instead of eight copies of nearly the same handler. */
    [Flags]
    private enum Edge
    {
      None = 0,
      Left = 1,
      Right = 2,
      Top = 4,
      Bottom = 8
    }

    /* The renderer and its counters are the same object now that there is one backend. */
    private readonly FctSkiaCanvas _canvas;
    private readonly List<FctHitCommand> _pending = [];

    /* The parser feed this window opened and closes. Held here rather than reached through FctManager.Instance, so hiding
     * or closing this window can only ever affect the feed this window itself created. */
    private readonly FctManager _manager;

    /*
     * Resize drag state. The running size is kept unsnapped (_resizeFreeW/H) and snapped only when applied, which is what makes
     * the offered sizes a magnet rather than a ratchet: leave the magnet's range and the drag continues from where it was rather
     * than from where it settled. The anchored edge is the one being moved away from, so the far edge cannot creep during a long
     * drag, and deltas are measured in device pixels then divided by the DPI scale because WPF gives DIPs while a moving window
     * makes window-relative positions shift under the pointer.
     */
    private Edge _resizeEdge;
    private Point _resizeLastDevice;
    private double _resizeFreeW, _resizeFreeH, _resizeAnchorRight, _resizeAnchorBottom;

    /*
     * Configure-mode paint, swapped out while locked so playing draws numbers and nothing else. Frozen: shared by every frame.
     * Neutral black glass with a hairline that is neither light nor dark, because this panel has to be readable over a snow zone and
     * over a dungeon corridor alike — the app's own palette has no blue in it, and a steel-blue frame looked like somebody else's
     * overlay pasted on top.
     */
    private static readonly SolidColorBrush PanelBackground = new(Color.FromArgb(0x3A, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush PanelBorder = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private HwndSource _hwndSource;
    private double _lastStatsMs = -1000;

    /* Which mode this window is in: locked means presenting (click-through, nothing drawn but numbers, the settings window
       out of sight), unlocked means configuring (the panel is up and its previews land straight on this canvas). It always
       opens locked — configure is entered from the app menu, never from the overlay itself. */
    private bool _locked;

    /* Settings are staged while configuring and committed by Save: the combos preview live so a style or a layout can be judged
       before it is kept, and leaving configure mode without Save puts back what was on disk. Writing on every change means one
       mis-click silently changes somebody's setup, and there is no Undo next to the control that did it. */
    /* The companion window configure mode lives in now, and the session-only demo switch its checkbox drives. */
    private FctSettingsWindow _settings;
    private bool _sampleData = true;

    /* What is staged while configuring: one settings snapshot rather than twenty-odd fields, because each of those had to be
       listed by hand in four places — read on open, worn on lock, handed to the panel, written at Save — and the show list has
       just grown to seventeen switches. A snapshot makes all four one-liners, so a setting cannot be staged but never applied,
       or saved but never restored: the class of bug that used to need a checklist and a Windows box to notice. */
    private FctConfigState _saved = new();

    // lets the View menu untick the overlay when the window closes
    public event Action EventsClosed;

    // keeps the View menu's Setup item and the window telling the same story
    public event Action<bool> EventsLockChanged;

    public bool Locked => _locked;

    public FctOverlayWindow()
    {
      InitializeComponent();

      _canvas = fctCanvas;

      RestoreSettings();

      // a fresh manager per window: it subscribes to the parsers and unsubscribes when we close, so a closed
      // overlay can never keep queueing hits for a dead window
      _manager = FctManager.Create();

      HookResizeBands();

      /* The demo wants the window's real size to place its numbers, which is not known until the first layout pass. Starting it is
         cheap and idempotent, so ask again after first paint rather than guessing at a size. */
      ContentRendered += (_, _) => RefreshDemo();

      _canvas.EventsFrame += OnCanvasFrame;
      SourceInitialized += OnSourceInitialized;
      IsVisibleChanged += OnVisibleChanged;
      LocationChanged += (_, _) => PositionSettings(); // dragging the overlay carries its settings panel along
      Closed += OnClosed;
    }

    /*
     * Configure mode driven from the View menu (and by the first enable of the feature, which comes in unlocked so the options are seen once) — the only
     * way in while clicks pass through. Entering it activates the window: a player who just asked to move it should get the keyboard and dragging
     * immediately. Leaving it any way other than Save puts the settings back, because abandoning a configuration session is not the same gesture as
     * approving one — Cancel is that exit made visible, and it is the only one: no key closes a configuration session,
     * because a keystroke should not silently discard what a click was willing to name. (Save ends configuring from inside, via ApplyLock.)
     */
    public void SetLocked(bool locked)
    {
      if (!locked)
      {
        ApplyLock(false);
        Activate();
        return;
      }

      Apply(_saved);

      /* A panel left open on a Cancel-shaped exit gets its knobs put back too, ready for next time. Silently: this is us
         filling the controls in, not the player changing anything, so it must not answer with another preview pass. */
      _settings?.LoadFrom(StagedState(), raisePreview: false);

      ApplyLock(true);
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
      _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
      ApplyLock(_locked);
    }

    /*
     * Extended styles for the current lock state, driven through the same NativeMethods constants as the timer,
     * text and toolbar overlays. Layered is what lets a transparent WPF window be hit-tested at all; toolwindow
     * keeps it out of the taskbar and Alt+Tab in both states (it is draggable while configuring, so positioning an
     * overlay never needs an Alt+Tab entry to be reachable). Locked adds transparent — clicks
     * fall through to EverQuest — and no-activate, so showing or moving it never takes focus mid-fight.
     */
    private int CurrentStyles()
    {
      var styles = (int)NativeMethods.GetWindowLongPtr(_hwndSource.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);
      styles |= (int)(NativeMethods.ExtendedWindowStyles.WsExLayered | NativeMethods.ExtendedWindowStyles.WsExToolwindow);

      if (_locked)
      {
        styles |= (int)(NativeMethods.ExtendedWindowStyles.WsExTransparent | NativeMethods.ExtendedWindowStyles.WsExNoActive);
      }
      else
      {
        styles &= ~(int)(NativeMethods.ExtendedWindowStyles.WsExTransparent | NativeMethods.ExtendedWindowStyles.WsExNoActive);
      }

      return styles;
    }

    private void ApplyLock(bool locked)
    {
      _locked = locked;

      // belt-and-braces for frames between the request and the style actually landing
      rootBorder.IsHitTestVisible = !locked;

      /* A click-through window must not offer anything to click, including its own resize bands. */
      resizeLayer.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

      /* The settings window is configure mode now: it comes with the controls and goes without them, so the overlay
         itself is only ever numbers. Hiding rather than closing keeps one instance and its state warm between passes. */
      if (locked)
      {
        _settings?.Hide();
      }

      /* The demo belongs to configure mode: it starts with the controls and stops with them, so a locked overlay over the game shows
         nothing but real numbers. Demo text that outlived setup would be fake damage the player has to learn to ignore — and unlike a
         static example, an un-stopped loop keeps asking for frames, which a window you are fighting in should not spend. */
      if (locked)
      {
        _canvas.StopDemo();
      }
      else
      {
        EnsureSettings();

        /* Every entry into setup starts from the saved values and the examples on — never from an abandoned preview
           (Cancel put those back already), and sample data is a view aid for this pass, not a preference. */
        _settings.LoadFrom(StagedState(), raisePreview: false);
        if (IsVisible)
        {
          AttachSettingsOwner();
          _settings.Show();
          PositionSettings();
        }

        RefreshDemo();
      }

      rootBorder.Background = locked ? Brushes.Transparent : PanelBackground;
      rootBorder.BorderBrush = locked ? Brushes.Transparent : PanelBorder;

      EventsLockChanged?.Invoke(locked);

      if (_hwndSource is not null)
      {
        NativeMethods.SetWindowLong(_hwndSource.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, new IntPtr(CurrentStyles()));
      }
    }

    /* Showing and hiding are a pair of ordered handshakes: the render clock and the parser feed switch on in that order
       and off in reverse, because a hit accepted by an enabled manager with no clock running is lost without being
       counted. An overlay nobody sees must not parse or raster either. */
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
      if ((bool)e.NewValue)
      {
        /* The clock starts before the feed opens. AddHit refuses hits while the canvas has no clock, so a manager enabled
         * first would lose the first seconds of a fight after a re-show without counting one of them as dropped. */
        _canvas.Start();

        /* Coming back on screen while configuring: the demo belongs with the controls, and so does the panel. */
        if (!_locked && _settings is not null)
        {
          AttachSettingsOwner();
          _settings.Show();
          PositionSettings();
        }

        RefreshDemo();
        _manager.Enabled = true;
      }
      else
      {
        /* The other direction of the same rule: gate the feed off first, so nothing arrives in the gap before the clock
         * stops. An overlay nobody sees must not parse or raster either. */
        _manager.Enabled = false;
        _canvas.Stop();
        _settings?.Hide(); // an overlay nobody sees takes its settings window out of sight with it
      }
    }

    /* The size the overlay ships with, named so Reset Position and an unusable stored size land on something that was measured
     * (§6.8) instead of on WPF's own default. These match the Width/Height attributes in the XAML. */
    internal const double DefaultWidth = 800;
    internal const double DefaultHeight = 560;

    private void RestoreSettings()
    {
      var left = ConfigUtil.GetSettingAsDouble(FctOverlaySettings.WindowLeftKey, 0);
      var top = ConfigUtil.GetSettingAsDouble(FctOverlaySettings.WindowTopKey, 0);
      var width = ConfigUtil.GetSettingAsDouble(FctOverlaySettings.WindowWidthKey, 0);
      var height = ConfigUtil.GetSettingAsDouble(FctOverlaySettings.WindowHeightKey, 0);

      /* Restored exactly as saved apart from the layout's floor: a size stored by an older build is raised to what the layout can
         draw in, but never snapped — reopening an overlay should not move it. The one thing that does move it is geometry that has
         no home any more: a monitor that went away leaves the window off-screen with no title bar to drag back, which for a
         chromeless click-through overlay is a feature that silently stopped existing. So a stored position must still have a
         quarter of its area on the desktop, the same rule App uses for the main window — measured here in DIPs against
         SystemParameters.VirtualScreen*, which is what Left/Top are in, rather than Screen.WorkingArea, which is in device pixels
         and drifts at any scaling other than 100%. A negative Left is legitimate (a display left of primary) and only fails this
         test if it genuinely hangs off the edge. */
      WindowStartupLocation = WindowStartupLocation.Manual;

      if (width > FctResize.MinWidth && height > FctResize.MinHeight && OnDesktop(left, top, width, height))
      {
        Left = left;
        Top = top;
        Width = Math.Max(FctResize.MinWidth, width);
        Height = Math.Max(FctResize.MinHeight, height);
      }
      else
      {
        CenterOnWorkArea();
      }

      /* Always locked: the overlay's whole reason to exist is to sit over the game without taking anything from it, so the state
         you have to leave is configure mode, never the other way round. The old FctOverlayLocked key is neither read nor written —
         a stale entry in someone's settings.ini is inert, as retired keys should be. */
      _locked = true;

      /* Everything the panel offers arrives from one call (FctOverlaySettings.LoadConfig): the mode and its shape, the three
         lanes and their directions, the dials, the threshold, the seventeen show switches and the label's seat. Applying it is
         the same call configure mode makes, so there is no second path by which a setting could be honoured at startup and
         ignored later — or, the way these bugs usually go, saved by the panel and never read again. */
      _saved = FctOverlaySettings.LoadConfig();
      Apply(_saved);
    }

    /*
     * Keeps the sample numbers running while configure mode is up, and restarts their cycle on every control change: the next cue lands at the new size
     * and speed, so a dial can be dragged slowly and watched. Idempotent — a canvas that is already running just keeps going (FctDemo does not restart a
     * cycle from here), and a locked or hidden window never asks for it. Nothing happens while the checkbox is off: that is the mode where somebody is
     * placing the overlay in the middle of a fight and wants to see only what is really landing.
     */
    private void RefreshDemo()
    {
      if (!_locked && IsVisible && _sampleData)
      {
        _canvas.StartDemo();
      }
    }

    /* How much of the window has to be on the desktop to count as findable. A quarter, like the main window's check: enough that
       a deliberately overhanging placement survives, small enough that a lost monitor does not. */
    private const double VisibleFraction = 0.25;

    private static bool OnDesktop(double left, double top, double width, double height)
    {
      var desktopLeft = SystemParameters.VirtualScreenLeft;
      var desktopTop = SystemParameters.VirtualScreenTop;
      var overlapWidth = Math.Max(0, Math.Min(left + width, desktopLeft + SystemParameters.VirtualScreenWidth) - Math.Max(left, desktopLeft));
      var overlapHeight = Math.Max(0, Math.Min(top + height, desktopTop + SystemParameters.VirtualScreenHeight) - Math.Max(top, desktopTop));

      return overlapWidth * overlapHeight >= width * height * VisibleFraction;
    }

    private void CenterOnWorkArea()
    {
      var area = SystemParameters.WorkArea;

      Width = DefaultWidth;
      Height = DefaultHeight;
      Left = area.Left + (area.Width - DefaultWidth) / 2;
      Top = area.Top + (area.Height - DefaultHeight) / 2;
    }

    /*
     * "Reset Position": forget the stored geometry so the next overlay built lands on the shipped size, centred. Written as empty
     * strings because that is how this app retires a value (Damage Meter's Reset does the same) and an unreadable number already
     * falls back to the default on the way in. Callers must do this after the window has closed — a closing overlay saves where it
     * was, which would put back exactly what was just cleared.
     */
    internal static void ForgetStoredGeometry()
    {
      ConfigUtil.SetSetting(FctOverlaySettings.WindowLeftKey, "");
      ConfigUtil.SetSetting(FctOverlaySettings.WindowTopKey, "");
      ConfigUtil.SetSetting(FctOverlaySettings.WindowWidthKey, "");
      ConfigUtil.SetSetting(FctOverlaySettings.WindowHeightKey, "");
    }

    private void SaveSettings()
    {
      ConfigUtil.SetSetting(FctOverlaySettings.WindowLeftKey, Left);
      ConfigUtil.SetSetting(FctOverlaySettings.WindowTopKey, Top);
      ConfigUtil.SetSetting(FctOverlaySettings.WindowWidthKey, ActualWidth > 0 ? ActualWidth : Width);
      ConfigUtil.SetSetting(FctOverlaySettings.WindowHeightKey, ActualHeight > 0 ? ActualHeight : Height);
    }

    /*
     * The one feed pump: drain the manager's queue into the canvas. Draining on the render tick instead of
     * per record is what keeps a full-raid burst from becoming thousands of dispatcher items, and FctManager
     * drops whatever aged out while the UI was stalled rather than replaying it.
     */
    private void OnCanvasFrame(double now)
    {
      _manager.DrainTo(_pending);
      foreach (var cmd in _pending)
      {
        _canvas.AddHit(cmd.Lane, cmd.Value, cmd.Source, cmd.Crit, minor: false, periodic: cmd.Periodic, valueText: cmd.ValueText,
          proc: cmd.Proc, row: cmd.Row, special: cmd.Special);
      }

      _pending.Clear();

      if (now - _lastStatsMs < 500)
      {
        return;
      }

      /* Stats are configure-mode furniture too: while locked the row they live in is hidden, so don't build the string. */
      if (_locked)
      {
        _lastStatsMs = now;
        return;
      }

      _lastStatsMs = now;
      var dropped = _canvas.DroppedCount + _manager.DroppedCount;
      var hidden = _canvas.HiddenCount;

      /* Both counters ride along when nonzero because neither loss is silent here: "dropped" is the overlay running out
         of room and "hidden" is the player's own filter doing its job — two different numbers for two different reasons. */
      var stats = $"{_canvas.Fps:0} fps · {_canvas.ActiveCount} live";
      if (dropped > 0)
      {
        stats += $" · {dropped} dropped";
      }

      if (hidden > 0)
      {
        stats += $" · {hidden} hidden";
      }

      /* A third number for a third reason: "filtered" is a row, word or side switched off — the count only ever grows
         when somebody asked for it, which is what makes seeing it there reassuring rather than alarming. */
      var filtered = _canvas.FilteredCount;
      if (filtered > 0)
      {
        stats += $" · {filtered} filtered";
      }

      _settings?.SetStats(stats);
    }

    /*
     * The settings window is configure mode now: everything that used to share the overlay's pixels lives there, and this
     * is the whole relationship — build one, hand it the staged snapshot, park it beside the overlay, and react to what it
     * raises. Owned so the pair stays over the game together; its ✕ behaves like Cancel, and a window closed by any means
     * at all locks the overlay back down. It never touches settings.ini itself: Save hands over a snapshot, and that is
     * the only road to the file.
     */
    private void EnsureSettings()
    {
      if (_settings is not null)
      {
        return;
      }

      /* The owner rule again: WPF wants an owner shown at least once, and configure can legitimately be entered
         while the overlay is still on its way on screen — the panel goes up with it either way (ApplyLock and
         OnVisibleChanged both attach what they show). So creation never touches Owner; whoever shows the panel
         owns it, and the first of those moments that arrives is by definition one where this window has shown. */
      _settings = new FctSettingsWindow();
      AttachSettingsOwner();
      _settings.PreviewChanged += SettingsPreview;
      _settings.Saved += SettingsSaved;
      _settings.Cancelled += () => SetLocked(true);
      _settings.DialReleased += () => _canvas.RestartDemo();
      _settings.Closed += (_, _) =>
      {
        _settings = null;
        if (!_locked)
        {
          SetLocked(true);
        }
      };
    }

    /* Ownership of the panel, claimed only once this window can legally own one: see EnsureSettings. The test is the
       HWND source, not a shown-flag — WPF has no public "has ever been shown" (Hide leaves a window as loadable as
       Show did), and ContentRendered can arrive after Show() returns, which would race an immediate unlock. The
       source exists from SourceInitialized (inside Show) until close, surviving Hide, which is exactly the interval
       WPF's owner rule accepts. Idempotent: every path that raises the panel calls it without asking which one it is. */
    private void AttachSettingsOwner()
    {
      if (_settings is not null && _settings.Owner is null && PresentationSource.FromVisual(this) is not null)
      {
        _settings.Owner = this;
      }
    }

    /* Parked to the left of the overlay — watching numbers on the canvas while the panel sits beside it is what the
       pairing is for — unless the screen's left edge objects, in which case it goes right. A panel somebody dragged
       keeps its spot until configure mode reopens; only a panel nobody moved follows the overlay across the screen. */
    private void PositionSettings()
    {
      if (_settings is null || !_settings.IsVisible || _settings.MovedByUser)
      {
        return;
      }

      const double PanelWidth = 308;
      var left = Left - PanelWidth - 12;
      if (left < SystemParameters.WorkArea.Left)
      {
        left = Math.Min(Left + Width + 12, SystemParameters.WorkArea.Right - PanelWidth);
      }

      _settings.Left = left;
      _settings.Top = Math.Clamp(Top, SystemParameters.WorkArea.Top,
        Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Math.Max(_settings.ActualHeight, 100)));
    }

    /* One apply for every way a configuration becomes visible: opening the overlay, leaving configure mode (which puts back
       what is staged), and previewing a change while configuring it. FctScale keeps its contract — a size lands when the next
       number is styled, a speed when the next spawns — so nothing already in flight is tugged, and the gates decide only what
       comes after them (FctSkiaCanvas.ApplyGates says why that part restarts the sample loop and when it leaves it alone). */
    private void Apply(FctConfigState state)
    {
      _canvas.Layout = state.BuildLayout();
      _canvas.MotionStyle = state.BuildMotion();
      _canvas.LabelSide = state.LabelSide;
      FctScale.Text = FctScale.ClampSize(state.TextScale);
      FctScale.Crit = FctScale.ClampCritSize(state.CritScale);
      FctScale.Time = FctScale.TimeFromSpeed(state.Speed);
      _canvas.ApplyGates(state);
    }

    /* What the panel displays when configure mode (re)opens: the staged values, because a cancelled preview was put back the
       moment it was cancelled. A copy rather than the staged object itself, so the window cannot edit what the overlay is
       running on before somebody presses Save — and one line rather than a list of twenty-five fields that had to agree with
       the loader by luck. */
    private FctConfigState StagedState() => _saved.Clone();

    /* The preview is the whole point of the arrangement: every control change wears the canvas immediately — layout,
       motion, gates, threshold, both dials — and writes nothing. FctScale keeps its contract too (size applies when the
       next number is styled, speed when the next spawns), so dragging a dial never tugs at text already in flight. */
    private void SettingsPreview(FctConfigState state)
    {
      _sampleData = state.SampleData;

      Apply(state);

      if (_sampleData)
      {
        RefreshDemo();
      }
      else
      {
        _canvas.StopDemo();
      }
    }

    /* Save is the only thing that writes: the snapshot becomes the staged truth, settings.ini gets every key the panel
       covers (plus the configured flag that ends first-run configure-on-open), and configure ends locked like every exit
       leaves it. */
    private void SettingsSaved(FctConfigState state)
    {
      /* The snapshot becomes the staged truth — a copy, because both windows hold one and neither may edit the other's — and
         settings.ini gets every key the panel covers in one call, plus the configured flag that ends first-run
         configure-on-open. Configure then ends locked, like every exit leaves it. */
      _saved = state.Clone();

      FctOverlaySettings.SaveConfig(_saved);
      FctOverlaySettings.SaveConfigured();
      SaveSettings();

      SetLocked(true);
    }

    /*
     * Every band does the same three things, so they are wired in one loop rather than with twenty-four XAML attributes. The
     * centre cell is intentionally empty: it is where the numbers live.
     */
    private void HookResizeBands()
    {
      foreach (var child in resizeLayer.Children)
      {
        if (child is not FrameworkElement band)
        {
          continue;
        }

        band.MouseLeftButtonDown += ResizeEdgeDown;
        band.MouseMove += ResizeEdgeMove;
        band.MouseLeftButtonUp += ResizeEdgeUp;
        band.LostMouseCapture += ResizeEdgeUp;
      }
    }

    private static Edge EdgeOf(string name) => name switch
    {
      "l" => Edge.Left,
      "r" => Edge.Right,
      "t" => Edge.Top,
      "b" => Edge.Bottom,
      "tl" => Edge.Top | Edge.Left,
      "tr" => Edge.Top | Edge.Right,
      "bl" => Edge.Bottom | Edge.Left,
      "br" => Edge.Bottom | Edge.Right,
      _ => Edge.None
    };

    private void ResizeEdgeDown(object sender, MouseButtonEventArgs e)
    {
      if (_locked || e.ButtonState != MouseButtonState.Pressed || sender is not FrameworkElement band)
      {
        return;
      }

      var edge = EdgeOf(band.Tag as string);
      if (edge is Edge.None)
      {
        return;
      }

      _resizeEdge = edge;
      _resizeFreeW = ActualWidth;
      _resizeFreeH = ActualHeight;
      _resizeAnchorRight = Left + ActualWidth;
      _resizeAnchorBottom = Top + ActualHeight;
      _resizeLastDevice = PointToScreen(e.GetPosition(this));

      // capture: a fast drag leaves the 12px band immediately, and losing the drag mid-way would leave the overlay half-sized
      ((UIElement)band).CaptureMouse();
      e.Handled = true;
    }

    private void ResizeEdgeMove(object sender, MouseEventArgs e)
    {
      if (_resizeEdge is Edge.None || e.LeftButton != MouseButtonState.Pressed)
      {
        return;
      }

      var at = PointToScreen(e.GetPosition(this));
      var scale = DeviceScale();
      var dx = (at.X - _resizeLastDevice.X) / scale;
      var dy = (at.Y - _resizeLastDevice.Y) / scale;
      _resizeLastDevice = at;

      if ((_resizeEdge & Edge.Right) != 0)
      {
        _resizeFreeW += dx;
      }

      if ((_resizeEdge & Edge.Left) != 0)
      {
        _resizeFreeW -= dx;
      }

      if ((_resizeEdge & Edge.Bottom) != 0)
      {
        _resizeFreeH += dy;
      }

      if ((_resizeEdge & Edge.Top) != 0)
      {
        _resizeFreeH -= dy;
      }

      /* Bounded by the desktop rather than the primary monitor's work area: numbers spread across two displays are legitimate, and
         a window that grew past the primary border could not be shrunk from an edge nobody can reach. VirtualScreen* is in DIPs,
         which is what these properties are in; a true per-monitor bound would need Win32 monitor enumeration for a chromeless
         window and buys nothing when the largest size offered is 980px. */
      FctResize.Fit(_resizeFreeW, _resizeFreeH, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, out var w, out var h);

      Width = w;
      Height = h;

      if ((_resizeEdge & Edge.Left) != 0)
      {
        Left = _resizeAnchorRight - w;
      }

      if ((_resizeEdge & Edge.Top) != 0)
      {
        Top = _resizeAnchorBottom - h;
      }
    }

    private void ResizeEdgeUp(object sender, MouseEventArgs e)
    {
      if (_resizeEdge is Edge.None)
      {
        return;
      }

      _resizeEdge = Edge.None;

      if (sender is UIElement band && band.IsMouseCaptured)
      {
        band.ReleaseMouseCapture();
      }

      SaveSettings();
    }

    private double DeviceScale()
    {
      var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
      return m > 0 ? m : 1.0;
    }

    /*
     * Anywhere that is not a control or a resize band moves the window, which is what "drag it over the cast bar" wants: hunting
     * for a 12-pixel header band with a number floating past it was the annoying version. Controls keep their own clicks because a
     * ComboBox or CheckBox handles the press before it reaches here, and the resize bands mark theirs handled. DragMove throws if
     * no button is actually held (synthetic events, double-fire on some setups), so that is checked rather than assumed.
     */
    private void OverlayDrag(object sender, MouseButtonEventArgs e)
    {
      if (_locked || _resizeEdge is not Edge.None || e.ButtonState != MouseButtonState.Pressed)
      {
        return;
      }

      e.Handled = true;

      DragMove();
      SaveSettings();
    }

    private void OnClosed(object sender, EventArgs e)
    {
      _hwndSource = null;

      /* Same order as hiding, for the same reason: close the feed before the clock goes away, so nothing is accepted into
       * a canvas that will never tick again and lost without being counted. */
      _manager.Enabled = false;
      _canvas.EventsFrame -= OnCanvasFrame;
      _canvas.Stop();

      SaveSettings();
      _manager.Dispose(); // unsubscribes the parsers: nothing may keep feeding a closed window

      EventsClosed?.Invoke();
    }
  }
}
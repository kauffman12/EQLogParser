using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
   * Motion is a combo rather than the old fountain checkbox because there are four styles now (hold, fountain, pulse,
   * spray) and they are presentation, not information: whichever is chosen, band and direction of travel still say who
   * acted. It applies to hits spawned afterwards, so trying a style during a pull is safe.
   *
   * It is resizable without being resizeable: Windows gives a transparent, chromeless window no frame to grab, so a band along each
   * edge drags the size and FctResize offers the sizes it settles on. Position and size persist together, and numbers already in
   * flight move with the window rather than staying where the old one was (FctResize.Rescale, called by the canvas).
   *
   * There are two states, and only one of them is a window. **Locked** is how it always opens and how it is played with: no header,
   * no panel, nothing drawn but the numbers, click-through and non-activating (WS_EX_TRANSPARENT + WS_EX_NOACTIVATE — the same recipe
   * TextOverlayWindow/TimerOverlayWindow use). **Configuring** is entered deliberately from the app menu and not from the overlay,
   * because this window sits in the middle of the screen where a permanent settings row would be fighting the player's view of the
   * game; the Damage Meter can carry a toolbar because you park it in a corner. Save finishes configuring and keeps the settings;
   * Esc finishes without keeping them.
   *
   * Configure mode is deliberately not a stored setting: nothing good comes from a window that reopens having grabbed the mouse, and
   * a state that outlives the session it was meant for turns an overlay into a click-eating rectangle the next time the game starts.
   * Settings are staged while configuring and written only by the Save button; Esc ends configuring with the previous ones back.
   * Placement is the exception, saved as soon as a drag or resize is released — where you left it is never ambiguous.
   *
   * Direction is vertical and the only scheme there is: my hits rise above an empty middle strip, hits on me sink below
   * it. The overlay cannot know where the player's target is on screen, so what makes direction readable is that strip plus
   * the direction of travel, so configure mode states it in two arrows and nothing else; a sentence about it gets read once and then
   * sits there being clutter. The old left/right halves switch is gone — see the FctLayout header for why it could not be made to
   * work with the styles built after it.
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
    private static readonly SolidColorBrush PanelBackground = new(Color.FromArgb(0x73, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush PanelBorder = new(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

    private HwndSource _hwndSource;
    private double _lastStatsMs = -1000;
    private bool _locked;

    /* Settings are staged while configuring and committed by Save: the combo previews live so a style can be judged before it is
       kept, and leaving configure mode without Save puts back what was on disk. Writing on every change means one mis-click
       silently changes somebody's setup, and there is no Undo next to the control that did it. */
    private FctMotionStyle _savedStyle;
    private double _savedTextScale = FctScale.Default;
    private double _savedTimeScale = FctScale.Default;

    /* The header controls fire their change handlers while being initialised; only user edits may write settings. */
    private bool _settingsReady;

    // lets the View menu untick the overlay when the window is closed with Esc
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
      FctManager.Create();

      HookResizeBands();

      /* The demo wants the window's real size to place its numbers, which is not known until the first layout pass. Starting it is
         cheap and idempotent, so ask again after first paint rather than guessing at a size. */
      ContentRendered += (_, _) => RefreshDemo();

      _canvas.EventsFrame += OnCanvasFrame;
      SourceInitialized += OnSourceInitialized;
      IsVisibleChanged += OnVisibleChanged;
      Closed += OnClosed;
    }

    /*
     * Configure mode driven from the Tools menu, the only way in while clicks pass through. Entering it activates the window: a
     * player who just asked to move it should get Esc and dragging immediately. Leaving it without Save puts the settings back —
     * abandoning a configuration session is not the same gesture as approving one. (Save ends it from inside, via ApplyLock.)
     */
    public void SetLocked(bool locked)
    {
      if (!locked)
      {
        ApplyLock(false);
        Activate();
        return;
      }

      _canvas.MotionStyle = _savedStyle;
      SelectMotionOption(_savedStyle);

      FctScale.Text = _savedTextScale;
      FctScale.Time = _savedTimeScale;
      sizeSlider.Value = _savedTextScale;
      timeSlider.Value = _savedTimeScale;

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
     * keeps it out of the taskbar and Alt+Tab in both states (it is draggable while configuring and Esc ends that, so positioning an
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

      /* Hidden rather than collapsed: the header keeps its height, so nothing drawn after it moves when you enter or leave
         configure mode. A settings row that shifts the numbers while you are positioning them would defeat the point. */
      headerGrid.Visibility = locked ? Visibility.Hidden : Visibility.Visible;

      /* The demo belongs to configure mode: it starts with the controls and stops with them, so a locked overlay over the game shows
         nothing but real numbers. Demo text that outlived setup would be fake damage the player has to learn to ignore — and unlike a
         static example, an un-stopped loop keeps asking for frames, which a window you are fighting in should not spend. */
      if (locked)
      {
        _canvas.StopDemo();
      }
      else
      {
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

    /* Canvas stopped and feed gated off while hidden: an overlay nobody sees must not parse or raster. */
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
      if ((bool)e.NewValue)
      {
        FctManager.Instance.Enabled = true;
        _canvas.Start();
        RefreshDemo(); // coming back on screen while configuring: the demo belongs with the controls
      }
      else
      {
        _canvas.Stop();
        FctManager.Instance.Enabled = false;
      }
    }

    /* The size the overlay ships with, named so Reset Position and an unusable stored size land on something that was measured
     * (§6.8) instead of on WPF's own default. These match the Width/Height attributes in the XAML. */
    internal const double DefaultWidth = 800;
    internal const double DefaultHeight = 560;

    private void RestoreSettings()
    {
      var left = ConfigUtil.GetSettingAsDouble("FctOverlayLeft", 0);
      var top = ConfigUtil.GetSettingAsDouble("FctOverlayTop", 0);
      var width = ConfigUtil.GetSettingAsDouble("FctOverlayWidth", 0);
      var height = ConfigUtil.GetSettingAsDouble("FctOverlayHeight", 0);

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

      _savedStyle = FctOverlaySettings.LoadMotion();
      _canvas.MotionStyle = _savedStyle;
      SelectMotionOption(_savedStyle);

      /* The two dials. Assigned before _settingsReady so restoring them cannot look like an edit: the sliders' ValueChanged handlers
         would otherwise rebuild the preview and repaint on a window that is not even shown yet. */
      _savedTextScale = FctOverlaySettings.LoadTextScale();
      _savedTimeScale = FctOverlaySettings.LoadTimeScale();
      FctScale.Text = _savedTextScale;
      FctScale.Time = _savedTimeScale;
      sizeSlider.Value = _savedTextScale;
      timeSlider.Value = _savedTimeScale;

      _settingsReady = true;
      ShowScaleReadouts();
    }

    /* -30 % … +30 %, next to each slider: snapped to 5 % steps, so these are exact and the shipped default reads as exactly nothing. */
    private void ShowScaleReadouts()
    {
      sizeValue.Text = ScaleText(sizeSlider.Value);
      timeValue.Text = ScaleText(timeSlider.Value);
    }

    private static string ScaleText(double scale) => $"{FctScale.Percent(scale):+0;-0;—}%";

    /*
     * Both dials take effect the moment they move - on the demo numbers still to come, and on any real number that lands afterwards - and
     * write nothing. Sizing is applied at style time (FctStyle.ApplyTo) and tempo at spawn (FctIngest), so dragging a slider never tugs at
     * text already in flight; the demo keeps producing events, which is what lets you watch a change arrive instead of imagining it.
     */
    private void ScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
      if (!_settingsReady)
      {
        return;
      }

      FctScale.Text = FctScale.Clamp(sizeSlider.Value);
      FctScale.Time = FctScale.Clamp(timeSlider.Value);
      ShowScaleReadouts();
      RefreshDemo();
    }

    /* Snap-to-tick makes the middle reachable by feel, but a slider dragged near the middle is still worth a certain way back. */
    private void ScaleDoubleClick(object sender, MouseButtonEventArgs e)
    {
      if (sender == sizeSlider)
      {
        sizeSlider.Value = FctScale.Default;
      }
      else if (sender == timeSlider)
      {
        timeSlider.Value = FctScale.Default;
      }
    }

    /*
     * Keeps the demo running while configure mode is up, and restarts its cycle on every control change: the next cue lands at the new
     * size and tempo, so a slider can be dragged slowly and watched. Idempotent — a canvas that is already running just keeps going
     * (FctDemo does not restart a cycle from here), and a locked or hidden window never asks for it.
     */
    private void RefreshDemo()
    {
      if (!_locked && IsVisible)
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
      ConfigUtil.SetSetting("FctOverlayLeft", "");
      ConfigUtil.SetSetting("FctOverlayTop", "");
      ConfigUtil.SetSetting("FctOverlayWidth", "");
      ConfigUtil.SetSetting("FctOverlayHeight", "");
    }

    private void SaveSettings()
    {
      ConfigUtil.SetSetting("FctOverlayLeft", Left);
      ConfigUtil.SetSetting("FctOverlayTop", Top);
      ConfigUtil.SetSetting("FctOverlayWidth", ActualWidth > 0 ? ActualWidth : Width);
      ConfigUtil.SetSetting("FctOverlayHeight", ActualHeight > 0 ? ActualHeight : Height);
    }

    /*
     * The one feed pump: drain the manager's queue into the canvas. Draining on the render tick instead of
     * per record is what keeps a full-raid burst from becoming thousands of dispatcher items, and FctManager
     * drops whatever aged out while the UI was stalled rather than replaying it.
     */
    private void OnCanvasFrame(double now)
    {
      FctManager.Instance.DrainTo(_pending);
      foreach (var cmd in _pending)
      {
        _canvas.AddHit(cmd.Lane, cmd.Value, cmd.Source, cmd.Crit, minor: false, periodic: cmd.Periodic, valueText: cmd.ValueText, proc: cmd.Proc);
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
      var dropped = _canvas.DroppedCount + FctManager.Instance.DroppedCount;
      statsText.Text = dropped > 0
        ? $"{_canvas.Fps:0} fps · {_canvas.ActiveCount} live · {dropped} dropped"
        : $"{_canvas.Fps:0} fps · {_canvas.ActiveCount} live";
    }

    /*
     * A presentation switch, not a per-record one: it takes effect on hits from now on, which is what makes "try each
     * for a minute in a real pull" the way to choose instead of a screenshot.
     */
    private void MotionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_settingsReady || motionCombo.SelectedItem is not ComboBoxItem item)
      {
        return;
      }

      /* Preview only: the next numbers use it so the style can be judged, and nothing reaches settings.ini until Save. */
      _canvas.MotionStyle = FctOverlaySettings.ParseMotion(item.Tag as string);
      RefreshDemo();
    }

    private void SelectMotionOption(FctMotionStyle style)
    {
      var name = FctOverlaySettings.Name(style);

      for (var i = 0; i < motionCombo.Items.Count; i++)
      {
        if (motionCombo.Items[i] is ComboBoxItem item && item.Tag as string == name)
        {
          motionCombo.SelectedIndex = i;
          return;
        }
      }

      motionCombo.SelectedIndex = 0; // an unknown stored name: show the default rather than an empty box
    }

    /*
     * Save is the only thing that writes: the previewed style becomes the setting, the window keeps its place, and configuring
     * ends. There is no "lock" checkbox here any more because that box was never about locking — it was the way out, labelled with
     * a side effect, so a player clicking it to finish was surprised by their mouse being taken away.
     */
    private void SaveClick(object sender, RoutedEventArgs e)
    {
      _savedStyle = _canvas.MotionStyle;
      _savedTextScale = FctScale.Text;
      _savedTimeScale = FctScale.Time;

      FctOverlaySettings.SaveMotion(_savedStyle);
      FctOverlaySettings.SaveTextScale(_savedTextScale);
      FctOverlaySettings.SaveTimeScale(_savedTimeScale);
      SaveSettings();
      ApplyLock(true);
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

    /*
     * Esc ends configure mode without saving — the same thing the menu does when unticked, so one key cannot quietly commit what the
     * preview was only showing off. It does not close the overlay: losing a carefully positioned window to a stray key is the worse
     * failure, and hiding it is a menu action anyway. A locked window is click-through and WS_EX_NOACTIVATE, so in practice it gets
     * no keyboard input at all — which is fine, because locked is where it starts and where Esc leaves you.
     */
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key != Key.Escape || _locked)
      {
        return;
      }

      SetLocked(true);
    }

    private void OnClosed(object sender, EventArgs e)
    {
      _hwndSource = null;

      _canvas.EventsFrame -= OnCanvasFrame;
      _canvas.Stop();

      SaveSettings();
      FctManager.Instance.Enabled = false;
      FctManager.Instance.Dispose(); // unsubscribes the parsers: nothing may keep feeding a closed window

      EventsClosed?.Invoke();
    }
  }
}

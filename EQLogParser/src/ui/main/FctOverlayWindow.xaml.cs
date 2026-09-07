using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

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
   * Locked (the in-game default) means WS_EX_TRANSPARENT + WS_EX_NOACTIVATE: clicks fall through to EverQuest
   * and the overlay stops stealing focus mid-fight — the same recipe TextOverlayWindow/TimerOverlayWindow use.
   * Unlock from the Tools menu, or press Esc while it has focus to close.
   *
   * Direction is vertical and the only scheme there is: my hits rise above an empty middle strip, hits on me sink below
   * it. The overlay cannot know where the player's target is on screen, so what makes direction readable is that strip plus
   * the direction of travel; the hint line states which is which because it is the only documentation a player sees while
   * fighting. The old left/right halves switch is gone — see the FctLayout header for why it could not be made to work with
   * the styles built after it.
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

    private readonly IFctCanvas _canvas;
    private readonly IFctDiagnostics _diagnostics;
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

    private HwndSource _hwndSource;
    private double _lastStatsMs = -1000;
    private bool _locked;

    /* The header controls fire their change handlers while being initialised; only user edits may write settings. */
    private bool _settingsReady;

    // lets the Tools menu item uncheck itself when the window is closed with Esc
    public event Action EventsClosed;

    // keeps the menu's lock item and the in-window checkbox telling the same story
    public event Action<bool> EventsLockChanged;

    public bool Locked => _locked;

    public FctOverlayWindow()
    {
      InitializeComponent();

      _canvas = fctCanvas;
      _diagnostics = fctCanvas;

      RestoreSettings();

      // a fresh manager per window: it subscribes to the parsers and unsubscribes when we close, so a closed
      // overlay can never keep queueing hits for a dead window
      FctManager.Create();

      HookResizeBands();

      _canvas.EventsFrame += OnCanvasFrame;
      SourceInitialized += OnSourceInitialized;
      IsVisibleChanged += OnVisibleChanged;
      Closed += OnClosed;
    }

    /*
     * Lock state driven from the Tools menu (the in-window checkbox is unreachable while clicks pass through).
     * Unlocking activates the window: a user who just asked to move it should get Esc and typing-free dragging.
     */
    public void SetLocked(bool locked)
    {
      ApplyLock(locked);

      if (!locked)
      {
        Activate();
      }
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
      _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
      ApplyLock(_locked);
    }

    /*
     * Extended styles for the current lock state, driven through the same NativeMethods constants as the timer,
     * text and toolbar overlays. Layered is what lets a transparent WPF window be hit-tested at all; toolwindow
     * keeps it out of the taskbar and Alt+Tab in both states (the header is draggable and Esc closes it, so an
     * overlay being positioned does not need an Alt+Tab entry to be reachable). Locked adds transparent — clicks
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

      lockCheck.IsChecked = locked;
      UpdateHint(locked);

      // belt-and-braces for frames between the request and the style actually landing
      rootBorder.IsHitTestVisible = !locked;

      /* A click-through window must not offer anything to click, including its own resize bands. */
      resizeLayer.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

      ConfigUtil.SetSetting("FctOverlayLocked", locked);
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
      }
      else
      {
        _canvas.Stop();
        FctManager.Instance.Enabled = false;
      }
    }

    private void RestoreSettings()
    {
      var left = ConfigUtil.GetSettingAsDouble("FctOverlayLeft", 0);
      var top = ConfigUtil.GetSettingAsDouble("FctOverlayTop", 0);
      var width = ConfigUtil.GetSettingAsDouble("FctOverlayWidth", 0);
      var height = ConfigUtil.GetSettingAsDouble("FctOverlayHeight", 0);

      /* Restored exactly as saved apart from the layout's floor: a size stored by an older build (or a second monitor that has
       * since gone) is raised to what the layout can draw in, but never snapped — reopening an overlay should not move it. */
      if (left > 0 && top >= 0 && width > 100 && height > 100)
      {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = Math.Max(FctResize.MinWidth, width);
        Height = Math.Max(FctResize.MinHeight, height);
      }

      _locked = ConfigUtil.IfSet("FctOverlayLocked");

      _canvas.MotionStyle = FctOverlaySettings.LoadMotion();
      SelectMotionOption(_canvas.MotionStyle);

      UpdateHint(_locked);
      _settingsReady = true;
    }

    /*
     * The hint is the only documentation on screen, so it says what the layout means instead of a generic "drag to move":
     * the strip to line up with your cast bar is the one instruction that makes direction click.
     */
    private void UpdateHint(bool locked)
    {
      if (locked)
      {
        hintText.Text = "locked · unlock from the Tools menu";
        return;
      }

      // terse on purpose: the hint shares one row with the motion combo and the lock checkbox, and a legend that gets
      // ellipsised is a legend nobody can read while fighting
      hintText.Text = "drag to move · edge to resize · gap above your cast bar · up = yours, down = hits on you · Esc closes";
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

      _lastStatsMs = now;
      var dropped = _diagnostics.DroppedCount + FctManager.Instance.DroppedCount;
      statsText.Text = dropped > 0
        ? $"{_diagnostics.Fps:0} fps · {_canvas.ActiveCount} active · {dropped} dropped"
        : $"{_diagnostics.Fps:0} fps · {_canvas.ActiveCount} active";
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

      _canvas.MotionStyle = FctOverlaySettings.ParseMotion(item.Tag as string);
      FctOverlaySettings.SaveMotion(_canvas.MotionStyle);
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

    private void LockChanged(object sender, RoutedEventArgs e) => ApplyLock(lockCheck.IsChecked == true);

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

      /* Bounded by the primary work area: an overlay bigger than the screen cannot be shrunk back by dragging an edge that is
       * off it, so the drag stops at the border instead of inventing a window nobody can reach. Multi-monitor sizing is a
       * per-monitor metric WPF does not hand a chromeless window for free, and this overlay is 980px wide at its largest. */
      var area = SystemParameters.WorkArea;
      FctResize.Fit(_resizeFreeW, _resizeFreeH, area.Width, area.Height, out var w, out var h);

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

    /* DragMove throws if no button is actually held (synthetic events, double-fire on some setups). */
    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
      if (_locked || e.ButtonState != MouseButtonState.Pressed)
      {
        return;
      }

      DragMove();
      SaveSettings();
    }

    /*
     * Esc closes while unlocked. A locked window is click-through and WS_EX_NOACTIVATE, so in practice it gets
     * no keyboard input at all and the Tools menu is the only way out - which is what its hint line says. If the
     * window does hold focus while locked, unlock rather than close: losing a carefully positioned overlay to a
     * stray Esc is the worse failure.
     */
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key != Key.Escape)
      {
        return;
      }

      if (_locked)
      {
        ApplyLock(false);
        Activate();
      }
      else
      {
        Close();
      }
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

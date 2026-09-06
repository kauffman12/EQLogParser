using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQLogParser
{
  /*
   * Production FCT host: topmost, non-activating overlay fed by FctManager's queue (live monitor lines
   * only — historical replay never reaches it). The queue is drained once per painted frame from the
   * canvas's EventsFrame, so a log burst becomes one cross-thread hop instead of one dispatcher item per
   * record. Position, fountain style and the click-through lock persist like every other overlay window.
   *
   * Locked (the in-game default) means WS_EX_TRANSPARENT: clicks fall through to EverQuest and the overlay
   * stops stealing focus mid-fight — the same recipe TextOverlayWindow/TimerOverlayWindow use. Unlock from
   * the Tools menu, or press Esc once while it has focus to unlock and again to close.
   */
  public partial class FctOverlayWindow : Window
  {
    private readonly IFctCanvas _canvas;
    private readonly IFctDiagnostics _diagnostics;
    private readonly List<FctHitCommand> _pending = [];

    private HwndSource _hwndSource;
    private double _lastStatsMs = -1000;
    private bool _locked;

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

      _canvas.EventsFrame += OnCanvasFrame;
      SourceInitialized += OnSourceInitialized;
      IsVisibleChanged += OnVisibleChanged;
      Closed += OnClosed;
    }

    /* Mirrors the lock state from the Tools menu (the checkbox is unreachable while clicks pass through). */
    public void SetLocked(bool locked) => ApplyLock(locked);

    private void OnSourceInitialized(object sender, EventArgs e)
    {
      _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
      _hwndSource?.AddHook(WndProc);
      ApplyLock(_locked);
    }

    /*
     * Extended window styles for the current lock state.
     *
     * Locked: transparent (clicks pass through) plus no-activate, so showing or moving the overlay never
     * takes focus away from the game. Unlocked: app-window, so an overlay that is being positioned is
     * reachable in Alt-Tab. Toolwindow in both states keeps it out of the taskbar (ShowInTaskbar only covers
     * the initial state).
     *
     * Note NativeMethods.GwlStyle is -20, i.e. Win32's GWL_EXSTYLE: every overlay here drives extended styles
     * through that constant and works; "fixing" the name without changing the value would break them all.
     */
    private int CurrentStyles()
    {
      var styles = GetWindowLong(_hwndSource.Handle, NativeMethods.GwlStyle) | (int)NativeMethods.ExtendedWindowStyles.WsExLayered;

      if (_locked)
      {
        styles |= (int)(NativeMethods.ExtendedWindowStyles.WsExTransparent | NativeMethods.ExtendedWindowStyles.WsExToolwindow | NativeMethods.ExtendedWindowStyles.WsExNoActive);
        styles &= ~(int)NativeMethods.ExtendedWindowStyles.WsExAppWindow;
      }
      else
      {
        styles |= (int)(NativeMethods.ExtendedWindowStyles.WsExToolwindow | NativeMethods.ExtendedWindowStyles.WsExAppWindow);
        styles &= ~(int)(NativeMethods.ExtendedWindowStyles.WsExTransparent | NativeMethods.ExtendedWindowStyles.WsExNoActive);
      }

      return styles;
    }

    private void ApplyLock(bool locked)
    {
      _locked = locked;

      lockCheck.IsChecked = locked;
      hintText.Text = locked ? "locked · unlock from the Tools menu" : "drag header to move · Esc closes";

      // belt-and-braces for frames between the request and the style actually landing
      rootBorder.IsHitTestVisible = !locked;

      ConfigUtil.SetSetting("FctOverlayLocked", locked);
      EventsLockChanged?.Invoke(locked);

      if (_hwndSource is not null)
      {
        SetWindowLong(_hwndSource.Handle, NativeMethods.GwlStyle, CurrentStyles());
      }
    }

    /* Re-asserted on every mouse message: WPF rewrites window styles as it likes, so this must too. */
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == NativeMethods.WmNcHitTest && _hwndSource is not null)
      {
        SetWindowLong(hwnd, NativeMethods.GwlStyle, CurrentStyles());
      }

      return IntPtr.Zero;
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

      if (left > 0 && top >= 0 && width > 100 && height > 100)
      {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
      }

      _locked = ConfigUtil.IfSet("FctOverlayLocked");

      var fountain = ConfigUtil.IfSet("FctOverlayFountain");
      fountainCheck.IsChecked = fountain;
      _canvas.FountainMotion = fountain;
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
        _canvas.AddHit(cmd.Lane, cmd.Value, cmd.Source, cmd.Crit, minor: false, periodic: cmd.Periodic, valueText: cmd.ValueText);
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

    private void FountainChanged(object sender, RoutedEventArgs e)
    {
      _canvas.FountainMotion = fountainCheck.IsChecked == true;
      ConfigUtil.SetSetting("FctOverlayFountain", fountainCheck.IsChecked == true);
    }

    private void LockChanged(object sender, RoutedEventArgs e) => ApplyLock(lockCheck.IsChecked == true);

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
      _hwndSource?.RemoveHook(WndProc);
      _hwndSource = null;

      _canvas.EventsFrame -= OnCanvasFrame;
      _canvas.Stop();

      SaveSettings();
      FctManager.Instance.Enabled = false;
      FctManager.Instance.Dispose(); // unsubscribes the parsers: nothing may keep feeding a closed window

      EventsClosed?.Invoke();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
  }
}

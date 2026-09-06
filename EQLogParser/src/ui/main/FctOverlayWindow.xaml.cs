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
   * record. Position, motion style, region scheme and the click-through lock persist like every other overlay window.
   *
   * Motion is a combo rather than the old fountain checkbox because there are four styles now (hold, fountain, pulse,
   * spray) and they are presentation, not information: whichever is chosen, band and direction of travel still say who
   * acted. Both it and the region scheme apply to hits spawned afterwards, so trying one during a pull is safe.
   *
   * Locked (the in-game default) means WS_EX_TRANSPARENT + WS_EX_NOACTIVATE: clicks fall through to EverQuest
   * and the overlay stops stealing focus mid-fight — the same recipe TextOverlayWindow/TimerOverlayWindow use.
   * Unlock from the Tools menu, or press Esc while it has focus to close.
   *
   * Two region schemes are available and switchable from the header: bands (default — my hits rise above an empty
   * middle strip, hits on me sink below it) and the original left/right halves. The overlay cannot know where the
   * player's target is on screen, so what makes direction readable is the empty strip plus the direction of travel;
   * the hint line states which is which because it is the only documentation a player sees while fighting.
   */
  public partial class FctOverlayWindow : Window
  {
    private readonly IFctCanvas _canvas;
    private readonly IFctDiagnostics _diagnostics;
    private readonly List<FctHitCommand> _pending = [];

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

      if (left > 0 && top >= 0 && width > 100 && height > 100)
      {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
      }

      _locked = ConfigUtil.IfSet("FctOverlayLocked");

      _canvas.MotionStyle = FctOverlaySettings.LoadMotion();
      SelectMotionOption(_canvas.MotionStyle);

      _canvas.Layout = FctOverlaySettings.LoadLayout();
      layoutCheck.IsChecked = _canvas.Layout == FctLayoutMode.Halves;
      UpdateHint(_locked);
      _settingsReady = true;
    }

    /*
     * The hint is the only documentation on screen, so it says what the current layout means instead of a generic
     * "drag to move": in bands mode the strip to line up with your cast bar is the useful instruction, and in
     * halves mode it is which side is which.
     */
    private void UpdateHint(bool locked)
    {
      if (locked)
      {
        hintText.Text = "locked · unlock from the Tools menu";
        return;
      }

      // terse on purpose: the hint shares one row with the motion combo and the two checkboxes, and a legend that gets
      // ellipsised is a legend nobody can read while fighting
      var meaning = _canvas.Layout == FctLayoutMode.Halves
        ? "left = hits on you, right = yours"
        : "gap above your cast bar · up = yours, down = hits on you";
      hintText.Text = $"drag to move · {meaning} · Esc closes";
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

    /* Applies to hits spawned from now on; a number already in flight keeps the geometry it was given. */
    private void LayoutChanged(object sender, RoutedEventArgs e)
    {
      _canvas.Layout = layoutCheck.IsChecked == true ? FctLayoutMode.Halves : FctLayoutMode.Bands;
      FctOverlaySettings.SaveLayout(_canvas.Layout);
      UpdateHint(_locked);
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

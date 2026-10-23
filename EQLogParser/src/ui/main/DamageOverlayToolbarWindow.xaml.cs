using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EQLogParser
{
  public partial class DamageOverlayToolbarWindow
  {
    private static readonly SolidColorBrush ActiveBrush = UiUtil.GetBrush("#FE9C1E");
    private static readonly SolidColorBrush InactiveBrush = UiUtil.GetBrush("#FFF");

    public event EventHandler ConfigureRequested;
    public event EventHandler CopyRequested;
    public event EventHandler ResetRequested;
    public event EventHandler CloseRequested;
    public event EventHandler DpsRequested;
    public event EventHandler TankRequested;
    internal event EventHandler ContentSizeChanged;

    // Set by DamageOverlayWindow from its panels choice, never by this window: which tabs exist and which one the operator picked -- which
    // is also what is drawn, because a meter never moves anybody to another tab.
    private MeterPanels _availablePanels = MeterPanels.Both;
    private bool _showingDps = true;

    public DamageOverlayToolbarWindow()
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();

      configureButton.Click += (_, _) => ConfigureRequested?.Invoke(this, EventArgs.Empty);
      copyButton.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
      resetButton.Click += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
      closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
      dpsButton.Click += (_, _) => DpsRequested?.Invoke(this, EventArgs.Empty);
      tankButton.Click += (_, _) => TankRequested?.Invoke(this, EventArgs.Empty);
      toolbarPanel.SizeChanged += (_, _) => ContentSizeChanged?.Invoke(this, EventArgs.Empty);

      SetShowingDps(true);
    }

    /*
     * Which tabs the meter has at all. A panel that is switched off loses its tab rather than sitting there greyed: a disabled button
     * in the title row is a dead hit target that still asks the eye to read it, and the toolbar hugs its content anyway (SizeToContent
     * plus ContentSizeChanged, which DamageOverlayWindow already follows), so nothing jumps when the pair becomes one.
     */
    internal void SetAvailableViews(MeterPanels panels)
    {
      _availablePanels = panels;
      ApplyEnabledState();
    }

    public void SetShowingDps(bool showingDps)
    {
      _showingDps = showingDps;
      ApplyEnabledState();
    }

    /*
     * A quiet nudge for the tab that is NOT on screen while the one that is has nothing to list: it borrows the app's "good" green -- the colour
     * the title bar's finished-loading message uses -- for a moment, then goes back. `DamageOverlayWindow` owns the rhythm (about one pulse every
     * five seconds, off the instant the reason disappears) because only it knows what each half holds; this window just paints. The colour is read
     * by resource key rather than cached so a theme change is followed: a colour travels as a key, never as a Brush.
     */
    internal void SetTabAttention(bool on)
    {
      if (!on)
      {
        ApplyEnabledState();
        return;
      }

      (_showingDps ? tankButton : dpsButton).Foreground = AttentionBrush;
    }

    private static Brush AttentionBrush => Application.Current.Resources["EQGoodForegroundBrush"] as Brush ?? ActiveBrush;

    private void ApplyEnabledState()
    {
      var damageEnabled = (_availablePanels & MeterPanels.Damage) != 0;
      var tankingEnabled = (_availablePanels & MeterPanels.Tanking) != 0;
      dpsButton.Visibility = damageEnabled ? Visibility.Visible : Visibility.Collapsed;
      tankButton.Visibility = tankingEnabled ? Visibility.Visible : Visibility.Collapsed;

      // The divider BETWEEN the two tabs is not a divider when only one tab is left.
      rect2.Visibility = damageEnabled && tankingEnabled ? Visibility.Visible : Visibility.Collapsed;

      dpsButton.Foreground = damageEnabled && _showingDps ? ActiveBrush : InactiveBrush;
      tankButton.Foreground = tankingEnabled && !_showingDps ? ActiveBrush : InactiveBrush;
    }

    public void UpdateFontSize(int fontSize, double headerHeight)
    {
      MinHeight = headerHeight;
      switch (fontSize)
      {
        case 10:
          dpsButton.FontSize = 11;
          tankButton.FontSize = 11;
          configImage.Height = 11;
          copyImage.Height = 11;
          resetImage.Height = 11;
          closeImage.Height = 10;
          rect1.Height = 12;
          rect2.Height = 12;
          break;

        case 12:
          dpsButton.FontSize = 13;
          tankButton.FontSize = 13;
          configImage.Height = 13;
          copyImage.Height = 12;
          resetImage.Height = 12;
          closeImage.Height = 11;
          rect1.Height = 14;
          rect2.Height = 14;
          break;

        case 14:
          dpsButton.FontSize = 15;
          tankButton.FontSize = 15;
          configImage.Height = 14;
          copyImage.Height = 13;
          resetImage.Height = 13;
          closeImage.Height = 12;
          rect1.Height = 16;
          rect2.Height = 16;
          break;

        case 16:
          dpsButton.FontSize = 17;
          tankButton.FontSize = 17;
          configImage.Height = 15;
          copyImage.Height = 14;
          resetImage.Height = 14;
          closeImage.Height = 13;
          rect1.Height = 18;
          rect2.Height = 18;
          break;
      }
    }

    internal double ContentHeight => toolbarPanel.ActualHeight + toolbarPanel.Margin.Top + toolbarPanel.Margin.Bottom;

    internal double ContentWidth => toolbarPanel.ActualWidth + toolbarPanel.Margin.Left + toolbarPanel.Margin.Right;

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      if (PresentationSource.FromVisual(this) is not HwndSource source)
      {
        return;
      }

      source.AddHook(NativeMethods.ContentSizedHeightHook);
      var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

      // Hide from Alt+Tab and prevent stealing focus from the game
      exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow
               | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;

      NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, exStyle);
      // Request a native resize after installing the hook; header spacing measures the panel independently.
      Height = MinHeight;
    }
  }
}

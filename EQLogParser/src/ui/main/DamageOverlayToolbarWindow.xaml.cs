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

      SetShowingDps(true);
    }

    public void SetShowingDps(bool showingDps)
    {
      dpsButton.Foreground = showingDps ? ActiveBrush : InactiveBrush;
      tankButton.Foreground = showingDps ? InactiveBrush : ActiveBrush;
    }

    public void UpdateFontSize(int fontSize)
    {
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

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      if (PresentationSource.FromVisual(this) is not HwndSource source)
      {
        return;
      }

      var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

      // Hide from Alt+Tab and prevent stealing focus from the game
      exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow
               | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;

      NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, (IntPtr)exStyle);
    }
  }
}

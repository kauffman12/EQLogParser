using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TextOverlayWindow.xaml
  /// </summary>
  public partial class TextOverlayWindow : Window
  {
    private static SolidColorBrush BarelyVisible = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#01000000") };
    private static SolidColorBrush BorderColor = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#AA000000") };
    private Overlay Overlay;
    private bool Preview = false;
    private double SavedHeight;
    private double SavedWidth;
    private double SavedTop = double.NaN;
    private double SavedLeft = double.NaN;

    public TextOverlayWindow(string overlayId, bool preview = false)
    {
      InitializeComponent();
      Preview = preview;
      this.border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + overlayId);
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + overlayId);
      Overlay = TriggerOverlayManager.Instance.GetTextOverlayById(overlayId, out _);

      this.Height = Overlay.Height;
      this.Width = Overlay.Width;
      this.Top = Overlay.Top;
      this.Left = Overlay.Left;

      if (preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        this.ResizeMode = ResizeMode.CanResizeWithGrip;
        this.Background = BarelyVisible;
        this.BorderBrush = BorderColor;
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;
      }
    }

    internal void AddTriggerText(string text, double beginTime)
    {
      var block = new TextBlock
      {
        Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text),
        TextAlignment = TextAlignment.Center,
        Padding = new Thickness(0),
        Margin = new Thickness(0),
        Tag = beginTime + Overlay.FadeDelay,
        FontWeight = FontWeights.Bold,
        TextWrapping = TextWrapping.Wrap
      };

      var effect = new DropShadowEffect { ShadowDepth = 2, Direction = 330, Color = Colors.Black, Opacity = 0.4, BlurRadius = 2 };
      block.Effect= effect;

      block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + Overlay.Id);
      block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + Overlay.Id);
      content.Children.Add(block);
    }

    internal bool Tick()
    {
      var currentTime = DateUtil.ToDouble(DateTime.Now);
      var removeList = new List<TextBlock>();

      foreach (var child in content.Children)
      {
        if (child is TextBlock block && block.Tag is double endTime)
        {
          if ((endTime - currentTime) == 1)
          {
            block.Opacity = 0.7;
          }
          else if (endTime == currentTime)
          {
            block.Opacity = 0.3;
          }
          else if (currentTime > endTime)
          {
            removeList.Add(block);
          }
        }
      }

      removeList.ForEach(child => content.Children.Remove(child));

      // return true if one
      return content.Children.Count == 0;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => TriggerOverlayManager.Instance.ClosePreviewTextOverlay(Overlay.Id);

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      this.DragMove();

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
      SavedHeight = this.Height;
      SavedWidth = this.Width;
      SavedTop = this.Top;
      SavedLeft = this.Left;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      Overlay.Height = SavedHeight = this.Height;
      Overlay.Width = SavedWidth = this.Width;
      Overlay.Top = SavedTop = this.Top;
      Overlay.Left = SavedLeft = this.Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerOverlayManager.Instance.UpdateOverlays();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      this.Height = SavedHeight;
      this.Width = SavedWidth;
      this.Top = SavedTop;
      this.Left = SavedLeft;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (!double.IsNaN(SavedTop))
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

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      if (!Preview)
      {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        // set to layered and topmost by xaml
        int exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
        exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW | (int)NativeMethods.ExtendedWindowStyles.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
      }
    }
  }
}

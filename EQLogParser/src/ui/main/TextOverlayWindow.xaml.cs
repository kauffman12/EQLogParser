using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    private Overlay TheOverlay;
    private bool Preview = false;
    private long SavedHeight;
    private long SavedWidth;
    private long SavedTop = long.MaxValue;
    private long SavedLeft = long.MaxValue;

    internal TextOverlayWindow(Overlay overlay, bool preview = false)
    {
      InitializeComponent();
      Preview = preview;
      TheOverlay = overlay;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + TheOverlay.Id);

      Height = TheOverlay.Height;
      Width = TheOverlay.Width;
      Top = TheOverlay.Top;
      Left = TheOverlay.Left;

      if (preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(Window.BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(Window.BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void AddTriggerText(string text, double beginTime)
    {
      var block = new TextBlock
      {
        Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text),
        TextAlignment = TextAlignment.Center,
        Padding = new Thickness(6, 0, 6, 2),
        Margin = new Thickness(0),
        Tag = beginTime + TheOverlay.FadeDelay,
        FontWeight = FontWeights.Bold,
        TextWrapping = TextWrapping.Wrap
      };

      var effect = new DropShadowEffect { ShadowDepth = 2, Direction = 330, Color = Colors.Black, Opacity = 0.4, BlurRadius = 2 };
      block.Effect = effect;

      block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + TheOverlay.Id);
      block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + TheOverlay.Id);
      content.Children.Add(block);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
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

    private void CloseClick(object sender, RoutedEventArgs e) => TriggerOverlayManager.Instance.ClosePreviewTextOverlay(TheOverlay.Id);

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      DragMove();

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
      SavedHeight = (long)this.Height;
      SavedWidth = (long)this.Width;
      SavedTop = (long)this.Top;
      SavedLeft = (long)this.Left;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      TheOverlay.Height = SavedHeight = (long)this.Height;
      TheOverlay.Width = SavedWidth = (long)this.Width;
      TheOverlay.Top = SavedTop = (long)this.Top;
      TheOverlay.Left = SavedLeft = (long)this.Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerOverlayManager.Instance.UpdateOverlays();
      TriggerOverlayManager.Instance.Select(TheOverlay);
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      Height = SavedHeight;
      Width = SavedWidth;
      Top = SavedTop;
      Left = SavedLeft;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (SavedTop != long.MaxValue)
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

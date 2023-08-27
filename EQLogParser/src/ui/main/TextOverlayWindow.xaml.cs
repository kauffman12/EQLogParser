using System;
using System.Collections.Generic;
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
    private LinkedList<TextData> TextDataList = new LinkedList<TextData>();
    private int MaxNodes = -1;
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

      // start off with one node
      content.Children.Add(CreateBlock());

      if (preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;

        // test data
        TextDataList.AddFirst(new TextData
        {
          Text = "test message",
          EndTicks = DateTime.Now.Ticks + (TheOverlay.FadeDelay * TimeSpan.TicksPerSecond)
        });
        content.Children.Add(CreateBlock());
        Tick();
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
      }
    }

    internal void AddTriggerText(string text, double beginTicks, SolidColorBrush brush)
    {
      if (MaxNodes == -1 && content.Children.Count > 0 && content.Children[0] is TextBlock textBlock && textBlock.ActualHeight > 0)
      {
        MaxNodes = (int)(Height / textBlock.ActualHeight) + 1;
      }

      lock (TextDataList)
      {
        TextDataList.AddFirst(new TextData
        {
          Text = text,
          EndTicks = beginTicks + (TheOverlay.FadeDelay * TimeSpan.TicksPerSecond),
          Brush = brush
        });

        if (TextDataList.Count > MaxNodes)
        {
          TextDataList.RemoveLast();
        }
      }

      if (content.Children.Count < MaxNodes && content.Children.Count < TextDataList.Count)
      {
        content.Children.Add(CreateBlock());
      }
    }

    internal bool Tick()
    {
      var currentTicks = DateTime.Now.Ticks;
      var done = false;

      if (TheOverlay.Width != Width)
      {
        Width = TheOverlay.Width;
      }
      else if (TheOverlay.Height != Height)
      {
        Height = TheOverlay.Height;
      }
      else if (TheOverlay.Top != Top)
      {
        Top = TheOverlay.Top;
      }
      else if (TheOverlay.Left != Left)
      {
        Left = TheOverlay.Left;
      }

      lock (TextDataList)
      {
        var node = TextDataList.First;
        var lastIndex = content.Children.Count - 1;

        while (node != null)
        {
          var nextNode = node.Next;
          if (lastIndex >= 0 && content.Children[lastIndex] is TextBlock block)
          {
            if (node.Value.EndTicks >= currentTicks)
            {
              block.Text = node.Value.Text;
              if (block.Visibility != Visibility.Visible)
              {
                block.Visibility = Visibility.Visible;
              }

              if (node.Value.Brush == null)
              {
                block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + TheOverlay.Id);
              }
              else
              {
                block.Foreground = node.Value.Brush;
              }
            }
            else
            {
              TextDataList.Remove(node);
              block.Visibility = Visibility.Collapsed;
              block.Text = "";
            }
          }
          else
          {
            TextDataList.Remove(node);
          }

          node = nextNode;
          lastIndex--;
        }

        done = TextDataList.Count == 0;
      }

      if (!done)
      {
        if (border.Visibility != Visibility.Visible)
        {
          border.Visibility = Visibility.Visible;
        }
      }
      else
      {
        border.Visibility = Visibility.Hidden;
      }

      // return true if done
      return done;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => TriggerOverlayManager.Instance.ClosePreviewTextOverlay(TheOverlay.Id);

    private TextBlock CreateBlock()
    {
      var block = new TextBlock
      {
        TextAlignment = TextAlignment.Center,
        Padding = new Thickness(6, 0, 6, 2),
        Margin = new Thickness(0),
        FontWeight = FontWeights.Bold,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Hidden,
        Effect = new DropShadowEffect { ShadowDepth = 2, Direction = 330, Color = Colors.Black, Opacity = 0.7, BlurRadius = 0 }
      };

      block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + TheOverlay.Id);
      block.SetResourceReference(TextBlock.FontFamilyProperty, "TextOverlayFontFamily-" + TheOverlay.Id);
      return block;
    }

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
      SavedHeight = (long)Height;
      SavedWidth = (long)Width;
      SavedTop = (long)Top;
      SavedLeft = (long)Left;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      TheOverlay.Height = SavedHeight = (long)Height;
      TheOverlay.Width = SavedWidth = (long)Width;
      TheOverlay.Top = SavedTop = (long)Top;
      TheOverlay.Left = SavedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerOverlayManager.Instance.Update(TheOverlay);
      TriggerOverlayManager.Instance.UpdateOverlays();
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
        var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
        exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW | (int)NativeMethods.ExtendedWindowStyles.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
      }
    }

    private class TextData
    {
      public string Text { get; set; }
      public double EndTicks { get; set; }
      public SolidColorBrush Brush { get; set; }
    }
  }
}

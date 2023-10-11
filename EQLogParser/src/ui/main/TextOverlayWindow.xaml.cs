using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private readonly LinkedList<TextData> TextDataList = new();
    private int MaxNodes = -1;
    private TriggerNode Node;
    private readonly bool Preview;
    private long SavedHeight;
    private long SavedWidth;
    private long SavedTop = long.MaxValue;
    private long SavedLeft = long.MaxValue;
    private Dictionary<string, Window> PreviewWindows;

    internal TextOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();
      Node = node;
      Preview = previews != null;
      PreviewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + Node.Id);

      Height = Node.OverlayData.Height;
      Width = Node.OverlayData.Width;
      Top = Node.OverlayData.Top;
      Left = Node.OverlayData.Left;

      // start off with one node
      content.Children.Add(CreateBlock());

      if (Preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + Node.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;

        // test data
        TextDataList.AddFirst(new TextData
        {
          Text = "test message",
          EndTicks = DateTime.Now.Ticks + (Node.OverlayData.FadeDelay * TimeSpan.TicksPerSecond)
        });

        content.Children.Add(CreateBlock());
        TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
        Tick();
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + Node.Id);
      }
    }

    internal void AddTriggerText(string text, double beginTicks, SolidColorBrush brush)
    {
      if (MaxNodes == -1 && content.Children.Count > 0 && content.Children[0] is TextBlock { ActualHeight: > 0 } textBlock)
      {
        MaxNodes = (int)(Height / textBlock.ActualHeight) + 1;
      }

      lock (TextDataList)
      {
        TextDataList.AddFirst(new TextData
        {
          Text = text,
          EndTicks = beginTicks + (Node.OverlayData.FadeDelay * TimeSpan.TicksPerSecond),
          Brush = brush
        });

        if (TextDataList.Count > MaxNodes)
        {
          TextDataList.RemoveLast();
        }
      }

      lock (TextDataList)
      {
        if (content.Children.Count < MaxNodes && content.Children.Count < TextDataList.Count)
        {
          content.Children.Add(CreateBlock());
        }
      }
    }

    internal bool Tick()
    {
      var currentTicks = DateTime.Now.Ticks;
      bool done;

      if (Node.OverlayData.Width != Width)
      {
        Width = Node.OverlayData.Width;
      }
      else if (Node.OverlayData.Height != Height)
      {
        Height = Node.OverlayData.Height;
      }
      else if (Node.OverlayData.Top != Top)
      {
        Top = Node.OverlayData.Top;
      }
      else if (Node.OverlayData.Left != Left)
      {
        Left = Node.OverlayData.Left;
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
              if (node.Value.Brush != null)
              {
                block.Foreground = node.Value.Brush;
              }
              else
              {
                block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + Node.Id);
              }

              if (block.Visibility != Visibility.Visible)
              {
                block.Visibility = Visibility.Visible;
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

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

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

      block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + Node.Id);
      block.SetResourceReference(TextBlock.FontFamilyProperty, "TextOverlayFontFamily-" + Node.Id);
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
      Node.OverlayData.Height = SavedHeight = (long)Height;
      Node.OverlayData.Width = SavedWidth = (long)Width;
      Node.OverlayData.Top = SavedTop = (long)Top;
      Node.OverlayData.Left = SavedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerStateManager.Instance.Update(Node);
      TriggerManager.Instance.CloseOverlay(Node.Id);
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

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (Node.Id == node.Id)
      {
        if (Node != node)
        {
          Node = node;
        }

        Height = Node.OverlayData.Height;
        Width = Node.OverlayData.Width;
        Top = Node.OverlayData.Top;
        Left = Node.OverlayData.Left;
        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        closeButton.IsEnabled = true;
      }
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

    private void WindowClosing(object sender, CancelEventArgs e)
    {
      TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
      PreviewWindows?.Remove(Node.Id);
      PreviewWindows = null;
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
      public string Text { get; init; }
      public double EndTicks { get; init; }
      public SolidColorBrush Brush { get; init; }
    }
  }
}

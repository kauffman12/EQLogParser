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
    private readonly LinkedList<TextData> _textDataList = new();
    private int _maxNodes = -1;
    private TriggerNode _node;
    private readonly bool _preview;
    private long _savedHeight;
    private long _savedWidth;
    private long _savedTop = long.MaxValue;
    private long _savedLeft = long.MaxValue;
    private Dictionary<string, Window> _previewWindows;

    internal TextOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();
      _node = node;
      _preview = previews != null;
      _previewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + _node.Id);

      Height = _node.OverlayData.Height;
      Width = _node.OverlayData.Width;
      Top = _node.OverlayData.Top;
      Left = _node.OverlayData.Left;

      // start off with one node
      content.Children.Add(CreateBlock());

      if (_preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;

        // test data
        _textDataList.AddFirst(new TextData
        {
          Text = "test overlay message",
          EndTicks = DateTime.Now.Ticks + (_node.OverlayData.FadeDelay * TimeSpan.TicksPerSecond)
        });

        content.Children.Add(CreateBlock());
        TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
        Tick();
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + _node.Id);
      }
    }

    internal void AddTriggerText(string text, double beginTicks, SolidColorBrush brush)
    {
      if (_maxNodes == -1 && content.Children.Count > 0 && content.Children[0] is TextBlock { ActualHeight: > 0 } textBlock)
      {
        _maxNodes = (int)(Height / textBlock.ActualHeight) + 1;
      }

      lock (_textDataList)
      {
        _textDataList.AddFirst(new TextData
        {
          Text = text,
          EndTicks = beginTicks + (_node.OverlayData.FadeDelay * TimeSpan.TicksPerSecond),
          Brush = brush
        });

        if (_textDataList.Count > _maxNodes)
        {
          _textDataList.RemoveLast();
        }
      }

      lock (_textDataList)
      {
        if (content.Children.Count < _maxNodes && content.Children.Count < _textDataList.Count)
        {
          content.Children.Add(CreateBlock());
        }
      }
    }

    internal bool Tick()
    {
      bool done;
      var currentTicks = DateTime.Now.Ticks;

      if (!_node.OverlayData.Width.Equals((long)Width))
      {
        Width = _node.OverlayData.Width;
      }
      else if (!_node.OverlayData.Height.Equals((long)Height))
      {
        Height = _node.OverlayData.Height;
      }
      else if (!_node.OverlayData.Top.Equals((long)Top))
      {
        Top = _node.OverlayData.Top;
      }
      else if (!_node.OverlayData.Left.Equals((long)Left))
      {
        Left = _node.OverlayData.Left;
      }

      lock (_textDataList)
      {
        var node = _textDataList.First;
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
                block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + _node.Id);
              }

              if (block.Visibility != Visibility.Visible)
              {
                block.Visibility = Visibility.Visible;
              }
            }
            else
            {
              _textDataList.Remove(node);
              block.Visibility = Visibility.Collapsed;
              block.Text = "";
            }
          }
          else
          {
            _textDataList.Remove(node);
          }

          node = nextNode;
          lastIndex--;
        }

        done = _textDataList.Count == 0;
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

      block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + _node.Id);
      block.SetResourceReference(TextBlock.FontFamilyProperty, "TextOverlayFontFamily-" + _node.Id);
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
      _savedHeight = (long)Height;
      _savedWidth = (long)Width;
      _savedTop = (long)Top;
      _savedLeft = (long)Left;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      _node.OverlayData.Height = _savedHeight = (long)Height;
      _node.OverlayData.Width = _savedWidth = (long)Width;
      _node.OverlayData.Top = _savedTop = (long)Top;
      _node.OverlayData.Left = _savedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerStateManager.Instance.Update(_node);
      TriggerManager.Instance.CloseOverlay(_node.Id);
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      Height = _savedHeight;
      Width = _savedWidth;
      Top = _savedTop;
      Left = _savedLeft;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (_node != null && _node.Id == node.Id)
      {
        if (_node != node)
        {
          _node = node;
        }

        Height = _node.OverlayData.Height;
        Width = _node.OverlayData.Width;
        Top = _node.OverlayData.Top;
        Left = _node.OverlayData.Left;
        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        closeButton.IsEnabled = true;
      }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (_savedTop != long.MaxValue)
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
      _previewWindows?.Remove(_node.Id);
      _previewWindows = null;
    }

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);
      var source = (HwndSource)PresentationSource.FromVisual(this)!;
      if (source != null)
      {
        source.AddHook(NativeMethods.BandAidHook); // Make sure this is hooked first. That ensures it runs last
        source.AddHook(NativeMethods.ProblemHook);

        if (!_preview)
        {
          // set to layered and topmost by xaml
          var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;
          NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, (IntPtr)exStyle);
        }
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    private TriggerNode _node;
    private readonly bool _preview;
    private List<TextBlock> _blockCache;
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

      // cache of text blocks
      CreateBlockCache();

      if (_preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;

        // test data
        AddTriggerText("test overlay message", DateTime.UtcNow.Ticks, null);

        TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
        Dispatcher.InvokeAsync(Tick);
      }
      else
      {
        content.SetResourceReference(Panel.BackgroundProperty, "OverlayBrushColor-" + _node.Id);
      }
    }

    internal void AddTriggerText(string text, double beginTicks, SolidColorBrush brush)
    {
      TextBlock block;
      if (content.Children.Count > 0 && content.Children.Count == _blockCache.Count)
      {
        block = (TextBlock)content.Children[0];
        content.Children.RemoveAt(0);
      }
      else
      {
        block = _blockCache.FirstOrDefault(b => b.Visibility == Visibility.Collapsed);
      }

      if (block != null)
      {
        block.Tag = beginTicks + (_node.OverlayData.FadeDelay * TimeSpan.TicksPerSecond);
        block.Text = text;

        if (brush != null)
        {
          block.Foreground = brush;
        }
        else
        {
          block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + _node.Id);
        }

        block.Visibility = Visibility.Visible;
        content.Children.Add(block);
      }
    }

    internal bool Tick()
    {
      var currentTicks = DateTime.UtcNow.Ticks;
      content.Visibility = Visibility.Collapsed;

      if (!_node.OverlayData.Width.Equals((long)Width))
      {
        Width = _node.OverlayData.Width;
      }

      if (!_node.OverlayData.Height.Equals((long)Height))
      {
        Height = _node.OverlayData.Height;
      }

      if (!_node.OverlayData.Top.Equals((long)Top))
      {
        Top = _node.OverlayData.Top;
      }

      if (!_node.OverlayData.Left.Equals((long)Left))
      {
        Left = _node.OverlayData.Left;
      }

      for (var last = content.Children.Count - 1; last >= 0; last--)
      {
        if (content.Children[last] is TextBlock { Tag: double end } block)
        {
          if (end < currentTicks)
          {
            content.Children.RemoveAt(last);
            block.Visibility = Visibility.Collapsed;
          }
          else if (content.Visibility != Visibility.Visible)
          {
            content.Visibility = Visibility.Visible;
          }
        }
      }

      // return true if done
      return content.Children.Count == 0;
    }

    private void CreateBlockCache()
    {
      // figure out how big a block will be
      var testBlock = new TextBlock
      {
        Text = "test"
      };

      testBlock.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + _node.Id);
      testBlock.SetResourceReference(TextBlock.FontFamilyProperty, "TextOverlayFontFamily-" + _node.Id);
      var blockSize = UiElementUtil.CalculateTextBoxHeight(testBlock.FontFamily, testBlock.FontSize, testBlock.Padding, new Thickness());

      // create cache of blocks needed to cover Overlay height
      var max = (Height / blockSize) + 1;
      _blockCache = Enumerable.Range(0, (int)max).Select(b =>
      {
        var block = new TextBlock
        {
          TextAlignment = TextAlignment.Center,
          Padding = new Thickness(6, 0, 6, 2),
          Margin = new Thickness(0),
          FontWeight = FontWeights.Bold,
          TextWrapping = TextWrapping.Wrap,
          Visibility = Visibility.Collapsed,
          Effect = new DropShadowEffect
          { ShadowDepth = 2, Direction = 330, Color = Colors.Black, Opacity = 0.7, BlurRadius = 0 },
        };

        block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + _node.Id);
        block.SetResourceReference(TextBlock.FontFamilyProperty, "TextOverlayFontFamily-" + _node.Id);
        return block;
      }).ToList();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

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
        if (!_node.Equals(node))
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
          NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, exStyle);
        }
      }
    }
  }
}

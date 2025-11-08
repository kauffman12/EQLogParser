using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class TextOverlayWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly long TopIntervalTimeStamp = MonoTime.SecondsToTicks(2); // 2s in Stopwatch ticks
    private readonly bool _preview;
    private readonly RingBuffer<TextData> _buffer;
    private readonly Timer _timer;
    private readonly List<TextBlock> _blockList;
    private readonly object _bufferLock = new();
    private readonly bool _streamerMode;
    private TriggerNode _node;
    private Dictionary<string, Window> _previewWindows;
    private long _lastTopTimeStamp;
    private long _savedHeight;
    private long _savedWidth;
    private long _savedTop = long.MaxValue;
    private long _savedLeft = long.MaxValue;
    private nint _windowHndl;
    private volatile int _isTicking;
    private volatile bool _isClosed;
    private volatile bool _newData;

    internal TextOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();
      _node = node;
      _preview = previews != null;
      _previewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + _node.Id);
      content.SetResourceReference(VerticalAlignmentProperty, "OverlayVerticalAlignment-" + _node.Id);
      _streamerMode = _node.OverlayData.StreamerMode;
      UpdateFields();

      // cache of text blocks
      _blockList = CreateBlocks();
      _buffer = new RingBuffer<TextData>(_blockList.Count);

      if (_preview)
      {
        MainActions.SetCurrentTheme(this);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        titleViewbox.Visibility = Visibility.Visible;
        buttonContent.Visibility = Visibility.Visible;
        content.Visibility = Visibility.Visible;

        // test data
        var now = MonoTime.NowStamp();
        _buffer.Add(new TextData { Text = "test overlay message", FadeTimeStamp = now + MonoTime.SecondsToTicks(2) });
        Render(now);
      }
      else
      {
        IsHitTestVisible = false;
        content.SetResourceReference(Panel.BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        _timer = new Timer(DoTick, null, Timeout.Infinite, 150);
      }

      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
    }

    // Keep on UI thread
    internal void HideOverlay() => EnsureVisible(false);

    internal void AddText(string text, long now, string fontColor)
    {
      if (_isClosed)
        return;

      var fadeTs = now + MonoTime.SecondsToTicks(_node.OverlayData.FadeDelay);
      var newText = new TextData { Text = text, FadeTimeStamp = fadeTs, FontColor = fontColor };

      lock (_bufferLock)
      {
        _buffer.Add(newText);
        _newData = true;
      }

      // ensure timer is running
      _timer?.Change(0, 150);
    }

    // Keep on UI thread
    internal void StopOverlay()
    {
      lock (_bufferLock)
      {
        // stop on next render
        _buffer.Clear();
      }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void DoTick(object state)
    {
      if (Interlocked.Exchange(ref _isTicking, 1) == 1)
        return;

      Dispatcher.InvokeAsync(() =>
      {
        try
        {
          var now = MonoTime.NowStamp();
          if (Visibility == Visibility.Visible && _windowHndl != 0 && (now - _lastTopTimeStamp) >= TopIntervalTimeStamp)
          {
            NativeMethods.SetWindowTopMost(_windowHndl);
            _lastTopTimeStamp = now;
          }

          // if nothing rendered then stop and hide window
          if (Render(now) == false)
          {
            // defer visibility change so layout can settle
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
              var doHide = false;
              lock (_bufferLock)
              {
                doHide = _buffer.Count == 0;
              }

              // check if still empty
              if (doHide)
              {
                EnsureVisible(false);
                content.UpdateLayout();
              }
            }));
          }
        }
        finally
        {
          Interlocked.Exchange(ref _isTicking, 0);
        }
      });
    }

    private bool Render(long now)
    {
      lock (_bufferLock)
      {
        if (_buffer.Count == 0)
        {
          // nothing to show
          StopOverlayUnsafe();
          return false;
        }

        var visibleCount = Math.Min(_buffer.Count, _blockList.Count);
        for (var i = 0; i < visibleCount; i++)
        {
          var blockIndex = _blockList.Count - 1 - i;
          var block = _blockList[blockIndex];

          if (_buffer.GetFromNewest(i) is { } td)
          {
            if (now >= td.FadeTimeStamp)
            {
              // cleanup this item and everything older than it
              for (var j = _buffer.Count - 1; j >= i; j--)
              {
                blockIndex = _blockList.Count - 1 - j;
                ClearBlock(blockIndex);
                _buffer.TryRemoveOldest(out _);
              }

              break;
            }
            else
            {
              // allow hide to work
              if (_newData)
              {
                EnsureVisible(true);
                _newData = false;
              }

              if (block.Text != td.Text)
              {
                block.Text = td.Text;
              }

              var brush = !string.IsNullOrEmpty(td.FontColor) ? UiUtil.GetBrush(td.FontColor, false) : null;
              if (string.IsNullOrEmpty(td.FontColor) || brush == null)
              {
                // restore resource binding if a local value exists
                if (block.ReadLocalValue(TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue)
                {
                  block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + _node.Id);
                }
              }
              else if (!Equals(block.Foreground, brush))
              {
                block.Foreground = brush;
              }

              if (block.Visibility != Visibility.Visible)
              {
                block.Visibility = Visibility.Visible;
              }
            }
          }
        }

        // clear remaining blocks
        for (var i = _blockList.Count - visibleCount - 1; i >= 0; i--)
        {
          ClearBlock(i);
        }
      }

      return true;
    }

    private void ClearBlock(int blockIndex)
    {
      if (blockIndex >= 0 && blockIndex < _blockList.Count)
      {
        var block = _blockList[blockIndex];
        if (block.Text != string.Empty)
        {
          block.Text = string.Empty;
          block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + _node.Id);
          block.Visibility = Visibility.Collapsed;
        }
      }
    }

    private List<TextBlock> CreateBlocks()
    {
      // figure out how big a block will be
      var testBlock = CreateBlock("Test");
      var blockSize = UiElementUtil.CalculateTextBlockHeight(testBlock, this);

      // create cache of blocks needed to cover Overlay height
      var size = Math.Max(1, (int)Math.Floor(Height / blockSize));
      var blocks = new List<TextBlock>(size);
      for (var i = 0; i < size; i++)
      {
        var block = CreateBlock();
        blocks.Add(block);
        content.Children.Add(block);
      }

      return blocks;
    }

    private void EnsureVisible(bool visible)
    {
      if (visible)
      {
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        if (content.Visibility != Visibility.Visible) content.Visibility = Visibility.Visible;
      }
      else
      {
        if (content.Visibility != Visibility.Collapsed) content.Visibility = Visibility.Collapsed;
        if (Visibility != Visibility.Collapsed) Visibility = Visibility.Collapsed;
      }
    }

    // Keep on UI thread and lock
    private void StopOverlayUnsafe()
    {
      for (var i = 0; i < _blockList.Count; i++)
      {
        ClearBlock(i);
      }

      _buffer.Clear();
      _newData = false;
      _timer?.Change(Timeout.Infinite, Timeout.Infinite);
      _lastTopTimeStamp = MonoTime.NowStamp();
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

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
      _node.OverlayData.Height = _savedHeight = (long)Height;
      _node.OverlayData.Width = _savedWidth = (long)Width;
      _node.OverlayData.Top = _savedTop = (long)Top;
      _node.OverlayData.Left = _savedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      await TriggerStateManager.Instance.Update(_node);
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

        UpdateFields();
        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        closeButton.IsEnabled = true;
      }
    }

    private void UpdateFields()
    {
      Height = _node.OverlayData.Height;
      Width = _node.OverlayData.Width;
      Top = _node.OverlayData.Top;
      Left = _node.OverlayData.Left;
      Title = _node.Name;

      if (_streamerMode != _node.OverlayData.StreamerMode && !_preview)
      {
        _ = Task.Run(async () =>
        {
          await TriggerOverlayManager.Instance.RestartOverlayAsync(_node.Id);
        });
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

    private TextBlock CreateBlock(string text = "")
    {
      var block = new TextBlock
      {
        TextAlignment = TextAlignment.Center,
        Padding = new Thickness(6, 0, 6, 2),
        Margin = new Thickness(0),
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        Effect = new DropShadowEffect { ShadowDepth = 2, Direction = 330, Color = Colors.Black, Opacity = 0.7, BlurRadius = 0 },
      };

      block.SetResourceReference(TextBlock.ForegroundProperty, "TextOverlayFontColor-" + _node.Id);
      block.SetResourceReference(TextBlock.FontSizeProperty, "TextOverlayFontSize-" + _node.Id);
      block.SetResourceReference(TextBlock.FontFamilyProperty, "TextOverlayFontFamily-" + _node.Id);
      block.SetResourceReference(TextBlock.FontWeightProperty, "TextOverlayFontWeight-" + _node.Id);
      block.SetResourceReference(TextBlock.HorizontalAlignmentProperty, "OverlayHorizontalAlignment-" + _node.Id);
      return block;
    }

    private async void WindowClosing(object sender, CancelEventArgs e)
    {
      try
      {
        _isClosed = true;
        _timer?.Dispose();
        TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        _previewWindows?.Remove(_node.Id);
        _previewWindows = null;
        await Task.Delay(750);
      }
      catch (Exception)
      {
        // do nothing
      }
    }

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      try
      {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        if (source != null)
        {
          source.AddHook(NativeMethods.BandAidHook); // Make sure this is hooked first. That ensures it runs last
          source.AddHook(NativeMethods.ProblemHook);

          NativeMethods.SetWindowTopMost(source.Handle);
          _windowHndl = source.Handle;

          if (!_preview)
          {
            // Get current extended styles
            var exStyle = (int)NativeMethods.GetWindowLongPtr(_windowHndl, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

            // Add transparency and layered styles
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExLayered | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;

            if (!_streamerMode)
            {
              // tool window to not show up in alt-tab
              exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;
            }

            // Apply the new extended styles
            NativeMethods.SetWindowLong(_windowHndl, (int)NativeMethods.GetWindowLongFields.GwlExstyle, new IntPtr(exStyle));
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Problem in OnSourceInitialized", ex);
      }
    }

    private class TextData
    {
      public long FadeTimeStamp { get; init; }
      public string Text { get; init; } = string.Empty;
      public string FontColor { get; init; }
    }
  }
}

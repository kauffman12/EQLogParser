using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace EQLogParser
{
  public partial class TextOverlayWindow : IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const long TopTimeout = TimeSpan.TicksPerSecond * 2;
    private readonly bool _preview;
    private readonly ConcurrentQueue<TextData> _queue;
    private readonly SemaphoreSlim _renderSemaphore = new(1, 1);
    private List<TextBlock> _blockCache;
    private TriggerNode _node;
    private Dictionary<string, Window> _previewWindows;
    private long _lastTopTicks = long.MinValue;
    private long _savedHeight;
    private long _savedWidth;
    private long _savedTop = long.MaxValue;
    private long _savedLeft = long.MaxValue;
    private nint _windowHndl;
    private volatile bool _isRendering;
    private volatile bool _isClosed;
    private bool _disposed;

    internal TextOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();
      _queue = [];
      _node = node;
      _preview = previews != null;
      _previewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + _node.Id);
      content.SetResourceReference(VerticalAlignmentProperty, "OverlayVerticalAlignment-" + _node.Id);

      Height = _node.OverlayData.Height;
      Width = _node.OverlayData.Width;
      Top = _node.OverlayData.Top;
      Left = _node.OverlayData.Left;

      // cache of text blocks
      CreateBlockCache();

      if (_preview)
      {
        MainActions.SetCurrentTheme(this);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + _node.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;

        // test data
        RenderText("test overlay message", DateTime.UtcNow.Ticks, null);
        Dispatcher.InvokeAsync(Tick);
      }
      else
      {
        content.SetResourceReference(Panel.BackgroundProperty, "OverlayBrushColor-" + _node.Id);
      }

      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
    }

    internal async void AddTextAsync(string text, long beginTicks, Brush brush)
    {
      if (_isClosed)
      {
        return;
      }

      await _renderSemaphore.WaitAsync();

      try
      {
        _queue.Enqueue(new TextData { Text = text, BeginTicks = beginTicks, FontColorBrush = brush });

        if (!_isRendering)
        {
          _isRendering = true;
          _ = StartRenderingAsync();
        }
      }
      finally
      {
        _renderSemaphore.Release();
      }
    }

    internal void Clear()
    {
      for (var last = content.Children.Count - 1; last >= 0; last--)
      {
        if (content.Children[last] is TextBlock { Tag: double end } block)
        {
          content.Children.RemoveAt(last);
          block.Visibility = Visibility.Collapsed;
        }
      }

      content.Visibility = Visibility.Collapsed;
    }

    private async Task StartRenderingAsync()
    {
      while (_isRendering)
      {
        var isComplete = false;
        await Dispatcher.InvokeAsync(() =>
        {
          while (_queue.TryDequeue(out var textData))
          {
            RenderText(textData.Text, textData.BeginTicks, textData.FontColorBrush);
            Visibility = Visibility.Visible;
          }

          if (Visibility != Visibility.Visible)
          {
            return;
          }

          // true if complete
          isComplete = Tick();
        });

        await Task.Delay(250);

        if (_isClosed)
        {
          return;
        }

        if (isComplete)
        {
          await _renderSemaphore.WaitAsync();

          try
          {
            if (_queue.IsEmpty)
            {
              _isRendering = false;

              Dispatcher.Invoke(() =>
              {
                Visibility = Visibility.Collapsed;
              });
            }
          }
          finally
          {
            _renderSemaphore.Release();
          }
        }
      }
    }

    private void RenderText(string text, double beginTicks, Brush brush)
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

        content.Children.Add(block);
        block.Visibility = Visibility.Visible;
      }
    }

    private bool Tick()
    {
      var currentTicks = DateTime.UtcNow.Ticks;
      if (_windowHndl != 0 && (_lastTopTicks == long.MinValue || (currentTicks - _lastTopTicks) > TopTimeout))
      {
        NativeMethods.SetWindowTopMost(_windowHndl);
        _lastTopTicks = currentTicks;
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
        }
      }

      // Set visibility only once after all updates
      content.Visibility = content.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
      testBlock.SetResourceReference(TextBlock.FontWeightProperty, "TextOverlayFontWeight-" + _node.Id);
      testBlock.SetResourceReference(TextBlock.HorizontalAlignmentProperty, "OverlayHorizontalAlignment-" + _node.Id);
      var blockSize = UiElementUtil.CalculateTextBoxHeight(testBlock.FontFamily, testBlock.FontSize, testBlock.Padding, new Thickness());

      // create cache of blocks needed to cover Overlay height
      var max = (Height / blockSize) + 1;
      _blockCache = Enumerable.Range(0, (int)max).Select(_ =>
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
        block.SetResourceReference(TextBlock.FontWeightProperty, "TextOverlayFontWeight-" + _node.Id);
        block.SetResourceReference(TextBlock.HorizontalAlignmentProperty, "OverlayHorizontalAlignment-" + _node.Id);
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

    private async void WindowClosing(object sender, CancelEventArgs e)
    {
      try
      {
        _isClosed = true;
        _isRendering = false;
        TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        _previewWindows?.Remove(_node.Id);
        _previewWindows = null;
        _queue.Clear();
        await Task.Delay(750);
        Dispose();
      }
      catch (Exception)
      {
        // do nothing
      }
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (_disposed) return;

      if (disposing)
      {
        _renderSemaphore?.Dispose();
      }

      _disposed = true;
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
            var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

            // Add transparency and layered styles
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExLayered | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;
            // tool window to not show up in alt-tab
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;

            // Apply the new extended styles
            NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, new IntPtr(exStyle));
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
      public long BeginTicks { get; init; }
      public string Text { get; init; }
      public Brush FontColorBrush { get; init; }
    }
  }
}

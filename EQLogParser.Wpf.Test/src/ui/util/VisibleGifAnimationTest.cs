using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfAnimatedGif;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class VisibleGifAnimationTest
  {
    [TestMethod]
    public void InitiallyHiddenGifStaysPausedThenFollowsEffectiveAncestorVisibility()
    {
      var initiallyPaused = false;
      var visiblePlaying = false;
      var hiddenPaused = false;
      var visibleAgainPlaying = false;
      Sta.Run(() =>
      {
        using var fixture = new GifWindow(hidden: true);
        initiallyPaused = fixture.Image.IsLoaded && !fixture.Image.IsVisible && fixture.Controller.IsPaused;
        fixture.Parent.Visibility = Visibility.Visible;
        visiblePlaying = fixture.Image.IsVisible && !fixture.Controller.IsPaused;
        fixture.Parent.Visibility = Visibility.Collapsed;
        hiddenPaused = !fixture.Image.IsVisible && fixture.Controller.IsPaused;
        fixture.Parent.Visibility = Visibility.Visible;
        visibleAgainPlaying = fixture.Image.IsVisible && !fixture.Controller.IsPaused;
      });

      Assert.IsTrue(initiallyPaused, "A hidden image may load without a subsequent visibility-change notification.");
      Assert.IsTrue(visiblePlaying);
      Assert.IsTrue(hiddenPaused);
      Assert.IsTrue(visibleAgainPlaying);
    }

    [TestMethod]
    public void SourceReplacementRechecksVisibilityForEveryNewController()
    {
      var hiddenReplacementPaused = false;
      var replacementResumed = false;
      var visibleReplacementPlaying = false;
      Sta.Run(() =>
      {
        using var fixture = new GifWindow(hidden: false);
        var original = fixture.Controller;
        fixture.Parent.Visibility = Visibility.Collapsed;
        fixture.ReplaceSource();
        var replacement = fixture.Controller;
        hiddenReplacementPaused = !ReferenceEquals(original, replacement) && replacement.IsPaused;
        fixture.Parent.Visibility = Visibility.Visible;
        replacementResumed = !replacement.IsPaused;
        fixture.ReplaceSource();
        visibleReplacementPlaying = !ReferenceEquals(replacement, fixture.Controller) && !fixture.Controller.IsPaused;
      });

      Assert.IsTrue(hiddenReplacementPaused);
      Assert.IsTrue(replacementResumed);
      Assert.IsTrue(visibleReplacementPlaying);
    }

    [TestMethod]
    public void UnloadDoesNotResumeTheDisposedControllerBeforeReloadCreatesItsReplacement()
    {
      var oldStayedPaused = false;
      var newHiddenPaused = false;
      var newVisiblePlaying = false;
      Sta.Run(() =>
      {
        using var fixture = new GifWindow(hidden: false);
        var original = fixture.Controller;
        fixture.Parent.Child = null;
        PumpUntil(() => !fixture.Image.IsLoaded);
        fixture.Image.Visibility = Visibility.Collapsed;
        fixture.Image.Visibility = Visibility.Visible;
        oldStayedPaused = original.IsPaused;
        fixture.Parent.Visibility = Visibility.Collapsed;
        fixture.Parent.Child = fixture.Image;
        PumpUntil(() => fixture.Image.IsLoaded
          && ImageBehavior.GetAnimationController(fixture.Image) is { } current && !ReferenceEquals(original, current));
        newHiddenPaused = fixture.Controller.IsPaused;
        fixture.Parent.Visibility = Visibility.Visible;
        newVisiblePlaying = !fixture.Controller.IsPaused;
      });

      Assert.IsTrue(oldStayedPaused);
      Assert.IsTrue(newHiddenPaused);
      Assert.IsTrue(newVisiblePlaying);
    }

    [TestMethod]
    public void EnablingAfterAnimationInitializationUsesCurrentVisibility()
    {
      var initiallyPaused = false;
      var enabledPlaying = false;
      var disabledPaused = false;
      Sta.Run(() =>
      {
        using var fixture = new GifWindow(hidden: false, enabled: false);
        initiallyPaused = fixture.Controller.IsPaused;
        VisibleGifAnimation.SetEnabled(fixture.Image, true);
        enabledPlaying = !fixture.Controller.IsPaused;
        VisibleGifAnimation.SetEnabled(fixture.Image, false);
        disabledPaused = fixture.Controller.IsPaused;
      });

      Assert.IsTrue(initiallyPaused);
      Assert.IsTrue(enabledPlaying);
      Assert.IsTrue(disabledPaused);
    }

    private static void PumpUntil(Func<bool> done)
    {
      var frame = new DispatcherFrame();
      var deadline = Environment.TickCount64 + 2000;
      var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
      timer.Tick += (_, _) =>
      {
        if (done() || Environment.TickCount64 >= deadline) frame.Continue = false;
      };
      timer.Start();
      try
      {
        Dispatcher.PushFrame(frame);
      }
      finally
      {
        timer.Stop();
      }
      if (!done()) throw new InvalidOperationException("The GIF fixture did not finish its load/unload lifecycle");
    }

    private sealed class GifWindow : IDisposable
    {
      // Two full 1x1 frames, red/blue, 100 ms each, looping. No repository files or pack-URI application state needed.
      private const string Gif = "R0lGODlhAQABAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQECgAAACwAAAAA"
        + "AQABAAAIBAABBAQAIfkEBQoAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAACAQAAQQEADs=";
      private readonly List<MemoryStream> _streams = [];
      private readonly Window _window;

      public void Dispose()
      {
        VisibleGifAnimation.SetEnabled(Image, false);
        ImageBehavior.SetAnimatedSource(Image, null);
        _window.Close();
        foreach (var stream in _streams) stream.Dispose();
      }

      internal Image Image { get; } = new() { Width = 16, Height = 16 };
      internal Border Parent { get; }
      internal ImageAnimationController Controller => ImageBehavior.GetAnimationController(Image)
        ?? throw new InvalidOperationException("GIF controller was not created");

      internal GifWindow(bool hidden, bool enabled = true)
      {
        ImageBehavior.SetAutoStart(Image, false);
        VisibleGifAnimation.SetEnabled(Image, enabled);
        ReplaceSource();
        Parent = new Border { Child = Image, Visibility = hidden ? Visibility.Collapsed : Visibility.Visible };
        _window = new Window
        {
          Content = Parent, Width = 40, Height = 40, Left = -10000, Top = -10000,
          WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
          ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
          _window.Show();
          PumpUntil(() => Image.IsLoaded && ImageBehavior.GetAnimationController(Image) is not null);
        }
        catch
        {
          Dispose();
          throw;
        }
      }

      internal void ReplaceSource()
      {
        var stream = new MemoryStream(Convert.FromBase64String(Gif));
        _streams.Add(stream);
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.StreamSource = stream;
        source.EndInit();
        ImageBehavior.SetAnimatedSource(Image, source);
      }
    }
  }
}

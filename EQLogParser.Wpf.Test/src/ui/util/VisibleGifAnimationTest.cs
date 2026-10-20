using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfAnimatedGif;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  [TestCategory("WindowsIntegration")]
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
        using var fixture = new GifHost(hidden: true);
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
        using var fixture = new GifHost(hidden: false);
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
        using var fixture = new GifHost(hidden: false);
        var original = fixture.Controller;
        fixture.Parent.Child = null;
        PumpUntil(() => !fixture.Image.IsLoaded);
        fixture.Image.Visibility = Visibility.Collapsed;
        fixture.Image.Visibility = Visibility.Visible;
        oldStayedPaused = original.IsPaused;
        fixture.Parent.Visibility = Visibility.Collapsed;
        fixture.Parent.Child = fixture.Image;
        PumpUntil(() => fixture.Image.IsLoaded
          && ImageBehavior.GetAnimationController(fixture.Image) is { } current && !ReferenceEquals(original, current),
          () => $"image loaded={fixture.Image.IsLoaded}, animation loaded={ImageBehavior.GetIsAnimationLoaded(fixture.Image)}, "
            + $"controller replaced={!ReferenceEquals(original, ImageBehavior.GetAnimationController(fixture.Image))}");
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
        using var fixture = new GifHost(hidden: false, enabled: false);
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

    private static void PumpUntil(Func<bool> done, Func<string>? describe = null)
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
      if (!done()) throw new InvalidOperationException("The GIF fixture did not finish its load/unload lifecycle: "
        + (describe?.Invoke() ?? "no diagnostics"));
    }

    private sealed class GifHost : IDisposable
    {
      // The fixture reads the same packaged animation as CharacterNodeTemplate.
      private static readonly Uri StatusGif = new("pack://application:,,,/EQLogParser;component/icons/waiting.gif");
      private readonly HwndSource _source;

      public void Dispose()
      {
        VisibleGifAnimation.SetEnabled(Image, false);
        ImageBehavior.SetAnimatedSource(Image, null);
        _source.Dispose();
      }

      internal Image Image { get; } = new() { Width = 16, Height = 16 };
      internal Border Parent { get; }
      internal ImageAnimationController Controller => ImageBehavior.GetAnimationController(Image)
        ?? throw new InvalidOperationException("GIF controller was not created");

      internal GifHost(bool hidden, bool enabled = true)
      {
        ImageBehavior.SetAutoStart(Image, false);
        VisibleGifAnimation.SetEnabled(Image, enabled);
        ReplaceSource();
        Parent = new Border { Child = Image, Visibility = hidden ? Visibility.Collapsed : Visibility.Visible };
        // The off-screen Window never reached Loaded on Windows; root the visual directly on this STA's HWND
        // so the test can exercise genuine Loaded and effective ancestor visibility.
        _source = new HwndSource(new HwndSourceParameters("Status GIF fixture")
        {
          Width = 40, Height = 40, PositionX = -10000, PositionY = -10000,
          WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE: effective IsVisible needs a shown native host.
          ExtendedWindowStyle = 0x08000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW.
        });
        try
        {
          _source.RootVisual = Parent;
          PumpUntil(() => Image.IsLoaded && ImageBehavior.GetAnimationController(Image) is not null,
            () => $"hwnd={_source.Handle != IntPtr.Zero}, attached={PresentationSource.FromVisual(Image) is not null}, "
              + $"image loaded={Image.IsLoaded}, visible={Image.IsVisible}, "
              + $"source={ImageBehavior.GetAnimatedSource(Image)}, animation loaded={ImageBehavior.GetIsAnimationLoaded(Image)}, "
              + $"controller={ImageBehavior.GetAnimationController(Image) is not null}");
        }
        catch
        {
          Dispose();
          throw;
        }
      }

      internal void ReplaceSource()
      {
        ImageBehavior.SetAnimatedSource(Image, new BitmapImage(StatusGif));
      }
    }
  }
}

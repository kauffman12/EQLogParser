using System.Windows;
using System.Windows.Controls;
using WpfAnimatedGif;

namespace EQLogParser
{
  // Opt-in status GIFs start paused; each new controller plays only while its image is loaded and effectively visible.
  // See docs/DesignNotes.md → "Character-status GIFs start paused until effectively visible".
  public static class VisibleGifAnimation
  {
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
      "Enabled", typeof(bool), typeof(VisibleGifAnimation), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty ReadyProperty = DependencyProperty.RegisterAttached(
      "Ready", typeof(bool), typeof(VisibleGifAnimation), new PropertyMetadata(false));

    public static bool GetEnabled(Image image) => (bool)image.GetValue(EnabledProperty);
    public static void SetEnabled(Image image, bool value) => image.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
      if (target is not Image image) return;
      Pause(image);
      image.ClearValue(ReadyProperty);
      image.IsVisibleChanged -= VisibilityChanged;
      image.Unloaded -= Unloaded;
      ImageBehavior.RemoveAnimationLoadedHandler(image, AnimationLoaded);
      if (args.NewValue is true)
      {
        image.SetCurrentValue(ImageBehavior.AutoStartProperty, false);
        image.IsVisibleChanged += VisibilityChanged;
        image.Unloaded += Unloaded;
        ImageBehavior.AddAnimationLoadedHandler(image, AnimationLoaded);
        if (image.IsLoaded && ImageBehavior.GetIsAnimationLoaded(image))
        {
          image.SetValue(ReadyProperty, true);
          Update(image);
        }
      }
    }

    private static void AnimationLoaded(object sender, RoutedEventArgs args)
    {
      if (sender is not Image image || args.OriginalSource != image || !GetEnabled(image)) return;
      image.SetValue(ReadyProperty, true);
      Update(image);
    }

    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
      if (sender is Image image) Update(image);
    }

    private static void Unloaded(object sender, RoutedEventArgs args)
    {
      if (sender is not Image image) return;
      Pause(image);
      // The library leaves a disposed controller attached until reload. Do not resume it on an early visibility event.
      image.ClearValue(ReadyProperty);
    }

    private static void Update(Image image)
    {
      if (image.GetValue(ReadyProperty) is not true
        || ImageBehavior.GetAnimationController(image) is not { } controller)
      {
        return;
      }

      if (image.IsLoaded && image.IsVisible)
      {
        if (controller.IsPaused) controller.Play();
      }
      else if (!controller.IsPaused)
      {
        controller.Pause();
      }
    }

    private static void Pause(Image image)
    {
      if (image.GetValue(ReadyProperty) is true
        && ImageBehavior.GetAnimationController(image) is { IsPaused: false } controller)
      {
        controller.Pause();
      }
    }
  }
}

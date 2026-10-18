using log4net;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EQLogParser
{
  // Opt-in scope follows newly generated template borders without replacing styles or watching ItemsSource/layout.
  // See docs/DesignNotes.md → "Frozen-column borders: finish the static state, keep the column frozen".
  internal static class FrozenCellAnimations
  {
    private static readonly ILog Log = LogManager.GetLogger(typeof(FrozenCellAnimations));
    private static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
      "Enabled", typeof(bool), typeof(FrozenCellAnimations),
      new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, EnabledChanged));
    private static readonly DependencyProperty HookProperty = DependencyProperty.RegisterAttached(
      "Hook", typeof(StateHook), typeof(FrozenCellAnimations));

    internal static void Configure(FrameworkElement scope)
    {
      scope.VerifyAccess();
      scope.SetValue(EnabledProperty, true);
    }

    internal static bool IsStaticFrozenBorder(VisualState state)
    {
      if (state?.Name != "FrozenColumnCell" || state.Storyboard is not { } storyboard
        || !HasStaticTiming(storyboard, true) || storyboard.Children.Count != 1
        || storyboard.Children[0] is not ThicknessAnimationUsingKeyFrames animation
        || !HasStaticTiming(animation, false) || animation.KeyFrames.Count != 1
        || Storyboard.GetTargetName(animation) != "PART_FrozenCellBorder"
        || Storyboard.GetTargetProperty(animation)?.Path != "BorderThickness") return false;

      var keyTime = animation.KeyFrames[0].KeyTime;
      return keyTime.Type == KeyTimeType.TimeSpan && keyTime.TimeSpan == TimeSpan.Zero;
    }

    private static bool HasStaticTiming(
      System.Windows.Media.Animation.Timeline timeline, bool allowAutomaticDuration) =>
      (timeline.Duration.HasTimeSpan && timeline.Duration.TimeSpan == TimeSpan.FromDays(1)
        || allowAutomaticDuration && timeline.Duration == Duration.Automatic)
      && timeline.BeginTime == TimeSpan.Zero && timeline.SpeedRatio == 1 && !timeline.AutoReverse
      && timeline.FillBehavior == FillBehavior.HoldEnd
      && timeline.RepeatBehavior.HasCount && timeline.RepeatBehavior.Count == 1;

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
      if (target is not Border border) return;
      border.Loaded -= BorderLoaded;
      border.Unloaded -= BorderUnloaded;
      Detach(border);
      if (args.NewValue is true)
      {
        // Names can be assigned after inheritance arrives; inspect the completed template at Loaded.
        border.Loaded += BorderLoaded;
        border.Unloaded += BorderUnloaded;
        if (border.IsLoaded) Attach(border);
      }
    }

    private static void BorderLoaded(object sender, RoutedEventArgs args)
    {
      if (sender is Border border) Attach(border);
    }

    private static void BorderUnloaded(object sender, RoutedEventArgs args)
    {
      if (sender is Border border) Detach(border);
    }

    private static void Attach(Border border)
    {
      if (border.Name != "PART_FrozenCellBorder" || border.GetValue(EnabledProperty) is not true
        || border.GetValue(HookProperty) is StateHook || border.TemplatedParent is not Control cell
        || VisualTreeHelper.GetChildrenCount(cell) == 0
        || VisualTreeHelper.GetChild(cell, 0) is not FrameworkElement root) return;

      foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(root))
      {
        if (group.Name != "BorderStates") continue;
        var hook = new StateHook(root, group);
        border.SetValue(HookProperty, hook);
        hook.Start();
        break;
      }
    }

    private static void Detach(Border border)
    {
      if (border.GetValue(HookProperty) is not StateHook hook) return;
      hook.Stop();
      border.ClearValue(HookProperty);
    }

    private sealed class StateHook(FrameworkElement root, VisualStateGroup group)
    {
      internal void Start()
      {
        group.CurrentStateChanged += StateChanged;
        Finish(group.CurrentState);
      }

      internal void Stop() => group.CurrentStateChanged -= StateChanged;

      private void StateChanged(object sender, VisualStateChangedEventArgs args)
      {
        // WPF raises this before updating group.CurrentState; the event carries the state just started.
        Finish(args.NewState);
      }

      private void Finish(VisualState state)
      {
        if (!IsStaticFrozenBorder(state)) return;
        try
        {
          // VSM starts controllable storyboards on this template root. Fill, rather than Remove, keeps the border.
          state.Storyboard.SkipToFill(root);
        }
        catch (InvalidOperationException ex)
        {
          if (RepeatCap.SpeaksOnce("frozen-border.skip-to-fill"))
          {
            Log.Warn("Unable to finish the static frozen-column border animation", ex);
          }
        }
      }
    }
  }
}

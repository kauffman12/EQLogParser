using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class FrozenCellAnimationsTest
  {
    private const string CellTemplate = """
      <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Control}">
        <Grid x:Name="Root">
          <VisualStateManager.VisualStateGroups>
            <VisualStateGroup x:Name="BorderStates">
              <VisualState x:Name="NormalCell" />
              <VisualState x:Name="FrozenColumnCell">
                <Storyboard>
                  <ThicknessAnimationUsingKeyFrames Duration="1" Storyboard.TargetName="PART_FrozenCellBorder"
                                                    Storyboard.TargetProperty="BorderThickness">
                    <EasingThicknessKeyFrame KeyTime="0" Value="0,0,1,0" />
                  </ThicknessAnimationUsingKeyFrames>
                </Storyboard>
              </VisualState>
            </VisualStateGroup>
          </VisualStateManager.VisualStateGroups>
          <Border x:Name="PART_FrozenCellBorder" BorderThickness="0" />
        </Grid>
      </ControlTemplate>
      """;

    [TestMethod]
    public void RecognizesOnlyTheStaticOneDayFrozenBorderWithoutEditingItsTimeline()
    {
      var recognized = false;
      var durationUnchanged = false;
      Sta.Run(() =>
      {
        var state = CreateState();
        recognized = FrozenCellAnimations.IsStaticFrozenBorder(state);
        durationUnchanged = ((AnimationTimeline)state.Storyboard.Children[0]).Duration.TimeSpan == TimeSpan.FromDays(1);
      });
      Assert.IsTrue(recognized);
      Assert.IsTrue(durationUnchanged, "The fix must not edit a possibly shared or sealed theme storyboard.");
    }

    [TestMethod]
    [DataRow("other-state")]
    [DataRow("other-target")]
    [DataRow("other-property")]
    [DataRow("short-duration")]
    [DataRow("second-frame")]
    [DataRow("late-frame")]
    [DataRow("repeat")]
    [DataRow("reverse")]
    [DataRow("stop-fill")]
    [DataRow("parent-repeat")]
    [DataRow("parent-duration")]
    [DataRow("mixed-storyboard")]
    public void LeavesGenuineOrUnrecognizedAnimationsAlone(string variant)
    {
      var recognized = true;
      Sta.Run(() =>
      {
        var state = CreateState();
        var storyboard = state.Storyboard;
        var animation = (ThicknessAnimationUsingKeyFrames)storyboard.Children[0];
        switch (variant)
        {
          case "other-state": state.Name = "FooterColumnCell"; break;
          case "other-target": Storyboard.SetTargetName(animation, "OtherBorder"); break;
          case "other-property": Storyboard.SetTargetProperty(animation, new PropertyPath("Margin")); break;
          case "short-duration": animation.Duration = new Duration(TimeSpan.FromSeconds(1)); break;
          case "second-frame": animation.KeyFrames.Add(new EasingThicknessKeyFrame()); break;
          case "late-frame": animation.KeyFrames[0].KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)); break;
          case "repeat": animation.RepeatBehavior = RepeatBehavior.Forever; break;
          case "reverse": animation.AutoReverse = true; break;
          case "stop-fill": animation.FillBehavior = FillBehavior.Stop; break;
          case "parent-repeat": storyboard.RepeatBehavior = RepeatBehavior.Forever; break;
          case "parent-duration": storyboard.Duration = new Duration(TimeSpan.FromDays(2)); break;
          case "mixed-storyboard": storyboard.Children.Add(new DoubleAnimation()); break;
        }
        recognized = FrozenCellAnimations.IsStaticFrozenBorder(state);
      });
      Assert.IsFalse(recognized, variant);
    }

    [TestMethod]
    public void FinishesExistingStateAndLaterEntriesForCellsCreatedAfterConfiguration()
    {
      var firstFilled = false;
      var reentryFilled = false;
      var newCellFilled = false;
      var borderPreserved = false;
      Sta.Run(() =>
      {
        var scope = new StackPanel();
        FrozenCellAnimations.Configure(scope);
        FrozenCellAnimations.Configure(scope);
        var first = AddCell(scope);
        var second = AddCell(scope);
        try
        {
          // VSM can enter the state before the template's Loaded event.
          VisualStateManager.GoToState(first.Cell, "FrozenColumnCell", false);
          RaiseLoaded(first.Border);
          firstFilled = WaitForFill(first.Storyboard, first.Root);
          borderPreserved = first.Border.BorderThickness == new Thickness(0, 0, 1, 0);

          VisualStateManager.GoToState(first.Cell, "NormalCell", false);
          VisualStateManager.GoToState(first.Cell, "FrozenColumnCell", false);
          reentryFilled = WaitForFill(first.Storyboard, first.Root);

          RaiseLoaded(second.Border);
          VisualStateManager.GoToState(second.Cell, "FrozenColumnCell", false);
          newCellFilled = WaitForFill(second.Storyboard, second.Root);
        }
        finally
        {
          first.Storyboard.Remove(first.Root);
          second.Storyboard.Remove(second.Root);
          RaiseUnloaded(first.Border);
          RaiseUnloaded(second.Border);
        }
      });
      Assert.IsTrue(firstFilled);
      Assert.IsTrue(reentryFilled,
        "StateChanged must use NewState; WPF has not updated CurrentState inside that event.");
      Assert.IsTrue(newCellFilled, "New containers must inherit the opt-in without another table configuration call.");
      Assert.IsTrue(borderPreserved, "Removing the animation instead of filling it loses the frozen-column boundary.");
    }

    [TestMethod]
    public void UnloadDetachesAndReloadFinishesAStateEnteredWhileDetached()
    {
      var initiallyFilled = false;
      var detachedActive = false;
      var reloadedFilled = false;
      Sta.Run(() =>
      {
        var scope = new StackPanel();
        FrozenCellAnimations.Configure(scope);
        var cell = AddCell(scope);
        try
        {
          RaiseLoaded(cell.Border);
          VisualStateManager.GoToState(cell.Cell, "FrozenColumnCell", false);
          initiallyFilled = WaitForFill(cell.Storyboard, cell.Root);
          RaiseUnloaded(cell.Border);
          VisualStateManager.GoToState(cell.Cell, "NormalCell", false);
          VisualStateManager.GoToState(cell.Cell, "FrozenColumnCell", false);
          PumpUntil(() => cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active);
          detachedActive = cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active;
          RaiseLoaded(cell.Border);
          reloadedFilled = WaitForFill(cell.Storyboard, cell.Root);
        }
        finally
        {
          cell.Storyboard.Remove(cell.Root);
          RaiseUnloaded(cell.Border);
        }
      });
      Assert.IsTrue(initiallyFilled);
      Assert.IsTrue(detachedActive);
      Assert.IsTrue(reloadedFilled);
    }

    [TestMethod]
    public void AReplacementTemplateIsHandledWithoutReloadingTheCellOrReconfiguringTheScope()
    {
      var replacementFilled = false;
      Sta.Run(() =>
      {
        var scope = new StackPanel();
        FrozenCellAnimations.Configure(scope);
        var old = AddCell(scope);
        RaiseLoaded(old.Border);
        RaiseUnloaded(old.Border);
        old.Cell.Template = (ControlTemplate)XamlReader.Parse(CellTemplate);
        old.Cell.ApplyTemplate();
        var replacement = Parts(old.Cell);
        try
        {
          RaiseLoaded(replacement.Border);
          VisualStateManager.GoToState(replacement.Cell, "FrozenColumnCell", false);
          replacementFilled = WaitForFill(replacement.Storyboard, replacement.Root);
        }
        finally
        {
          replacement.Storyboard.Remove(replacement.Root);
          RaiseUnloaded(replacement.Border);
        }
      });
      Assert.IsTrue(replacementFilled);
    }

    [TestMethod]
    public void AnUnconfiguredScopeKeepsItsOriginalAnimation()
    {
      var stillActive = false;
      Sta.Run(() =>
      {
        var cell = AddCell(new StackPanel());
        try
        {
          RaiseLoaded(cell.Border);
          VisualStateManager.GoToState(cell.Cell, "FrozenColumnCell", false);
          PumpUntil(() => cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active);
          stillActive = cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active;
        }
        finally
        {
          cell.Storyboard.Remove(cell.Root);
          RaiseUnloaded(cell.Border);
        }
      });
      Assert.IsTrue(stillActive);
    }

    private static VisualState CreateState()
    {
      var animation = new ThicknessAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromDays(1)) };
      animation.KeyFrames.Add(new EasingThicknessKeyFrame
      {
        KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = new Thickness(0, 0, 1, 0)
      });
      Storyboard.SetTargetName(animation, "PART_FrozenCellBorder");
      Storyboard.SetTargetProperty(animation, new PropertyPath("BorderThickness"));
      var storyboard = new Storyboard();
      storyboard.Children.Add(animation);
      return new VisualState { Name = "FrozenColumnCell", Storyboard = storyboard };
    }

    private static (Control Cell, FrameworkElement Root, Border Border, Storyboard Storyboard) AddCell(Panel scope)
    {
      var cell = new Control { Template = (ControlTemplate)XamlReader.Parse(CellTemplate) };
      scope.Children.Add(cell);
      cell.ApplyTemplate();
      return Parts(cell);
    }

    private static (Control Cell, FrameworkElement Root, Border Border, Storyboard Storyboard) Parts(Control cell)
    {
      var root = (FrameworkElement)VisualTreeHelper.GetChild(cell, 0);
      var border = (Border)cell.Template.FindName("PART_FrozenCellBorder", cell);
      if (VisualStateManager.GetVisualStateGroups(root)[0] is not VisualStateGroup group
        || group.States[1] is not VisualState { Storyboard: { } storyboard })
      {
        throw new InvalidOperationException("The fixture's frozen border state is missing");
      }
      return (cell, root, border, storyboard);
    }

    private static void RaiseLoaded(Border border) =>
      border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

    private static void RaiseUnloaded(Border border) =>
      border.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

    private static bool WaitForFill(Storyboard storyboard, FrameworkElement root)
    {
      PumpUntil(() => storyboard.GetCurrentState(root) == ClockState.Filling);
      return storyboard.GetCurrentState(root) == ClockState.Filling;
    }

    private static void PumpUntil(Func<bool> done)
    {
      var frame = new DispatcherFrame();
      var deadline = Environment.TickCount64 + 1500;
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
    }
  }
}

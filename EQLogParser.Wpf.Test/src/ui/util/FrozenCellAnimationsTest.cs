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
        using var host = new CellHost(scope);
        try
        {
          // VSM can enter the state before the template's Loaded event.
          EnterState(first.Cell, "FrozenColumnCell");
          host.Show();
          firstFilled = WaitForFill(first.Storyboard, first.Root);
          borderPreserved = first.Border.BorderThickness == new Thickness(0, 0, 1, 0);

          EnterState(first.Cell, "NormalCell");
          EnterState(first.Cell, "FrozenColumnCell");
          reentryFilled = WaitForFill(first.Storyboard, first.Root);

          var second = AddCell(scope);
          try
          {
            EnterState(second.Cell, "FrozenColumnCell");
            newCellFilled = WaitForFill(second.Storyboard, second.Root);
          }
          finally
          {
            second.Storyboard.Remove(second.Root);
          }
        }
        finally
        {
          first.Storyboard.Remove(first.Root);
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
        using var host = new CellHost(scope);
        try
        {
          host.Show();
          EnterState(cell.Cell, "FrozenColumnCell");
          initiallyFilled = WaitForFill(cell.Storyboard, cell.Root);
          scope.Children.Remove(cell.Cell);
          PumpUntil(() => !cell.Border.IsLoaded);
          if (cell.Border.IsLoaded) throw new InvalidOperationException("The fixture cell did not unload.");
          EnterState(cell.Cell, "NormalCell");
          EnterState(cell.Cell, "FrozenColumnCell");
          PumpUntil(() => cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active);
          detachedActive = cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active;
          scope.Children.Add(cell.Cell);
          reloadedFilled = WaitForFill(cell.Storyboard, cell.Root);
        }
        finally
        {
          cell.Storyboard.Remove(cell.Root);
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
        using var host = new CellHost(scope);
        host.Show();
        PumpUntil(() => old.Border.IsLoaded);
        if (!old.Border.IsLoaded) throw new InvalidOperationException("The original template did not load.");
        old.Cell.Template = (ControlTemplate)XamlReader.Parse(CellTemplate);
        old.Cell.ApplyTemplate();
        var replacement = Parts(old.Cell);
        try
        {
          EnterState(replacement.Cell, "FrozenColumnCell");
          replacementFilled = WaitForFill(replacement.Storyboard, replacement.Root);
        }
        finally
        {
          replacement.Storyboard.Remove(replacement.Root);
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
        var scope = new StackPanel();
        var cell = AddCell(scope);
        using var host = new CellHost(scope);
        try
        {
          host.Show();
          EnterState(cell.Cell, "FrozenColumnCell");
          PumpUntil(() => cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active);
          stillActive = cell.Storyboard.GetCurrentState(cell.Root) == ClockState.Active;
        }
        finally
        {
          cell.Storyboard.Remove(cell.Root);
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
      var cell = new Control { Height = 20, Template = (ControlTemplate)XamlReader.Parse(CellTemplate) };
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

    private static void EnterState(Control cell, string state)
    {
      if (!VisualStateManager.GoToState(cell, state, false))
      {
        throw new InvalidOperationException($"The fixture could not enter {state}.");
      }
    }

    private static bool WaitForFill(Storyboard storyboard, FrameworkElement root)
    {
      PumpUntil(() => storyboard.GetCurrentState(root) == ClockState.Filling);
      return storyboard.GetCurrentState(root) == ClockState.Filling;
    }

    // A dispatcher alone is not a presentation source for animation timing; use real load/unload events too.
    private sealed class CellHost(Panel scope) : IDisposable
    {
      private readonly Window _window = new()
      {
        Content = scope,
        Width = 160,
        Height = 100,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize
      };

      public void Dispose() => _window.Close();

      internal void Show() => _window.Show();
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

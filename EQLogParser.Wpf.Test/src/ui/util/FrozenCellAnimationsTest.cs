using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class FrozenCellAnimationsTest
  {
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
  }
}

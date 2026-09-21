using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using System;
using System.Windows;

namespace EQLogParser
{
  /*
   * The configure-mode lane guide, drawn straight onto an Skia canvas. What is pinned here is the ordering the guide used to get wrong:
   * its SKPaints come from EnsureSkiaResources, which ran only when a number was measured or a font fetched, so a canvas asked for its map
   * of the lanes before its first hit configured a null paint. WPF calls the render from inside layout, the dispatcher swallows what comes
   * back, and the visible result was a stutter plus a stack trace per resize - which is how it reached a player's log rather than a crash.
   * Drawing on an empty canvas is most of this test: before the guard, the first case threw.
   *
   * Every case runs through Sta, because FctSkiaCanvas derives from UIElement and WPF will not construct one on the MTA thread MSTest
   * supplies - the constructor needs the thread's InputManager, long before any drawing is asked of it. The wrapper is the test's only
   * unusual feature and says nothing about the guide: see Sta.cs.
   */
  [TestClass]
  public sealed class FctLaneGuidePaintTest
  {
    /* An overlay opened unlocked shows its lane map on a surface that has never spawned a number. */
    [TestMethod]
    public void TheGuideOfACanvasThatNeverDrewANumberDraws() => Sta.Run(GuideOnAnUntouchedCanvas);

    /* The guide follows the booking: a ByType layout whose categories were all sent to "none" has no column left to outline. */
    [TestMethod]
    public void ALayoutWithNoLanesDrawsNothing() => Sta.Run(GuideOnALayoutWithNoBookedLanes);

    /* Bands has no columns to map, so the guide leaves the surface alone even in configure mode. */
    [TestMethod]
    public void BandsHasNoGuide() => Sta.Run(GuideOnBands);

    /*
     * The case that failed before the guard: measured and arranged by hand, because DrawLaneGuide reads ActualWidth/ActualHeight and a bare
     * element has none until somebody lays it out. The surface below has never been near a hit, so nothing has called EnsureSkiaResources.
     */
    private static void GuideOnAnUntouchedCanvas()
    {
      var canvas = new FctSkiaCanvas();
      try
      {
        canvas.Measure(new Size(320, 200));
        canvas.Arrange(new Rect(0, 0, 320, 200));
        canvas.Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Right);
        canvas.SetConfigure(true);

        using var target = new SKBitmap(320, 200);
        using var skia = new SKCanvas(target);
        skia.Clear(SKColors.Transparent);

        canvas.DrawLaneGuide(skia); // the guard lives inside this call: resources before anything is drawn with them

        Assert.IsTrue(HasInk(target), "the guide drew nothing on a layout that books all three categories");
      }
      finally
      {
        canvas.Stop();
      }
    }

    private static void GuideOnALayoutWithNoBookedLanes()
    {
      var canvas = new FctSkiaCanvas();
      try
      {
        canvas.Measure(new Size(320, 200));
        canvas.Arrange(new Rect(0, 0, 320, 200));
        canvas.Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Right,
          healLane: FctRailLane.None, incomingDamageLane: FctRailLane.None, outgoingDamageLane: FctRailLane.None);
        canvas.SetConfigure(true);

        using var target = new SKBitmap(320, 200);
        using var skia = new SKCanvas(target);
        skia.Clear(SKColors.Transparent);
        canvas.DrawLaneGuide(skia);

        Assert.IsFalse(HasInk(target), "a category nobody booked is not outlined");
      }
      finally
      {
        canvas.Stop();
      }
    }

    private static void GuideOnBands()
    {
      var canvas = new FctSkiaCanvas();
      try
      {
        canvas.Measure(new Size(320, 200));
        canvas.Arrange(new Rect(0, 0, 320, 200));
        canvas.Layout = FctLayoutChoice.Bands;
        canvas.SetConfigure(true);

        using var target = new SKBitmap(320, 200);
        using var skia = new SKCanvas(target);
        skia.Clear(SKColors.Transparent);
        canvas.DrawLaneGuide(skia);

        Assert.IsFalse(HasInk(target), "Bands has no columns, so there is nothing to outline");
      }
      finally
      {
        canvas.Stop();
      }
    }

    /* Any non-transparent pixel: the guide's own fills are alpha 14-84 over a cleared surface, so one lit pixel means it drew. */
    private static bool HasInk(SKBitmap bitmap)
    {
      for (var y = 0; y < bitmap.Height; y += 2)
      {
        for (var x = 0; x < bitmap.Width; x += 2)
        {
          if (bitmap.GetPixel(x, y).Alpha != 0)
          {
            return true;
          }
        }
      }

      return false;
    }
  }
}

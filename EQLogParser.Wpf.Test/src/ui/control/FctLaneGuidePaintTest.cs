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
   */
  [TestClass]
  public sealed class FctLaneGuidePaintTest
  {
    /* An overlay opened unlocked shows its lane map on a surface that has never spawned a number. */
    [TestMethod]
    public void TheGuideOfACanvasThatNeverDrewANumberDraws()
    {
      var canvas = new FctSkiaCanvas();
      try
      {
        // measured and arranged by hand: DrawLaneGuide reads ActualWidth/ActualHeight, and a bare element has none until asked
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

    /* The guide follows the booking: a ByType layout whose categories were all sent to "none" has no column left to outline. */
    [TestMethod]
    public void ALayoutWithNoLanesDrawsNothing()
    {
      var canvas = new FctSkiaCanvas();
      try
      {
        // measured and arranged by hand: DrawLaneGuide reads ActualWidth/ActualHeight, and a bare element has none until asked
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

    /* Bands has no columns to map, so the guide leaves the surface alone even in configure mode. */
    [TestMethod]
    public void BandsHasNoGuide()
    {
      var canvas = new FctSkiaCanvas();
      try
      {
        // measured and arranged by hand: DrawLaneGuide reads ActualWidth/ActualHeight, and a bare element has none until asked
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

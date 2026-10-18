using SkiaSharp;
using System.Windows;

namespace EQLogParser
{
  [TestClass]
  public sealed class FctLaneGuidePaintTest
  {
    [TestMethod]
    public void DrawingBeforeAnyHitInitializesThePaintResources()
    {
      Sta.Run(() =>
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
          // A new configure surface has never measured a hit; drawing must initialize its own paints.
          canvas.DrawLaneGuide(skia);
        }
        finally
        {
          canvas.Stop();
        }
      });
    }
  }
}

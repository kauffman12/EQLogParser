using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Resizing, which makes two promises.
   *
   * The first is about sizes: a drag settles on one of the values the layout was measured at, and anywhere in between if the hand
   * stops there, but never below what the layout can draw in. The second is about the numbers already in flight: motion is a pure
   * function of (hit, age) with no canvas argument, so unless somebody revisits their geometry a resize leaves them drawing where
   * the old window used to be — probed at 980x640 shrunk to 620x400 that was 10 of 15 held numbers outside the overlay. FctResize
   * is that somebody, and the tests below are the difference between that measurement and zero.
   */
  [TestClass]
  public sealed class FctResizeTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
    /* Near an offered size it settles on it; between offers it stays exactly where the drag left it. */
    [TestMethod]
    [DataRow(808.0, 566.0, 800.0, 560.0)]
    [DataRow(792.0, 632.0, 800.0, 640.0)]
    [DataRow(724.0, 512.0, 720.0, 520.0)]
    [DataRow(820.0, 540.0, 820.0, 540.0)]
    public void ADragSettlesOnTheNearestOffer(double width, double height, double wantW, double wantH)
    {
      FctResize.Fit(width, height, 2560, 1440, out var fittedW, out var fittedH);

      Assert.AreEqual(wantW, fittedW, 0.001, $"{width}x{height} did not settle right");
      Assert.AreEqual(wantH, fittedH, 0.001, $"{width}x{height} did not settle down");
    }

    /* The floor is what the layout was probed at; the ceiling is whatever the caller says the screen is. */
    [TestMethod]
    public void FitStaysBetweenTheLayoutsFloorAndTheScreensCeiling()
    {
      FctResize.Fit(120, 90, 2560, 1440, out var smallW, out var smallH);
      Assert.IsTrue(smallW >= FctResize.MinWidth && smallH >= FctResize.MinHeight,
        $"a drag below the floor came back {smallW}x{smallH}: that band is shallower than a line of text");

      FctResize.Fit(40000, 40000, 1280, 800, out var bigW, out var bigH);
      Assert.IsTrue(bigW <= 1280 && bigH <= 800, $"a drag past the screen came back {bigW}x{bigH}");
    }

    /* Snapping is per axis, so dragging one edge gets the magnet too — that is why an edge drag cannot land on a lopsided size
     * just because the other axis happened to be mid-air. */
    [TestMethod]
    public void DraggingOneAxisStillSnaps()
    {
      FctResize.Fit(764, 540, 2560, 1440, out var fittedW, out var fittedH);

      Assert.AreEqual(760, fittedW, 0.001, "the dragged axis ignored the offer beside it");
      Assert.AreEqual(540, fittedH, 0.001, "the axis nobody touched moved");
    }

    /*
     * The resize itself: shrink, grow and shrink again, with numbers in flight for every style. Each one must stay inside the
     * window and out of the protected strip at every point of its remaining life — the invariant the overlay has always had, now
     * demanded across a size change instead of within one.
     */
    [TestMethod]
    [DataRow(nameof(FctMotionStyle.Freeze))]
    [DataRow(nameof(FctMotionStyle.Fountain))]
    [DataRow(nameof(FctMotionStyle.Spray))]
    public void NumbersInFlightComeAlongWithTheWindow(string style)
    {
      var motions = Enum.Parse<FctMotionStyle>(style);

      foreach (var size in new[] { (620.0, 400.0), (1400.0, 900.0), (FctResize.MinWidth, FctResize.MinHeight) })
      {
        var hits = LiveHits(motions, 980, 640, 12);
        FctResize.Rescale(hits, 980, 640, size.Item1, size.Item2);

        foreach (var hit in hits)
        {
          for (var t = 0.0; t <= 1.0; t += 0.1)
          {
            var block = BlockOf(hit, FctMotion.Progress(hit, t) * hit.LifetimeMs);

            Assert.IsTrue(block.Left > -1 && block.Right < size.Item1 + 1,
              $"{motions}: a number drawn {(block.Left):0.#}..{(block.Right):0.#} after a resize to {size.Item1:0}x{size.Item2:0}");
            Assert.IsTrue(block.Top > FctLayout.EdgePad - 1 && block.Bottom < size.Item2 + 1,
              $"{motions}: a number drawn {(block.Top):0.#}..{(block.Bottom):0.#} vertically after that resize");

            /* The strip belongs to EQ, and that is not negotiable across a resize either. */
            if (hit.Incoming)
            {
              Assert.IsTrue(block.Top >= (size.Item2 * FctLayout.GapBottomFrac) - 1,
                $"{motions}: a hit on me rose into the protected strip after resizing to {size.Item1:0}x{size.Item2:0}");
            }
            else
            {
              Assert.IsTrue(block.Bottom <= (size.Item2 * FctLayout.GapTopFrac) + 1,
                $"{motions}: my hit dropped into the protected strip after resizing to {size.Item1:0}x{size.Item2:0}");
            }
          }
        }
      }
    }

    /* Free text scales by ratio and bands are re-derived from the size, so going back should come home. Tolerances are generous
     * because the clamps are allowed to have had an opinion in the middle — this pins that nothing is lost, not that pixels match. */
    [TestMethod]
    public void GrowingThenShrinkingBringsNumbersHome()
    {
      var hits = LiveHits(FctMotionStyle.Fountain, 980, 640, 10);

      var before = new double[hits.Count];
      for (var i = 0; i < hits.Count; i++)
      {
        before[i] = FctMotion.ArcedX(hits[i], 0.5);
      }

      FctResize.Rescale(hits, 980, 640, 1400, 900);
      FctResize.Rescale(hits, 1400, 900, 980, 640);

      for (var i = 0; i < hits.Count; i++)
      {
        Assert.IsTrue(Math.Abs(FctMotion.ArcedX(hits[i], 0.5) - before[i]) < 12,
          $"a number ended {(FctMotion.ArcedX(hits[i], 0.5) - before[i]):0.#}px from where it started after a round trip");
      }
    }

    /* A resize that is not a resize must be ignored: OnRenderSizeChanged fires during first layout with an empty previous size,
     * and mapping by a ratio of zero would flatten every number onto the top-left corner. */
    [TestMethod]
    public void ASizingThatIsNotOneChangesNothing()
    {
      var hits = LiveHits(FctMotionStyle.Freeze, 980, 640, 6);
      var first = hits[0].X0;
      var rise = hits[0].Rise;

      FctResize.Rescale(hits, 0, 0, 980, 640);
      FctResize.Rescale(hits, 980, 640, 980.2, 640.1);

      Assert.AreEqual(first, hits[0].X0, 0.001, "an unmeasurable canvas moved a number");
      Assert.AreEqual(rise, hits[0].Rise, 0.001, "a change too small to see changed the travel");
    }

    private static List<FctHitState> LiveHits(FctMotionStyle style, double w, double h, int count)
    {
      var hits = new List<FctHitState>();
      /* Bands, said out loud: these live hits exist so a resize can be watched against the strip it keeps clear. */
      var ingest = new FctIngest(new Random(5)) { Style = style, Layout = FctLayoutChoice.Bands };

      for (var i = 0; i < count; i++)
      {
        ingest.PruneExpired(hits, i * 240.0);

        foreach (var lane in new[] { FctLane.DamageDealt, FctLane.DamageTaken })
        {
          ingest.Accept(hits, lane, 1000 + (i * 61), "Flurry", i % 4 == 0, false, false, null, w, h, i * 240.0);
        }
      }

      return hits;
    }

    /* What a hit covers on screen at one age: the drawn value's box, scaled the way the renderer scales it. A second copy of this
     * exists in FctLayoutTest and FctMotionTest for the same reason — they are separate test classes and the geometry is four
     * lines; sharing it would mean a test-only helper in production code. */
    private static (double Left, double Top, double Right, double Bottom) BlockOf(FctHitState hit, double ageMs)
    {
      var t = FctMotion.Progress(hit, ageMs);
      var scale = FctMotion.ScaleOf(hit, ageMs);
      var half = (hit.ValueWidth * scale) / 2.0;
      var x = FctMotion.ArcedX(hit, t);

      return (x - half, FctMotion.RaisedY(hit, t), x + half, FctMotion.RaisedY(hit, t) + (FctLayout.TextHeight(hit) * scale));
    }
  }
}
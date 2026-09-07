using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The configure-mode examples: five numbers held at chosen moments so a size or style can be seen without a fight. What is worth
   * asserting is that they are still real geometry (inside the canvas, in a band, with a tempo to be a phase OF) and that the dials
   * reach them — a preview that ignores the slider would be worse than no preview, because it teaches the setting does nothing.
   *
   * What they must never do is take part in policy: they are built here rather than handed to FctIngest, which is the mechanism, so
   * there is nothing to assert about folding or eviction beyond that fact.
   */
  [TestClass]
  public class FctPreviewTest
  {
    private const double CanvasWidth = 800;
    private const double CanvasHeight = 560;

    private static void AssertWellFormed(List<FctHitState> hits, int expected)
    {
      Assert.AreEqual(expected, hits.Count);

      foreach (var hit in hits)
      {
        Assert.IsTrue(hit.PreviewPhase >= 0.08 && hit.PreviewPhase <= 0.78,
          $"phase {hit.PreviewPhase} would freeze an example mid-fade or before it appears");
        Assert.IsTrue(hit.LifetimeMs > 0 && hit.MotionMs > 0, $"{hit.Lane} has no flight for the phase to be a fraction of");
        Assert.AreEqual(0, hit.SpawnMs, "an example is not stamped with wall-clock time: its backend draws it by phase");
        /* Pulse is the one style that puts a number in one place and lets it sit there — "a fixed grid, not choreography" — so its
           examples carry size and fade rather than a direction. Everything else has to show which way it runs. */
        if (hit.Style is not FctMotionStyle.Pulse)
        {
          Assert.IsTrue(hit.Rise != 0, $"{hit.Lane} in {hit.Style} has no travel direction, so it cannot show what the style looks like");
        }
        Assert.IsTrue(hit.ValueWidth > 0 && hit.ValueFontSize > 0, $"{hit.Lane} has no size to judge");

        var height = hit.ValueFontSize * 1.3; // a generous line box: the assertion is about being roughly inside the canvas
        Assert.IsTrue(hit.X0 >= -1 && hit.X0 <= CanvasWidth + 1, $"{hit.Lane} sits outside the canvas at x {hit.X0}");
        Assert.IsTrue(hit.Y0 >= -height && hit.Y0 <= CanvasHeight + height, $"{hit.Lane} sits outside the canvas at y {hit.Y0}");
      }
    }

    [TestMethod]
    public void Build_ExamplesSpreadAcrossBothBands()
    {
      var hits = FctPreview.Build(CanvasWidth, CanvasHeight, FctMotionStyle.Fountain);
      AssertWellFormed(hits, 5);

      /* Both directions have to be present: the whole point of the row is that dealt numbers and received numbers are told apart by
         which side of the middle they run, and one-sided examples would show half of it. */
      Assert.IsTrue(hits.Any(h => !h.Incoming), "no outgoing example");
      Assert.IsTrue(hits.Any(h => h.Incoming), "no incoming example");

      Assert.AreEqual(FctMotionStyle.Fountain, hits[0].Style);

      // spread left to right, so the examples cannot stack on each other
      var xs = hits.Select(h => h.X0).ToList();
      Assert.IsTrue(xs.Max() - xs.Min() > CanvasWidth * 0.5, "examples are clustered instead of spread across the canvas");

      // one example carries a fold count, because that is a look nothing else in the row has
      Assert.IsTrue(hits.Any(h => h.MergeCount > 1), "nothing shows the ×N fold display");

      // and one is a word rather than a number: labels are drawn by the same path and sized by the same rules
      Assert.IsTrue(hits.Any(h => h.FixedText != null), "no label example (DODGE/IMMUNE) to judge");
    }

    [TestMethod]
    public void Build_EveryStyleLaysOut()
    {
      foreach (FctMotionStyle style in Enum.GetValues(typeof(FctMotionStyle)))
      {
        var hits = FctPreview.Build(CanvasWidth, CanvasHeight, style);
        AssertWellFormed(hits, 5);

        /*
         * Falling belongs to the two choreographed styles and to both bands, mirrored: an outgoing number turns over toward the bottom
         * of the screen, an incoming one back up toward the protected strip — that mirroring is what keeps the incoming band from
         * parking its numbers against its own bottom edge. The examples must carry exactly what FctIngest bakes (same call, same order)
         * because a preview showing a look the real overlay does not have is worse than no preview: it teaches the setting does
         * something it did not.
         */
        var falls = style is FctMotionStyle.Fountain or FctMotionStyle.Spray;
        foreach (var hit in hits)
        {
          Assert.AreEqual(falls, hit.FallDist != 0, $"{style} {hit.Lane}: fall does not match what FctIngest bakes");

          if (falls)
          {
            Assert.IsTrue(hit.Incoming ? hit.FallDist < 0 : hit.FallDist > 0,
              $"{style} {hit.Lane}: wrong fall direction — outgoing sinks toward the bottom, incoming back up toward the strip");
          }
        }
      }
    }

    [TestMethod]
    public void Build_TextScaleReachesTheExamples()
    {
      var before = FctPreview.Build(CanvasWidth, CanvasHeight, FctMotionStyle.Hold);

      var previous = FctScale.Text;
      try
      {
        FctScale.Text = FctScale.Max;
        var after = FctPreview.Build(CanvasWidth, CanvasHeight, FctMotionStyle.Hold);

        for (var i = 0; i < before.Count; i++)
        {
          Assert.IsTrue(after[i].ValueFontSize > before[i].ValueFontSize,
            "the size dial did not change the examples, so configure mode would look like it does nothing");
        }
      }
      finally
      {
        FctScale.Text = previous;
      }
    }

    [TestMethod]
    public void Build_NothingToLayOutInACanvasTooSmall()
    {
      // the configure row reserves its height, so a canvas this small is not a state to draw examples into
      CollectionAssert.AreEqual(new FctHitState[0], FctPreview.Build(200, 200, FctMotionStyle.Pulse));
    }
  }
}

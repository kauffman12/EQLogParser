namespace EQLogParser
{
  /*
   * Motion is pure maths over (hit, age), so the whole animation contract is assertable without a window:
   * the label text must survive, the hold style must stop where it says, the fountain must come back down,
   * and crit scale must actually animate. Rationale for the timings: docs/DesignNotes.md → Floating Combat Text.
   */
  [TestClass]
  public sealed class FctMotionTest
  {
    private static FctHitState NewHit(double lifetimeMs = 3500) => new()
    {
      Lane = FctLane.DamageDealt,
      SpawnMs = 0,
      LifetimeMs = lifetimeMs,
      FadeMs = 875,
      MotionMs = 2000,
      Y0 = 500,
      Rise = 400,
      X0 = 700,
      Arc = 40,
      ValueWidth = 120,
      ValueFontSize = 31,
      TargetValue = 1000,
      CountBaseValue = 1000,
    };

    /*
     * The P0 regression: zero-damage records carry Value 0. If anything formats the value over a label, the
     * player sees "0" where "Dodge" belongs — and 0 is also a legal absorb amount, so it is not obviously wrong.
     */
    [TestMethod]
    public void LabelsAreNeverReplacedByTheNumericValue()
    {
      var hit = NewHit();
      hit.FixedText = Labels.Dodge;

      FctMotion.RefreshText(hit, 0);
      Assert.AreEqual(Labels.Dodge, hit.DisplayText);
      Assert.IsTrue(hit.TextDirty);

      // a later refresh (every frame does one) must not resurrect the number
      hit.TextDirty = false;
      FctMotion.RefreshText(hit, 1200);
      Assert.AreEqual(Labels.Dodge, hit.DisplayText);
      Assert.IsFalse(hit.TextDirty);
    }

    [TestMethod]
    public void NumericHitsShowTheFormattedValue()
    {
      var hit = NewHit();
      FctMotion.RefreshText(hit, 10);
      Assert.AreEqual("1,000", hit.DisplayText);
    }

    [TestMethod]
    public void CountUpInterpolatesThenSettles()
    {
      var hit = NewHit();
      hit.CountBaseValue = 1000;
      hit.TargetValue = 1500;
      hit.CountUpMs = FctMotion.CountUpMs;
      hit.AgeAtCountStartMs = 0;

      Assert.AreEqual(1000, FctMotion.DisplayValue(hit, 0), 0.001);
      Assert.AreEqual(1250, FctMotion.DisplayValue(hit, FctMotion.CountUpMs / 2), 0.001);
      Assert.AreEqual(1500, FctMotion.DisplayValue(hit, FctMotion.CountUpMs * 2), 0.001);

      FctMotion.RefreshText(hit, FctMotion.CountUpMs * 2);
      Assert.AreEqual("1,500", hit.DisplayText);
    }

    [TestMethod]
    public void HoldStyleRisesAndStays()
    {
      var hit = NewHit(); // FallDist 0 = hold style

      Assert.AreEqual(hit.Y0, FctMotion.RaisedY(hit, 0), 0.001);
      Assert.AreEqual(hit.Y0 - hit.Rise, FctMotion.RaisedY(hit, 1.0), 0.001);

      // monotonic upward travel: no bounce on the way up
      var previous = FctMotion.RaisedY(hit, 0);
      for (var t = 0.05; t <= 1.0; t += 0.05)
      {
        var y = FctMotion.RaisedY(hit, t);
        Assert.IsTrue(y <= previous + 0.0001, $"text moved down at t={t:0.00}");
        previous = y;
      }
    }

    [TestMethod]
    public void FountainStyleRisesThenFallsBackDown()
    {
      var hit = NewHit();
      hit.FallDist = 180;

      Assert.AreEqual(hit.Y0, FctMotion.RaisedY(hit, 0), 0.001);
      Assert.AreEqual(hit.Y0 - hit.Rise, FctMotion.RaisedY(hit, 1.0 - FctMotion.FallPhaseFrac), 0.5);

      // the fall is the second half of life: it ends below the apex, and it accelerates
      var top = hit.Y0 - hit.Rise;
      var mid = FctMotion.RaisedY(hit, 0.85);
      Assert.IsTrue(mid > top, "fountain text must come back down");
      Assert.AreEqual(top + hit.FallDist, FctMotion.RaisedY(hit, 1.0), 0.001);

      var firstHalf = FctMotion.RaisedY(hit, 0.9) - mid;
      var lastQuarter = FctMotion.RaisedY(hit, 1.0) - FctMotion.RaisedY(hit, 0.9);
      Assert.IsTrue(lastQuarter > firstHalf, "fall should accelerate (ease-in)");
    }

    [TestMethod]
    public void ScalePopsForCritsOnly()
    {
      var normal = NewHit();
      Assert.AreEqual(1.0, FctMotion.ScaleOf(normal, 0), 0.001);
      Assert.AreEqual(1.0, FctMotion.ScaleOf(normal, 1500), 0.001);

      var crit = NewHit(2800);
      crit.Lane = FctLane.Crit;
      crit.Blowout = true;

      Assert.AreEqual(1.0, FctMotion.ScaleOf(crit, 0), 0.001);
      Assert.AreEqual(FctMotion.CritPeakScale, FctMotion.ScaleOf(crit, FctMotion.CritScaleInMs), 0.001);
      Assert.AreEqual(FctMotion.CritPeakScale, FctMotion.ScaleOf(crit, 1200), 0.001);

      // collapse over the tail so it shrinks out instead of blinking off
      var late = FctMotion.ScaleOf(crit, crit.LifetimeMs - 10);
      Assert.IsTrue(late < 0.3, $"crit should have mostly shrunk away, was {late:0.00}");
    }

    [TestMethod]
    public void OpacityFadesInHoldsThenOut()
    {
      var hit = NewHit();

      Assert.AreEqual(0.0, FctMotion.FadeOpacity(hit, 0), 0.001);
      Assert.AreEqual(1.0, FctMotion.FadeOpacity(hit, FctMotion.FadeInMs), 0.001);
      Assert.AreEqual(1.0, FctMotion.FadeOpacity(hit, 1500), 0.001);
      Assert.AreEqual(0.0, FctMotion.FadeOpacity(hit, hit.LifetimeMs), 0.001);

      // never negative past the end of life (the pruner removes it there, but drawing must stay legal)
      Assert.AreEqual(0.0, FctMotion.FadeOpacity(hit, hit.LifetimeMs + 5000), 0.001);
    }

    [TestMethod]
    public void ArcIsClampedToTheSidesAndSurvivesAnImpossibleWidth()
    {
      var hit = NewHit();
      hit.SideMin = 624; // right half of a 980 px canvas, inner edge at center + clearance
      hit.SideMax = 972;

      // a label wider than its half would invert the clamp band and used to throw inside Math.Clamp
      var wide = NewHit();
      wide.SideMin = 624;
      wide.SideMax = 972;
      wide.ValueWidth = 5000;

      for (var t = 0.0; t <= 1.0; t += 0.1)
      {
        var x = FctMotion.ArcedX(hit, t);
        Assert.IsTrue(x >= hit.SideMin - 0.001 && x <= hit.SideMax - 0.001, $"x={x} left the band at t={t:0.0}");
      }

      var middle = (wide.SideMin + wide.SideMax) / 2.0;
      Assert.AreEqual(middle, FctMotion.ArcedX(wide, 0.5), 0.001);
    }
  }
}

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

    /*
     * The incoming band mirrors the fountain rather than losing it: sink, stop, then accelerate back up toward the gap
     * by a fraction of the travel already spent below it. Mirrored because gravity points at the bottom of the screen
     * and that is where their band lives — a literal fall parks the number against its own bottom edge, which reads as
     * stuck rather than as physics.
     */
    [TestMethod]
    public void NegativeFallDistMirrorsTheFountainUpward()
    {
      var hit = NewHit();
      hit.Rise = -300;              // incoming hits travel downwards in bands mode: Rise is negative
      hit.FallDist = -150;          // and half of it comes back up, screen-relative

      var low = hit.Y0 - hit.Rise;  // deepest point, reached when the travel phase ends
      Assert.AreEqual(low, FctMotion.RaisedY(hit, 1.0 - FctMotion.FallPhaseFrac), 0.5);
      Assert.AreEqual(low + hit.FallDist, FctMotion.RaisedY(hit, 1.0), 0.001);

      var justAfter = FctMotion.RaisedY(hit, 0.70) - low;
      var muchLater = FctMotion.RaisedY(hit, 0.95) - low;
      Assert.IsTrue(muchLater < justAfter, "the mirrored fall accelerates upward; it does not drift");
      Assert.IsTrue(FctMotion.ScaleOf(hit, hit.MotionMs) < 1.0, "a hit in its fall phase shrinks, either direction");
    }

    /* The seam most parabolas show: travel hands over to fall. Neither side may arrive or leave with speed, or the
     * number visibly jerks at the exact moment it changes its mind about gravity. */
    [TestMethod]
    public void TravelHandsOverToFallWithoutAJerk()
    {
      var hit = NewHit();
      hit.FallDist = 180;

      var seam = 1.0 - FctMotion.FallPhaseFrac;

      double SpeedAt(double t) => (FctMotion.RaisedY(hit, t + 0.001) - FctMotion.RaisedY(hit, t - 0.001)) / 0.002;

      // px per unit of life: mid-travel is over 1300 here, so these two say the ends really do brake and start gently
      Assert.IsTrue(Math.Abs(SpeedAt(seam - 0.01)) < 60, $"still braking into the seam: {SpeedAt(seam - 0.01):0.#}");
      Assert.IsTrue(Math.Abs(SpeedAt(seam + 0.01)) < 60, $"already launched out of the seam: {SpeedAt(seam + 0.01):0.#}");
    }

    /* Opacity must not change slope visibly at fadeStart and must reach zero by the end of life: a linear ramp is
     * "steady, then suddenly dimmer, then gone", which is the pop the eased ends remove. */
    [TestMethod]
    public void FadeBeginsAndEndsGentlyAndNeverReverses()
    {
      var hit = NewHit();
      var fadeStart = hit.LifetimeMs - hit.FadeMs;

      Assert.IsTrue(FctMotion.FadeOpacity(hit, fadeStart * 0.5) > 0.99, "mid-life is fully lit");
      Assert.IsTrue(FctMotion.FadeOpacity(hit, fadeStart + (hit.FadeMs * 0.5)) is > 0.4 and < 0.6, "halfway through the fade is halfway dim");

      var before = FctMotion.FadeOpacity(hit, fadeStart - 1);
      var justAfter = FctMotion.FadeOpacity(hit, fadeStart + 1);
      Assert.IsTrue(before > 0.99 && justAfter > 0.99, $"fading must begin gently, was {before:0.00} -> {justAfter:0.00}");

      var prev = 1.0;
      for (var age = fadeStart - hit.FadeMs; age <= hit.LifetimeMs; age += 25)
      {
        var o = FctMotion.FadeOpacity(hit, age);
        Assert.IsTrue(o <= prev + 0.0001, $"opacity went back up at {age:0} ms");
        prev = o;
      }
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

    /*
     * A celled number barely moves, so its swell is the entire announcement: in small, past full size, settle onto it — and
     * never back below 1.0 afterwards, because a shrinking number reads as leaving before the fade has said so.
     */
    [TestMethod]
    public void PulseSwellsThenRestsAtFullSize()
    {
      var hit = NewHit();
      hit.Style = FctMotionStyle.Pulse;

      Assert.AreEqual(FctMotion.PulseStartScale, FctMotion.ScaleOf(hit, 0), 0.001);
      Assert.IsTrue(FctMotion.ScaleOf(hit, 60) > FctMotion.PulseStartScale, "it grows from the first frame");
      Assert.AreEqual(FctMotion.PulsePeakScale, FctMotion.ScaleOf(hit, FctMotion.PulseInMs), 0.001);
      Assert.AreEqual(1.0, FctMotion.ScaleOf(hit, FctMotion.PulseSettleMs), 0.001);
      Assert.AreEqual(1.0, FctMotion.ScaleOf(hit, hit.LifetimeMs - 100), 0.001, "and it rests at full size until the fade");

      // a crit outranks the style: it must still get its blowout pop, not the modest swell
      hit.Blowout = true;
      Assert.AreEqual(1.0, FctMotion.ScaleOf(hit, 0), 0.001);
      Assert.AreEqual(FctMotion.CritPeakScale, FctMotion.ScaleOf(hit, FctMotion.CritScaleInMs), 0.001);
    }

    /*
     * Spray and fountain have to differ in flight, not just in where numbers end up, so the shape of the path is pinned.
     * Both axes used to run on one ease curve, which made every spray angle a straight line from origin to apex: the cone
     * existed only in the endpoints and mid-flight a wide spray was a slanted fountain. Hold still draws a straight line -
     * that is what a thing with no gravity looks like - and spray must visibly leave that line.
     */
    [TestMethod]
    public void SprayDrawsAnArcWhileHoldGoesStraight()
    {
      var hold = ArcHit(FctMotionStyle.Hold);
      var spray = ArcHit(FctMotionStyle.Spray);

      for (var t = 0.05; t < 0.96; t += 0.05)
      {
        Assert.AreEqual(0.0, Deviation(hold, t), 0.01, $"hold drifted off its own line at t {t:0.##}");
      }

      var strayed = 0.0;
      for (var t = 0.05; t < 0.96; t += 0.05)
      {
        strayed = Math.Max(strayed, Deviation(spray, t));
      }

      Assert.IsTrue(strayed > 20, $"spray should draw an arc, not a line (never left the straight path by more than {strayed:0.#} px)");

      /*
       * And it must bend the useful way: most of the sideways travel spent before the climb ends, so the path flattens into
       * a fan at the top instead of arriving as a corner and dropping straight down.
       */
      var apex = 1.0 - FctMotion.FallPhaseFrac;
      var lateralAtApex = (At(spray, apex).X - spray.X0) / spray.Arc;
      Assert.IsTrue(lateralAtApex > 0.75, $"spray should be nearly spread out by the time it peaks ({lateralAtApex:0.##})");
    }

    /* Perpendicular distance from the straight origin-to-apex path, in px: zero means the hit is flying in a line. */
    private static double Deviation(FctHitState hit, double t)
    {
      var vx = At(hit, 1).X - hit.X0;
      var vy = At(hit, 1).Y - hit.Y0;
      var len = Math.Sqrt((vx * vx) + (vy * vy));
      if (len <= 0.001)
      {
        return 0;
      }

      var wx = At(hit, t).X - hit.X0;
      var wy = At(hit, t).Y - hit.Y0;
      return Math.Abs((wx * vy) - (wy * vx)) / len;
    }

    private static (double X, double Y) At(FctHitState hit, double t) => (FctMotion.ArcedX(hit, t), FctMotion.RaisedY(hit, t));

    /* A wide cone draw: well to one side as it climbs. Hold gets no fall so its line stays a line. */
    private static FctHitState ArcHit(FctMotionStyle style)
    {
      var hit = NewHit();
      hit.Style = style;
      hit.BandMinY = 40;
      hit.BandMaxY = 520;
      hit.SideMin = 0;
      hit.SideMax = 1400;
      hit.X0 = 700;
      hit.Y0 = 480;
      hit.Rise = 220;
      hit.Arc = 300;
      hit.FallDist = style is FctMotionStyle.Spray ? 88 : 0;
      hit.MotionMs = style is FctMotionStyle.Spray ? FctMotion.SprayMotionWindowMs : FctMotion.MotionWindowMs;
      return hit;
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

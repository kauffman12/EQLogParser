namespace EQLogParser
{
  /*
   * Motion is pure maths over (hit, age), so the whole animation contract is assertable without a window:
   * the label text must survive, the freeze style must stop where it says, the fountain must come back down,
   * and crit scale must actually animate. Rationale for the timings: docs/DesignNotes.md → Floating Combat Text.
   */
  [TestClass]
  public sealed class FctMotionTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
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
      Sway = 40,
      ValueWidth = 120,
      ValueFontSize = 31,
      Value = 1000,
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

      FctMotion.RefreshText(hit);
      Assert.AreEqual(Labels.Dodge, hit.DisplayText);
      Assert.IsTrue(hit.TextDirty);

      // a later refresh (every fold does one) must not resurrect the number
      hit.TextDirty = false;
      FctMotion.RefreshText(hit);
      Assert.AreEqual(Labels.Dodge, hit.DisplayText);
      Assert.IsFalse(hit.TextDirty);
    }

    [TestMethod]
    public void NumericHitsShowTheFormattedValue()
    {
      var hit = NewHit();
      FctMotion.RefreshText(hit);
      Assert.AreEqual("1,000", hit.DisplayText);
    }

    /*
     * A number standing for several identical hits says how many and never shows a total: the drawn amount has to stay a
     * value some single hit actually landed for, or the player divides to find out what happened.
     */
    [TestMethod]
    public void ARepeatedHitShowsItsCountInsteadOfATotal()
    {
      var hit = NewHit();

      FctMotion.RefreshText(hit);
      Assert.AreEqual("1,000", hit.DisplayText, "one hit is just its own number");

      hit.MergeCount = 2;
      FctMotion.RefreshText(hit);
      Assert.AreEqual("1,000 ×2", hit.DisplayText, "two identical hits are still a thousand each");

      hit.TextDirty = false;
      hit.MergeCount = 12;
      FctMotion.RefreshText(hit);
      Assert.AreEqual("1,000 ×12", hit.DisplayText);
      Assert.IsTrue(hit.TextDirty, "a changed count has to dirty the text so the backend rebuilds the glyphs");
    }

    [TestMethod]
    public void FreezeStyleRisesAndStays()
    {
      var hit = NewHit(); // FallDist 0 = freeze style

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
      Assert.AreEqual(hit.Y0 - hit.Rise, FctMotion.RaisedY(hit, FctMotion.ApexFraction(hit)), 0.5);

      // it ends below the apex, and it accelerates all the way there
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
      hit.Rise = -300;              // incoming hits travel downwards: Rise is negative
      hit.FallDist = -150;          // and half of it comes back up, screen-relative

      var low = hit.Y0 - hit.Rise;  // deepest point, reached at the apex of a flight that runs downward
      Assert.AreEqual(low, FctMotion.RaisedY(hit, FctMotion.ApexFraction(hit)), 0.5);
      Assert.AreEqual(low + hit.FallDist, FctMotion.RaisedY(hit, 1.0), 0.001);

      var justAfter = FctMotion.RaisedY(hit, 0.70) - low;
      var muchLater = FctMotion.RaisedY(hit, 0.95) - low;
      Assert.IsTrue(muchLater < justAfter, "the mirrored fall accelerates upward; it does not drift");

      // and it collapses on the fade's clock: sized down at the end of a life that is nearly over
      Assert.AreEqual(1.0, FctMotion.ScaleOf(hit, hit.LifetimeMs - hit.FadeMs), 0.001,
        "a choreographed number keeps the size its font gave it right up until the fade begins");
      Assert.IsTrue(FctMotion.ScaleOf(hit, hit.LifetimeMs) < 1.0, "a hit near the end of its life shrinks, either direction");
    }

    /*
     * The apex is the only still point in a ballistic flight, and it must be a gentle one: the number brakes into it and
     * leaves it without a jerk, or the moment it "changes its mind about gravity" is exactly the moment the eye catches a
     * kink. Pinned alongside the other half of what makes this a throw rather than a lift — it must LEAVE at speed, and
     * come back down moving. A curve that eases out of the spawn at zero velocity cannot do that: it parks at the top,
     * which is how the fountain read as hanging and melting before it was made a projectile.
     */
    [TestMethod]
    public void TheApexIsTheOnlyStillPointAndNothingJerksThroughIt()
    {
      var hit = NewHit();
      hit.FallDist = 180;

      var apex = FctMotion.ApexFraction(hit);

      double SpeedAt(double t) => (FctMotion.RaisedY(hit, t + 0.001) - FctMotion.RaisedY(hit, t - 0.001)) / 0.002;

      // px per unit of life. The launch is over 1300: a fountain throws its numbers out.
      Assert.IsTrue(Math.Abs(SpeedAt(0.002)) > 1000, $"a thrown number leaves at speed, was {SpeedAt(0.002):0.#}");
      Assert.IsTrue(Math.Abs(SpeedAt(apex - 0.01)) < 60, $"still braking into the apex: {SpeedAt(apex - 0.01):0.#}");
      Assert.IsTrue(Math.Abs(SpeedAt(apex + 0.01)) < 60, $"already launched out of the apex: {SpeedAt(apex + 0.01):0.#}");
      /* Constant acceleration is what "one curve, not two eased halves" actually means, and it is measurable: speed grows
         in proportion to time away from the apex (a parabola's central difference is its exact derivative), on both sides,
         with equal magnitudes — which is continuity through zero without a probe that straddles the turn-around. Two eased
         halves meet at zero speed too, but their acceleration reverses there, and the ratios below are where that shows. */
      Assert.IsTrue(SpeedAt(apex - 0.08) < 0 && SpeedAt(apex + 0.08) > 0,
        $"a fountain brakes into its apex and leaves it downward, was {SpeedAt(apex - 0.08):0.#} -> {SpeedAt(apex + 0.08):0.#}");

      var nearUp = Math.Abs(SpeedAt(apex - 0.02));
      var farUp = Math.Abs(SpeedAt(apex - 0.08));
      var nearDown = Math.Abs(SpeedAt(apex + 0.02));
      var farDown = Math.Abs(SpeedAt(apex + 0.08));
      Assert.IsTrue(Math.Abs((farUp / nearUp) - 4.0) < 0.05, $"climb decelerates at one rate: {nearUp:0.#} -> {farUp:0.#}");
      Assert.IsTrue(Math.Abs((farDown / nearDown) - 4.0) < 0.05, $"descent accelerates at one rate: {nearDown:0.#} -> {farDown:0.#}");
      Assert.IsTrue(Math.Abs(farUp - farDown) < 0.05 * farUp, $"the apex flips the sign but not the acceleration: {farUp:0.#} vs {farDown:0.#}");

      // monotone acceleration after the apex: equal slices of life, ever longer distances
      var previous = FctMotion.RaisedY(hit, apex);
      var step = (1.0 - apex) / 5.0;
      double gained = 0.0;
      for (var i = 1; i <= 5; i++)
      {
        var y = FctMotion.RaisedY(hit, apex + (step * i));
        Assert.IsTrue((y - previous) > gained, "the descent decelerates: each equal slice must cover more road than the last");
        gained = y - previous;
        previous = y;
      }
    }

    /*
     * The two numbers a flight is built from are the climb and the return, so their relationship IS the shape. This is the
     * closed form (apex at 1/(1+sqrt(k)), k being fall depth over climb) pinned at the three points that matter: a shallow
     * fall peaks late and gives the climb the clock, an even one peaks at the half, and no fall at all means no descent.
     * Getting this wrong is not a subtle visual difference — it moves every number in flight, including ones already on
     * screen when the window is resized (FctResize scales Rise and FallDist by the same factor, so k survives).
     */
    [TestMethod]
    public void TheApexSitsWhereBallisticsPutIt()
    {
      var hit = NewHit();
      hit.Rise = 300;

      hit.FallDist = 75;               // a quarter of the climb back down: the descent is a third of what the climb gets
      Assert.AreEqual(1.0 / 1.5, FctMotion.ApexFraction(hit), 0.0001);
      Assert.AreEqual(1.0 / 3.0, FctMotion.DescentFrac(hit), 0.0001);

      hit.FallDist = 300;              // the whole way back: out and down are the same journey run twice
      Assert.AreEqual(0.5, FctMotion.ApexFraction(hit), 0.0001);
      Assert.AreEqual(hit.Y0, FctMotion.RaisedY(hit, 1.0), 0.001, "a full-return fountain ends on the line it was born on");

      hit.FallDist = 0;                // not a choreographed flight at all: nothing descends
      Assert.AreEqual(1.0, FctMotion.ApexFraction(hit), 0.0001);
    }

    /* A number with a fall but no climb is dropped, not thrown. ApexFraction has no peak to find and the flight must still
     * move — a silent Y0 here is a number that sits in one place for its whole life and reads as a stuck overlay. */
    [TestMethod]
    public void ADroppedNumberStillFalls()
    {
      var hit = NewHit();
      hit.Rise = 0;
      hit.FallDist = 120;

      Assert.AreEqual(1.0, FctMotion.ApexFraction(hit), 0.0001);
      Assert.AreEqual(hit.Y0, FctMotion.RaisedY(hit, 0), 0.001);
      Assert.AreEqual(hit.Y0 + 120, FctMotion.RaisedY(hit, 1.0), 0.001);
      Assert.IsTrue(FctMotion.RaisedY(hit, 0.5) < hit.Y0 + 60, "it accelerates from rest rather than drifting at one rate");

      var mirrored = NewHit();
      mirrored.Rise = 0;
      mirrored.FallDist = -120;        // the incoming band's mirror of the same drop: it runs back up
      Assert.AreEqual(hit.Y0 - 120, FctMotion.RaisedY(mirrored, 1.0), 0.001);
    }

    /* Stretching the overlay must not re-shape a flight already in the air: FctResize multiplies Rise and FallDist by the
     * same sy, so k — the only input the apex fraction has — is invariant, and the path in units of the climb is identical. */
    [TestMethod]
    public void ResizingTheCanvasRescalesTheFlightWithoutReShapingIt()
    {
      var hit = NewHit();
      hit.Rise = 300;
      hit.FallDist = 180;

      var before = new double[11];
      for (var i = 0; i <= 10; i++)
      {
        before[i] = (hit.Y0 - FctMotion.RaisedY(hit, i / 10.0)) / hit.Rise;
      }

      var apexBefore = FctMotion.ApexFraction(hit);
      hit.Y0 *= 0.5;
      hit.Rise *= 0.5;
      hit.FallDist *= 0.5;             // exactly what FctResize does on a vertical drag

      Assert.AreEqual(apexBefore, FctMotion.ApexFraction(hit), 0.0001, "the peak moved as a share of life when only the canvas did");
      for (var i = 0; i <= 10; i++)
      {
        Assert.AreEqual(before[i], (hit.Y0 - FctMotion.RaisedY(hit, i / 10.0)) / hit.Rise, 0.0001,
          $"the path changed shape at t={i / 10.0:0.#}");
      }
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

    /*
     * The blowout envelope: in from below full size, rest AT it, collapse out. Resting at exactly 1.0 is load-bearing - the big class
     * carries its size in the font and the crit dial, so any scale above 1 here would multiply what the player set and quietly re-stack
     * the bug this curve used to be.
     */
    [TestMethod]
    public void ScaleSwellsForCritsOnly()
    {
      var normal = NewHit();
      Assert.AreEqual(1.0, FctMotion.ScaleOf(normal, 0), 0.001);
      Assert.AreEqual(1.0, FctMotion.ScaleOf(normal, 1500), 0.001);

      var crit = NewHit(2800);
      crit.Lane = FctLane.Crit;
      crit.Blowout = true;

      Assert.AreEqual(FctMotion.CritScaleInStart, FctMotion.ScaleOf(crit, 0), 0.001, "the swell starts from below full size - arrival, not overshoot");
      Assert.AreEqual(1.0, FctMotion.ScaleOf(crit, FctMotion.CritScaleInMs), 0.001);
      Assert.AreEqual(1.0, FctMotion.ScaleOf(crit, 1200), 0.001, "it rests at exactly the size the font says - the dial owns size, this curve never adds to it");

      // collapse over the tail so it shrinks out instead of blinking off
      var late = FctMotion.ScaleOf(crit, crit.LifetimeMs - 10);
      Assert.IsTrue(late < 0.3, $"crit should have mostly shrunk away, was {late:0.00}");
    }

    /*
     * The scale animation must not STEER. ArcedX used to compensate for the frame's blowout so the right edge stayed
     * nailed to the rail through it — which meant a dying crit walked sideways toward its own rail as it collapsed,
     * and on a straight line that looked absurd: every ordinary row rose straight up while crits and marks veered off
     * to the upper right. The centre is now the pivot (where the genre anchors its pops), so x cannot move with scale.
     * Pinned here two ways: the centre never shifts across the whole life, and the drawn box only ever tucks further
     * INSIDE the rail (scale stays <= 1), so the odometer's edge is unbroken in both directions.
     */
    [TestMethod]
    public void ACollapsedCritNeverWalksSideways()
    {
      var crit = NewHit(2800);
      crit.Lane = FctLane.Crit;
      crit.Blowout = true;
      crit.Style = FctMotionStyle.Straight;
      crit.SideMin = 0;
      crit.SideMax = 1400;

      for (var age = 0.0; age <= crit.LifetimeMs; age += 50)
      {
        var t = FctMotion.Progress(crit, age);
        var s = FctMotion.ScaleOf(crit, age);
        var centre = FctMotion.ArcedX(crit, t);
        Assert.AreEqual(crit.X0 - (crit.ValueWidth / 2.0), centre, 0.001, $"x moved at {age:0} ms - the scale is steering again");
        Assert.IsTrue(centre + ((crit.ValueWidth * s) / 2.0) <= crit.X0 + 0.001, $"the drawn box crossed the rail at {age:0} ms");
      }
    }

    /*
    /*
     * Spray and fountain have to differ in flight, not just in where numbers end up, so the shape of the path is pinned.
     * Both axes used to run on one ease curve, which made every spray angle a straight line from origin to apex: the cone
     * existed only in the endpoints and mid-flight a wide spray was a slanted fountain. Freeze still draws a straight line -
     * that is what a thing with no gravity looks like - and spray must visibly leave that line.
     */
    [TestMethod]
    public void SprayDrawsAnArcWhileFreezeGoesStraight()
    {
      var freeze = TraceHit(FctMotionStyle.Freeze);
      var spray = TraceHit(FctMotionStyle.Spray);

      for (var t = 0.05; t < 0.96; t += 0.05)
      {
        Assert.AreEqual(0.0, Deviation(freeze, t), 0.01, $"freeze drifted off its own line at t {t:0.##}");
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
      /*
       * And it must bend the way a throw bends. The old assertion here was that spray is "nearly spread out by the time it
       * peaks", which was the right shape for an easing vertical and wrong for an accelerating one: spending all the sideways
       * distance before the descent begins left the fan's outer numbers falling straight down, and put the widest point of the
       * flight in its middle. A projectile's sideways run is constant, so its widest point is the end of the arc.
       */
      var apex = FctMotion.ApexFraction(spray);
      var atApex = (At(spray, apex).X - spray.X0) / spray.Sway;
      Assert.IsTrue(Math.Abs(atApex - apex) < 0.01,
        $"spray's sideways run is not constant: {atApex:P0} of the way out at {apex:P0} of its life");
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

    /* Rail coordinates: a right-aligned value draws in a box hanging ValueWidth/2 LEFT of the rail, and X0 *is* the
       rail (FctMotion.ArcedX). Putting the half-width back lets these asserts measure choreography against the spawn
       point instead of against the alignment. */
    private static (double X, double Y) At(FctHitState hit, double t) => (FctMotion.ArcedX(hit, t) + (hit.ValueWidth / 2.0), FctMotion.RaisedY(hit, t));

    /*
     * The horizontal half of the same law that made the fountain a projectile: nothing in flight accelerates sideways, so
     * equal slices of life cover equal sideways distance, and the arc reaches its full Sway only as it dies. Freeze is the
     * control — it claims no gravity on either axis, so its sideways travel eases along with its climb and is deliberately
     * NOT uniform. Rail styles never reach this code (no fall), which is what keeps the conveyor's column still.
     */
    [TestMethod]
    public void AirborneNumbersHoldTheirHorizontalSpeed()
    {
      foreach (var style in new[] { FctMotionStyle.Fountain, FctMotionStyle.Spray })
      {
        var hit = TraceHit(style);
        hit.FallDist = 200;                    // airborne: this is what makes a number a projectile to LateralProgress

        double Step(double from) => At(hit, from + 0.1).X - At(hit, from).X;
        var first = Step(0.0);
        for (var t = 0.1; t <= 0.85; t += 0.05)
        {
          Assert.IsTrue(Math.Abs(Step(t) - first) < 0.5,
            $"{style} changed sideways speed at t={t:0.##}: {first:0.###} -> {Step(t):0.###}");
        }

        Assert.AreEqual(hit.X0 + hit.Sway, At(hit, 1.0).X, 0.001, $"{style} did not reach the full sway of its draw");
      }

      var freeze = TraceHit(FctMotionStyle.Freeze);
      var freezeOpensWith = At(freeze, 0.1).X - freeze.X0;
      var freezeMidFlight = At(freeze, 0.6).X - At(freeze, 0.5).X;
      Assert.IsTrue(freezeOpensWith < (freezeMidFlight * 0.75),
        $"freeze is supposed to glide, not launch: its first tenth covered {freezeOpensWith:0.#} against a mid-flight {freezeMidFlight:0.#}");
    }

    /* A wide cone draw: well to one side as it climbs. Freeze gets no fall so its line stays a line. */
    private static FctHitState TraceHit(FctMotionStyle style)
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
      hit.Sway = 300;
      hit.FallDist = style is FctMotionStyle.Spray ? (220 * FctLayout.SprayFallRiseRatio) : 0;
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

    /*
     * A hit with no motion left to travel is *finished*, not undefined. MotionMs is allowed to be 0 — a style that arrives in
     * place, a life shorter than its travel, a caller that wants the endpoint asked for directly — and dividing by it used to
     * produce NaN on the first frame. NaN then survived Math.Clamp and both band clamps, because NaN compares false against
     * every limit, and the number simply failed to be drawn.
     */
    [TestMethod]
    public void AHitWithNoMotionIsFinishedAndNotNaN()
    {
      var hit = NewHit();
      hit.MotionMs = 0;

      Assert.AreEqual(1.0, FctMotion.Progress(hit, 0), 0.001, "a zero-length motion is complete from its first frame");
      Assert.IsFalse(double.IsNaN(FctMotion.ArcedX(hit, FctMotion.Progress(hit, 0))));
      Assert.IsFalse(double.IsNaN(FctMotion.RaisedY(hit, FctMotion.Progress(hit, 0))));
    }
  }
}
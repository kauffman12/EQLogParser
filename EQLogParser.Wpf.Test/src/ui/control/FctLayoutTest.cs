using System;

namespace EQLogParser
{
  /*
   * Layout is what keeps the overlay usable in game: text stays inside its region, out of the protected middle
   * (EQ's own windows and the spell effects being looked at live there), and inside the canvas — at any window size,
   * including one so small a clamp band would otherwise invert. Bands mode also pins direction to travel, which is
   * the whole point of it: outgoing rises, incoming sinks, so nobody has to learn which side means what before the
   * overlay is readable.
   */
  [TestClass]
  public sealed class FctLayoutTest
  {
    private const double Width = 980;
    private const double Height = 640;

    /* FctLane is internal to the app and a public test method cannot take it as a parameter, so the rows carry
     * names — nameof keeps them tied to the enum through a rename. */
    [TestMethod]
    [DataRow(nameof(FctLane.DamageTaken), true)]
    [DataRow(nameof(FctLane.HealingReceived), true)]
    [DataRow(nameof(FctLane.Defensive), true)]
    [DataRow(nameof(FctLane.DamageDealt), false)]
    [DataRow(nameof(FctLane.HealingDealt), false)]
    [DataRow(nameof(FctLane.Missed), false)]
    public void IncomingLanesAreTheOnesThatHappenToMe(string lane, bool incoming) =>
      Assert.AreEqual(incoming, FctLayout.IsIncoming(Enum.Parse<FctLane>(lane)));

    /* Direction is carried by the band plus the direction of travel: up for my hits, down for hits on me. */
    [TestMethod]
    public void OutgoingHitsRiseAndIncomingHitsSink()
    {
      var outgoing = Spawn(FctLane.DamageDealt, incoming: false, new Random(1));
      var incoming = Spawn(FctLane.DamageTaken, incoming: true, new Random(1));

      Assert.IsTrue(outgoing.Rise > 0, $"outgoing text must travel up (Rise {outgoing.Rise})");
      Assert.IsTrue(incoming.Rise < 0, $"incoming text must travel down (Rise {incoming.Rise})");

      Assert.IsTrue(outgoing.Y0 < Height * FctLayout.GapTopFrac, "outgoing hits must spawn above the protected strip");
      Assert.IsTrue(incoming.Y0 >= (Height * FctLayout.GapBottomFrac) - 0.001, "incoming hits must spawn below it");
    }

    /* The protected strip has to stay empty for the whole life of a hit, not just at spawn — including at crit scale. */
    [TestMethod]
    public void TextNeverEntersTheProtectedStrip()
    {
      var rand = new Random(7);

      for (var i = 0; i < 400; i++)
      {
        foreach (var incoming in new[] { true, false })
        {
          var hit = Spawn(incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, crit: incoming);

          for (var t = 0.0; t <= 1.0; t += 0.05)
          {
            var y = FctMotion.RaisedY(hit, t);
            var bottom = y + FctLayout.TextReserve(hit); // y is the top of the value text

            if (incoming)
            {
              Assert.IsTrue(y >= (Height * FctLayout.GapBottomFrac) - 0.001, $"incoming text entered the strip at t={t:0.00} (top {y})");
            }
            else
            {
              Assert.IsTrue(bottom <= (Height * FctLayout.GapTopFrac) + 0.001, $"outgoing text entered the strip at t={t:0.00} (bottom {bottom})");
            }

            Assert.IsTrue(y is >= 0 and < Height, $"text left the canvas at t={t:0.00} (y {y})");
          }
        }
      }
    }

    /* A number must stay inside the window for its whole life, in both directions. */
    [TestMethod]
    public void SpawnAndTravelStayInsideTheCanvas()
    {
      var rand = new Random(20_260_714);

      for (var i = 0; i < 500; i++)
      {
        foreach (var incoming in new[] { true, false })
        {
          var hit = Spawn(FctLane.DamageDealt, incoming, rand);

          Assert.IsTrue(hit.X0 >= 0 && hit.X0 <= Width, $"spawn x {hit.X0} off canvas");
          Assert.IsTrue(FctMotion.RaisedY(hit, 1.0) is >= 0 and < Height, $"travel end off canvas ({hit.Y0}, rise {hit.Rise})");
        }
      }
    }

    /*
     * Halves mode keeps its own promise: the centre column stays clear, at crit scale and at every frame. The styles are
     * looped because each one has a different widest frame — a pulse grows to PulsePeakScale with no travel to warn you,
     * and an x clamp built on 1.0 would let exactly that style bleed over the protected centre.
     */
    [TestMethod]
    public void HalvesModeKeepsTextOutOfTheProtectedCenter()
    {
      var rand = new Random(7);
      var center = Width / 2;

      for (var i = 0; i < 400; i++)
      {
        foreach (var style in new[] { FctMotionStyle.Hold, FctMotionStyle.Pulse })
        {
          foreach (var incoming in new[] { true, false })
          {
            var hit = Spawn(incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, crit: incoming, mode: FctLayoutMode.Halves, style: style);
            hit.ValueWidth = 180; // a wide label: the clamp has to reserve half of it, scaled

            for (var t = 0.0; t <= 1.0; t += 0.05)
            {
              var x = FctMotion.ArcedX(hit, t);
              var peak = hit.Blowout ? FctMotion.CritPeakScale : style is FctMotionStyle.Pulse ? FctMotion.PulsePeakScale : 1.0;
              var half = (hit.ValueWidth * peak) / 2.0;

              if (incoming)
              {
                Assert.IsTrue(x + half <= center - FctLayout.CenterClearance + 0.001, $"{style} incoming text crossed the center at t={t:0.00} (right edge {x + half:0.#})");
              }
              else
              {
                Assert.IsTrue(x - half >= center + FctLayout.CenterClearance - 0.001, $"{style} outgoing text crossed the center at t={t:0.00} (left edge {x - half:0.#})");
              }
            }
          }
        }
      }
    }

    /* Halves mode puts no vertical limit on a hit; FctMotion reads an unset band as "no clamp" rather than pinning
     * everything to y=0. */
    [TestMethod]
    public void HalvesModeLeavesTheVerticalBandUnset()
    {
      var halves = Spawn(FctLane.DamageDealt, incoming: false, new Random(5), mode: FctLayoutMode.Halves);
      Assert.AreEqual(0, halves.BandMinY);
      Assert.AreEqual(0, halves.BandMaxY);
      Assert.AreEqual(halves.Y0 - (halves.Rise * 1.0), FctMotion.RaisedY(halves, 1.0), 0.0001, "an unset band must not clamp");
    }

    /* A tiny overlay must degrade instead of throwing: an inverted clamp band used to crash every frame. */
    [TestMethod]
    public void TinyCanvasesStillProduceADrawablePosition()
    {
      var rand = new Random(3);

      foreach (var mode in new[] { FctLayoutMode.Bands, FctLayoutMode.Halves })
      {
        foreach (var size in new[] { 100, 160, 240 })
        {
          foreach (var incoming in new[] { true, false })
          {
            var hit = Spawn(FctLane.DamageDealt, incoming, rand, size, size, mode: mode);
            Assert.IsTrue(hit.SideMin <= hit.SideMax, $"x clamp band inverted at {size}px ({hit.SideMin}..{hit.SideMax})");
            Assert.IsTrue(hit.BandMaxY > hit.BandMinY || hit.BandMaxY == 0, $"y band inverted at {size}px ({hit.BandMinY}..{hit.BandMaxY})");

            var x = FctMotion.ArcedX(hit, 0.5); // must not throw
            var y = FctMotion.RaisedY(hit, 0.5);
            Assert.IsTrue(x >= 0 && x <= size, $"x={x} outside a {size}px canvas");
            Assert.IsTrue(y >= 0 && y <= size, $"y={y} outside a {size}px canvas");
          }
        }
      }
    }

    [TestMethod]
    public void CritsSpreadWiderThanNormalHits()
    {
      var rand = new Random(11);
      var normalMax = 0.0;
      var critMax = 0.0;

      for (var i = 0; i < 300; i++)
      {
        normalMax = Math.Max(normalMax, Math.Abs(Spawn(FctLane.DamageDealt, incoming: false, rand).X0 - (Width * 0.42)));
        critMax = Math.Max(critMax, Math.Abs(Spawn(FctLane.DamageDealt, incoming: false, rand, crit: true).X0 - (Width * 0.52)));
      }

      Assert.IsTrue(critMax > normalMax, "crits should occupy a wider band than ordinary damage");
    }

    /*
     * The clip a player actually notices: the whole drawn block — value, descenders, the source line under it, all of
     * it scaled up during a crit's pop — has to stay inside the window for the hit's entire life, not merely its anchor
     * point. Incoming hits are the case that used to fail, because they travel downwards and spend their last seconds
     * against the bottom edge with only one em of reserve.
     */
    [TestMethod]
    public void DrawnBlockStaysInsideTheWindow()
    {
      var rand = new Random(4);

      foreach (var mode in new[] { FctLayoutMode.Bands, FctLayoutMode.Halves })
      {
        for (var i = 0; i < 300; i++)
        {
          foreach (var incoming in new[] { true, false })
          {
            var hit = Spawn(incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, crit: incoming, mode: mode, source: "Crushing Blow");

            for (var t = 0.0; t <= 1.0; t += 0.05)
            {
              var bottom = FctMotion.RaisedY(hit, t) + FctLayout.TextReserve(hit);
              Assert.IsTrue(bottom <= Height - FctLayout.EdgePad + 0.001,
                $"text clipped at the bottom edge in {mode} mode at t={t:0.00} (bottom {bottom:0.#}, canvas {Height})");
            }
          }
        }
      }
    }

    /*
     * A pulse grows where it stands, so its spawn point has to leave room for the swell instead of for unscaled text.
     * Nothing travels here, which makes this the one style whose clipping would be pure arithmetic: reserve smaller than
     * the peak and every pulse is cut off at its widest frame.
     */
    [TestMethod]
    public void PulseSpawnsLeaveRoomForTheirOwnSwell()
    {
      var rand = new Random(11);

      for (var i = 0; i < 200; i++)
      {
        var hit = Spawn(FctLane.DamageDealt, incoming: false, rand, mode: FctLayoutMode.Bands, source: "Crushing Blow", style: FctMotionStyle.Pulse);

        Assert.AreEqual(0.0, hit.Rise, "a pulse travels nowhere, so only its scale can leave the band");
        Assert.IsTrue(FctLayout.TextReserve(hit) > (hit.ValueFontSize * FctLayout.TextHeightFactor),
          $"the reserve must carry the swell (got {FctLayout.TextReserve(hit):0.#} for a {hit.ValueFontSize:0} px value)");
        Assert.IsTrue(hit.Y0 + FctLayout.TextReserve(hit) <= (Height * FctLayout.GapTopFrac) + 0.001,
          $"its widest frame crosses into the protected strip ({hit.Y0 + FctLayout.TextReserve(hit):0.#} > {Height * FctLayout.GapTopFrac:0.#})");
      }
    }

    /*
     * A style with no travel is read where it spawns, so spawning against the strip - correct for something leaving it -
     * put a pulse nearer the cast bar than the same number ever got while floating away. Asserted as a distance rather
     * than as a coordinate, and asserted against hold as well: whatever settles into this rule, the static text has to
     * start further from the protected middle than the moving text does.
     */
    [TestMethod]
    public void PulseStartsAwayFromTheProtectedStrip()
    {
      var rand = new Random(9);
      var pulseClosest = double.MaxValue;
      var travelFurthest = 0.0;

      for (var i = 0; i < 200; i++)
      {
        foreach (var incoming in new[] { false, true })
        {
          foreach (var style in new[] { FctMotionStyle.Pulse, FctMotionStyle.Hold })
          {
            var hit = Spawn(incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, style: style);
            var span = hit.BandMaxY - hit.BandMinY;

            // clearance from the band edge nearest the strip, which already carries the text reserve
            var clearance = incoming ? hit.Y0 - hit.BandMinY : hit.BandMaxY - hit.Y0;

            Assert.IsTrue(clearance >= 0, $"{style} spawned outside its own band ({clearance:0.#})");

            if (style is FctMotionStyle.Pulse)
            {
              Assert.IsTrue(clearance >= span * FctLayout.PulseInsetFrac - 0.001,
                $"pulse started {clearance:0.#} px inside the band, want at least {span * FctLayout.PulseInsetFrac:0.#}");
              pulseClosest = Math.Min(pulseClosest, clearance);
            }
            else
            {
              travelFurthest = Math.Max(travelFurthest, clearance);
            }
          }
        }
      }

      Assert.IsTrue(pulseClosest > travelFurthest,
        $"static text should start further from the strip than travelling text ({pulseClosest:0.#} vs {travelFurthest:0.#})");
    }

    private static FctHitState Spawn(FctLane lane, bool incoming, Random rand, double w = Width, double h = Height, bool crit = false, FctLayoutMode mode = FctLayoutMode.Bands, string? source = null, FctMotionStyle style = FctMotionStyle.Hold)
    {
      // Source and style must be set before layout runs: the vertical reserve and the travel are both derived from them,
      // exactly as FctIngest does it
      var hit = new FctHitState
      {
        Lane = crit ? FctLane.Crit : lane,
        Incoming = incoming,
        Style = style,
        Source = source,
        TargetValue = 1234,
        CountBaseValue = 1234,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      FctLayout.Spawn(hit, w, h, rand, mode);
      return hit;
    }
  }
}

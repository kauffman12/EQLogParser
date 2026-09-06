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
            var bottom = y + hit.ValueFontSize; // y is the top of the value text

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

    /* Halves mode keeps its own promise: the centre column stays clear, at crit scale and at every frame. */
    [TestMethod]
    public void HalvesModeKeepsTextOutOfTheProtectedCenter()
    {
      var rand = new Random(7);
      var center = Width / 2;

      for (var i = 0; i < 400; i++)
      {
        foreach (var incoming in new[] { true, false })
        {
          var hit = Spawn(incoming ? FctLane.DamageTaken : FctLane.DamageDealt, incoming, rand, crit: incoming, mode: FctLayoutMode.Halves);
          hit.ValueWidth = 180; // a wide label: the clamp has to reserve half of it, scaled

          for (var t = 0.0; t <= 1.0; t += 0.05)
          {
            var x = FctMotion.ArcedX(hit, t);
            var half = (hit.ValueWidth * (hit.Blowout ? FctMotion.CritPeakScale : 1.0)) / 2.0;

            if (incoming)
            {
              Assert.IsTrue(x + half <= center - FctLayout.CenterClearance + 0.001, $"incoming text crossed the center at t={t:0.00} (right edge {x + half})");
            }
            else
            {
              Assert.IsTrue(x - half >= center + FctLayout.CenterClearance - 0.001, $"outgoing text crossed the center at t={t:0.00} (left edge {x - half})");
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

    private static FctHitState Spawn(FctLane lane, bool incoming, Random rand, double w = Width, double h = Height, bool crit = false, FctLayoutMode mode = FctLayoutMode.Bands)
    {
      var hit = new FctHitState
      {
        Lane = crit ? FctLane.Crit : lane,
        Incoming = incoming,
        TargetValue = 1234,
        CountBaseValue = 1234,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      FctLayout.Spawn(hit, w, h, rand, mode);
      return hit;
    }
  }
}

using System;

namespace EQLogParser
{
  /*
   * Layout is what keeps the overlay usable in game: text has to stay on its own half, out of the protected
   * center band (the player's cast bar, target ring and spell gems live there), and inside the canvas — at
   * any window size, including one so small the clamp band would otherwise invert.
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
    public void LanesBelongToTheSideTheyHappenOn(string lane, bool left) => Assert.AreEqual(left, FctLayout.IsLeftSide(Enum.Parse<FctLane>(lane)));

    [TestMethod]
    public void SpawnKeepsTextInsideTheCanvas()
    {
      var rand = new Random(20_260_714);

      for (var i = 0; i < 500; i++)
      {
        foreach (var left in new[] { true, false })
        {
          var hit = Spawn(FctLane.DamageDealt, left, rand);
          Assert.IsTrue(hit.X0 >= 0 && hit.X0 <= Width, $"spawn x {hit.X0} off canvas");
          Assert.IsTrue(hit.Y0 is > 0 && hit.Y0 < Height, $"spawn y {hit.Y0} off canvas");

          // the top of the travel has to stay on screen too: that is where the number fades out
          Assert.IsTrue(hit.Y0 - hit.Rise >= 0, $"rise would push text above the canvas ({hit.Y0 - hit.Rise})");
        }
      }
    }

    /* The protected center band: neither half may enter it, at any point of the motion or at crit scale. */
    [TestMethod]
    public void TextNeverCrossesTheProtectedCenter()
    {
      var rand = new Random(7);
      var center = Width / 2;

      for (var i = 0; i < 400; i++)
      {
        foreach (var left in new[] { true, false })
        {
          var hit = Spawn(left ? FctLane.DamageTaken : FctLane.DamageDealt, left, rand, crit: left);
          hit.ValueWidth = 180; // a wide label: the clamp has to reserve half of it, scaled

          for (var t = 0.0; t <= 1.0; t += 0.05)
          {
            var x = FctMotion.ArcedX(hit, t);
            var half = (hit.ValueWidth * (hit.Blowout ? FctMotion.CritPeakScale : 1.0)) / 2.0;

            if (left)
            {
              Assert.IsTrue(x + half <= center - FctLayout.CenterClearance + 0.001, $"left text crossed the center at t={t:0.00} (right edge {x + half})");
            }
            else
            {
              Assert.IsTrue(x - half >= center + FctLayout.CenterClearance - 0.001, $"right text crossed the center at t={t:0.00} (left edge {x - half})");
            }
          }
        }
      }
    }

    /* A tiny overlay must degrade instead of throwing: an inverted clamp band used to crash every frame. */
    [TestMethod]
    public void TinyCanvasesStillProduceADrawablePosition()
    {
      var rand = new Random(3);

      foreach (var size in new[] { 100, 160, 240 })
      {
        foreach (var left in new[] { true, false })
        {
          var hit = Spawn(FctLane.DamageDealt, left, rand, size, size);
          Assert.IsTrue(hit.SideMin <= hit.SideMax, $"clamp band inverted at {size}px ({hit.SideMin}..{hit.SideMax})");

          var x = FctMotion.ArcedX(hit, 0.5); // must not throw
          Assert.IsTrue(x >= 0 && x <= size, $"x={x} outside a {size}px canvas");
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
        normalMax = Math.Max(normalMax, Math.Abs(Spawn(FctLane.DamageDealt, false, rand).X0 - (Width * 0.65)));
        critMax = Math.Max(critMax, Math.Abs(Spawn(FctLane.DamageDealt, false, rand, crit: true).X0 - (Width * 0.75)));
      }

      Assert.IsTrue(critMax > normalMax, "crits should occupy a wider band than ordinary damage");
    }

    private static FctHitState Spawn(FctLane lane, bool left, Random rand, double w = Width, double h = Height, bool crit = false)
    {
      var hit = new FctHitState
      {
        Lane = crit ? FctLane.Crit : lane,
        LeftSide = left,
        TargetValue = 1234,
        CountBaseValue = 1234,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      FctLayout.Spawn(hit, w, h, rand);
      return hit;
    }
  }
}

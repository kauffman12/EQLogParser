using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * Which way an arc leans was never a question, only an answer — and the two schemes gave different ones. Fountain bows each column away from
   * the shared middle strip; split asks the lane which of its own hands is open and spends that on the bend, because a split lane is private and
   * pushing its rail off its spine to buy an outward curve starves every name on the lane. With one category on a side the open hand points at
   * the middle of the screen, so a default split with heals left and damage right leans BOTH halves inward (measured at 1920x1080: the damage
   * column at spine 1685 bends to -319.6, the healing column at spine 235 bends to +319.6). A player who reads that as "the arc goes the wrong
   * way on my left side" is reading it correctly; nobody ever offered them the choice. Narrower overlays change the answer again — at 1280 the
   * damage column flips to +147 because the lane's open hand moved — which is the other half of the complaint: the direction is not even stable
   * across window sizes, because it is a consequence of lane geometry rather than something anybody picked.
   *
   * These tests pin all four words of FctArcBend against that measured reality: open keeps what shipped (the numbers here are the guard — if a
   * future change moves the default, one of them fails and it is a decision rather than a drift), out mirrors the halves away from each other,
   * and left/right say one direction for every column at every width. The last two assert the part that makes a forced lean safe: the bend is
   * capped against the room on the side it was pointed at, so a lane with thin air draws a shorter curve rather than a number through a wall,
   * and — the part that would be a bug — never one that turns around.
   */
  [TestClass]
  public sealed class FctArcBendTest
  {
    private const double Width = 1920;
    private const double Height = 1080;

    /* The lean is a process dial exactly like size and speed (FctLayout.ArcBend), so this class starts from the shipped ambient state and
       every test sets the word it is asking about rather than inheriting whatever the previous test left stamped. */
    [TestInitialize]
    public void StartFromShippedAmbient() => FctAmbient.Reset();

    /*
     * Split with one claimant per half — heals on the left, damage on the right — which is the arrangement the complaint came from: each column
     * owns its whole half, so there is no neighbour to explain the direction away. One spawn pass reads the lane's band, then the spine and the
     * spawn edge are pinned the way FctPlacement does, so these asserts measure the lean and not the origin jitter an unstreamed throw adds.
     */
    private static FctHitState Lean(FctArcBend bend, bool heal, double w = Width) => Lean(bend, heal, FctStage.ByType(
      FctRegionSide.Left, incomingUp: false, outgoingUp: false, w, Height));

    /* The same pass over a stage the caller picked, so a two-column half can be asked about as easily as a one-column one. */
    private static FctHitState Lean(FctArcBend bend, bool heal, FctStage stage)
    {
      FctLayout.ArcBend = bend;
      var hit = new FctHitState
      {
        Lane = heal ? FctLane.HealingDealt : FctLane.DamageDealt,
        Incoming = false,
        Heal = heal,
        Style = FctMotionStyle.Arc,
        Source = "Probe",
        Value = 12345,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      hit.ValueWidth = FctLayout.EstimateTextWidth("123,456", hit.ValueFontSize);

      FctLayout.Spawn(hit, stage, new Random(7));
      var spine = FctLayout.ColumnCentre(hit, stage);
      FctLayout.Spawn(hit, stage, new Random(7), (spine, stage.UpFor(hit) > 0 ? hit.BandMaxY : hit.BandMinY));
      return hit;
    }

    /* The curve the shape asks for at this lane's width: AssignTravel spends territory * ArcBowFrac and no more, so that is what a paid-for lean
       measures. Asked of the stage rather than guessed, because a half split between two columns wants half the bend of one that owns it alone. */
    private static double FullCurve(FctStage stage, FctHitState hit) => stage.TerritoryFor(hit) * FctLayout.ArcBowFrac;

    /* A file with no lean in it leans open — which is the arrangement every settings.ini written before this key exists already draws. Junk
       lands there too rather than reaching the geometry as garbage. */
    [TestMethod]
    public void AFileWithNoLeanLeansOpen()
    {
      Assert.AreEqual(FctArcBend.Open, FctOverlaySettings.ShippedArcBend(null), "a fresh install keeps the curve it has always drawn");
      Assert.AreEqual(FctArcBend.Open, FctOverlaySettings.ShippedArcBend(""), "an empty value is the same as no value");
      Assert.AreEqual(FctArcBend.Open, FctOverlaySettings.ShippedArcBend("sideways"), "an unreadable word gets the shipped lean");
    }

    /* Every word reaches its own arrangement, case-free, and no two arrangements claim the same word — which is what makes the panel's four
       items, the saved file and the engine one vocabulary instead of three that drift. */
    [TestMethod]
    public void EveryLeanRoundTripsThroughItsWord()
    {
      foreach (var bend in new[] { FctArcBend.Open, FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
      {
        var word = FctOverlaySettings.WordForArcBend(bend);
        Assert.AreEqual(bend, FctOverlaySettings.ShippedArcBend(word), $"{bend} has to read back from \"{word}\"");
        Assert.AreEqual(bend, FctOverlaySettings.ShippedArcBend(word.ToUpperInvariant()), "spelling is case-free, as everywhere else in the file");
      }

      var saved = new HashSet<string> { "open", "out", "left", "right" };
      foreach (var bend in new[] { FctArcBend.Open, FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
      {
        Assert.IsTrue(saved.Remove(FctOverlaySettings.WordForArcBend(bend)),
          $"{bend} saved a word the other three do not own, or one they already had");
      }

      Assert.AreEqual(0, saved.Count, "every word in the file has to reach an arrangement");
    }

    /* The shipped answer, pinned with the numbers that were measured: both halves lean inward toward the middle strip. This is the behaviour a
       player calls "the arc goes the wrong way", and it stays the default on purpose — changing it silently would re-shape every existing
       overlay — but it does not stay unchosen. */
    [TestMethod]
    public void OpenLeansBothHalvesInward()
    {
      var damage = Lean(FctArcBend.Open, heal: false);
      var healing = Lean(FctArcBend.Open, heal: true);

      Assert.AreEqual(-319.6, Math.Round(damage.Bow, 1), "the right-hand column bends back over the gutter");
      Assert.AreEqual(319.6, Math.Round(healing.Bow, 1), "and the left-hand column bends at it, so both point at the middle");
    }

    /* "out" is fountain's rule handed to split: away from the middle of the overlay, which means the two halves bend apart. Same two columns, opposite
       signs and — since each half buys the curve it promised — the same depth: a mirrored pair, which is what a player means by "the standard arc" and
       what this dial could not draw before the lane paid for the lean (see EveryExplicitLeanDrawsItsFullCurve). */
    [TestMethod]
    public void OutBendsTheTwoHalvesApart()
    {
      var damage = Lean(FctArcBend.Out, heal: false);
      var healing = Lean(FctArcBend.Out, heal: true);

      Assert.IsTrue(damage.Bow > 0, $"the right-hand column should bend toward the right edge (actual: {damage.Bow:F1})");
      Assert.IsTrue(healing.Bow < 0, $"the left-hand column should bend toward the left edge (actual: {healing.Bow:F1})");
    }

    /* Four words are four arrangements, not four spellings of two: the sign pairs across (damage column, healing column) are distinct. */
    [TestMethod]
    public void TheFourWordsAreFourDifferentCurves()
    {
      /* One pair of signs per word: which way each of the two columns actually moved. */
      static string Sign(double bow) => bow > 0 ? "+" : bow < 0 ? "-" : "=";
      string Shape(FctArcBend bend) => $"{Sign(Lean(bend, heal: false).Bow)}{Sign(Lean(bend, heal: true).Bow)}";

      var shapes = new[] { Shape(FctArcBend.Open), Shape(FctArcBend.Out), Shape(FctArcBend.Left), Shape(FctArcBend.Right) };

      Assert.AreEqual("++", shapes[3], "right bows both columns right");
      Assert.AreEqual("--", shapes[2], "left bows both columns left");
      CollectionAssert.AllItemsAreUnique(shapes);
    }

    /* One direction means one direction, at every width an overlay gets dragged to: a forced lean cannot be answered by the lane's own open hand
       on the way through, which is what made the shipped answer move when the window moved. */
    [TestMethod]
    public void LeftAndRightHoldTheirDirectionAtEveryWidth()
    {
      foreach (var w in new[] { 700, 1280, Width, 2560 })
      {
        foreach (var heal in new[] { false, true })
        {
          var left = Lean(FctArcBend.Left, heal, w);
          var right = Lean(FctArcBend.Right, heal, w);

          Assert.IsTrue(left.Bow <= 0, $"left at {w}px ({(heal ? "heals" : "damage")}) leaned the wrong way: {left.Bow:F1}");
          Assert.IsTrue(right.Bow >= 0, $"right at {w}px ({(heal ? "heals" : "damage")}) leaned the wrong way: {right.Bow:F1}");
        }
      }
    }

    /* The cap is a wish's brake, not a veto. A lane that cannot pay parks as far as its own walls allow and draws a shorter curve — it does not push the
       column through a neighbour, and never past zero into the other direction, which would hand one column two shapes depending on how lucky its digits
       were. The unaffordable case is a narrow overlay split into columns: 700px, two claimants in the left half, so each owns ~157px and cannot fit a
       reserve plus a bend between its walls. */
    [TestMethod]
    public void ALeanWithoutRoomShrinksInsteadOfTurningAround()
    {
      var narrow = FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, 700, Height,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Right1);

      var thin = Lean(FctArcBend.Left, heal: true, narrow);
      var roomy = Lean(FctArcBend.Left, heal: true);

      /* At this width a single rail reserve (~156px of glyphs at the crit ceiling) is most of the column, so there is genuinely nowhere to park: the
         honest answer is a very short curve or none, and the thing that must never happen is a sign flip. */
      Assert.IsTrue(thin.Bow <= 0, $"a column with no room on its left must not lean right (actual: {thin.Bow:F1})");
      Assert.IsTrue(Math.Abs(thin.Bow) < Math.Abs(roomy.Bow),
        $"the thin lane should bend less than the roomy one ({thin.Bow:F1} vs {roomy.Bow:F1})");

      // and the rail never leaves the band the clamp was given, at any point of the flight or in any word
      foreach (var bend in new[] { FctArcBend.Open, FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
      {
        foreach (var heal in new[] { false, true })
        {
          var hit = Lean(bend, heal);
          for (var step = 0; step <= 24; step++)
          {
            var x = FctMotion.ArcedX(hit, step / 24.0);
            Assert.IsTrue(x >= hit.SideMin - 1 && x <= hit.SideMax + 1,
              $"{bend} put the rail at {x:F1}, outside [{hit.SideMin:F1}, {hit.SideMax:F1}] at t={step / 24.0:F2}");
          }
        }
      }
    }

    /*
     * A lean that draws nothing is not a lean. This is the guard this dial shipped without: every assertion above reads a SIGN, and a bow clamped to
     * exactly zero passes "not positive" while the player watches a straight vertical scroll and reports that left looks like nothing and out works only
     * on one side. Measured before the fix, split with one column per side (docs/DesignNotes.md -> "Which way an arc leans"): 448 px of air right of
     * the rail, 0 px left of it, because a rail row is RIGHT-aligned and parks its own glyphs in the hand a leftward bend would sweep. So every explicit
     * word has to buy its depth — ParkForBend moves the column off the wall it leans at until the lane can pay — and this asserts the curve is the full
     * one the shape asked for, at widths an overlay actually gets resized to, on both halves and on both one- and two-column halves.
     */
    [TestMethod]
    public void EveryExplicitLeanDrawsItsFullCurve()
    {
      foreach (var w in new[] { 1280.0, Width, 2560.0 })
      {
        var stages = new (string Name, FctStage Stage)[]
        {
          ("one column per half", FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, w, Height)),
          ("two columns left",
            FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, w, Height,
              healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Right1)),
        };

        foreach (var (name, stage) in stages)
        {
          foreach (var bend in new[] { FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
          {
            foreach (var heal in new[] { false, true })
            {
              var hit = Lean(bend, heal, stage);
              var want = FullCurve(stage, hit);

              Assert.IsTrue(Math.Abs(hit.Bow) >= want - 0.1,
                $"{bend} at {w}px ({(heal ? "heals" : "damage")}, {name}) drew {hit.Bow:F1} of a {want:F1} curve — " +
                "a lean this short reads as a straight line, which is the bug this test exists for");
            }
          }
        }
      }
    }

    /* The park is a property of the LANE, never of the number that arrived: two rows on one column, one narrow and one six nines wide, still share a
       rail and a path. This is what separates paying for a bend from the per-row shove (75e2992a) that put a column out of line with itself. */
    [TestMethod]
    public void APaidForLeanIsTheSameCurveForEveryRowOnTheColumn()
    {
      var stage = FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, Width, Height);

      FctLayout.ArcBend = FctArcBend.Left;
      var small = new FctHitState { Lane = FctLane.DamageDealt, Heal = false, Style = FctMotionStyle.Arc, Value = 7 };
      var large = new FctHitState { Lane = FctLane.DamageDealt, Heal = false, Style = FctMotionStyle.Arc, Value = 999999 };

      foreach (var hit in new[] { small, large })
      {
        FctStyle.ApplyTo(hit, hit.Lane, minor: false);
        hit.ValueWidth = FctLayout.EstimateTextWidth("999,999", hit.ValueFontSize);
        FctLayout.Spawn(hit, stage, new Random(7), (FctLayout.ColumnCentre(hit, stage), hit.BandMinY));
      }

      Assert.AreEqual(small.X0, large.X0, 0.01, "one spine per column, whatever the digits");
      Assert.AreEqual(small.Bow, large.Bow, 0.01, "and one curve per column, whatever the digits");
    }

    /* Open is the arrangement the layout can afford rather than the one it was told to buy, so it parks nothing: the shipped columns stand exactly where
       the lane puts them. Pinned because every settings.txt written before FctOverlayArcBend exists reads as open, and a player who never touched the
       dial must not find their numbers moved sideways by an update. */
    [TestMethod]
    public void OpenParksNothingAndKeepsTheShippedLook()
    {
      var healing = Lean(FctArcBend.Open, heal: true);
      Assert.AreEqual(235.0, Math.Round(healing.X0, 1), "the healing column still stands at its slot's centre (measured at 1920px)");

      var parked = Lean(FctArcBend.Left, heal: true);
      Assert.IsTrue(parked.X0 > healing.X0 + 100,
        $"a leftward lean has to move the column off the wall it leans at (spine {parked.X0:F1} vs open's {healing.X0:F1})");
    }

    /* Parked columns are still inside their lane: the vertex at half height is where a lean that overspends would show, so sweep the whole flight of
       every word against the walls it was placed between, on halves that own everything and halves divided with a neighbour. */
    [TestMethod]
    public void AParkedColumnStillTravelsInsideItsLane()
    {
      var stage = FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, Width, Height,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Right1);

      foreach (var bend in new[] { FctArcBend.Open, FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
      {
        foreach (var heal in new[] { false, true })
        {
          var hit = Lean(bend, heal, stage);

          for (var step = 0; step <= 24; step++)
          {
            var x = FctMotion.ArcedX(hit, step / 24.0);
            Assert.IsTrue(x >= hit.SideMin - 1 && x <= hit.SideMax + 1,
              $"{bend} put the rail at {x:F1}, outside [{hit.SideMin:F1}, {hit.SideMax:F1}] at t={step / 24.0:F2}");
          }
        }
      }
    }

    /* Fountain is not offered an arc to lean — its shapes are spray and freeze (FctOverlaySettings.ClampShape) — so the dial has nothing to say
       there. Pinned because the panel hides the combo on that mode, and the hiding is only honest while the clamp holds. */
    [TestMethod]
    public void FountainIsNeverHandedAnArcToLean()
    {
      foreach (var bend in new[] { FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
      {
        FctLayout.ArcBend = bend;
        Assert.IsFalse(FctMotionStyles.IsRail(FctOverlaySettings.ClampShape(FctLayoutMode.Bands, FctMotionStyle.Arc)),
          $"{bend} should not be able to smuggle an arc into fountain");
      }
    }
  }
}

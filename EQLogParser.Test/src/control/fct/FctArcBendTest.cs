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
    private static FctHitState Lean(FctArcBend bend, bool heal, FctStage stage) => Lean(bend, heal, stage, FctMotionStyle.Arc);

    /* The same pass with the shape stated, so `line` can be asked whether a lean moves it (it must not: a rail with no bow has no hand to make air
       for, and the shipped right-alignment is what every straight column draws today). */
    private static FctHitState Lean(FctArcBend bend, bool heal, FctStage stage, FctMotionStyle style)
    {
      FctLayout.ArcBend = bend;
      var hit = new FctHitState
      {
        Lane = heal ? FctLane.HealingDealt : FctLane.DamageDealt,
        Incoming = false,
        Heal = heal,
        Style = style,
        Source = "Probe",
        Value = 12345,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      hit.ValueWidth = FctLayout.EstimateTextWidth("123,456", hit.ValueFontSize);

      FctLayout.Spawn(hit, stage, new Random(7));
      var spine = FctLayout.ColumnCentre(hit, stage);
      FctLayout.Spawn(hit, stage, new Random(7), (spine, stage.UpFor(hit) > 0 ? hit.BandMaxY : hit.BandMinY));

      /* Exactly what ingest does after a row is placed: the label is cut to the room the rail left on each hand (FctIngest). A fixture that skipped
         this would measure rows of digits only, and every "did the words steal the bend" assertion below would pass while proving nothing. */
      FctLayout.FitSource(hit, FctLayout.EstimateTextWidth);
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

      /* A named lean does move the column, but far less than it used to: a row that turns around to lean left leaves its own digits on the
         other side of the rail, so the bill at the wall it leans at is the curve and not the curve plus a column of glyphs. Measured at this
         width: 235.0 (open) to 327.6 — where charging the reserve as well cost 484. */
      Assert.IsTrue(parked.X0 > healing.X0 + 50,
        $"a leftward lean has to move the column off the wall it leans at (spine {parked.X0:F1} vs open's {healing.X0:F1})");
    }

    /*
     * Which way a row's own digits hang, which is the half of a lean nobody had asked about until the lean was real. A rail row has hung LEFT of
     * its rail since f3ba116f, so a leftward bend was sweeping the hand the glyphs already stood in: that is why `left` measured 0 px at 1280 while
     * 448 px of air sat unused on the right of the same rail (docs/DesignNotes.md → "Which way an arc leans"). The fix is to turn the row around,
     * and this asserts the turning is real in the one place a player sees it — where the number sits relative to the spine it climbs along.
     */
    [TestMethod]
    public void ALeftwardLeanTurnsTheRowAround()
    {
      var turned = Lean(FctArcBend.Left, heal: false);
      var shipped = Lean(FctArcBend.Open, heal: false);

      Assert.IsTrue(turned.HangRight, "a column told to lean left has to hang its digits right, into the air the lean is spending");
      Assert.IsFalse(shipped.HangRight, "`open` draws what every file written before this dial draws: digits left of the rail");

      /* Rail to centre, both ways, measured at rest (t=0) where the bow contributes nothing and only the hang moves the number. Which HAND of the
         spine the digits sit on is the whole difference between a curve and a straight line, so it gets asserted at both ends of the flight: at rest
         on the rail, and at the vertex, where the rail itself has moved by exactly the bow. */
      Assert.AreEqual(turned.X0 + (turned.ValueWidth / 2.0), FctMotion.ArcedX(turned, 0.0), 1.0,
        "a turned row's digits hang right of its rail");
      Assert.AreEqual(turned.X0 + turned.Bow + (turned.ValueWidth / 2.0), FctMotion.ArcedX(turned, 0.5), 1.0,
        "and still hang right at the vertex, where the lean is spending the air they vacated");
      Assert.AreEqual(shipped.X0 - (shipped.ValueWidth / 2.0), FctMotion.ArcedX(shipped, 0.0), 1.0,
        "and an unturned one hangs left of it, exactly as shipped");
    }

    /* `out` is the mirrored pair a player means by "the standard arc": the halves bend apart, and each one's digits stand on the hand the curve
       leaves free — so the two columns are aligned differently from each other ON PURPOSE, and every row inside a column still shares one edge. */
    [TestMethod]
    public void OutHangsEachHalfOffTheHandItsCurveLeavesFree()
    {
      var damage = Lean(FctArcBend.Out, heal: false);
      var healing = Lean(FctArcBend.Out, heal: true);

      Assert.IsFalse(damage.Bow < 0, $"the right-hand column should bend outward (actual: {damage.Bow:F1})");
      Assert.IsTrue(healing.HangRight, "the left-hand column bends left, so its digits hang right of the rail");
      Assert.IsFalse(damage.HangRight, "while the right-hand one keeps the shipped hang");
    }

    /* Straight is not a bow with the bend turned down by accident — it has no lean to make air for, and it is the shape whose exact geometry every
       player already knows. So the dial may not reach it: no turn, no bow, at any width and in any word. */
    [TestMethod]
    public void AStraightRailKeepsTheShippedAlignmentUnderEveryLean()
    {
      foreach (var bend in new[] { FctArcBend.Open, FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
      {
        foreach (var heal in new[] { false, true })
        {
          var hit = Lean(bend, heal, FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, Width, Height),
            FctMotionStyle.Straight);

          Assert.IsFalse(hit.HangRight, $"{bend} turned a straight column around; only a bend needs the air");
          Assert.AreEqual(0.0, hit.Bow, 1e-9, $"{bend} gave a straight column a bow");
          Assert.AreEqual(hit.X0 - (hit.ValueWidth / 2.0), FctMotion.ArcedX(hit, 0.5), 1.0,
            $"{bend} moved a straight column's digits off the shipped edge");
        }
      }
    }

    /* The block's extents follow the hang and nothing else does: no words means nothing crosses to the bare hand, and the digits plus their mark sit
       entirely on the hanging one. Get this inverted and every clamp below protects the empty side of the rail while the drawn row walks through a wall. */
    [TestMethod]
    public void ATurnedRowReachesItsOwnHandOnly()
    {
      var turned = Lean(FctArcBend.Left, heal: false);
      var (left, right) = FctLayout.BlockFromRail(turned, 0);

      Assert.AreEqual(0.0, left, 1e-6, "a turned row with no words leaves the rail's left hand empty");
      Assert.AreEqual(turned.ValueWidth + turned.IconAllowance, right, 1e-6, "and carries its digits, and its mark, on the right");

      var shipped = Lean(FctArcBend.Open, heal: false);
      var (shippedLeft, shippedRight) = FctLayout.BlockFromRail(shipped, 0);
      Assert.AreEqual(0.0, shippedRight, 1e-6, "which is the shipped arrangement read backwards");
      Assert.AreEqual(shipped.ValueWidth + shipped.IconAllowance, shippedLeft, 1e-6);
    }

    /* A name still has to fit the hand it draws on. FitSource is rail-relative so a turned column should cut its names against the right walls by
       itself — but that is exactly the kind of "by itself" a swap breaks quietly, and a too-wide name on a turned row is drawn across the lane seam.
       The measurer is synthetic (glyphs per character) for the same reason as FctLabelFitTest: this is about the budget, not the font. */
    [TestMethod]
    public void ATurnedColumnCutsItsNamesToTheHandItHas()
    {
      foreach (var seat in new[] { FctLabelSide.Left, FctLabelSide.Right, FctLabelSide.Below })
      {
        FctLayout.LabelSide = seat;
        var turned = Lean(FctArcBend.Left, heal: false);

        FctLayout.FitSource(turned, (text, size) => text.Length * size * 0.5);

        var (left, right) = FctLayout.BlockFromRail(turned, turned.SourceWidth);
        Assert.IsTrue(left <= turned.X0 - turned.SideMin + 0.5,
          $"{seat} words on a turned column reach {left:F1} left of a rail with {turned.X0 - turned.SideMin:F1} to spare");
        Assert.IsTrue(right <= turned.SideMax - turned.X0 + 0.5,
          $"{seat} words on a turned column reach {right:F1} right of a rail with {turned.SideMax - turned.X0:F1} to spare");
      }
    }

    /* The whole drawn box, every word, every label seat, every width, the whole flight — and this time the BOX rather than the centre the older sweeps
       read. A mirror is exactly the kind of change that keeps a centre honest while its digits cross a wall, which is why the assertion carries
       ValueWidth on both sides; and it reads every seat because a seat is where the words live, and words are what can quietly steal a bend. */
    [TestMethod]
    public void EveryLeanKeepsItsWholeBoxInsideTheLane()
    {
      foreach (var w in new[] { 1024.0, 1280.0, Width, 2560.0 })
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
          /* Every label seat, because the words are the widest part of a row and the clamp that keeps them in is the same one that could take the bend away. */
          foreach (var seat in new[] { FctLabelSide.None, FctLabelSide.Left, FctLabelSide.Right, FctLabelSide.Below })
          {
            FctLayout.LabelSide = seat;

            foreach (var bend in new[] { FctArcBend.Open, FctArcBend.Out, FctArcBend.Left, FctArcBend.Right })
            {
              foreach (var heal in new[] { false, true })
              {
                var hit = Lean(bend, heal, stage);

                for (var step = 0; step <= 24; step++)
                {
                  var centre = FctMotion.ArcedX(hit, step / 24.0);
                  var half = hit.ValueWidth / 2.0 + hit.IconAllowance;
                  Assert.IsTrue(centre - half >= hit.SideMin - 1 && centre + half <= hit.SideMax + 1,
                    $"{bend} at {w}px ({(heal ? "heals" : "damage")}, {name}, {seat} label) drew a box across its wall at t={step / 24.0:F2}: " +
                    $"[{centre - half:F1}, {centre + half:F1}] against [{hit.SideMin:F1}, {hit.SideMax:F1}]");

                  /* And the shape is the one the column was promised. Containment alone would pass a curve silently shortened at its vertex: the cap sizes
                     a bow against the AMOUNT (so every row of a column bends identically) while ArcedX clamps against the whole block INCLUDING the words,
                     so a name wide enough to cross the rail steals the bend from the row that carries it — and then two rows on one spine trace two paths,
                     which is the weld promise this whole engine is built on. */
                  if (step == 12 && hit.Style is FctMotionStyle.Arc)
                  {
                    var railAtVertex = hit.HangRight
                      ? centre - (hit.ValueWidth / 2.0)
                      : centre + (hit.ValueWidth / 2.0);
                    Assert.AreEqual(hit.X0 + hit.Bow, railAtVertex, 0.5,
                      $"{bend} at {w}px ({(heal ? "heals" : "damage")}, {name}, {seat} label) drew its vertex at {railAtVertex:F1} instead of " +
                      $"{hit.X0 + hit.Bow:F1} \u2014 the row's label ate {(hit.X0 + hit.Bow) - railAtVertex:F1} px of its bend");
                  }
                }
              }
            }
          }
        }
      }
    }

    /*
     * What happens when a name and a lean want the same pixels, which is a question the engine had never been asked on purpose. The measured case is
     * a 1024 px overlay, one column per half, names seated right: the curve sweeps the hand the words stand in, and before this rule the drawn vertex
     * landed at 959.7 of the 1016.0 the column was given — 56.3 px of bend quietly missing, AND missing by a different amount on every row depending on
     * how long that row's name happened to be, which is one spine drawing two paths. The rule now: the shape keeps its shape and the annotation steps
     * out (FctLayout.FitSource), because a name with no room would have to be cut below what still reads as a word, and hiding an annotation states
     * no falsehood while a column that misaligns itself cannot be un-seen.
     */
    [TestMethod]
    public void ANameStandingInTheLeansPathStepsAside()
    {
      FctLayout.LabelSide = FctLabelSide.Right;

      var tight = Lean(FctArcBend.Open, heal: false, 1024.0);
      Assert.IsNull(tight.SourceLabel,
        $"a name that cannot fit the lean's path has to be dropped, not drawn short and at the curve's expense (it measured {tight.SourceWidth:F1} px)");

      /* Same narrow corner, same seat, no lean: nothing stands in the name's way, so it keeps the old answer — cut to the floor with an honest ellipsis
         and let the rail's clamp absorb the overflow. The refusal belongs to the bow, not to narrowness. */
      var straight = Lean(FctArcBend.Open, heal: false,
        FctStage.ByType(FctRegionSide.Left, incomingUp: false, outgoingUp: false, 1024, Height), FctMotionStyle.Straight);
      Assert.IsNotNull(straight.SourceLabel, "a column with no bend has nothing in a name's way, so the name still draws");

      // and a roomy overlay pays for both without anyone noticing
      var roomy = Lean(FctArcBend.Open, heal: false, 2560.0);
      Assert.IsNotNull(roomy.SourceLabel, "a column with air keeps its names AND its curve");
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
    /* Is the sweep above carrying words at all? A row whose label never got measured would make every seat assertion below pass on digits alone, which
       is a test that proves nothing while looking like coverage. This pins the fixture: with a seat set and a source named, the row must arrive with a
       measured label wider than nothing. */
    [TestMethod]
    public void TheSweepRowsReallyCarryWords()
    {
      foreach (var seat in new[] { FctLabelSide.Left, FctLabelSide.Right, FctLabelSide.Below })
      {
        FctLayout.LabelSide = seat;
        var hit = Lean(FctArcBend.Left, heal: false);
        Assert.IsTrue(hit.SourceWidth > 10, $"{seat} measured a label of {hit.SourceWidth:F1} px on a source named \"Probe\"");
      }
    }
  }
}

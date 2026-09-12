using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests
{
  /*
   * The marked events end to end at the engine's edge: the mark colours, is written at the big class's size whether or not the log called
   * it a crit, reserves room for its glyph beside the number (geometry charges for it), never breaks the odometer, and
   * can never be folded — swallowed by a count — into any other row. The mask-to-mark mapping itself is Core's
   * LineModifiersParserTest.
   */
  [TestClass]
  public class FctSpecialTest
  {
    private const double Width = 800;
    private const double Height = 900;

    /* Size assertions below compare against the shipped class sizes, so both dials start at their shipped positions - a parked slider
       from another test suite would otherwise move the very numbers this file measures. */
    [TestInitialize]
    public void ResetPlayerScale()
    {
      FctLayout.LabelSide = FctAmbient.LabelSideDefault;
      FctScale.Text = FctScale.SizeDefault;
      FctScale.Crit = FctScale.CritSizeDefault;
    }

    /* A rail only exists in split: bands degrades Straight to freeze (FctSplitModesTest pins that), so these tests ask for
       the scheme where the style survives. My damage owns one column, and every row written here — plain, marked, folded —
       shares that column's spine, which is the odometer they are all measuring. */
    private static FctIngest Ingest() => new(new Random(41))
    {
      Style = FctMotionStyle.Straight,
      Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left),
    };

    /* The mark is an identity: purple over the lane colour, a reserved strip beside the number, and a crit's size
       emphasis (the blowout) even when the log never called it a crit. */
    [TestMethod]
    public void AMarkedRowWearsTheMarkAndReservesItsRoom()
    {
      var ingest = Ingest();
      var hits = new List<FctHitState>();

      var plain = ingest.Accept(hits, FctLane.DamageDealt, 950, null, false, false, false, null, Width, Height, 0);
      var marked = ingest.Accept(hits, FctLane.DamageDealt, 30000, "Double Backstab", false, false, false, null, Width, Height, 100,
        special: FctSpecial.Assassinate);

      Assert.IsNotNull(plain);
      Assert.IsNotNull(marked);
      Assert.AreEqual(FctStyle.SpecialArgb, marked.ValueArgb, "the mark overrides the lane hue");
      Assert.AreNotEqual(FctStyle.SpecialArgb, plain.ValueArgb);
      Assert.IsTrue(marked.IconAllowance > 10.0, $"the glyph reserves real room ({marked.IconAllowance:0.#})");
      Assert.AreEqual(0.0, plain.IconAllowance, "unmarked rows pay nothing");

      // big-class size: marked events are written at the crit class's font and ride its effects, whether or not the log called them crits
      Assert.IsTrue(marked.Blowout, "a mark must ride the crit effects lever even un-critted");
      Assert.IsFalse(plain.Blowout);
      Assert.AreEqual(FctStyle.DamageDealtFontSize * FctScale.CritSizeDefault, marked.ValueFontSize, 0.001,
        "the mark is written at the big class's size - its own dial, not the lane tier and never multiplied by both");
    }

    /*
     * The glyph hangs OUTSIDE the odometer: a marked row and a plain one in the same column still share a right edge.
     * Measured on a wide canvas because the ONE thing that may lawfully shift a rail is the edge clamp — a number whose
     * crit-size pop no longer fits between the rail and the wall gives ground to the wall — and a lane is only a quarter of
     * the overlay, so at this file's 800 px a nine-figure value is past that limit before the mark has any say in it.
     */
    [TestMethod]
    public void AMarkNeverMovesTheOdometer()
    {
      var ingest = Ingest();
      var hits = new List<FctHitState>();

      var plain = ingest.Accept(hits, FctLane.DamageDealt, 900, null, false, false, false, null, 2400, Height, 0);
      var marked = ingest.Accept(hits, FctLane.DamageDealt, 987654321, "Crush", false, false, false, null, 2400, Height, 0,
        special: FctSpecial.SlayUndead);
      Assert.IsNotNull(plain);
      Assert.IsNotNull(marked);

      for (var t = 0.0; t <= 1.0001; t += 0.1)
      {
        var plainRight = FctMotion.ArcedX(plain, t) + (plain.ValueWidth / 2.0);
        var markedRight = FctMotion.ArcedX(marked, t) + (marked.ValueWidth / 2.0);
        Assert.AreEqual(plainRight, markedRight, 1e-9, $"the mark shoved the number off its rail at t={t:F1}");
      }
    }

    /*
     * Fold isolation. A marked event rides the crit rule: it never folds, in either direction — "an assassinate" hiding
     * inside a growing "×6" of swings erases the event the row exists to report, and two identical marks are two events
     * worth seeing, not one row and a count. Plain duplicates underneath a mark still fold with each other as normal.
     */
    [TestMethod]
    public void MarksNeverFoldInEitherDirection()
    {
      var ingest = Ingest();
      ingest.Accumulate = true; // folding's policy, tested with the door open; see FctAccumulationTest for the door itself
      var hits = new List<FctHitState>();

      var plain = ingest.Accept(hits, FctLane.DamageDealt, 2040, "Backstab", false, false, false, null, Width, Height, 0);
      Assert.IsNotNull(plain);

      // same value, same source, but marked: it must NOT vanish into the plain row
      var marked = ingest.Accept(hits, FctLane.DamageDealt, 2040, "Backstab", false, false, false, null, Width, Height, 1,
        special: FctSpecial.Assassinate);
      Assert.IsNotNull(marked, "a marked event cannot be absorbed by an ordinary row of the same number");
      Assert.AreEqual(2, hits.Count);

      // a plain twin folds — and the backward scan meets the MARKED row first; it must step over it and join its own kind
      var plainAgain = ingest.Accept(hits, FctLane.DamageDealt, 2040, "Backstab", false, false, false, null, Width, Height, 2);
      Assert.IsNull(plainAgain, "ordinary duplicates still fold");
      Assert.AreEqual(2, hits.Count);
      Assert.AreEqual(1, marked.MergeCount, "the fold went under the mark - that glyph now claims a hit that was not one");
      Assert.AreEqual(2, plain.MergeCount, "the ordinary twins found each other");

      // identical to the marked row in every way including the mark: still two events, because big events never stack away
      var markedAgain = ingest.Accept(hits, FctLane.DamageDealt, 2040, "Backstab", false, false, false, null, Width, Height, 3,
        special: FctSpecial.Assassinate);
      Assert.IsNotNull(markedAgain, "two identical marks are two events; neither may be counted away");
      Assert.AreEqual(1, marked.MergeCount);
      Assert.AreEqual(3, hits.Count);
    }
  }
}
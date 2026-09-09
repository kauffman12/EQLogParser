using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests
{
  /*
   * The marked events end to end at the engine's edge: the mark colours, reserves room for its glyph beside the
   * number (geometry charges for it), never breaks the odometer, and can never be folded — swallowed by a count —
   * into an ordinary row of the same value. The mask-to-mark mapping itself is Core's LineModifiersParserTest.
   */
  [TestClass]
  public class FctSpecialTest
  {
    private const double Width = 800;
    private const double Height = 900;

    private static FctIngest Ingest() => new(new Random(41)) { Style = FctMotionStyle.Straight };

    /* The mark is an identity: purple over the lane colour, and a reserved strip beside the number. */
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
    }

    /* The glyph hangs OUTSIDE the odometer: a marked row and a plain one in the same column still share a right edge. */
    [TestMethod]
    public void AMarkNeverMovesTheOdometer()
    {
      var ingest = Ingest();
      var hits = new List<FctHitState>();

      var plain = ingest.Accept(hits, FctLane.DamageDealt, 900, null, false, false, false, null, Width, Height, 0);
      var marked = ingest.Accept(hits, FctLane.DamageDealt, 987654321, "Strike", false, false, false, null, Width, Height, 0,
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
     * Fold isolation. A marked hit may fold into an identical LIVE MARKED row (the glyph keeps standing, the count says
     * two), but never into a plain one and never let a plain one move in beside its mark — "an assassinate" hiding
     * inside a growing "×6" of swings erases the event the row exists to report.
     */
    [TestMethod]
    public void MarksFoldOnlyWithTheirOwnKind()
    {
      var ingest = Ingest();
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

      // identical to the marked row in every way including the mark: THIS is the fold that is allowed
      var markedAgain = ingest.Accept(hits, FctLane.DamageDealt, 2040, "Backstab", false, false, false, null, Width, Height, 3,
        special: FctSpecial.Assassinate);
      Assert.IsNull(markedAgain, "two identical marks should read as one row and a count");
      Assert.AreEqual(2, marked.MergeCount);
      Assert.AreEqual(2, hits.Count);
    }
  }
}

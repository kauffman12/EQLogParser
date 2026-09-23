namespace EQLogParser
{
  /*
   * Which way an arc leans, in the one layout that has a spine to bend.
   *
   *   out    away from the middle of the overlay: the left half bows left, the right half bows right. The default, and the shape the word
   *          "arc" asks for — a column's numbers curve away from where the target frame sits, and both halves read as a pair.
   *   left   every column bows left, wherever it stands. One direction across the whole overlay is a readable shape when the halves are far
   *          apart and nobody reads them as a pair, and it is the answer for an overlay parked on one side of the screen.
   *   right  the same, the other way.
   *
   * There used to be a fourth word, `open`: each lane asked which of its own hands was roomier and spent that on the bend. It was not a choice
   * anybody would have made once the question was asked; it was what the lanes did before there was a dial, kept as a word so nobody's overlay
   * changed on update. Measured against `out`, it turns out to mean nothing of its own:
   *
   * - fountain never heard it at all: bands is not a column layout and is never handed an arc (`ClampShape`, asserted by
   *   `FountainIsNeverHandedAnArcToLean`), so the word spoke only to split, where it could not even hold a direction still;
   * - in split it is not stable enough to be a preference: one category per half, damage on the right, and the direction of that same column
   *   changes with window width — +99.5 at 900 px, +210.8 at 1280, then -238.0 from 1440 up. Resize the window and your arc bends the other way,
   *   because the answer is a consequence of lane geometry rather than something anybody picked;
   * - and it was never the cheaper or deeper curve either: at those same widths `out` bows 146.2 / 210.8 / 238.0 where `open` managed 99.5 / 147.0 /
   *   238.0, once the row turns to hang in the hand its curve leans at (FctHitState.HangRight).
   *
   * So the word is gone and `out` is what a file with no FctOverlayArcBend key draws. A settings.txt written while the dial was on `develop` may
   * still carry "open"; it reads as the default rather than as an error, which is the whole compatibility story a word nobody shipped needed to be.
   *
   * What a lean costs has not changed: a named direction is paid for by the lane (FctStage.ParkForBend) and capped against the room on the side it
   * was pointed at, so a lane with thin air draws a shorter curve rather than pushing digits through a neighbour — and never one that flips sign,
   * since a bow that changed direction mid-lane would give one column two shapes.
   */
  internal enum FctArcBend
  {
    Out,
    Left,
    Right,
  }
}

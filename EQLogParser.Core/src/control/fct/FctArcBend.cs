namespace EQLogParser
{
  /*
   * Which way an arc leans. The shape has always had one answer per mode and no question asked, and the two answers disagree in a way
   * that reads as a bug when you are looking for your own numbers: fountain bows every column away from the shared middle strip (the genre
   * rule, where a lean into the other stream would tangle it), while split asks each lane which of its own hands is open and spends THAT on
   * the bend, because a split lane is private and shoving its rail off its spine to buy an outward curve starved every name on the lane to
   * "(Des" (measured: 126 px off the spine, FctLayout.Spawn). With one category on a side the open hand points at the middle of the screen,
   * so both halves lean inward and a player who reads that as "the arc goes the wrong way" is reading it correctly - it was never their choice.
   *
   * Hence four words, all of them honest about what they do to both halves:
   *
   *   open   the lane's own open hand, split's answer since the lanes were built, and the shipped default because it is what existing
   *          config already looks like. Nothing moves until somebody picks one of the three below.
   *   out    away from the middle of the overlay: the left half bows left, the right half bows right. Fountain's rule, offered for split -
   *          the "arc away from my target frame" look, which is what most people mean by "the standard".
   *   left   every column bows left, wherever it stands. One direction across the whole overlay is a readable shape when the two halves are
   *          far apart and nobody reads them as a pair, and it is the answer for an overlay parked on one side of the screen.
   *   right  the same, the other way.
   *
   * A direction is a wish, not a promise: the bend is capped against the room on that side (FctLayout.AssignTravel), so forcing a lane toward
   * a wall it has no air for shortens the curve rather than pushing the number through the wall - and never flips it, since a bow that changed
   * sign mid-lane would give one column two different shapes.
   */
  internal enum FctArcBend
  {
    Open,
    Out,
    Left,
    Right,
  }
}

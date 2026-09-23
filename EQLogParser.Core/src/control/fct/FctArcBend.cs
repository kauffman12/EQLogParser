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
   * A named direction costs the lane something, because a rail row is right-aligned and hangs its whole block to the LEFT of its spine: that leaves
   * every split column with free air on its right hand and its own glyphs on its left, which is why "left" drew a straight line and "out" curved on one
   * side when these words first shipped (measured at 1280: 448 px of air right of the rail, 0 px left of it). So FctStage.ParkForBend moves a column off
   * the wall it leans at until the lane can pay for the curve it was promised - one shift per COLUMN, from facts about the lane and never from the number
   * that arrived, so every row still shares one rail. Open pays nothing and moves nothing: it is what files written before this key exist already draw,
   * including the shorter curve an unbuyable lane can afford.
   *
   * The cap in FctLayout.AssignTravel stays the brake it always was - a lane whose walls leave nowhere to park draws a shorter curve rather than pushing
   * digits through a neighbour, and never one that flips sign, since a bow that changed sign mid-lane would give one column two shapes.
   */
  internal enum FctArcBend
  {
    Open,
    Out,
    Left,
    Right,
  }
}

namespace EQLogParser
{
  /*
   * Where the source label — the little "(slash)" or "(Flurry)" that names what made a number — sits relative to its
   * amount. Below is the shipped answer because that is where the overlay has always drawn it; left and right inline it
   * against the value the way Nag's default does, for people who read the pair as one token. The number never moves for
   * its label: in right mode the label hangs off the value's right edge, in left mode off its left, so a column of
   * amounts keeps one visual spine no matter which side the words live on.
   */
  internal enum FctLabelSide
  {
    Left,
    Below,
    Right,
  }
}
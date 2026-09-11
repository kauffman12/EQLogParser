namespace EQLogParser
{
  /*
   * The engine's dials are process globals: FctScale.Text/Crit/Time carry the player's size and speed choices, and FctLayout.LabelSide
   * carries where the canvas put the words. Nothing about that is wrong in the running overlay — one process, one overlay, stamped from
   * settings at startup — but it makes any two pieces of test code neighbours by accident: a pace test that doubles Time for one frame
   * mid-flight will otherwise travel into whatever class happens to be reading geometry next, and a doubled speed dial keeps rows in
   * flight long enough that crowding overflows sideways, which is the exact behaviour FctSplitModesTest exists to catch (a leaked 2.0 was
   * measured moving a row's spine from 1200 to 1273.8). So every test of this engine starts from the shipped ambient state rather than
   * from whatever ran before it: assumptions stated, not inherited.
   */
  internal static class FctAmbient
  {
    internal static void Reset()
    {
      FctScale.Text = FctScale.SizeDefault;
      FctScale.Crit = FctScale.CritSizeDefault;
      FctScale.Time = FctScale.TimeDefault;
      FctLayout.LabelSide = LabelSideDefault;
    }

    /* What the geometry uses before a canvas has stamped the setting — the same value the shipped overlay draws with. */
    internal static FctLabelSide LabelSideDefault => FctLabelSide.Below;
  }
}

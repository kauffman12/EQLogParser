namespace EQLogParser
{
  /*
   * One floating text in flight, shared by every render backend. Hit policy and geometry live here and
   * in FctLayout/FctMotion/FctIngest; a backend owns only its substrate resources (glyph objects,
   * brushes, halo sprites) and its draw loop, so tuning happens once instead of per renderer.
   */
  internal sealed class FctHitState
  {
    public FctLane Lane;

    // half of the canvas this hit belongs to; captured before crit pooling, so a taken crit stays incoming
    public bool LeftSide;

    // spawn anchor: x is the value text's center, y its top
    public double X0, Y0;

    // total upward travel / sideways arc amplitude / fountain fall distance (px)
    public double Rise, Arc, FallDist;

    // rise+arc finish by this age, then hold position until the fade (see FctMotion.RaisedY)
    public double MotionMs;

    // clamp band for the value's center x, already inset for text width and scale
    public double SideMin, SideMax;

    public double LifetimeMs, FadeMs;
    public double ValueFontSize, SourceFontSize;

    // 0xAARRGGBB, converted per substrate; keeps the style table free of WPF/Skia types
    public int ValueArgb, SourceArgb;

    // crit scale pop on top of the float curve
    public bool Blowout;

    // count-up: TargetValue is reached CountUpMs after AgeAtCountStartMs
    public double SpawnMs, AgeAtCountStartMs, CountUpMs;
    public double TargetValue, CountBaseValue;

    // ability/verb drawn under the value; null = no source line. Parentheses are added when drawn.
    public string Source;

    // literal main line for the zero-damage labels ("Dodge"); null means format the numeric value
    public string FixedText;

    // what is actually drawn this frame; FctMotion.RefreshText owns it, backends read it
    public string DisplayText = "";

    // measured width of DisplayText at ValueFontSize, written by the backend when it rebuilds glyphs
    public double ValueWidth;

    // set when DisplayText changed; the backend rebuilds glyphs + ValueWidth and clears it
    public bool TextDirty;
  }
}

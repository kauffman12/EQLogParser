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

    /* An item or spell proc rather than the attack or cast somebody aimed: reads a little smaller (FctStyle) and
     * leaves sooner (FctIngest.ApplyProcTempo). A crit proc is exempt from both — the pop already says it matters. */
    public bool Proc;

    /* A damage-over-time tick rather than one hit somebody aimed. Stored because folding needs it: a tick may only join
     * another number of the same kind, and once a number is on screen its kind cannot be re-derived from the value — 900
     * could be a swing or a tick, and letting those share a number is how one ends up claiming to be something it is not. */
    public bool Periodic;

    /* A healing number rather than a damaging one, captured from the producing lane before crit pooling: a heal crit pools onto
     * FctLane.Crit like any other, and without this flag the "+" in "its" text would lose track of what it was signing. */
    public bool Heal;

    /*
     * Index of this hit's cell in its band's fixed grid, or -1 while it floats. Only pulse mode allocates cells; the pool
     * is picked by band and by whether the hit is a proc, so this one number names the slot. FctCellGrid owns it.
     */
    public int Cell = -1;

    /* How this hit moves over its life: travel, fountain, pulse in place, or spray. Copied from the canvas's current
     * setting by ingest, then read by FctLayout for geometry and by FctMotion for scale. A hit keeps the style it was
     * spawned with, so flipping the overlay's choice mid-fight changes what comes next rather than teleporting what is
     * already on screen. */
    public FctMotionStyle Style;

    /* Whether this hit is about something happening to me — the bottom band, which it sinks away from. Captured from the
     * producing lane before crit pooling, so a taken crit stays on the incoming side. */
    public bool Incoming;

    // spawn anchor: x is the value text's center, y its top
    public double X0, Y0;

    /* Total travel (px, signed): positive rises, negative sinks — incoming hits in bands mode travel downwards so
     * that direction of motion says who acted. Arc is sideways amplitude (sign = which way first). */
    public double Rise, Arc;

    /*
     * Parabola's sideways drift (px, signed): reaches Bow at t=1, quadratically — x ∝ t² while y runs linearly, which is
     * exactly the genre's parabola with y as the independent variable. Separate from Arc because its time law differs
     * (quadratic vs ease), and a shared field would force FctMotion to guess which law a number meant.
     */
    public double Bow;

    /*
     * Fountain fall distance, and note the sign runs opposite to Rise because it is screen-relative: positive
     * accelerates toward the bottom of the screen (the usual gravity tail), negative back up toward the gap, which is
     * how the incoming band mirrors the outgoing fountain instead of parking against its own bottom edge. Zero means
     * hold style: no fall phase at all. Mixing the two conventions up is what makes this worth a comment.
     */
    public double FallDist;

    // rise+arc finish by this age, then hold position until the fade (see FctMotion.RaisedY)
    public double MotionMs;

    // clamp band for the value's center x, already inset for text width and scale
    public double SideMin, SideMax;

    /* Clamp band for the value's top y, inset so the drawn text stays clear of the protected middle strip. Left
     * unset (0..0) by layouts that put no vertical limit on a hit; FctMotion then skips the vertical clamp. */
    public double BandMinY, BandMaxY;

    public double LifetimeMs, FadeMs;
    public double ValueFontSize, SourceFontSize;

    // 0xAARRGGBB, converted per substrate; keeps the style table free of WPF/Skia types
    public int ValueArgb, SourceArgb;

    // crit scale pop on top of the float curve
    public bool Blowout;

    public double SpawnMs;

    /*
     * The face amount of one hit, drawn as it is and never added to. When several identical hits land on the same number
     * (FctIngest's fold) this stays the value of one of them and MergeCount says how many — a total would make the player
     * divide to find out what landed, and let eight small hits wear the face value of a big one.
     */
    public double Value;

    /* How many hits this one number stands for; 1 means it is simply its own hit. Drawn as "2,040 ×2" (FctText). */
    public int MergeCount = 1;

    // ability/verb drawn under the value; null = no source line. Parentheses are added when drawn.
    public string Source;

    // literal main line for the zero-damage labels ("Dodge"); null means format the numeric value
    public string FixedText;

    // what is actually drawn this frame; FctMotion.RefreshText owns it, backends read it
    public string DisplayText = "";

    // measured width of DisplayText at ValueFontSize, written by the backend when it rebuilds glyphs
    public double ValueWidth;

    /* Measured width of "(source)" at SourceFontSize, same rebuild. The inline label placements hang the label off the
       value's edge, which needs both widths in hand at draw time — measuring per frame to save this field would be the
       worse trade. */
    public double SourceWidth;

    // set when DisplayText changed; the backend rebuilds glyphs + ValueWidth and clears it
    public bool TextDirty;

    /*
     * Shallow copy, for FctPlacement's trials: geometry is fields and everything else (value, source, style, colours) is
     * read-only from here on, so a copy that shares the strings is enough to test a different launch position. Nothing that
     * owns a substrate resource is cloned — a trial never reaches a canvas, and only canvases cache per-hit resources.
     */
    public FctHitState Clone() => (FctHitState) MemberwiseClone();
  }
}

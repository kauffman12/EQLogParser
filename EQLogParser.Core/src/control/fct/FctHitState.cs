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

    /* One of the rare marked events (assassinate, headshot, slay undead, finishing blow, decapitation). Part of the fold
     * key: a marked hit may only fold into another hit carrying the same mark, so an assassinate can never hide inside a
     * growing "×6" of ordinary swings — the mark is the whole reason the row exists. */
    public FctSpecial Special;

    /* Horizontal room reserved for that event's glyph: geometry charges for it next to ValueWidth (clamps, collision,
     * lane spacing), and it never moves the odometer — the glyph hangs outside the number's left edge while the value
     * keeps its right edge on the rail. Set with the fonts in FctStyle.ApplyTo. */
    public double IconAllowance;

    /* How this hit moves over its life: queued down a lane, thrown as a fountain, sprayed, or held in place. Copied from the canvas's current
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

    /* The size-INDEPENDENT crit effects: swell in from below, halo, wider spray, top draw pass (see FctMotion.BlowoutScale and
       FctStyle.ApplyTo). Size itself is not part of this flag - the big class carries its dial in ValueFontSize - so nothing that
       prices a row's extent multiplies what the crit dial already set. */
    public bool Blowout;

    /*
     * The deliberate rail (FctConveyor): this row rides a lane clock instead of its own flight, so its place on the column is
     * distance rather than elapsed time. Set by FctConveyor.Enrol and stamped every frame by FctConveyor.Advance; read by
     * FctMotion for position, opacity and the crit's collapse, and by FctIngest.PruneExpired for the row's end. Rows that are
     * not on a conveyor — hold, fountain, spray, anything thrown rather than queued — leave these alone and keep the time law below.
     *
     * ConveyorQ is how far the lane has carried this row, in pixels, and it starts NEGATIVE: a row whose slot has not reached
     * the mouth yet waits off-column (invisible, still folding duplicates) until the lane brings its turn. ConveyorTravel is
     * the whole flight — |Rise|, measured after the row was pinned to the mouth — and passing it is the row's last frame.
     */
    public bool OnConveyor;
    public int ConveyorLane;
    public double ConveyorBirthPhase;
    public double ConveyorQ;
    public double ConveyorTravel = -1.0;

    /*
     * The congestion accelerator, FctConveyor.PressFloor..1.0, copied off the lane this row enrolled into (FctConveyor.Enrol) and
     * multiplied into its rail tempo exactly once (FctIngest.FinalizeRailTempo). A column at the dialled pace holds a certain number of
     * rows in flight; text arriving faster than that cannot be separated at that pace no matter where it enters, so a crowded lane runs
     * its whole queue faster — the backlog speeds up while the flood lasts and relaxes back to the player's setting as the column empties.
     * It is the LANE's number rather than this row's own decision, which is the difference between a convoy and a crowd: two rows on one
     * column at different presses drift apart, and the player reports it. Bounded below because even fully accelerated a number must stay
     * readable, and bounded in effect: the floor still clears the region inside about a second and a half, so a fight that stops never
     * keeps typing for seconds after. Rows outside a pressed lane are 1.0.
     */
    public double RailPress = 1.0;

    public double SpawnMs;

    /*
     * The face amount of one hit, drawn as it is and never added to. When several identical hits land on the same number
     * (FctIngest's fold) this stays the value of one of them and MergeCount says how many — a total would make the player
     * divide to find out what landed, and let eight small hits wear the face value of a big one.
     */
    public double Value;

    /* How many hits this one number stands for; 1 means it is simply its own hit. Drawn as "2,040 ×2" (FctText). */
    public int MergeCount = 1;

    /*
     * The amount as the player reads it ("12.5k"), cached at spawn because it is part of the fold key: FctIngest.TryAbsorb
     * compares it against every live number in the lane, and formatting both sides per candidate allocated two strings for
     * every comparison on the busiest path in the overlay during a burst. The drawn amount never changes after spawn — a fold
     * counts, it does not add — so this cannot go stale. Null until something asks (a hit built straight into a list by a
     * test, or a fixed-text label that never folds), which TryAbsorb handles by formatting once and keeping the result.
     */
    public string FormattedValue;

    // ability/verb drawn under the value; null = no source line. Parentheses are added when drawn.
    public string Source;

    /* The source with its parentheses, built once at spawn. DrawHit paints this and RebuildGlyphs measures it — the
       old $"({Source})" at draw time allocated a fresh string per hit per frame, which is raid traffic multiplied by
       display rate for text that never changes. Null exactly when Source is null or empty. */
    public string SourceLabel;

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
    public FctHitState Clone() => (FctHitState)MemberwiseClone();
  }
}
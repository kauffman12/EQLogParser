using System.Collections.Generic;
using SkiaSharp;

namespace EQLogParser
{
  /*
   * The marks for the special events (FctSpecial): five chunky glyphs drawn as Skia paths in code — no image assets,
   * no new dependency, crisp at any text size and tinted by the same brush logic as the numbers. Each glyph is built
   * once, on a 24x24 unit grid, and scaled by the caller to sit beside its number; bodies cache so a frame allocates
   * nothing.
   *
   * The set: a dagger (assassinate), an arrow (headshot), a ghost (slay undead), a skull (finishing blow — the
   * universal kill mark, and deliberately a skull while the undead mark is a ghost) and a double-bit battle axe
   * (decapitation, the Berserker two-hander). Shapes are chosen for one job: read at roughly thirty pixels in
   * peripheral vision over a moving game view. Nothing thin, nothing with more than a silhouette.
   */
  internal static class FctMarks
  {
    internal const double Unit = 24.0;

    /* The silhouette (drawn outlined-and-filled like the text) plus optional dark detail drawn over it in black
       — eyes and nose are what make eyes and nose rather than two blobs. Null for glyphs that need none. */
    internal readonly record struct Mark(SKPath Body, SKPath Dark);

    private static readonly Dictionary<FctSpecial, Mark> Cache = [];

    internal static Mark For(FctSpecial special)
    {
      if (Cache.TryGetValue(special, out var cached))
      {
        return cached;
      }

      var mark = Build(special);
      Cache[special] = mark;
      return mark;
    }

    private static Mark Build(FctSpecial special) => special switch
    {
      FctSpecial.Assassinate => Dagger(),
      FctSpecial.Headshot => Arrow(),
      FctSpecial.SlayUndead => Ghost(),
      FctSpecial.FinishingBlow => Skull(),
      FctSpecial.Decapitation => BattleAxe(),
      _ => new Mark(new SKPath(), null),
    };

    /* Point-down dagger: diamond blade, crossguard, grip, pommel — one vertical object, unmistakably a knife. */
    private static Mark Dagger()
    {
      var body = new SKPath();
      body.MoveTo(12, 1);
      body.LineTo(15.1f, 10.4f);
      body.LineTo(12, 13.4f);
      body.LineTo(8.9f, 10.4f);
      body.Close();
      body.AddRect(new SKRect(7.2f, 13.2f, 16.8f, 15.2f)); // crossguard
      body.AddRect(new SKRect(10.7f, 15.2f, 13.3f, 20.6f)); // grip
      body.AddCircle(12, 21.9f, 1.9f); // pommel
      return new Mark(body, null);
    }

    /* Arrow flying to the upper right: a solid triangle head on a thick shaft — two shapes, no thin lines. */
    private static Mark Arrow()
    {
      /*
       * One connected silhouette: a swept head hugging the corner, a shaft running out of the head's notch down to
       * the nock, fletting flaring off the nock end. The first cut floated the fletting off the shaft and rendered as
       * a diamond on a stick — every point here is shared with its neighbour so the glyph stays ONE object.
       */
      var body = new SKPath();
      body.MoveTo(23, 1); // head: corner triangle with a notch cut at the bisector
      body.LineTo(23, 11.4f);
      body.LineTo(18.4f, 7.6f);
      body.LineTo(12.6f, 1);
      body.Close();

      // shaft: from just inside the notch corners down to the nock
      body.MoveTo(19.9f, 8.5f);
      body.LineTo(7.4f, 21.0f);
      body.LineTo(4.6f, 18.2f);
      body.LineTo(17.1f, 5.7f);
      body.Close();

      // fletting: a chevron whose inner points sit ON the shaft's own end corners
      body.MoveTo(4.6f, 18.2f);
      body.LineTo(1.8f, 18.9f);
      body.LineTo(5.5f, 22.6f);
      body.LineTo(7.4f, 21.0f);
      body.Close();
      return new Mark(body, null);
    }

    /* Classic ghost: dome head, wavy hem, and two dark eyes — the silhouette does most of the talking. */
    private static Mark Ghost()
    {
      var body = new SKPath();
      body.MoveTo(4.2f, 22);
      body.LineTo(4.2f, 11.6f);
      body.ArcTo(new SKRect(4.2f, 2.2f, 19.8f, 17.8f), 180, 180, false); // dome down to the right wall
      body.LineTo(19.8f, 22);
      body.LineTo(16.9f, 19.2f);
      body.LineTo(14f, 22);
      body.LineTo(12, 19.2f);
      body.LineTo(9.9f, 22);
      body.LineTo(7f, 19.2f);
      body.Close();

      var dark = new SKPath();
      dark.AddOval(new SKRect(8.3f, 9.4f, 11f, 12.6f));
      dark.AddOval(new SKRect(13f, 9.4f, 15.7f, 12.6f));
      return new Mark(body, dark);
    }

    /* Skull: round dome over a squared jaw; eye sockets and nose cut out in black. */
    private static Mark Skull()
    {
      var body = new SKPath();
      body.AddOval(new SKRect(4.4f, 2.6f, 19.6f, 17.8f)); // cranium
      body.AddRoundRect(new SKRect(7.6f, 14.5f, 16.4f, 21.8f), 2.4f, 2.4f); // jaw

      var dark = new SKPath();
      dark.AddOval(new SKRect(7.3f, 8.4f, 10.9f, 12.6f));
      dark.AddOval(new SKRect(13.1f, 8.4f, 16.7f, 12.6f));
      dark.MoveTo(12, 12.9f);
      dark.LineTo(10.6f, 15.4f);
      dark.LineTo(13.4f, 15.4f);
      dark.Close(); // nose

      var teeth = new SKPath(); // two jaw gaps so the jaw reads as teeth, not a box
      teeth.AddRect(new SKRect(10.4f, 17.6f, 11.3f, 21.8f));
      teeth.AddRect(new SKRect(12.7f, 17.6f, 13.6f, 21.8f));
      dark.AddPath(teeth);
      return new Mark(body, dark);
    }

    /*
     * Two-handed double-bit axe. The first cut drew the bits as canopy arcs closing across the haft and rendered as an
     * umbrella; the fix is the silhouette's furniture: the haft must PROTRUDE above and below the head (that long grip
     * is what says "two-handed axe" at a glance), and each bit is a fin flanking it — straight edge against the haft,
     * outer edge bulging away, tips fore and aft.
     */
    private static Mark BattleAxe()
    {
      var body = new SKPath();
      body.AddRoundRect(new SKRect(10.9f, 1.0f, 13.1f, 22.8f), 1.1f, 1.1f); // haft, sticking out well above and below

      var left = new SKPath();
      left.MoveTo(10.9f, 4.0f);                      // shoulder against the haft
      left.LineTo(3.6f, 2.2f);                       // fore tip above the shoulder: a flare, not a canopy
      left.QuadTo(1.0f, 7.5f, 3.6f, 13.2f);          // bellied outer edge down to the cutting tip
      left.LineTo(10.9f, 11.4f);                     // straight back along the haft
      left.Close();
      body.AddPath(left);

      var right = new SKPath();
      right.MoveTo(13.1f, 4.0f);
      right.LineTo(20.4f, 2.2f);
      right.QuadTo(23.0f, 7.5f, 20.4f, 13.2f);
      right.LineTo(13.1f, 11.4f);
      right.Close();
      body.AddPath(right);
      return new Mark(body, null);
    }
  }
}

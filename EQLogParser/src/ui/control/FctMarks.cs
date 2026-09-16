using SkiaSharp;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The marks for the special events (FctSpecial): seven chunky glyphs drawn as Skia paths in code — no image assets,
   * no new dependency, crisp at any text size and tinted by the same brush logic as the numbers. Each glyph is built
   * once, on a 24x24 unit grid, and scaled by the caller to sit beside its number; bodies cache so a frame allocates
   * nothing.
   *
   * The set: a dagger (assassinate), a fletched war arrow, point first (headshot), a ghost (slay undead), a
   * tombstone (finishing blow — the universal kill mark; death reads, and round blobs stay the ghost's and the
   * skull's own), a double-bit battle axe (decapitation, the Berserker two-hander), a pointed wizard hat with a dark
   * band (mana burn) and the skull for life burn. The hat and the tombstone are bake-off survivors like the arrow was:
   * the hat's straight triangle read as an arrowhead until it grew a thick brim and a bent tip; the kill mark's scythe
   * fused blade-and-haft into "a slash" at shipped size, and a sword driven point-first was the dagger's silhouette
   * family — the one thing the set cannot have twice. The necromancer's own figure failed too (anatomy at 16 px reads
   * as a lightbulb), so the skull belongs to Life Burn outright.
   * Shapes are chosen for one job: read at roughly thirty pixels in peripheral vision over a moving game view.
   * Nothing thin, nothing with more than a silhouette.
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
      FctSpecial.FinishingBlow => Tombstone(),
      FctSpecial.Decapitation => BattleAxe(),
      FctSpecial.ManaBurn => WizardHat(),
      FctSpecial.LifeBurn => Skull(), // the necro shares the kill mark's skull — see the header note
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

    /*
     * Headshot is an ARROW the way an archer holds one — not a direction sign, which is what the first cut (a plain
     * triangle on a stick flying to the corner) read as. Three parts, all visible at the 16-18 px the mark ships at:
     * a leaf-bladed broadhead with a concave base, bare shaft between them, and two vanes swept back like real
     * feathering. Every part keeps enough mass to hold its shape: a slimmer redraw than this one lost most of its
     * strokes at 16 px, which is the same size war the glyph has been losing and re-earning since the start.
     *
     * A whole bow stood behind this shot in one attempt — players say "bow and arrow", so the phrase deserved a try —
     * and rendered as soup; a later variant stood the bow VERTICAL beside the arrow and failed the same way from a
     * different angle: two objects side by side each get half a silhouette, and half of 17 px is outlines eating the
     * gap. The classical split-bow-around-the-shaft icon drew beautifully at 150 px and collapsed into a wreath at
     * 18. Diagonal flights (the classic quiver icon) collapsed into a checkmark. What survives the size war is
     * upright, one object, and unmistakable: arrow, point first.
     */
    private static Mark Arrow()
    {
      var body = new SKPath();

      // broadhead, point up: a rounded leaf blade — barbs read off the wide concave base without needing sharp corners
      body.MoveTo(12, 0.5f);
      body.QuadTo(7.9f, 5.6f, 8.2f, 10.0f);
      body.QuadTo(12, 7.4f, 15.8f, 10.0f);
      body.QuadTo(16.1f, 5.6f, 12, 0.5f);
      body.Close();

      // bare shaft — the gap between head and fletch is what keeps them separate at small sizes
      body.AddRect(new SKRect(10.65f, 9.0f, 13.35f, 21.2f));

      /* Two vanes swept back ~45 degrees from the shaft: feathering, not a podium. The first survivor of the size war
         flared out to square shoulders wider than the head and read as a trophy stand under the point; the sweep
         gives the same silhouette mass with an arrow's posture — head heaviest, tail feathering away. */
      body.MoveTo(10.65f, 14.6f);
      body.LineTo(6.2f, 22.2f);
      body.LineTo(6.2f, 17.6f);
      body.LineTo(10.65f, 11.4f);
      body.Close();
      body.MoveTo(13.35f, 14.6f);
      body.LineTo(17.8f, 22.2f);
      body.LineTo(17.8f, 17.6f);
      body.LineTo(13.35f, 11.4f);
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

    /*
     * The tombstone for the universal kill mark. Broad slab, arc crown, and a FLAT ground slab across the bottom —
     * that footer is what stops it rendering as a round blob at 16 px (the skull can keep its roundness because it has
     * eyes; the stone trades eyes for two dark engraving bars, wide horizontals the eye finds first). Chosen over a
     * scythe and over a sword driven point-first, both eliminated in the bake-off — see the header.
     */
    private static Mark Tombstone()
    {
      var body = new SKPath();
      body.MoveTo(4.6f, 20.4f);
      body.LineTo(4.6f, 9.4f);
      body.QuadTo(4.6f, 1.6f, 12, 1.6f);          // arc crown
      body.QuadTo(19.4f, 1.6f, 19.4f, 9.4f);
      body.LineTo(19.4f, 20.4f);
      body.Close();
      body.AddRoundRect(new SKRect(2.4f, 19.2f, 21.6f, 23.0f), 1.6f, 1.6f); // ground slab

      var dark = new SKPath();                    // engraving: two wide bars, not letters — letters are soup here
      dark.AddRoundRect(new SKRect(8.4f, 7.6f, 15.6f, 9.6f), 0.9f, 0.9f);
      dark.AddRoundRect(new SKRect(9.6f, 11.4f, 14.4f, 13.2f), 0.9f, 0.9f);
      return new Mark(body, dark);
    }

    /*
     * Mana Burn is the wizard's, so it wears the wizard hat: a broad, THICK brim and a cone that bends over at the end
     * — the bent tip is what separates "wizard hat" from "party furniture" and from an arrowhead, both of which the
     * straight triangle turned out to read as. The dark band sits where cone meets brim: at mark size it does the job
     * eyes do on the skull — a horizontal feature the eye finds first, which is what makes the blob read as HAT.
     */
    private static Mark WizardHat()
    {
      var body = new SKPath();
      body.MoveTo(7.0f, 16.8f);                          // cone: rises from the brim, bending right
      body.QuadTo(9.4f, 5.8f, 18.6f, 2.6f);
      body.LineTo(20.6f, 6.2f);                          // a thick corner for the drooped tip, never a needle
      body.QuadTo(14.4f, 9.6f, 16.6f, 16.8f);            // right edge back down with a shallow S
      body.Close();
      body.AddRoundRect(new SKRect(1.2f, 15.4f, 22.8f, 21.0f), 2.8f, 2.8f); // brim, tall enough to survive 16 px

      /* The band rides MID-CONE: drawn across the cone/brim joint it re-cut the silhouette in two and rendered as a
         horn on a pill, so it stays strictly inside the cone with purple bridging to the brim below it. */
      var dark = new SKPath();
      dark.AddRoundRect(new SKRect(9.3f, 9.4f, 15.4f, 11.6f), 0.8f, 0.8f); // hatband
      return new Mark(body, dark);
    }

  }
}
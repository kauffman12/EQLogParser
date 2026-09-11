using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{

  /*
   * A source name is drawn, and it is spaced for: those have to be the same string in the same place. Geometry used to charge the digits
   * alone, so an inline "(Glormok)" was painted outward from a box nobody had measured — across the seam into the neighbouring column at
   * 980 px, and further into it the wider the words or the narrower the window. FctLayout.BlockAboutCentre is where the drawn block is now
   * defined (amount, event glyph, label wherever the label side put it) and FctLayout.FitSource is where a name is shortened — only when its
   * column genuinely cannot carry it, which is why the same name survives whole at a smaller font or in a wider window, and why nothing is
   * cut that fits.
   */
  [TestClass]
  [DoNotParallelize]
  public sealed class FctLabelFitTest
  {
    private const double Width = 980;
    private const double Height = 560;

    // eleven characters: inside the ceiling (FctLayout.MaxSourceChars), so anything cut from it was cut for lack of room and nothing else
  private const string FitsFine = "GlormokFury";

  // a font-agnostic measurer: five pixels per point per character, so the arithmetic below is readable and exact
    private static double FakeWidth(string text, double size) => text.Length * size * 0.5;

    /* Shorten a name only when there is no room for it — the user's own caveat, stated as a rule: "if someone chooses smaller fonts maybe it
       can fit". A wide column and a narrow one are given the same name here, and only the second one pays for it. */
    [TestMethod]
    public void ANameIsCutOnlyWhenItsColumnRunsOutOfRoom()
    {
      var side = FctLayout.LabelSide;
      try
      {
        FctLayout.LabelSide = FctLabelSide.Right;

        var roomy = Hit(FitsFine, 400);
        Assert.AreEqual($"({FitsFine})", roomy.SourceLabel, "a name that fits is drawn whole — no ellipsis, no trimming, however long");

        var tight = Hit(FitsFine, 180);   // the whole block needs 200 px, so this column is ten px short of that name
        Assert.IsTrue(tight.SourceLabel!.EndsWith("…)", StringComparison.Ordinal),
          $"the same name in a narrower column has to give ground, got {tight.SourceLabel}");
        Assert.IsTrue(tight.SourceWidth < roomy.SourceWidth, "and it gives exactly the letters that do not fit");
        AssertBlockFits(tight, 180);

        // ...but never past the point where the label says something: "(…)" names nothing, so the ellipsis is dropped before that
        var desperate = Hit(FitsFine, 1);
        Assert.IsTrue(desperate.SourceLabel!.Length >= 5, $"a name still has to be a name, got {desperate.SourceLabel}");
      }
      finally
      {
        FctLayout.LabelSide = side;
      }
    }

    /* The other bound: past a dozen characters the extra letters stop identifying the mob and start eating the column, so that is the most a
       name is worth even in a window with room to spare. A ceiling on usefulness, not on pixels — which is what makes cutting predictable
       rather than a function of how wide somebody's crit happened to be. */
    [TestMethod]
    public void TwelveCharactersIsTheMostANameIsWorth()
    {
      var side = FctLayout.LabelSide;
      try
      {
        FctLayout.LabelSide = FctLabelSide.Right;

        var hit = Hit("Supercalifragilistic", 100_000);
        Assert.AreEqual("(Supercalifra…)", hit.SourceLabel,
          "twelve characters of name and an ellipsis, whatever room there is");

        var short_ = Hit("Glormok", 100_000);
        Assert.AreEqual("(Glormok)", short_.SourceLabel, "a name inside the ceiling is untouched by it");
      }
      finally
      {
        FctLayout.LabelSide = side;
      }
    }

    /* The block itself, per label side — the numbers every clamp and collision test charges. Right: the words open a reach on the side a
       right-aligned amount never had. Left: they deepen the one it already had, which is why the two sides are not symmetric and cannot be.
       Below: the second line can overhang either way by half the difference between it and its number. */
    [TestMethod]
    public void EachLabelSideHasItsOwnBlock()
    {
      var side = FctLayout.LabelSide;
      try
      {
        var hit = Hit("Flurry", 100_000);
        var gap = FctLayout.LabelGap(hit);

        FctLayout.LabelSide = FctLabelSide.Right;
        var right = FctLayout.BlockFromRail(hit, hit.SourceWidth, 1.0);
        Assert.AreEqual(0.0, right.Left - (hit.ValueWidth + hit.IconAllowance), 1e-9, "the value's own box still hangs left of the rail");
        // measured from the rail, which IS the value's right edge: the words add their gap and themselves, and nothing else
        Assert.AreEqual(gap + hit.SourceWidth, right.Right, 1e-9, "and the words are the whole of that new reach to the right");

        FctLayout.LabelSide = FctLabelSide.Left;
        var left = FctLayout.BlockFromRail(hit, hit.SourceWidth, 1.0);
        Assert.AreEqual(0.0, left.Right, 1e-9, "nothing reaches past the rail when the words went the other way");
        Assert.IsTrue(left.Left > right.Left + hit.SourceWidth, "the side that already hung left of the rail now hangs further");

        FctLayout.LabelSide = FctLabelSide.Below;
        var below = FctLayout.BlockFromRail(hit, hit.SourceWidth, 1.0);
        Assert.AreEqual(below.Left - hit.ValueWidth - hit.IconAllowance, Math.Max(0, (hit.SourceWidth - hit.ValueWidth) / 2.0), 1e-9,
          "a second line overhangs its amount on both sides by half the difference");
      }
      finally
      {
        FctLayout.LabelSide = side;
      }
    }

    /* The complaint this answers: with the words beside their amounts, a name was drawn through the column boundary because the geometry had
       only ever charged for the digits. Every row of a split fight — both damage streams, heals, crits, long names — is now sampled along its
       whole flight and must keep its entire block inside its own territory. */
    [TestMethod]
    public void NothingIsDrawnAcrossAColumnBoundary()
    {
      var side = FctLayout.LabelSide;
      try
      {
        FctLayout.LabelSide = FctLabelSide.Right;

        var choice = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp: false, outgoingUp: true,
          healSide: FctRegionSide.Right);
        var ingest = new FctIngest(new Random(5)) { Style = FctMotionStyle.Parabola, Layout = choice };
        var stage = choice.Stage(Width, Height);
        var hits = new List<FctHitState>();

        var lanes = new[] { FctLane.DamageDealt, FctLane.DamageTaken, FctLane.HealingReceived };
        var names = new[] { "Flurry", "Backstab", "Supercalifragilistic", "Complete Heal", "Kurnuphos" };
        for (var i = 0; i < 24; i++)
        {
          Assert.IsNotNull(ingest.Accept(hits, lanes[i % lanes.Length], 100 + (i * 517), names[i % names.Length], i % 4 == 0, false, false,
            null, Width, Height, i * 220.0));
          ingest.PruneExpired(hits, (i * 220.0) + 110);

          foreach (var hit in hits)
          {
            AssertBlockInsideItsTerritory(hit, stage);
          }
        }

        // and the words really were the widest part of those rows: had geometry kept charging the digits alone, it would not have noticed them
        var widest = 0.0;
        foreach (var hit in hits)
        {
          widest = Math.Max(widest, FctLayout.BlockHalf(hit) - (hit.ValueWidth / 2.0));
        }

        Assert.IsTrue(widest > 4, $"a label wider than its number should have been charged for, saw {widest:0.#} px of extra reach");
      }
      finally
      {
        FctLayout.LabelSide = side;
      }
    }

    /* A row's block also has to survive the window shrinking under it: FctResize re-derives the territory and marks the text, so the name is
       re-fitted against the room there now — and a column made wider gives back the letters it took. The room arithmetic is what is pinned here;
       the canvas is what re-runs it with real glyphs. */
    [TestMethod]
    public void ANameFollowsTheRoomItHas()
    {
      var side = FctLayout.LabelSide;
      try
      {
        FctLayout.LabelSide = FctLabelSide.Left;

        var previous = string.Empty;
        for (var room = 1200.0; room >= 60.0; room -= 40.0)
        {
          var hit = Hit(FitsFine, room);
          AssertBlockFits(hit, room);
          Assert.IsTrue(hit.SourceLabel!.Length <= previous.Length || previous.Length == 0,
            $"a narrower column cannot carry a longer name: {previous} then {hit.SourceLabel}");
          previous = hit.SourceLabel;
        }
      }
      finally
      {
        FctLayout.LabelSide = side;
      }
    }

    /* A hit with the fonts a lane would give it, its label fitted to `room` pixels of territory. */
    private static FctHitState Hit(string source, double room)
    {
      var hit = new FctHitState
      {
        Source = source,
        ValueFontSize = 34,
        SourceFontSize = 20,
        ValueWidth = 60,
      };

      FctLayout.FitSource(hit, room, FakeWidth);
      return hit;
    }

    /* Either the block fits, or the name is already at the floor where cutting says more than it removes (FctLayout.MinSourceChars): a column
       this narrow keeps its three letters and lets the clamp centre it, which is a documented degradation rather than a silent truncation. */
    private static void AssertBlockFits(FctHitState hit, double room)
    {
      var (left, right) = FctLayout.BlockFromRail(hit, hit.SourceWidth, 1.0);
      var inner = hit.SourceLabel![1..^1].TrimEnd('\u2026').Length;
      Assert.IsTrue(left + right <= room + 1e-9 || inner <= 3,
        $"{hit.SourceLabel} still needs {(left + right):0.#} px of a {room:0} px column and is not at the floor yet");
    }

    private static void AssertBlockInsideItsTerritory(FctHitState hit, FctStage stage)
    {
      var region = stage.RegionFor(hit);

      // the clamp band is the territory less its edge pad; SideMin/SideMax carry it (FctLayout.Refit)
      Assert.AreEqual(region.X + FctLayout.EdgePad, hit.SideMin, 1e-6, "the row's band belongs to its own column");

      var (left, right) = FctLayout.BlockAboutCentre(hit, hit.SourceWidth, 1.0);
      for (var t = 0.0; t <= 1.0; t += 0.1)
      {
        var centre = FctMotion.ArcedX(hit, t);
        Assert.IsTrue(centre - left >= hit.SideMin - 0.5 && centre + right <= hit.SideMax + 0.5,
          $"{hit.SourceLabel} was drawn outside its column at t={t:F1}: [{centre - left:0.#}, {centre + right:0.#}] against " +
          $"[{hit.SideMin:0.#}, {hit.SideMax:0.#}]");
      }
    }
  }
}
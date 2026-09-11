using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EQLogParser
{
  /*
   * The configure-mode demo: a scripted loop of thirty-two events so a size, speed or style change can be seen moving without a fight.
   *
   * The important assertions are about honesty rather than animation. Its vocabulary has to be EverQuest's own — the melee verbs the
   * parser recognises, spell names that exist in the shipped data/spells.txt, proc names from data/procs.txt, and Labels constants for the
   * zero-damage words — because a demo showing "Backhand" trains a player to expect text that never appears. And its schedule has to be a
   * loop that lets every number finish: a cycle that cleared live text at the seam would look like the overlay truncates fades.
   */
  [TestClass]
  public class FctDemoTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
    /* The six verbs DamageLineParser produces for melee, in the base form FctManager.DisplaySource shows (it singularises "Bites"). */
    private static readonly string[] MeleeVerbs = ["Backstab", "Bite", "Claw", "Crush", "Pierce", "Punch", "Slash"];

    private static readonly string[] Words =
      [Labels.Dodge, Labels.Parry, Labels.Block, Labels.Miss, Labels.Riposte, Labels.Absorb, Labels.Invulnerable, Labels.Resist];

    [TestMethod]
    public void Script_CoversWhatAPlayerHasToTellApart()
    {
      var script = FctDemo.Script;

      Assert.IsTrue(script.Count >= 18, $"only {script.Count} events: too sparse to read as combat");
      /* The ceiling is the show list's, not a taste for brevity: seventeen switches have to be demonstrable in one pass, which is what the
         count is spent on. Room above that is a raid log — configure mode teaches what a switch does, it is not a fight re-enactment. */
      Assert.IsTrue(script.Count <= 34, $"{script.Count} events is a raid log, not a demo");

      Assert.IsTrue(script.Any(c => !FctLayout.IsIncoming(c.Lane)), "nothing in the outgoing band");
      Assert.IsTrue(script.Any(c => FctLayout.IsIncoming(c.Lane)), "nothing in the incoming band");
      Assert.IsTrue(script.Count(c => c.Crit) >= 2, "a single crit cannot show both dealt and taken crit looks");
      Assert.IsTrue(script.Any(c => c.Proc), "no proc, so the quicker tier is never shown");
      Assert.IsTrue(script.Any(c => c.ValueText != null), "no zero-damage word (DODGE/PARRY/MISS)");

      // the fold has to be seen happening: three identical ticks in the same lane, which is exactly what folds into "412 ×3"
      var folds = script.Where(c => c.Periodic && c.ValueText is null)
        .GroupBy(c => (c.Lane, c.Source, c.Value))
        .Where(g => g.Count() >= 3);
      Assert.IsTrue(folds.Any(), "no damage-over-time tick repeated three times, so the ×N fold is never demonstrated");
    }

    /*
     * Value-shape coverage. The formatter has six bands - two digits, three, commaed thousands, tenths-of-k, hundreds of k, and m -
     * and the odometer's whole case lives at the extremes: a "47" and an "896.8k" must agree to share a right edge with everything
     * between them. A demo whose values all sit in one band proves the alignment nowhere, so the script owes every band at least
     * one cue; the same goes for the five special-attack marks, which cost nothing to show and everything to discover in a real
     * fight if configure mode never showed them.
     */
    [TestMethod]
    public void Script_CoversEveryValueShapeAndEveryMark()
    {
      var numeric = FctDemo.Script.Where(c => c.ValueText is null).Select(c => c.Value).ToList();

      string Band(double v) =>
        v < 100 ? "two digits" :
        v < 1_000 ? "three digits" :
        v < 10_000 ? "commaed thousands" :
        v < 100_000 ? "tenths of k" :
        v < 1_000_000 ? "hundreds of k" : "m";

      foreach (var band in new[] { "two digits", "three digits", "commaed thousands", "tenths of k", "hundreds of k", "m" })
      {
        Assert.IsTrue(numeric.Any(v => Band(v) == band), $"no cue lands in the {band} band: that column never scrolls in configure mode");
      }

      var marks = FctDemo.Script.Select(c => c.Special).Where(s => s is not FctSpecial.None).Distinct().ToList();
      foreach (FctSpecial special in Enum.GetValues<FctSpecial>())
      {
        if (special is FctSpecial.None)
        {
          continue;
        }

        Assert.IsTrue(marks.Contains(special), $"{special} never appears in the demo: a player could configure the overlay without ever seeing the mark");
      }
    }

    /*
     * Switch coverage, the same honesty rule as the vocabulary below it. The panel offers seventeen switches — nine rows of numbers and eight
     * words — and configure mode is the only place a player meets them without a fight, so every one has to be provable in twelve seconds: mute
     * "pet melee" and something in the sample has to go quiet. A row with no cue reads as a broken switch, and it gets tested in exactly the
     * direction that shows nothing, because that is the switch the player just clicked. This is why the demo carries a pet swinging and casting:
     * without those two cues "pet melee" and "pet spells" could only be discovered by getting a pet into a real fight.
     */
    [TestMethod]
    public void Script_CoversEverySwitchInTheShowList()
    {
      var script = FctDemo.Script;

      foreach (var row in FctShowList.Rows)
      {
        Assert.IsTrue(script.Any(c => c.Row == row.Row), $"no cue for '{row.Label}': its switch cannot be seen working without a fight");
      }

      foreach (var word in FctShowList.Words)
      {
        Assert.IsTrue(script.Any(c => c.ValueText == word.Word), $"no cue for the word '{word.Label}'");
      }

      // and no number may be row-less: it would answer to no switch, which reads as an overlay ignoring its own settings
      foreach (var cue in script.Where(c => c.ValueText is null))
      {
        Assert.IsTrue(cue.Row is not FctRow.Word, $"'{cue.Source}' is a number with no row, so nothing on the panel can hide it");
      }
    }

    [TestMethod]
    public void Script_UsesEverQuestsOwnVocabulary()
    {
      // LoadNames fails the test rather than returning nothing: a vocabulary check that quietly ran against an empty set would pass forever
      var spells = LoadNames("spells.txt");
      var procs = LoadNames("procs.txt");

      foreach (var cue in FctDemo.Script)
      {
        // a label line says what the game said, not what this file invented
        if (cue.ValueText != null)
        {
          Assert.IsTrue(Words.Contains(cue.ValueText), $"{cue.ValueText} is not a label the parser assigns");
          continue;
        }

        Assert.IsNotNull(cue.Source, "a numeric event with no source line has nothing to show and teaches nothing");

        if (MeleeVerbs.Contains(cue.Source!))
        {
          continue; // melee verbs come from the parser's own list, not from spells.txt
        }

        var inSpells = spells.Contains(cue.Source!);
        var inProcs = procs.Contains(cue.Source!);
        Assert.IsTrue(inSpells || inProcs, $"\"{cue.Source}\" is not a spell or proc EQLogParser knows - invented vocabulary");

        if (cue.Proc)
        {
          Assert.IsTrue(inProcs, $"{cue.Source} is shown as a proc but is not in data/procs.txt");
        }
      }
    }

    [TestMethod]
    public void Player_SpawnsTheScriptIntoItsOwnList()
    {
      var demo = new FctDemo();
      Assert.IsFalse(demo.Advance(100, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, null, null), "an unstarted demo must not run");

      var spawned = 0;
      demo.Start(0);

      for (var now = 0.0; now <= 3000; now += 100)
      {
        if (demo.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, _ => spawned++, null))
        {
          Assert.IsTrue(demo.Hits.Count > 0, "a frame that changed should have something in it");
        }
      }

      Assert.IsTrue(spawned >= 5, $"only {spawned} events in the first three seconds");

      var lanes = demo.Hits.Select(h => h.Lane).ToHashSet();
      Assert.IsTrue(lanes.Any(FctLayout.IsIncoming) && lanes.Any(l => !FctLayout.IsIncoming(l)),
        "three seconds of the script should show both bands");

      foreach (var hit in demo.Hits)
      {
        Assert.IsTrue(hit.LifetimeMs > 0, $"{hit.Lane} landed with no lifetime");
        Assert.AreEqual(FctMotionStyle.Hold, hit.Style, "the default style is what the demo has to start on");
      }
    }

    /*
     * Why the style is an argument of Advance instead of something set on the demo once. The loop runs its own ingest - that is what keeps it out of
     * the counters - which also means it holds its own copy of the motion style, and a copy set from outside can be forgotten. It was: selecting
     * a style played hold, and the dropdown looked dead. Everything the caller passes in beside the canvas size arrives every frame, so it cannot go
     * stale between a control changing and a number spawning.
     */
    [TestMethod]
    public void Advance_PlaysTheStyleTheCallerAskedFor()
    {
      /* Split, not the shipped bands: a rail is only a rail in the scheme whose regions are columns, and bands answering
         Parabola with Hold (FctSplitModesTest pins that degradation) would make this test about the wrong thing. Every style
         in this loop is legal in split, which is what makes one loop over all of them the honest shape. */
      var split = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left);
      foreach (var style in new[] { FctMotionStyle.Hold, FctMotionStyle.Fountain, FctMotionStyle.Spray, FctMotionStyle.Parabola })
      {
        var demo = new FctDemo();
        demo.Start(0);

        var born = new List<FctHitState>();
        for (var now = 100.0; now <= 900; now += 100)
        {
          demo.Advance(now, 800, 560, style, split, born.Add, null);
        }

        Assert.IsTrue(born.Count > 0, $"{style}: nothing was spawned to check");
        Assert.IsTrue(born.All(h => h.Style == style),
          $"{born.Count(h => h.Style != style)} of {born.Count} numbers were laid out for {string.Join(",", born.Select(h => h.Style))}, not {style}");

        /*
         * The other half of the fingerprint, and it is structural rather than a measurement of travel: only the choreographed styles get a gravity
         * tail baked in (FctIngest calls ApplyFall for fountain and spray and nothing else), so their numbers must have depth to fall through.
         *
         * What this deliberately does NOT assert is anything about how far a style travels: Rise measures the flight, not the look, and two styles
         * with the same Rise can move nothing alike. The shapes themselves belong to FctMotion and are covered in FctMotionTest.
         */
        var choreographed = style is FctMotionStyle.Fountain or FctMotionStyle.Spray;

        Assert.IsTrue(choreographed ? born.All(h => Math.Abs(h.FallDist) > 0) : born.All(h => h.FallDist == 0),
          $"{style}: {(choreographed ? "no gravity tail, so it is not the style it claims to be" : "got a gravity tail it should not have")}");
      }
    }

    /*
     * A loop must never cut a number off mid-flight, and it is made true by scheduling rather than by cleverness at the seam: the last
     * cue lands early enough that every number has launched, travelled, held and faded before the cycle restarts. The quiet four seconds
     * at the end are deliberate - they are also what makes it read as a loop instead of as noise.
     */
    [TestMethod]
    public void Script_LetsEveryNumberFinishInsideTheLoop()
    {
      Assert.IsTrue(FctDemo.Script.Select(c => c.OffsetMs).SequenceEqual(FctDemo.Script.Select(c => c.OffsetMs).OrderBy(x => x)),
        "Advance walks the script once per cycle, so the offsets have to be in order");

      /* Measured off the overlay rather than remembered: hold lives 1.4 motion windows, which is the longest anything gets.
         Comparing the last cue against that number is what notices when the loop gets crowded or the lifetimes get longer. */
      var longest = FctMotion.MotionWindowMs * 1.4;

      Assert.IsTrue(longest < FctDemo.TailMs, $"a number can live {longest:F0} ms but the loop leaves only {FctDemo.TailMs:F0} ms of tail");

      foreach (var cue in FctDemo.Script)
      {
        Assert.IsTrue(cue.OffsetMs + longest <= FctDemo.CycleMs,
          $"the cue at {cue.OffsetMs:F0} ms cannot finish inside a {FctDemo.CycleMs:F0} ms cycle");
      }
    }

    [TestMethod]
    public void Player_ReplaysAfterTheCycleEnds()
    {
      var demo = new FctDemo();
      demo.Start(0);

      for (var now = 0.0; now < FctDemo.CycleMs; now += 100)
      {
        demo.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, null, null);
      }

      Assert.AreEqual(0, demo.Hits.Count, "everything should have expired in the tail before the loop restarts");

      var spawned = 0;
      demo.Advance(FctDemo.CycleMs + 700, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, _ => spawned++, null);

      Assert.IsTrue(spawned > 0, "the script ran once and stopped - configure mode would go quiet");
    }

    /*
     * The reason the loop is an animation and not a slideshow. A render pump asks for the next frame when it has something moving, and what it
     * looks at is the canvas's own list - which deliberately does not contain these numbers. Animated is the answer the demo gives that question,
     * so it has to say yes for every tick a number is in flight: waking only when a cue fires repaints twice a second, and text hangs in place
     * before jumping.
     */
    [TestMethod]
    public void Animated_DemandsAFrameForEveryFlight()
    {
      var demo = new FctDemo();
      demo.Start(0);
      Assert.IsFalse(demo.Animated, "nothing is on screen yet");

      var ticks = 0;
      var moving = 0;

      for (var now = 1000.0 / 60; now <= 9000; now += 1000.0 / 60)
      {
        ticks++;
        demo.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, null, null);

        if (demo.Animated)
        {
          moving++;
        }
      }

      // from the first cue to the end of the last flight almost every frame has something in it; a pump that woke for cues alone would land far below
      Assert.IsTrue(moving > ticks * 0.9, $"only {moving} of {ticks} ticks had anything moving");

      /* The quiet seconds at the end of a cycle are dead time on purpose, and dead time must not cost rasters. */
      demo.Advance(FctDemo.CycleMs - 1, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, null, null);
      Assert.IsFalse(demo.Animated, "the tail between loops should stop asking for frames");
    }

    [TestMethod]
    public void Player_ReleasesEveryNumberItHandsBack()
    {
      var demo = new FctDemo();
      demo.Start(0);
      for (var now = 0.0; now < 1500; now += 100)
      {
        demo.Advance(now, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, null, null);
      }

      var live = demo.Hits.ToList();
      Assert.IsTrue(live.Count > 0);

      var released = new List<FctHitState>();
      demo.Clear(released.Add);

      CollectionAssert.AreEquivalent(live.Select(h => h.GetHashCode()).ToList(), released.Select(h => h.GetHashCode()).ToList(),
        "a number whose caches were not released leaks a glyph run, and for a crit a referenced halo surface");
      Assert.AreEqual(0, demo.Hits.Count);
      Assert.IsFalse(demo.Active);

      // and the loop can be started again after configure mode closes and opens
      demo.Start(2000);
      Assert.IsTrue(demo.Advance(2100, 800, 560, FctMotionStyle.Hold, FctLayoutChoice.Bands, null, null), "the demo cannot be restarted");
    }

    /*
     * Where the shipped lists live. Under the app's own test project they are copied next to the binaries; a harness running from
     * elsewhere can point at them with EQLOGPARSER_DATA rather than the assertion quietly passing on nothing.
     */
    private static HashSet<string> LoadNames(string file)
    {
      if (FindDataFile(file) is not string path)
      {
        /* A check that skipped silently is a check that passes forever, which is worse than a failing one. */
        Assert.Fail($"{file} was not found next to the binaries or in the repo; the vocabulary claim needs it (EQLOGPARSER_DATA points at it explicitly)");
        return [];
      }

      var names = new HashSet<string>();
      foreach (var line in File.ReadLines(path))
      {
        if (line.Length == 0 || line[0] == '#')
        {
          continue;
        }

        // spells.txt is id^name^..., procs.txt is one name per line
        names.Add(file == "spells.txt" ? line.Split('^')[1] : line);
      }

      return names;
    }

    private static string? FindDataFile(string file)
    {
      var roots = new List<string>();
      var env = Environment.GetEnvironmentVariable("EQLOGPARSER_DATA");
      if (!string.IsNullOrEmpty(env))
      {
        roots.Add(env);
      }

      foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
      {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
          roots.Add(Path.Combine(dir.FullName, "data"));
          roots.Add(Path.Combine(dir.FullName, "EQLogParser", "data"));
        }
      }

      return roots.Select(root => Path.Combine(root, file)).FirstOrDefault(File.Exists);
    }
  }
}
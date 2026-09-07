using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The configure-mode demo: a scripted loop of about twenty events so a size, speed or style change can be seen moving without a fight.
   *
   * The important assertions are about honesty rather than animation. Its vocabulary has to be EverQuest's own — the melee verbs the
   * parser recognises, spell names that exist in the shipped data/spells.txt, proc names from data/procs.txt, and Labels constants for the
   * zero-damage words — because a demo showing "Backhand" trains a player to expect text that never appears. And its schedule has to be a
   * loop that lets every number finish: a cycle that cleared live text at the seam would look like the overlay truncates fades.
   */
  [TestClass]
  public class FctDemoTest
  {
    /* The six verbs DamageLineParser produces for melee, in the base form FctManager.DisplaySource shows (it singularises "Bites"). */
    private static readonly string[] MeleeVerbs = ["Bite", "Claw", "Crush", "Pierce", "Punch", "Slash"];

    private static readonly string[] Words =
      [Labels.Dodge, Labels.Parry, Labels.Block, Labels.Miss, Labels.Riposte, Labels.Absorb, Labels.Invulnerable];

    [TestMethod]
    public void Script_CoversWhatAPlayerHasToTellApart()
    {
      var script = FctDemo.Script;

      Assert.IsTrue(script.Count >= 18, $"only {script.Count} events: too sparse to read as combat");
      Assert.IsTrue(script.Count <= 30, $"{script.Count} events is a raid log, not a demo");

      Assert.IsTrue(script.Any(c => !FctLayout.IsIncoming(c.Lane)), "nothing in the outgoing band");
      Assert.IsTrue(script.Any(c => FctLayout.IsIncoming(c.Lane)), "nothing in the incoming band");
      Assert.IsTrue(script.Count(c => c.Crit) >= 2, "a single crit cannot show both dealt and taken crit looks");
      Assert.IsTrue(script.Any(c => c.Proc), "no proc, so the smaller-and-quicker tier is never shown");
      Assert.IsTrue(script.Any(c => c.ValueText != null), "no zero-damage word (DODGE/PARRY/MISS)");

      // the fold has to be seen happening: three identical ticks in the same lane, which is exactly what folds into "412 ×3"
      var folds = script.Where(c => c.Periodic && c.ValueText is null)
        .GroupBy(c => (c.Lane, c.Source, c.Value))
        .Where(g => g.Count() >= 3);
      Assert.IsTrue(folds.Any(), "no damage-over-time tick repeated three times, so the ×N fold is never demonstrated");
    }

    [TestMethod]
    public void Script_UsesEverQuestsOwnVocabulary()
    {
      var spells = LoadNames("spells.txt");
      var procs = LoadNames("procs.txt");

      if (spells is null && procs is null)
      {
        Assert.Inconclusive("no data/ directory next to the test binaries or at $EQLOGPARSER_DATA - vocabulary not checked");
      }

      foreach (var cue in FctDemo.Script)
      {
        // a label line says what the game said, not what this file invented
        if (cue.ValueText != null)
        {
          Assert.IsTrue(Words.Contains(cue.ValueText), $"{cue.ValueText} is not a label the parser assigns");
          continue;
        }

        Assert.IsNotNull(cue.Source, "a numeric event with no source line has nothing to show and teaches nothing");

        if (MeleeVerbs.Contains(cue.Source))
        {
          continue; // melee verbs come from the parser's own list, not from spells.txt
        }

        var inSpells = spells?.Contains(cue.Source) == true;
        var inProcs = procs?.Contains(cue.Source) == true;
        Assert.IsTrue(inSpells || inProcs, $"\"{cue.Source}\" is not a spell or proc EQLogParser knows - invented vocabulary");

        if (cue.Proc)
        {
          Assert.IsTrue(inProcs, $"{cue.Source} is shown as a proc but is not in data/procs.txt");
        }
      }
    }

    [TestMethod]
    public void Script_LeavesRoomForEveryNumberToFinish()
    {
      var last = FctDemo.Script[^1].OffsetMs;

      Assert.IsTrue(FctDemo.Script.Select(c => c.OffsetMs).SequenceEqual(FctDemo.Script.Select(c => c.OffsetMs).OrderBy(x => x)),
        "Advance walks the script once per cycle, so offsets have to be in order");

      // the longest lifetime in play is around 2.8 s (hold/pulse at default tempo); the tail is measured off that
      Assert.IsTrue(FctDemo.TailMs >= 4000, $"the last cue lands at {last:F0} ms with only {FctDemo.TailMs:F0} ms of loop left");
    }

    [TestMethod]
    public void Player_SpawnsTheScriptIntoItsOwnList()
    {
      var demo = new FctDemo();
      Assert.IsFalse(demo.Advance(100, 800, 560, null, null), "an unstarted demo must not run");

      var spawned = 0;
      demo.Start(0);

      for (var now = 0.0; now <= 3000; now += 100)
      {
        if (demo.Advance(now, 800, 560, _ => spawned++, null))
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
     * A loop must never cut a number off mid-flight, and it is made true by scheduling rather than by cleverness at the seam: the last
     * cue lands early enough that every number has launched, travelled, held and faded before the cycle restarts. The quiet four seconds
     * at the end are deliberate - they are also what makes it read as a loop instead of as noise.
     */
    [TestMethod]
    public void Script_LetsEveryNumberFinishInsideTheLoop()
    {
      // 3 s is longer than anything the overlay gives a number today (hold and pulse run 2.8 s at default tempo)
      const double LongestFlightMs = 3000;

      foreach (var cue in FctDemo.Script)
      {
        Assert.IsTrue(cue.OffsetMs + LongestFlightMs <= FctDemo.CycleMs,
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
        demo.Advance(now, 800, 560, null, null);
      }

      Assert.AreEqual(0, demo.Hits.Count, "everything should have expired in the tail before the loop restarts");

      var spawned = 0;
      demo.Advance(FctDemo.CycleMs + 700, 800, 560, _ => spawned++, null);

      Assert.IsTrue(spawned > 0, "the script ran once and stopped - configure mode would go quiet");
    }

    [TestMethod]
    public void Player_ReleasesEveryNumberItHandsBack()
    {
      var demo = new FctDemo();
      demo.Start(0);
      for (var now = 0.0; now < 1500; now += 100)
      {
        demo.Advance(now, 800, 560, null, null);
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
      Assert.IsTrue(demo.Advance(2100, 800, 560, null, null), "the demo cannot be restarted");
    }

    /*
     * Where the shipped lists live. Under the app's own test project they are copied next to the binaries; a harness running from
     * elsewhere can point at them with EQLOGPARSER_DATA rather than the assertion quietly passing on nothing.
     */
    private static HashSet<string> LoadNames(string file)
    {
      var path = FindDataFile(file);
      if (path is null)
      {
        return null;
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

    private static string FindDataFile(string file)
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

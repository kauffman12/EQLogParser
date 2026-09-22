using System;

namespace EQLogParser
{
  /*
   * Who stopped the world, in as many words as these four numbers can carry. The runtime's cumulative pause counter is the tie-breaker:
   * it counts time the collector held every thread, so a gap that the collections do not explain was stopped by something outside the
   * managed runtime - a profiler or gcdump suspending the process, power management, or a machine with no CPU left for anybody.
   *
   * It takes its numbers rather than reading the collector itself, which keeps it arithmetic and string building with nothing WPF in it.
   * That is why it lives here instead of beside the watchdog that calls it: the wording is the product, and asserting wording needs a test
   * run. As app-side code its only tests sat in the Windows-only test assembly, which builds everywhere but runs on one machine - the exact
   * shape that let "{collections} collection(s) account for" ship against an assertion wanting "3 collections", and pass on the machine that
   * wrote both.
   */
  internal static class PerfGap
  {
    /*
     * gapMs is how long nothing ran; pauseMs is how much of it the collector admits to holding every thread for; gen0/1/2 are collection
     * counts over the same span. Each number arrives as a delta between two samples, so any of them can come back negative when two reads
     * cross on different threads - hence the floors.
     */
    internal static string Classify(double gapMs, double pauseMs, int gen0, int gen1, int gen2)
    {
      var collections = Math.Max(0, gen0) + Math.Max(0, gen1) + Math.Max(0, gen2);

      if (pauseMs >= gapMs * 0.5)
      {
        return $"collector ({(gen2 > 0 ? "gen2, the full collection" : gen1 > 0 ? "gen1" : "gen0")}), it held every thread for {Math.Max(0, pauseMs):0} of the {gapMs:0} ms";
      }

      if (collections == 0)
      {
        return $"no collection in that gap: {gapMs:0} ms stopped from outside the runtime (profiler or gcdump, power management, or no CPU for anybody)";
      }

      /* One sentence rather than a template with a parenthetical in it: "1 collection accounts", "3 collections account". */
      var said = collections == 1 ? "collection accounts" : "collections account";

      return $"{collections} {said} for only {Math.Max(0, pauseMs):0} of the {gapMs:0} ms - the rest is not the collector";
    }
  }
}

namespace EQLogParser
{
  /*
   * The one switch that decides whether a player's log is written to at all. Everything the UI-thread instrumentation has to say goes through PerfJournal, so this
   * is the place to hold the promise that a normal session says nothing: off by default, and turning it on is enough by itself — no restart, and no inherited
   * silence from whatever was suppressed while nobody was listening. Counters and measurements are deliberately NOT gated (they cost interlocked adds, not lines).
   */
  [TestClass]
  public class PerfJournalTest
  {
    /* Off until asked: App reads `PerfReport` / `Debug` from settings.txt, and a fresh process must not have decided for itself. */
    [TestMethod]
    public void TheJournalIsQuietUnlessSomeoneTurnsItOn()
    {
      Assert.IsFalse(PerfJournal.Enabled, "the shipped default is quiet — a normal session writes no perf lines at all");

      var was = PerfJournal.Enabled;
      try
      {
        PerfJournal.Enabled = false;

        // Nothing here may reach the log, and nothing here may throw on its way to not reaching it: these are called from the watchdog and from render paths.
        Assert.IsFalse(PerfJournal.SlowPass("journal.off", PerfJournal.SlowPassMs + 40), "disabled means no line");
        PerfJournal.Beat("journal test: a beat that must not be written");
        PerfJournal.Note("journal test: a note that must not be written");
        PerfJournal.Stall("journal test: a stall that must not be written");
      }
      finally
      {
        PerfJournal.Enabled = was;
      }
    }

    /*
     * Someone turns it on in the middle of a bad patch and wants the next offender, now. Warnings suppressed during the quiet stretch are not held against it — the
     * throttle table is only written when a line actually goes out, which is what makes enabling enough on its own.
     */
    [TestMethod]
    public void EnablingTheJournalReportsAtOnceWithoutInheritingTheSilence()
    {
      var was = PerfJournal.Enabled;
      try
      {
        PerfJournal.Enabled = false;
        Assert.IsFalse(PerfJournal.SlowPass("journal.gate", PerfJournal.SlowPassMs + 40));

        PerfJournal.Enabled = true;
        Assert.IsTrue(PerfJournal.SlowPass("journal.gate", PerfJournal.SlowPassMs + 40),
          "the first slow pass after enabling has to be written, not muted by the quiet stretch");
        Assert.IsFalse(PerfJournal.SlowPass("journal.gate", PerfJournal.SlowPassMs + 40), "and from there it throttles per span as always");
      }
      finally
      {
        PerfJournal.Enabled = was;
      }
    }
  }
}

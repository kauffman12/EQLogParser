using EQLogParser.Mirror;

namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// One display row of the derived fight list — the strings the grid binds. The duration is the point: it used
  /// to run through DateUtil.FormatGeneralTime, the fuzzy words formatter, and on this column the words were worse
  /// than approximate. Under a minute they are an EMPTY string (so Waxwork Abolishion's first life, 18:34:08 to
  /// 18:34:53, showed no duration at all), and 112 s and 162 s collapse to "1 minute"/"2 minutes", which is no way
  /// to compare two pulls. The legacy grid has no duration column whatsoever (its seconds lived in the row tooltip,
  /// `Time Alive: 46s` for that same life), so this is the mirror's own number — and it is counted inclusively, which
  /// is what makes it the SAME 46 as the tooltip rather than the 45 you get by subtracting two timestamps. The numbers
  /// below are those measured rows; see DerivedFightTest for why the +1 belongs to DerivedFight rather than here.
  /// </summary>
  [TestClass]
  public class MirrorFightRowsTest
  {
    private const double T0 = 1_000;

    private static MirrorFightRow Row(double beginS, double lastS, DerivedFightEnd end = DerivedFightEnd.Open)
    {
      var fight = new DerivedFight
      {
        Name = "Grul", Id = 1, BeginTime = T0 + beginS, LastTime = T0 + lastS, EndReason = end,
        Dead = end is DerivedFightEnd.Slain or DerivedFightEnd.Charmed,
      };
      var snapshot = MirrorFightRows.Build([fight], new EntityTimeline(), 0,
        new DamageFactTable(8), new MirrorDamageIndex());

      Assert.AreEqual(1, snapshot.Rows.Count, "one fight, one row, no divider");
      return snapshot.Rows[0];
    }

    [TestMethod]
    public void ADurationUnderAMinuteIsSecondsRatherThanNothing()
    {
      // Waxwork Abolishion's first life on eqlog_Kizant_xegony.txt: 18:34:08 .. 18:34:53, and legacy's tooltip for
      // that row reads "Time Alive: 46s". The column now says the same thing the tooltip says.
      Assert.AreEqual("00:46", Row(0, 45).Duration, "the words formatter returned an empty string here");
    }

    /*
     * The case that decides the convention. `A corrupted egg` at 18:52:46 takes 36.5M damage and both bounds land in
     * the same second, so subtracting them says 0 — and one click later its own damage summary says "Time Alive: 1s"
     * and divides that damage by one second (TimeSegment.Total counts a span inclusively). A duration cell of "00:00"
     * over a DPS number is the grid contradicting itself, so the shortest honest answer is 00:01.
     */
    [TestMethod]
    public void AFightInsideASingleSecondReadsOneSecond()
    {
      Assert.AreEqual("00:01", Row(0, 0).Duration, "and never 00:00: the summary one click away divides by this many seconds");
    }

    [TestMethod]
    public void DurationsDoNotCollapseIntoTheSameWord()
    {
      Assert.AreEqual("01:53", Row(0, 112).Duration);
      Assert.AreEqual("02:43", Row(0, 162).Duration, "these two used to read as one minute and two minutes");
    }

    [TestMethod]
    public void ADurationPastAnHourCarriesTheHour()
    {
      Assert.AreEqual("01:02:04", Row(0, 3723).Duration);
    }

    [TestMethod]
    public void ARowCarriesItsFightAndHowItEnded()
    {
      var live = Row(0, 90);
      Assert.AreEqual("Grul", live.Name);
      Assert.IsFalse(live.IsDivider);
      Assert.AreEqual("Grul", live.Fight.Name, "a click needs the row's data, not its strings");
      Assert.AreEqual(string.Empty, live.Status);

      Assert.AreEqual("dead, charmed", Row(0, 90, DerivedFightEnd.Charmed).Status);
      Assert.AreEqual("dead", Row(0, 90, DerivedFightEnd.Slain).Status);
      Assert.AreEqual(string.Empty, Row(0, 90, DerivedFightEnd.Gap).Status, "a fight that merely stopped says nothing");
    }
  }
}

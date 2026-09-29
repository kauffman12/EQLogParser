using EQLogParser.Mirror;

namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// One display row of the derived fight list — the strings the grid binds. The duration is the point: it used
  /// to run through DateUtil.FormatGeneralTime, the fuzzy words formatter, and on this column the words were worse
  /// than approximate. Under a minute they are an EMPTY string (so Waxwork Abolishion's 46-second first life showed
  /// no duration at all), and 112 s and 162 s collapse to "1 minute"/"2 minutes", which is no way to compare two
  /// pulls. The legacy grid has no duration column whatsoever (its seconds lived in the row tooltip,
  /// `Time Alive: 46s`), so this is the mirror's own number and it is exact.
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
      Assert.AreEqual("00:46", Row(0, 46).Duration, "the words formatter returned an empty string here");
    }

    [TestMethod]
    public void DurationsDoNotCollapseIntoTheSameWord()
    {
      Assert.AreEqual("01:52", Row(0, 112).Duration);
      Assert.AreEqual("02:42", Row(0, 162).Duration, "these two used to read as one minute and two minutes");
    }

    [TestMethod]
    public void ADurationPastAnHourCarriesTheHour()
    {
      Assert.AreEqual("01:02:04", Row(0, 3724).Duration);
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

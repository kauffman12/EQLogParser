namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class DerivedFightRowsTest
  {
    [TestMethod]
    [DataRow(0.0, "00:01")]
    [DataRow(45.0, "00:46")]
    [DataRow(112.0, "01:53")]
    [DataRow(162.0, "02:43")]
    [DataRow(3723.0, "01:02:04")]
    public void TheRealRowAdapterFormatsInclusiveSecondsRatherThanFuzzyWords(double elapsed, string expected)
    {
      Assert.AreEqual(expected, Row(new DerivedFight
      {
        Name = "Grul", Id = 1, BeginTime = 1_000, LastTime = 1_000 + elapsed
      }).Duration);
    }

    [TestMethod]
    public void ARowRetainsItsSourceFightForSelectionAndItsEndReason()
    {
      foreach (var end in new[] { DerivedFightEnd.Open, DerivedFightEnd.Slain, DerivedFightEnd.Charmed, DerivedFightEnd.Gap })
      {
        var fight = new DerivedFight
        {
          Name = "Grul", Id = 1, BeginTime = 1_000, LastTime = 1_090, EndReason = end,
          Dead = end is DerivedFightEnd.Slain or DerivedFightEnd.Charmed
        };
        var row = Row(fight);
        Assert.AreSame(fight, row.Fight);
        Assert.AreEqual("Grul", row.Name);
        Assert.IsFalse(row.IsDivider);
        Assert.AreEqual(end, row.Fight.EndReason);
      }
    }

    private static DerivedFightRow Row(DerivedFight fight)
    {
      var snapshot = DerivedFightRows.Build([fight], new EntityTimeline(), 0,
        new DamageFactTable(8), new FightFactIndex());
      Assert.HasCount(1, snapshot.Rows);
      return snapshot.Rows[0];
    }
  }
}

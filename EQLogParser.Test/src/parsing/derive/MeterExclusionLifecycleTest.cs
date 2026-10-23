namespace EQLogParser;

public partial class DamageMeterCalculationTest
{
  [TestMethod]
  [DataRow(0, 31, 32, (int)MeterPanels.Both)]
  [DataRow(10, 11, 12, (int)MeterPanels.Both)]
  [DataRow(10, 11, 12, (int)MeterPanels.Damage)]
  public void PartiallyExcludingReceiptsHidesWithoutRetiringTheRunningAmounts(int mode, int hideAt, int restoreAt,
    int panelValue)
  {
    var saved = AppSettings.IsBaneDamageEnabled;
    try
    {
      AppSettings.IsBaneDamageEnabled = true;
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 600);
      log.Add("Illuminai", "a rat", 9, 600, LabelTypes.Bane);
      var panels = (MeterPanels)panelValue;
      log.Check(9, mode, panels: panels);
      Assert.IsNotNull(log.Last);
      Assert.AreEqual(1200L, log.Last.DamageStats.RaidStats.Total);

      AppSettings.IsBaneDamageEnabled = false;
      log.Check(hideAt, mode, panels: panels);
      Assert.IsNull(log.Last, "the older allowed receipt cannot keep the filtered board visible");
      Assert.AreEqual(0, log.Lifecycle.SelectCandidates(1, log.Project(), mode).FirstOrdinal,
        "a filter change cannot consume the original receipt's remaining timeout");
      var copied = log.Lifecycle.Copy();
      Assert.IsTrue(copied.TrySelect(1, log.Project(), T0 + hideAt, mode,
        log.Arrivals.Latest(log.Facts, log.Timeline, mode, panels), log.Facts.FactCount, out var hidden,
        activity: log.Calculation.Activity.For, panels: panels));
      Assert.AreEqual(0, hidden.FirstOrdinal, "the copied panel choice must retain its own retirement clock");

      AppSettings.IsBaneDamageEnabled = true;
      log.Calculation.Clear();
      log.Check(restoreAt, mode, compareFresh: true, panels: panels);
      Assert.IsNotNull(log.Last);
      Assert.AreEqual(1200L, log.Last.DamageStats.RaidStats.Total);
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = saved;
    }
  }

  [TestMethod]
  public void APartialExclusionStillRetiresAmountsAfterTheirOriginalDeadline()
  {
    var saved = AppSettings.IsBaneDamageEnabled;
    try
    {
      AppSettings.IsBaneDamageEnabled = true;
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 600);
      log.Add("Illuminai", "a rat", 9, 600, LabelTypes.Bane);
      log.Check(9, 10);
      AppSettings.IsBaneDamageEnabled = false;
      log.Check(11, 10);
      Assert.AreEqual(0, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal);
      log.Check(20, 10);
      Assert.AreEqual(2, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal);
      AppSettings.IsBaneDamageEnabled = true;
      log.Check(20, 10);
      Assert.IsNull(log.Last);
      log.Add("Illuminai", "a rat", 21, 200);
      log.Check(21, 10, compareFresh: true);
      Assert.IsNotNull(log.Last);
      Assert.AreEqual(200L, log.Last.DamageStats.RaidStats.Total);
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = saved;
    }
  }

  [TestMethod]
  public void WideningPanelsPreservesTheAlreadyEnabledLanesRetirementClock()
  {
    var saved = AppSettings.IsBaneDamageEnabled;
    try
    {
      AppSettings.IsBaneDamageEnabled = true;
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 600);
      log.Add("Illuminai", "a rat", 9, 600, LabelTypes.Bane);
      log.Check(9, 10, panels: MeterPanels.Damage);
      AppSettings.IsBaneDamageEnabled = false;
      log.Check(11, 10, panels: MeterPanels.Damage);
      log.Check(12, 10, panels: MeterPanels.Both);
      Assert.IsNull(log.Last);
      Assert.AreEqual(0, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal,
        "widening has hidden no lane and cannot discard the still-running Damage accumulation");
      AppSettings.IsBaneDamageEnabled = true;
      log.Check(13, 10, compareFresh: true, panels: MeterPanels.Both);
      Assert.IsNotNull(log.Last);
      Assert.AreEqual(1200L, log.Last.DamageStats.RaidStats.Total);
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = saved;
    }
  }

  [TestMethod]
  public void NarrowingPanelsDoesNotBorrowTheHiddenLanesRetirementDeadline()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Add("a rat", "Illuminai", 9, 600);
    log.Check(9, 10);
    Assert.IsNotNull(log.Last);
    log.Check(11, 10, panels: MeterPanels.Damage);
    Assert.IsNull(log.Last);
    Assert.AreEqual(2, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal);
    log.Check(12, 10, panels: MeterPanels.Both);
    Assert.IsNull(log.Last, "widening cannot undo a single-panel expiry already applied");
  }
}

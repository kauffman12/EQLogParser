namespace EQLogParser;

/*
 * Six-second RDPS through the meter's own incremental walk. Every Check() below still compares the meter against
 * DerivedTotals' full-build reference, so these also hold the law that the rolling lane changed no encounter total,
 * rate, share, rank or copied parse.
 *
 * The clock law these tests exist to pin: there is ONE clock. The window ends at the newest SECOND THE CAPTURE HOLDS, exactly
 * like the encounter columns' denominator, and no build instant enters a value or a timeout. While no new line arrives the edge
 * stands still, so this column stands still too; it moves when data moves and ages when data ages.
 */
public partial class DamageMeterCalculationTest
{
  /*
   * A capture that stops producing lines is not a fight that ended: the edge is the newest captured second, so
   * nothing ages out and every value — RDPS, encounter DPS, total, Sec — stands still, which is what the other columns do for
   * the same reason. Labels expire when DATA moves the edge, not when this machine's clock ticks.
   */
  [TestMethod]
  public void NothingArrivingLeavesEveryValueStandingStillAndNewDataAgesIt()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 60_000);

    log.Check(0, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(10_000L, DamageRdps(log, "Illuminai"), "six seconds wide from the first hit");
    Assert.AreEqual(10_000L, log.Last.DamageRdps.Total);
    AssertRatesAgreeWithAColdBuild(log);

    log.Check(5);
    Assert.AreEqual(10_000L, DamageRdps(log, "Illuminai"), "the label is still inside the window its own second chose");

    // With no new line the capture's newest second remains zero, even after nearly half a minute.
    log.Check(29);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(10_000L, DamageRdps(log, "Illuminai"), "a quiet capture holds its last rate instead of inventing a new one");
    Assert.AreEqual(60_000L, log.Last.DamageStats.RaidStats.Total, "and it is the same freeze the encounter columns already show");

    // How long a quiet board stays up at all is the overlay lifecycle's decision (its engagement gap / timed retention), and this
    // column asks nothing of it: when the scope retires, the whole board goes, RDPS along with its total.
    log.Check(31);
    Assert.IsNull(log.Last, "past the lifecycle's own quiet period there is no board to rate");

    // New data from ANYBODY in scope moves the shared edge, and an idle player's label falls on its own schedule.
    log.Add("Bithika", "a rat", 35, 1_200);
    log.Check(35);
    Assert.AreEqual(0L, DamageRdps(log, "Illuminai"), "second 0 is far behind the edge the new line carried");
    Assert.AreEqual(1_200L / RollingWindow.Seconds, DamageRdps(log, "Bithika"));
    Assert.AreEqual(1_200L, log.Last.DamageStats.RaidStats.Total,
      "35 s apart is a new life to the projection: the retired fight's damage is out of this scope, its labels too");
  }

  [TestMethod]
  public void RdpsDividesBySixFromTheFirstHitAndStampsItsWindow()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 3, 100);
    log.Check(3);

    Assert.IsNotNull(log.Last);
    Assert.AreEqual(100L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "a one-second-old fight is still divided by six, never by its age or hit count");
    Assert.AreEqual((long)(T0 + 3), log.Last.DamageRdps.WindowSecond, "the window says which LOG second it ends at");
    Assert.AreEqual(MeterRecentWindow.NoSecond, log.Last.TankRdps.WindowSecond, "an empty direction says so");
  }

  /*
   * The meter's half of a law the chart lane already pins (ChartRollingSeriesTest.WideAmountsAreNeverRoundedThroughFloatingPoint):
   * a six-second sum can be carried past 2^53, where a double no longer holds every integer, so RDPS divides as integers
   * (StatsUtil.RateOverWholeSeconds). Borrowing the elapsed-duration formula (StatsUtil.RatePerSecond) here prints a whole-damage-per-second
   * figure this capture does not contain, and no smaller amount shows it.
   */
  [TestMethod]
  public void AWideWindowSumDividesWithoutLosingAUnit()
  {
    var log = new Capture();
    const long max = TextUtils.MaxAmount;                     // one line's ceiling; the SUM is what leaves exact double range
    const int lines = 70;                                     // 1.97e16: past 2^53, so the dividend itself needs more than a double holds
    for (var i = 0; i < lines; i++) log.Add("Illuminai", "a rat", 0, max);
    log.Check(0);

    var window = (long)lines * max;
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(window, log.Last.DamageStats.RaidStats.Total, "the amounts themselves survive the walk");
    Assert.AreEqual(window / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "the quotient stays exact; rounding through floating point comes out short");
  }

  [TestMethod]
  public void RdpsRidesLogTimeWhenLinesArriveLate()
  {
    var log = new Capture();
    for (var second = 6; second <= 10; second++) log.Add("Illuminai", "a rat", second, 600);

    log.Check(10, compareFresh: true);
    Assert.AreEqual(500L, DamageRdps(log, "Illuminai"), "labels 6 through 10, divided by six");
    AssertRatesAgreeWithAColdBuild(log);

    // A late line carrying second 12, delivered when this build ran at second 18: eight seconds of lag. The window ends
    // where the capture ends (label 12, so 7 through 12), because those are the six seconds of damage that exist. Hung on
    // the wall clock it would have covered 13 through 18 — three buckets of real damage dropped and three empty ones added.
    log.Add("Illuminai", "a rat", 12, 600);
    log.Check(18, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(500L, DamageRdps(log, "Illuminai"),
      "(labels 7, 8, 9, 10 and 12) / 6 — label 6 aged out on its own schedule");
    Assert.AreEqual((long)(T0 + 12), log.Last.DamageRdps.WindowSecond, "the window rides the data, not the build instant");
    Assert.AreEqual(3600L, log.Last.DamageStats.RaidStats.Total, "and the encounter total never noticed either clock");
    AssertRatesAgreeWithAColdBuild(log);
  }

  [TestMethod]
  public void LaterDataPushesTheWindowForwardAndEachLabelFallsOnItsOwnDay()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 1000);
    log.Add("Illuminai", "a rat", 1, 2000);
    log.Add("Illuminai", "a rat", 2, 3000);
    log.Add("Illuminai", "a rat", 3, 4000);

    log.Check(3);
    Assert.AreEqual(10_000L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"), "all four labels held");

    // Second 6 is the only new damage. The window slides to 1 through 6, so label 0's 1000 leaves whole while 1, 2 and 3
    // stay: a fall per label, never a fade across the column.
    log.Add("Illuminai", "a rat", 6, 6000);
    log.Check(6);
    Assert.AreEqual((2000L + 3000L + 4000L + 6000L) / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "the oldest label left; the rest are untouched");
    Assert.AreEqual(16_000L, log.Last!.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void RdpsSumsAnOwnerAndItsPetBeforeDividing()
  {
    var log = new Capture();
    var pet = "Illuminai`s pet";
    log.Timeline.SetIdentity(pet, IdentityKind.Pet, RuleStrength.Manual, "manual");
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add(pet, "a rat", 1, 200, flags: DamageFact.FlagOwnerInLine);

    log.Check(1, compareFresh: true);
    Assert.IsNotNull(log.Last);
    var row = log.Last.DamageStats.StatsList.Single();
    Assert.AreEqual("Illuminai +Pets", row.Name);
    Assert.AreEqual(300L / RollingWindow.Seconds, DamageRdps(log, row.Name),
      "one rate for the folded row, summed before the divide");
    Assert.AreEqual(row.Total, log.Last.DamageStats.RaidStats.Total);
    AssertRatesAgreeWithAColdBuild(log);
  }

  /*
   * Buckets are per life and the actor's rate is summed across them, so one player's RDPS is what they did to everything in
   * scope — a per-player ring could not withdraw one NPC's damage without a subtraction ledger.
   */
  [TestMethod]
  public void DamageOnTwoLivesInScopeSumsIntoOneRateBeforeDividing()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Add("Illuminai", "a bat", 1, 600);

    log.Check(1, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(1_200L, log.Last.DamageStats.RaidStats.Total, "both lives are in scope");
    Assert.AreEqual(1_200L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "one rate for the actor across both lives, summed before the divide");
    AssertRatesAgreeWithAColdBuild(log);
  }

  [TestMethod]
  public void TankRdpsFollowsEachDefenderWithoutFolding()
  {
    var log = new Capture();
    log.Timeline.SetIdentity("Fluffy", IdentityKind.Pet, RuleStrength.Manual, "manual");
    log.Add("a rat", "Bithika", 0, 300);
    log.Add("a rat", "Fluffy", 1, 300);

    log.Check(1, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(50L, TankRdps(log, "Bithika"));
    Assert.AreEqual(50L, TankRdps(log, "Fluffy"), "every defender keeps its own tanking row and its own rate");
    Assert.AreEqual(100L, log.Last.TankRdps.Total);
    Assert.AreEqual(0L, log.Last.DamageRdps.Total, "incoming damage earns no outgoing rate");
    AssertRatesAgreeWithAColdBuild(log);
  }

  /*
   * A later ordinal carrying an earlier second. The window keeps its own per-label stamps, so the amount lands on the
   * second the line says and ages out on that schedule. The seconds here stay inside the life's already-seen range on
   * purpose: a late second EARLIER than a row's first sighting inverts the row's activity span, and the encounter side
   * already answers differently there (-1 seconds from the full build, 0 from the meter) before RDPS exists.
   */
  [TestMethod]
  public void ALateArrivalInsideTheWindowCountsItsOwnLabelAndAgedOutHistoryStaysOut()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Check(0);
    Assert.AreEqual(100L, DamageRdps(log, "Illuminai"));

    log.Add("Illuminai", "a rat", 5, 600);
    log.Check(5);
    Assert.AreEqual(200L, DamageRdps(log, "Illuminai"), "labels 0 and 5 are both held");

    log.Add("Illuminai", "a rat", 3, 1800);
    log.Check(5);
    Assert.AreEqual((600L + 600L + 1800L) / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "the late amount joins ITS OWN label, not the current one");

    // Still nothing arriving: the window is where second 5 put it, so all three labels remain at any later build instant.
    log.Check(9);
    Assert.AreEqual(500L, DamageRdps(log, "Illuminai"), "the window sits on the data, not on the instant this build ran");

    log.Check(11);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(500L, DamageRdps(log, "Illuminai"), "and it stays there while the capture is silent");
    Assert.AreEqual(3000L, log.Last.DamageStats.RaidStats.Total);

    // A line carrying second 12 is what moves the edge, and it ages 0, 3 and 5 out together: the data decides, in its own time.
    log.Add("Illuminai", "a rat", 12, 600);
    log.Check(12);
    Assert.AreEqual(600L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "edge 12 covers seconds 7 through 12, so only the new label is inside it");
  }

  /*
   * A retained completed fight holds its rolling answer for as long as it is on the board and nothing newer has arrived — the
   * same freeze its 600 total and its DPS show. What may NOT happen is that damage joining later inherits it, which is the test
   * immediately below.
   */
  [TestMethod]
  public void ACompletedLifeKeepsItsResultAndItsRollingAnswerWhileTheCaptureIsQuiet()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Facts.AddDeath(new DeathFact(0, (long)(T0 + 1), log.Facts.InternName("a rat"), -1));

    log.Check(2, mode: 90);
    Assert.AreEqual(100L, DamageRdps(log, "Illuminai"));

    log.Check(20, mode: 90);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(600L, log.Last.DamageStats.RaidStats.Total, "the timed mode still retains the completed life");
    Assert.AreEqual(100L, DamageRdps(log, "Illuminai"), "nothing has arrived to move the edge, so the rate stands still");

    // The pull that replaces it is judged on its own damage: the retained rat's second 0 is long gone from any live window.
    log.Add("Illuminai", "a bat", 22, 300);
    log.Check(22, mode: 90);
    Assert.AreEqual(300L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "the new pull inherits no rolling damage from the retained one");
  }

  /*
   * Withdrawal is the other half of per-life storage: a life that leaves scope takes its buckets with it, so the pull that
   * replaced it cannot inherit damage that is still inside its own six seconds. Retention here would answer 150, not 50.
   */
  [TestMethod]
  public void AKilledLifeTakesItsRollingDamageOutOfTheNextPull()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Facts.AddDeath(new DeathFact(0, (long)(T0 + 1), log.Facts.InternName("a rat"), -1));
    log.Check(1);
    Assert.AreEqual(100L, DamageRdps(log, "Illuminai"));

    log.Add("Illuminai", "a bat", 2, 300);
    log.Check(2, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(300L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"),
      "the slain rat is out of scope and its second-0 damage went with it");
    Assert.AreEqual(300L, log.Last.DamageStats.RaidStats.Total, "the encounter side already agreed");
    AssertRatesAgreeWithAColdBuild(log);
  }

  [TestMethod]
  public void ChangingTheDamageFilterRebuildsTheRollingAnswerWithTheEncounterAnswer()
  {
    var previous = AppSettings.IsBaneDamageEnabled;
    try
    {
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 100, LabelTypes.Bane);
      log.Add("Illuminai", "a rat", 0, 60);

      AppSettings.IsBaneDamageEnabled = true;
      log.Check(1);
      Assert.AreEqual(160L / RollingWindow.Seconds, DamageRdps(log, "Illuminai"));

      AppSettings.IsBaneDamageEnabled = false;
      log.Check(1);
      Assert.IsNotNull(log.Last);
      Assert.AreEqual(60L, log.Last.DamageStats.RaidStats.Total);
      Assert.AreEqual(10L, log.Last.DamageRdps.Total, "only the admitted hit earns a rate");
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = previous;
    }
  }

  /*
   * Five damage each: every row truncates to zero on its own, so a totals row built by adding rounded rows would answer
   * zero here too. It answers six — the whole eligible scope summed before its one divide, visible rows or not.
   */
  [TestMethod]
  public void TheTotalsRowRateIsTheScopeSummedNotTheRowsRounded()
  {
    var log = new Capture();
    for (var i = 0; i < 8; i++)
    {
      var name = "Player" + i;
      log.Timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Manual, "manual");
      log.Add(name, "a rat", 0, 5);
    }

    log.Check(0);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(0L, DamageRdps(log, "Nobody Here"), "an unseen name carries no rate");
    foreach (var row in log.Last.DamageStats.StatsList)
      Assert.AreEqual(0L, log.Last.DamageRdps.RateFor(row.Name), $"{row.Name} rounds to nothing on its own");
    Assert.AreEqual(8 * 5L / RollingWindow.Seconds, log.Last.DamageRdps.Total,
      "40 across the scope divided once beats eight zeros");
  }

  /*
   * The incremental rolling answer must equal what a cold calculation over the same scope reaches — the property that
   * makes retained window state safe to trust. Either half may legitimately be absent (a damage-only fight has no tank
   * rows), so the comparison goes through helpers that treat "no direction published" as its own answer.
   */
  private static void AssertRatesAgreeWithAColdBuild(Capture log)
  {
    Assert.IsNotNull(log.Last);
    Assert.IsNotNull(log.Fresh, "the test asked Check for a cold build");
    Assert.AreEqual(RateTotal(log.Fresh.DamageRdps), RateTotal(log.Last.DamageRdps), "damage scope rate");
    Assert.AreEqual(RateTotal(log.Fresh.TankRdps), RateTotal(log.Last.TankRdps), "tank scope rate");

    var damageRows = log.Last.DamageStats?.StatsList;
    if (damageRows is not null)
      foreach (var row in damageRows)
        Assert.AreEqual(RateOf(log.Fresh.DamageRdps, row.Name), RateOf(log.Last.DamageRdps, row.Name),
          $"{row.Name} damage rate after the walk");

    var tankRows = log.Last.TankStats?.StatsList;
    if (tankRows is not null)
      foreach (var row in tankRows)
        Assert.AreEqual(RateOf(log.Fresh.TankRdps, row.Name), RateOf(log.Last.TankRdps, row.Name),
          $"{row.Name} tank rate after the walk");
  }

  private static long RateTotal(MeterDirectionRates rates) => rates?.Total ?? -1L;

  private static long RateOf(MeterDirectionRates rates, string rowName) => rates?.RateFor(rowName) ?? -1L;

  private static long DamageRdps(Capture log, string rowName) => log.Last?.DamageRdps?.RateFor(rowName) ?? -1L;

  private static long TankRdps(Capture log, string rowName) => log.Last?.TankRdps?.RateFor(rowName) ?? -1L;
}

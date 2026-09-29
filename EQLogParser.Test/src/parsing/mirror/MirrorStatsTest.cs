using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * MirrorStats — the one calculation a surface asks about the rows it is showing.
 *
 * The requirement these tests exist for: a damage meter that displays "the fights this session touched" and a fight
 * list where the operator selects those same fights and presses summary must print the same digits, because they are
 * the same question asked of the same facts through the same arithmetic. Nothing here is tuned to agree; the seam is
 * asserted instead, so that a change to either side's plumbing fails loudly rather than shipping two totals.
 *
 *   - one row's scope reports exactly the damage its own line evidence holds (the hits aimed at that row's name), and
 *     says nothing at all when asked about no rows — an empty event would be a zero the log never wrote;
 *   - scopes add: the union of two disjoint row sets totals the same as their separate totals, which is what makes
 *     "the session so far" and "select-all in the list" one number (each fact belongs to exactly one row);
 *   - a scope builds on its OWN DamageStatsBuilder, so an overlay refreshing once a second cannot repaint the board
 *     an open summary is showing. That is the failure mode of "just reuse the builder": shared singleton, two scopes,
 *     and the operator's summary starts flickering through the meter's window;
 *   - a heal-less scope arrives as an empty heal list rather than null, so a refresh clears healing instead of
 *     leaving the previous scope's numbers on screen.
 */
[TestClass]
public class MirrorStatsTest
{
    private const double T0 = 1_000;

    // Same fixture shape as MirrorSummaryFightsTest: facts by attacker/defender/amount/second/label.
    private static DamageFactTable BuildFacts(params (string Atk, string Def, long Dmg, double T, byte Label)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t, label) in rows)
        {
            var a = facts.InternName(atk);
            var d = facts.InternName(def);
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: (uint)dmg, typeId: label,
              flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    // The pass MirrorSession runs over a capture: rules, then the projection with its index sink.
    private static (List<DerivedFight> Fights, MirrorDamageIndex Index, DamageFactTable Facts) Derive(
      DamageFactTable facts, EntityTimeline? timeline = null)
    {
        var line = timeline ?? new EntityTimeline();
        ClassificationRules.Apply(facts, line);

        var index = new MirrorDamageIndex();
        var fights = FightProjection.Build(facts, line, index.OnFact);
        Sectionizer.StampGroupIds(fights);
        return (fights, index, facts);
    }

    // A raid hitting two mobs, each mob hitting back once. The hits BACK are the tanking side: they belong to the
    // rows but not to the raid's damage column, and they are what makes an expected total worth writing out by hand.
    private static (List<DerivedFight> Fights, MirrorDamageIndex Index, DamageFactTable Facts) TwoPulls()
    {
        var facts = BuildFacts(
            ("Illuminai", "Grimling", 500, 0, LabelTypes.Melee),
            ("Bithika", "Grimling", 300, 1, LabelTypes.Dd),
            ("Grimling", "Illuminai", 120, 2, LabelTypes.Melee),      // tanking side of the Grimling row
            ("Illuminai", "Skeleton", 700, 40, LabelTypes.Melee),
            ("Bithika", "Skeleton", 250, 41, LabelTypes.Dd),
            ("Skeleton", "Bithika", 90, 42, LabelTypes.Melee));       // tanking side of the Skeleton row

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        return Derive(facts, timeline);
    }

    private static DerivedFight Row(List<DerivedFight> rows, string name)
        => rows.First(r => r.Name == name);

    private static HealFactTable NoHeals(DamageFactTable facts) => new(facts);

    // ---- one scope, one row set ----

    [TestMethod]
    public void AScopeReportsTheDamageAimedAtItsOwnRows()
    {
        var (rows, index, facts) = TwoPulls();

        var stats = MirrorStats.For([Row(rows, "Grimling")], index, facts, NoHeals(facts))?.CombinedStats;

        Assert.IsNotNull(stats, "a scope with a row in it produces a board");
        // 500 + 300: everything aimed at the Grimling row's name. The 120 it dealt to Illuminai is the raid taking a
        // hit (tanking side) and must not appear as the raid's damage output.
        Assert.AreEqual(800L, (long)stats.RaidStats.Total, "one row's scope is that row's own damage");
        // The raid row's own Hits comes back 0 out of the damage builder (measured, not assumed), so a scope reads
        // player rows for hit counts — which is where the meter's own list gets them from too.
        Assert.AreEqual(2, stats.StatsList.Count, "one row per raider");
        Assert.AreEqual(2L, stats.StatsList.Sum(p => (long)p.Hits), "each raider's own hit count");
    }

    [TestMethod]
    public void ScopesAddUpToTheSameTotalAsTheirUnion()
    {
        var (rows, index, facts) = TwoPulls();
        var grim = Row(rows, "Grimling");
        var bone = Row(rows, "Skeleton");

        var first = MirrorStats.For([grim], index, facts, NoHeals(facts))?.CombinedStats;
        var second = MirrorStats.For([bone], index, facts, NoHeals(facts))?.CombinedStats;
        var both = MirrorStats.For([grim, bone], index, facts, NoHeals(facts))?.CombinedStats;

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNotNull(both);
        // The exactness law in its smallest form: taking the fights one at a time and taking them together cannot come
        // to different damage, because FightProjection files each fact into exactly one row. If a fact ever landed on
        // two rows this sum reads high; if it filed onto none, low.
        Assert.AreEqual((long)first.RaidStats.Total + second.RaidStats.Total, (long)both.RaidStats.Total,
          "select-all totals the same as the parts (no fact counted twice, none dropped)");
        Assert.AreEqual((long)first.RaidStats.Hits + second.RaidStats.Hits, (long)both.RaidStats.Hits, "and the same for hits");
    }

    [TestMethod]
    public void TenPullsInOneWindowTotalTheSameAsTheRowsOneByOne()
    {
        // The scenario from the requirement: monitoring a log, ten mobs die inside the meter's reset window. Whatever
        // set of rows that session covers, the meter's total is the list's select-all total.
        var legs = new List<(string Atk, string Def, long Dmg, double T, byte Label)>();
        for (var mob = 0; mob < 10; mob++)
        {
            var name = $"Mob{mob:00}";
            // Ten seconds of work per mob, spaced so each is its own row.
            var at = mob * 60.0;
            legs.Add(("Illuminai", name, (uint)(100 + mob), at, LabelTypes.Melee));
            legs.Add(("Bithika", name, (uint)(1_000 + mob * 7), at + 1, LabelTypes.Dd));
            legs.Add(("Illuminai", name, (uint)(50 + mob), at + 2, LabelTypes.Dot));
        }

        var facts = BuildFacts([.. legs]);
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        var (rows, index, table) = Derive(facts, timeline);

        var expected = legs.Sum(leg => leg.Dmg);
        var scope = MirrorStats.For(rows, index, table, NoHeals(table))?.CombinedStats;

        Assert.IsNotNull(scope);
        Assert.AreEqual(10, rows.Count, "ten mobs, ten rows");
        Assert.AreEqual(expected, (long)scope.RaidStats.Total, "the session's total is every fact the rows hold");
        Assert.AreEqual(legs.Count, scope.StatsList.Sum(p => (long)p.Hits), "every hit lands on a player row");
    }

    // ---- what a refresh must not touch ----

    [TestMethod]
    public void ARefreshLeavesTheBoardsLastAnswerAlone()
    {
        var (rows, index, facts) = TwoPulls();

        // Prime the shared builder with a DIFFERENT scope: one row only. This is what an open summary holds between
        // clicks, and it is what a meter refreshing through the singleton would overwrite once per second.
        DamageStatsBuilder.Instance.BuildTotalStats(SummaryForRow(Row(rows, "Grimling"), index, facts));
        var before = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats.Total;
        Assert.AreEqual(800L, before, "the summary's own scope was built through the shared builder");

        _ = MirrorStats.For(rows, index, facts, NoHeals(facts));

        var after = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats.Total;
        Assert.AreEqual((long)before!, (long)after!,
          "a scope calculation ran on its own builder — the board an open summary shows is untouched");
    }

    // Options for the shared builder holding exactly one derived row, the way a one-row click would.
    private static GenerateStatsOptions SummaryForRow(DerivedFight row, MirrorDamageIndex index, DamageFactTable facts)
    {
        var input = MirrorSummaryFights.Build([row], index, facts);
        var options = new GenerateStatsOptions { AllRanges = input.AllRanges };
        foreach (var fight in input.Fights) options.Npcs.Add(fight);
        return options;
    }

    // ---- the two empty cases, which are different ----

    [TestMethod]
    public void AScopeOfNoRowsSaysNothing()
    {
        var (rows, index, facts) = TwoPulls();

        Assert.IsNull(MirrorStats.For([], index, facts, NoHeals(facts)),
          "no rows is no scope: an empty board would be a zero the log never wrote");
        Assert.IsNull(MirrorStats.For(null!, index, facts, NoHeals(facts)));
    }

    [TestMethod]
    public void AScopeWithoutHealingArrivesAsAnEmptyHealList()
    {
        var (rows, index, facts) = TwoPulls();
        var row = Row(rows, "Grimling");

        // What MirrorStats hands the builder on the healing half. Null would mean "say nothing about healing" and a
        // surface would go on showing the PREVIOUS scope's numbers after a refresh; this capture has no heals, so an
        // empty list is the honest answer, and the damage half of the same scope still reports.
        var input = MirrorSummaryFights.Build([row], index, facts);
        Assert.AreEqual(0, MirrorSummaryHeals.Materialize(NoHeals(facts), input.AllRanges).Count,
          "a heal-less scope says 'nothing healed', not 'nothing to say about healing'");
        Assert.IsNotNull(MirrorStats.For([row], index, facts, NoHeals(facts))?.CombinedStats);
    }

  /*
   * A meter reset is a TIME question, not a row question: zeroing the overlay does not choose different fights, it
   * asks the same fights about the seconds since the reset. Legacy answers that by keeping its own running totals
   * (DamageOverlayStatsBuilder, with `OverlayDamageMode` deciding expiry: 0 = on kill, else N seconds of quiet);
   * the mirror answers it with one extra argument to the calculation it already had. These four tests are what keeps
   * that argument honest — a slice that silently double-counted its seam, widened its own clock, or got cached under
   * the unwindowed row would each look plausible on screen.
   */

  private static HealFactTable HealsAt(DamageFactTable facts, params (string Healer, string Healed, uint Amount, double T)[] heals)
  {
      var table = new HealFactTable(facts);
      var seq = 0;
      foreach (var (healer, healed, amount, t) in heals)
      {
          // Names come from the damage table's pool on purpose: one name, one id, both streams (the law in
          // MirrorHealCaptureTest), so a raider is the same person on the heal board as on the damage one.
          table.AddHeal(new HealFact(seq++, (long)(T0 + t), facts.InternName(healer), facts.InternName(healed),
            amount, overTotal: 0, LabelTypes.Heal, flags: 0, modMask: 0, subIdx: HealFact.NoSpell));
      }
      return table;
  }

  [TestMethod]
  public void AMeterSliceCountsOnlyTheSecondsSinceItsReset()
  {
      var (rows, index, facts) = TwoPulls();

      // Zeroed at t=30, i.e. after the Grimling fight and during the Skeleton one. The earlier row contributes
      // nothing at all - not even a row - because a slice in which a mob did no work is not part of this scope.
      var sliced = MirrorStats.For(rows, index, facts, NoHeals(facts), T0 + 30, double.PositiveInfinity)?.CombinedStats;
      var skeletonOnly = MirrorStats.For([Row(rows, "Skeleton")], index, facts, NoHeals(facts))?.CombinedStats;

      Assert.IsNotNull(sliced);
      Assert.IsNotNull(skeletonOnly);
      Assert.AreEqual((long)skeletonOnly.RaidStats.Total, (long)sliced.RaidStats.Total,
        "the window since a reset is exactly the work done since it");
      Assert.AreEqual(950L, (long)sliced.RaidStats.Total, "700 + 250, and none of the 800 from before the reset");

      // A window opening in the middle of a running fight: the clock starts at the window, not at the mob's birth,
      // because DPS divided by seconds nobody fought in would understate the meter — legacy measures against the
      // activity it accumulated since the reset. Measured, not assumed: the same row from its own birth reads 950.
      var mid = MirrorSummaryFights.Build([Row(rows, "Skeleton")], index, facts, T0 + 41, double.PositiveInfinity);
      Assert.AreEqual(1, mid.Fights.Count);
      Assert.AreEqual(T0 + 41, mid.Fights[0].BeginTime, "a reset mid-fight moves the clock's start to the reset");
      Assert.AreEqual(250L, mid.Fights[0].DamageTotal, "and only the hit landed after it (the 700 was before)");
  }

  [TestMethod]
  public void AdjacentSlicesOfTheSameRowsAddUpToTheWhole()
  {
      var (rows, index, facts) = TwoPulls();
      var whole = (long)MirrorStats.For(rows, index, facts, NoHeals(facts))!.CombinedStats.RaidStats.Total;

      // Split at 39.5: no fact sits in the seam, so the two windows partition every fact exactly once - which is the
      // property that makes a meter's per-reset totals sum to what select-all says over the same rows.
      var before = (long)MirrorStats.For(rows, index, facts, NoHeals(facts), double.NegativeInfinity, T0 + 39.5)!
        .CombinedStats.RaidStats.Total;
      var after = (long)MirrorStats.For(rows, index, facts, NoHeals(facts), T0 + 39.5, double.PositiveInfinity)!
        .CombinedStats.RaidStats.Total;

      Assert.AreEqual(800L, before, "the pull that finished before the reset");
      Assert.AreEqual(950L, after, "the pull still running when it was zeroed");
      Assert.AreEqual(whole, before + after, "and a fact is either inside a window or outside it, never twice");
  }

  [TestMethod]
  public void ASlicedRowIsNeverCachedAsTheRow()
  {
      var (rows, index, facts) = TwoPulls();
      var grim = Row(rows, "Grimling");

      // Slice first: a window holding no second of this row's work says nothing about it...
      Assert.AreEqual(0, MirrorSummaryFights.Build([grim], index, facts, T0 + 500, double.PositiveInfinity).Fights.Count,
        "a window with nothing in it contributes no fight");
      // ...and must not have left that emptiness under the row itself, because the next click on the same row is
      // unwindowed and would be handed a summary of nothing. This is the hazard of threading a window through the
      // per-row cache: both surfaces still agree, and one of them is wrong.
      var afterSlice = (long)MirrorStats.For([grim], index, facts, NoHeals(facts))!.CombinedStats.RaidStats.Total;
      Assert.AreEqual(800L, afterSlice, "the unwindowed row still reports its whole damage after a slice ran");

      // Other direction too: build the cached full row first, then slice; the cache must stay the full one.
      var sliced = MirrorSummaryFights.Build([grim], index, facts, T0 + 0.5, T0 + 1.5).Fights;
      Assert.AreEqual(1, sliced.Count, "one second of this row's work is inside that window");
      Assert.AreEqual(300L, sliced[0].DamageTotal, "only the hit at t=1");
      Assert.AreEqual(800L, (long)MirrorStats.For([grim], index, facts, NoHeals(facts))!.CombinedStats.RaidStats.Total,
        "and the cached whole row is still whole");
  }

  [TestMethod]
  public void TheHealWindowIsTheMetersWindow()
  {
      var (rows, index, facts) = TwoPulls();
      // Both heals sit inside a row's own span, since that is what windows them: the selection's spans, not wall
      // clock to now (the same reason a heal landed between two pulls belongs to neither board).
      var heals = HealsAt(facts, ("Bithika", "Illuminai", 400, 1), ("Bithika", "Illuminai", 600, 41));

      var whole = MirrorStats.For(rows, index, facts, heals)?.CombinedStats;
      Assert.IsNotNull(whole);

      // Healing is windowed, never selected (a heal opens no encounter), and the window it gets is this scope's own
      // sliced span - so zeroing the meter mid-fight moves the heal board with the damage board instead of showing
      // the whole evening's healing beside a slice of its damage.
      var before = MirrorSummaryFights.Build(rows, index, facts, double.NegativeInfinity, T0 + 39.5);
      Assert.AreEqual(1, MirrorSummaryHeals.Materialize(heals, before.AllRanges).Count,
        "the heal before the reset leaves when the window does");
      var after = MirrorSummaryFights.Build(rows, index, facts, T0 + 39.6, double.PositiveInfinity);
      Assert.AreEqual(1, MirrorSummaryHeals.Materialize(heals, after.AllRanges).Count, "and the later one stays");
  }

  /*
   * The meter and the list are the same question, so they must print the same digits. This is the assertion behind the
   * opt-in damage overlay (OverlayDamageFromMirror): the overlay gets DamageOverlayStats, a click gets an event, and if
   * either shape were built from a different population — hidden pet rows in one and not the other, healing included
   * on one side — this is where it shows. Measured values below are asserted twice: once against the list's number,
   * once written out, so that a change to the calculation cannot move both sides of the comparison together.
   */

  [TestMethod]
  public void TheMeterAndASelectionOfTheSameRowsPrintTheSameNumber()
  {
      var (rows, index, facts) = TwoPulls();
      var heals = NoHeals(facts);

      var meter = MirrorStats.ForOverlay(rows, index, facts, heals, double.NegativeInfinity, double.PositiveInfinity);
      var list = MirrorStats.For(rows, index, facts, heals)?.CombinedStats;

      Assert.IsNotNull(meter?.DamageStats, "a scope with fights in it paints a damage board");
      Assert.IsNotNull(list);
      Assert.AreEqual(1750L, (long)list.RaidStats.Total, "both rows, both raiders, every hit aimed at those rows");
      Assert.AreEqual((long)list.RaidStats.Total, (long)meter.DamageStats.RaidStats.Total,
        "the meter's damage column is the same total a select-all of these rows prints");
      Assert.AreEqual((long)list.StatsList.Sum(p => (long)p.Total), (long)meter.DamageStats.StatsList.Sum(p => (long)p.Total),
        "and it is the same per-player rows, not merely the same sum");

      // The other direction of the same facts: the meter shows a tank column too, and it comes from the very same rows.
      Assert.IsNotNull(meter.TankStats, "the meter's tanking half is built from the same scope");
      Assert.AreEqual(210L, (long)meter.TankStats.StatsList.Sum(p => (long)p.Total),
        "120 + 90: the two hits the mobs landed, which is what the tank column counts");

      // A meter window with no seconds in it paints nothing rather than a zeroed board; the caller decides what an empty
      // board means for the surface (the overlay holds its last one until the expiry rule says otherwise).
      Assert.IsNull(MirrorStats.ForOverlay(rows, index, facts, heals, T0 + 9_000, double.PositiveInfinity),
        "a window with nothing inside it is no scope at all");

      // And a windowed meter agrees with a windowed list, which is the equality to check against the fight list after a
      // reset: same rows, same seconds, one arithmetic.
      var slicedMeter = MirrorStats.ForOverlay(rows, index, facts, heals, T0 + 39.6, double.PositiveInfinity);
      var slicedList = MirrorStats.For(rows, index, facts, heals, T0 + 39.6, double.PositiveInfinity)?.CombinedStats;
      Assert.AreEqual((long)slicedList!.RaidStats.Total, (long)slicedMeter!.DamageStats.RaidStats.Total,
        "after a reset the meter still equals the list measured over the same seconds");
      Assert.AreEqual(950L, (long)slicedMeter.DamageStats.RaidStats.Total, "the pull that was running when it zeroed");
  }

  [TestMethod]
  public void TheMeterBuildsOnItsOwnBuilders()
  {
      var (rows, index, facts) = TwoPulls();

      // Prime both shared builders with a different scope, then run a meter refresh: neither board an open summary is
      // showing may move, because a refresh happens about once a second and would otherwise repaint the tabs through
      // the meter's window. Same law as ARefreshLeavesTheBoardsLastAnswerAlone, now for both halves.
      DamageStatsBuilder.Instance.BuildTotalStats(SummaryForRow(Row(rows, "Grimling"), index, facts));
      var damageBefore = (long)DamageStatsBuilder.Instance.GetLastStats()!.CombinedStats.RaidStats.Total;

      var tankAnnouncements = 0;
      void WatchTank(StatsGenerationEvent _) => tankAnnouncements++;
      TankingStatsBuilder.Instance.EventsGenerationStatus += WatchTank;
      try
      {
          _ = MirrorStats.ForOverlay(rows, index, facts, NoHeals(facts), double.NegativeInfinity, double.PositiveInfinity);
      }
      finally
      {
          TankingStatsBuilder.Instance.EventsGenerationStatus -= WatchTank;
      }

      Assert.AreEqual(damageBefore, (long)DamageStatsBuilder.Instance.GetLastStats()!.CombinedStats.RaidStats.Total,
        "the shared damage builder still holds the summary's answer");
      Assert.AreEqual(0, tankAnnouncements,
        "and the shared tank board never announced anything - a meter refresh is invisible to open tabs");
  }
}

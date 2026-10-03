using EQLogParser;

namespace EQLogParser;

/*
 * "Is a fight happening right now?" over derived rows, which is the question the damage meter had answered for it by
 * FightManager's overlay-fight set: a dictionary that a line of the log added to, so "something is going on" was a fact
 * somebody else maintained. Nothing maintains it in the mirror — a row is a reconstruction — so the rule gets written down
 * here and pinned, because everything about it is a choice the legacy code never had to state:
 *
 *   - WHICH CLOCK. The capture's newest event, not the wall. A load running at 170k facts/s is minutes behind the clock;
 *     wall time would call the middle of that file live, and go quiet on the tail (DeriveEngine supplies nowT; these tests
 *     pass it explicitly so the choice is visible in the assertion).
 *   - WHICH TIMESTAMPS. A row's last activity in EITHER direction, since a row can be running with all its traffic pointed
 *     at the raid (a mob beating a player is tanking traffic on that row) while dealing nothing.
 *   - WHICH ROWS. Every row, including the hidden pet rows and a charmed raider's: hiding is a decision about the fight
 *     LIST, and a meter that opened for part of a pull's damage would be a bug pretending to be a policy.
 */
[TestClass]
public class LiveFightsTest
{
    private const double T0 = 1_000_000;   // a stand-in "newest event in the capture"

    private static DerivedFight Row(string name, double lastDamage, double lastTanking = double.NaN, bool dead = false,
                                    double? lastTime = null)
        => new()
        {
            Name = name,
            BeginTime = lastTime ?? T0 - 60,
            LastTime = lastTime ?? T0 - 1,
            LastDamageTime = lastDamage,
            LastTankingTime = lastTanking,
            Dead = dead
        };

    [TestMethod]
    public void ARowHitInsideTheGapIsLive_QuietForLongerIsNot()
    {
        var fresh = Row("Boss", T0 - 5, double.NaN);
        var stale = Row("Old Boss", T0 - 31, double.NaN);

        Assert.IsTrue(LiveFights.IsLive(fresh, T0, LiveFights.GapS), "damage five seconds ago is a fight");
        Assert.IsFalse(LiveFights.IsLive(stale, T0, LiveFights.GapS), "thirty-one silent seconds is the next pull's problem");

        // The boundary belongs to "still going": the gap is how long a row may be quiet and remain ONE life, and a meter
        // that disagreed at exactly 30 s would open (or close) on a fact the list still counts as the same fight.
        Assert.IsTrue(LiveFights.IsLive(Row("Edge", T0 - LiveFights.GapS, double.NaN), T0, LiveFights.GapS));
    }

    [TestMethod]
    public void ADeadRowIsNotLive_HoweverRecentItsLastHitWas()
    {
        var killed = Row("Boss", T0 - 1, double.NaN, dead: true);

        Assert.IsFalse(LiveFights.IsLive(killed, T0, LiveFights.GapS), "a kill ends the question; mode 0 zeroes on it");
        Assert.IsFalse(LiveFights.AnyLive([killed], T0, LiveFights.GapS), "and a capture of nothing but corpses is quiet");
    }

    /*
     * The reason this test exists rather than a one-line `LastDamageTime` check: a row whose whole traffic is pointed AT the
     * raid (a mob beating a player, or the tanking half of an encounter) has no damage window at all, and reading only
     * LastDamageTime would call it dead while the raid is being eaten. Asserting the NaN case alone would be enough to pass
     * on a broken max() — so both directions are pinned, and a row whose last thing was a killing blow on somebody else's
     * row (both windows empty) falls back to its span instead of reading as infinitely old.
     */
    [TestMethod]
    public void ARowIsJudgedByWhicheverDirectionMovedLast()
    {
        var tankingOnly = Row("A crazed flesh horror", double.NaN, T0 - 4);
        Assert.IsTrue(LiveFights.IsLive(tankingOnly, T0, LiveFights.GapS), "the raid is being hit; that is a fight");

        var damageOlderThanTank = Row("Boss", T0 - 25, T0 - 2);
        Assert.AreEqual(T0 - 2, LiveFights.LastActivityAt(damageOlderThanTank), "the newer window answers, not the first");

        var damageNewerThanTank = Row("Boss", T0 - 2, T0 - 25);
        Assert.AreEqual(T0 - 2, LiveFights.LastActivityAt(damageNewerThanTank));

        var noDirectionAtAll = new DerivedFight { Name = "Grul", BeginTime = T0 - 10, LastTime = T0 - 3 };
        Assert.AreEqual(T0 - 3, LiveFights.LastActivityAt(noDirectionAtAll), "a row with no window is judged by its span");
    }

    // The meter's dial is the gap: a board set to zero after 5 s of quiet has no business staying open at 20 s.
    [TestMethod]
    public void TheQuietWindowComesFromTheMeterDial()
    {
        var row = Row("Boss", T0 - 20, double.NaN);

        Assert.IsTrue(LiveFights.AnyLive([row], T0, LiveFights.TimeoutFor(0)), "0 = on kill, i.e. the engagement gap");
        Assert.IsFalse(LiveFights.AnyLive([row], T0, LiveFights.TimeoutFor(5)));

        // The default dial is the row-splitting gap, pinned as BEHAVIOUR rather than as a constant comparison: a test that
        // asserts `GapS == FightProjection.EngagementGapS` compares two spellings of one number and passes even if someone
        // changes it to 300, while this catches the drift that matters — a live question wider than the gap that ends a life.
        Assert.IsTrue(LiveFights.AnyLive([Row("Boss", T0 - 29)], T0, LiveFights.TimeoutFor(0)));
        Assert.IsFalse(LiveFights.AnyLive([Row("Boss", T0 - 31)], T0, LiveFights.TimeoutFor(0)));
    }

    /*
     * The announcement rule is "damage came in", NOT "a new fight started". Legacy raised EventsNewOverlayFight on EVERY
     * damage line of a fight (FightManager.UpdateIfNewFightMap fires it whenever DamageHits > 0, outside the new-fight
     * branch), which is why closing a meter with the X during a pull brought it back within a line or two. The first port of
     * auto-open announced one row per life instead, and that is the defect this pins: close the meter mid-pull and it stayed
     * shut until the next pull, minutes later.
     */
    [TestMethod]
    public void TheAnnouncementMeansNewerDamage_NotANewFight()
    {
        var sameLife = Row("Boss", T0 - 1);                 // same name, same begin as any earlier pass: not a new fight

        Assert.IsTrue(LiveFights.HasFreshDamage([sameLife], double.NegativeInfinity, T0, LiveFights.GapS),
            "newer activity than the last announcement, with something still live, is the whole condition");

        // Nothing newer arrived: an idle tail says nothing, so a closed meter is not reopened over seconds nobody fought in.
        Assert.IsFalse(LiveFights.HasFreshDamage([sameLife], T0 - 1, T0, LiveFights.GapS));
        Assert.IsFalse(LiveFights.HasFreshDamage([sameLife], T0, T0, LiveFights.GapS));

        // Fresh activity but nothing live any more: the last hit of a kill must not pop a board over a corpse.
        var killed = Row("Boss", T0 - 1);
        Assert.IsTrue(LiveFights.HasFreshDamage([killed], double.NegativeInfinity, T0, LiveFights.GapS));
        killed.Dead = true;
        Assert.IsFalse(LiveFights.HasFreshDamage([killed], double.NegativeInfinity, T0, LiveFights.GapS));

        // Activity older than the gap says nothing even on a first look — a finished log must not open a meter on its tail.
        Assert.IsFalse(LiveFights.HasFreshDamage([Row("Ancient", T0 - 400)], double.NegativeInfinity, T0, LiveFights.GapS));
    }

    // The clock the announcement compares against: newest across rows, in either direction (a mob beating a raider is
    // traffic too), and dead rows contribute nothing.
    [TestMethod]
    public void TheNewestActivityIsTheNewestRow_ActivityOrTanking()
    {
        Assert.IsTrue(double.IsNaN(LiveFights.LatestActivityAt([])));
        Assert.AreEqual(T0 - 2, LiveFights.LatestActivityAt([Row("Boss", T0 - 9), Row("Add", T0 - 2)]), 1e-6);

        var beaten = new DerivedFight { Name = "Rune", BeginTime = T0 - 30, LastTime = T0 - 30, LastTankingTime = T0 - 4 };
        Assert.AreEqual(T0 - 4, LiveFights.LatestActivityAt([Row("Boss", T0 - 9), beaten]), 1e-6,
            "a raider being hit is the newest thing on the capture");

        var slain = Row("Boss", T0 - 2);
        slain.Dead = true;
        Assert.IsTrue(double.IsNaN(LiveFights.LatestActivityAt([slain])), "a corpse is not activity");
    }

    /*
     * "The X closes the window and it continues where it left off" — which for a derived board means the WINDOW START, since
     * nothing else survives the close (the numbers are recomputed from facts inside those seconds). Legacy got the survival
     * free from statics; here it has to be the rule, so the rule is what is asserted: inside the quiet range the same start,
     * outside it a fresh one, and an explicit clear (no window at all) starting now.
     */
    [TestMethod]
    public void AClosedMeterReopensOntoTheSecondsItLeft()
    {
        const double opened = T0 - 60;                       // the pull began a minute of facts ago
        var midPull = T0 - 1;

        Assert.AreEqual(opened, LiveFights.WindowStartFor(opened, midPull, midPull + 1, LiveFights.TimeoutFor(0)),
            "still inside the range the dial allows, so the reopened board covers the same seconds as before the X");

        Assert.AreEqual(T0, LiveFights.WindowStartFor(opened, T0 - 20, T0, LiveFights.TimeoutFor(15)),
            "a meter configured to expire after 15 quiet seconds starts over once the pull has been over that long");
        Assert.AreEqual(T0, LiveFights.WindowStartFor(opened, T0 - 100, T0, LiveFights.TimeoutFor(0)));

        // What the clear button contributes: no window at all, which begins now.
        Assert.AreEqual(T0, LiveFights.WindowStartFor(-1, midPull, T0, LiveFights.TimeoutFor(0)));

        // A capture with no facts cannot expire — nothing moves the start except a real reset.
        Assert.AreEqual(opened, LiveFights.WindowStartFor(opened, double.NaN, T0, LiveFights.TimeoutFor(0)));
    }

    // Pet rows count. CharmPetRows hides them from the LIST because a summon is not an encounter; the meter shows their
    // damage (folded into `X +Pets`), so it has to treat them as activity or the board and the window disagree.
    [TestMethod]
    public void ARaidPetRowCountsAsActivity()
    {
        var pet = Row("Ziggy`s pet", T0 - 2);
        pet.RaidPet = true;

        Assert.IsTrue(LiveFights.AnyLive([pet], T0, LiveFights.GapS));
    }
}

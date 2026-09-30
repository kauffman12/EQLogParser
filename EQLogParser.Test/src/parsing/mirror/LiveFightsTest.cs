using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * "Is a fight happening right now?" over derived rows, which is the question the damage meter had answered for it by
 * FightManager's overlay-fight set: a dictionary that a line of the log added to, so "something is going on" was a fact
 * somebody else maintained. Nothing maintains it in the mirror — a row is a reconstruction — so the rule gets written down
 * here and pinned, because everything about it is a choice the legacy code never had to state:
 *
 *   - WHICH CLOCK. The capture's newest event, not the wall. A load running at 170k facts/s is minutes behind the clock;
 *     wall time would call the middle of that file live, and go quiet on the tail (MirrorSession supplies nowT; these tests
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

    [TestMethod]
    public void ANewFightIsOneTheLastPassDidNotReportLive()
    {
        var running = Row("Boss", T0 - 20);
        var secondPassBoss = Row("Boss", T0 - 2);                     // same name AND same begin: the same life, grown
        var newborn = Row("Add", T0 - 1);

        Assert.AreEqual("Add", LiveFights.FindNewLive([running], [secondPassBoss, newborn], T0, LiveFights.GapS)?.Name,
            "the row that grew is not news; the one that appeared is");

        // A third pass over the same rows announces nothing: an auto-open rule that re-fired every derive would reopen a
        // meter the user closed one second ago.
        Assert.IsNull(LiveFights.FindNewLive([secondPassBoss, newborn], [secondPassBoss, newborn], T0, LiveFights.GapS));
    }

    /*
     * Another pull of the same mob is a new fight — the raid pulled something, which is precisely what the meter wants to
     * know. Name alone would swallow it (the name was already live), so the key carries the row's begin: the same law that
     * gives one row per life in the fight list.
     */
    [TestMethod]
    public void AReopenedNameIsANewFight()
    {
        // Two lives of the same name alive on the same capture clock: an earlier row that has not aged out of the gap, and a
        // second one the raid just started. Keyed on NAME alone the newcomer is "already seen" and the meter never opens;
        // keyed on name + begin it is announced, which is the same law that gives one row per life in the fight list.
        var stillWarm = new DerivedFight { Name = "Boss", BeginTime = T0 - 25, LastTime = T0 - 6, LastDamageTime = T0 - 6 };
        var newborn = new DerivedFight { Name = "Boss", BeginTime = T0 - 1, LastTime = T0 - 1, LastDamageTime = T0 - 1 };

        Assert.AreEqual("Boss", LiveFights.FindNewLive([stillWarm], [stillWarm, newborn], T0, LiveFights.GapS)?.Name);
        Assert.IsNull(LiveFights.FindNewLive([stillWarm, newborn], [stillWarm, newborn], T0, LiveFights.GapS),
            "and once both are known, neither is news on the next pass");
    }

    // First pass over a capture: nothing was reported before, so whatever is live counts as started. A log opened mid-raid
    // should put a meter on screen, and the caller decides whether to act (MainWindow gates on its settings).
    [TestMethod]
    public void TheFirstPassAnnouncesWhateverIsLive()
    {
        Assert.AreEqual("Boss", LiveFights.FindNewLive(null, [Row("Boss", T0 - 3)], T0, LiveFights.GapS)?.Name);
        Assert.IsNull(LiveFights.FindNewLive(null, [Row("Ancient", T0 - 400)], T0, LiveFights.GapS),
            "a capture whose newest row is old news starts no fight — a finished log must not pop a meter open on a corpse");
    }

    /*
     * A row that had gone quiet and caught fire again IS announced: the raid resumed, and the closed meter should come back.
     * Asserting this explicitly because the obvious implementation (remember every name ever seen) gets it wrong in the
     * other direction — it would never re-open for the mob that paused mid-pull and resumed.
     */
    [TestMethod]
    public void AFightThatResumedIsANewFight()
    {
        var quiet = Row("Boss", T0 - 90, double.NaN, lastTime: T0 - 90);   // live on the earlier capture clock
        Assert.IsTrue(LiveFights.IsLive(quiet, T0 - 60, LiveFights.GapS), "it was going on a minute of facts ago");

        var resumed = Row("Boss", T0 - 1);                                 // same life, new damage
        Assert.AreEqual("Boss", LiveFights.FindNewLive([quiet], [resumed], T0, LiveFights.GapS)?.Name);
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

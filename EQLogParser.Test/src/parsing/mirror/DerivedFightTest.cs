using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * How long a row of the fight list covers — and, more to the point, in WHICH convention. The product counts seconds
 * inclusively: TimeSegment.Total is `EndTime - BeginTime + 1`, and that expression is the denominator every DPS figure
 * on the damage board is divided by. FightManager writes the same arithmetic into the legacy tooltip
 * (`var ttl = fight.LastTime - fight.BeginTime + 1;` → "Time Alive: 46s"), and FightSummarySource reproduces that text
 * so a derived row's summary matches the legacy one word for word. Subtracting two timestamps here instead would make
 * the grid disagree with both of them, and disagree worst exactly where it matters: a mob hit once inside a second
 * (`A corrupted egg`, 18:52:46, 36.5M damage in one second on eqlog_Kizant_xegony.txt) would show "00:00" while its own
 * summary prints "Time Alive: 1s" and divides that damage by one second. A zero-length duration is not a short fight;
 * it is an infinity-shaped hole in every rate built on it.
 */
[TestClass]
public class DerivedFightTest
{
    private const double T0 = 1_000;

    private static DerivedFight Row(double beginS, double lastS)
      => new() { Name = "Grul", BeginTime = T0 + beginS, LastTime = T0 + lastS };

    [TestMethod]
    public void AFightInsideASingleSecondLivedOneSecond()
    {
        Assert.AreEqual(1, Row(0, 0).DurationSeconds, "one timestamp is one second of fighting, not zero");
    }

    [TestMethod]
    public void SecondsAreCountedInclusivelyLikeTheBoardCountsThem()
    {
        // The grid's duration column and the summary's DPS clock have to be the same number: measured on the Waxwork
        // Abolishion first life (18:34:08 .. 18:34:53), whose legacy tooltip says "Time Alive: 46s".
        Assert.AreEqual(46, Row(0, 45).DurationSeconds);

        // One rule, one source: the span the damage summary sums for its DPS denominator is a TimeSegment.
        var span = new TimeSegment(T0, T0 + 45);
        Assert.AreEqual(span.Total, Row(0, 45).DurationSeconds, "the row cannot outlive the seconds the board divides by");
        Assert.AreEqual(new TimeSegment(T0, T0).Total, Row(0, 0).DurationSeconds);
    }

    /*
     * The same expression DerivedFightRows puts in the grid's duration cell, asserted where it can run on any platform
     * (the row itself lives in the WPF assembly and is covered by DerivedFightRowsTest). Digits matter as much as the
     * number here: "00:46" is what the legacy tooltip of that row says in words other than words.
     */
    [TestMethod]
    public void TheDurationCellPrintsTheTooltipsNumber()
    {
        static string Cell(DerivedFight fight)
          => DateUtil.FormatTicks(TimeSpan.FromSeconds(fight.DurationSeconds).Ticks, DateUtil.TimeFormat.HMSCompact);

        Assert.AreEqual("00:01", Cell(Row(0, 0)));
        Assert.AreEqual("00:46", Cell(Row(0, 45)));
        Assert.AreEqual("02:43", Cell(Row(0, 162)));
    }

    [TestMethod]
    public void ARowWithoutBoundsSaysNothing()
    {
        // BeginTime/LastTime start at ±infinity for a row that never saw a fact. Formatting that would hand
        // TimeSpan.FromSeconds a NaN, which throws on the way to a tick count.
        var empty = new DerivedFight { Name = "Grul" };

        Assert.AreEqual(0, empty.DurationSeconds);
        Assert.IsTrue(double.IsFinite(empty.DurationSeconds), "a display duration is always formattable");
    }
}

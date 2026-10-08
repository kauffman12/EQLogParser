using System.Text;

namespace EQLogParser;

/*
 * Shared plumbing for the three board goldens (DamageBoardGoldenTest, TankingBoardGoldenTest, HealingBoardGoldenTest).
 *
 * What belongs here is only the part that is genuinely one thing across boards: where the fixture and golden files live,
 * how one PlayerStats is rendered as text, and how a mismatch is reported. The SECTIONS differ per board — the damage
 * grid folds pets into `X +Pets`, the tanking grid has no children at all, the healing grid hangs the "who healed me"
 * tree off SubStats2 — so each test writes its own Snapshot and this class never sees a CombinedStats.
 *
 * The column set is the grids' own MappingNames (plus the raw counters the rates are computed from, because a rate that
 * silently stops summing is invisible in a rendered cell and obvious here). Floats print rounded to what a grid shows, so
 * a formatter change cannot make a golden churn.
 */
internal static class BoardGolden
{
    internal static string FixturePath(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "mini-data", "board", name);

    internal static string F(double v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The window the caller asked for, as a golden section header.</summary>
    internal static string WindowLabel(int minSeconds, int maxSeconds) =>
        minSeconds <= 0 && maxSeconds < 0 ? "all" : $"{minSeconds}..{maxSeconds}";

    /*
     * One row of the board: every column a summary grid binds. Kept as one line so a diff names a row and a column
     * (`name=Vael ... total=1430` vs `total=1429`) instead of a paragraph.
     */
    internal static string StatsLine(string tag, PlayerStats s) =>
        $"{tag}\tname={s.Name}" +
        $"\trank={s.Rank}" +
        $"\torig={s.OrigName}" +
        $"\tclass={s.ClassName}" +
        $"\tgroup={s.AssignedGroup}" +
        $"\ttop={s.IsTopLevel}" +
        $"\ttotal={s.Total}" +
        $"\tdps={s.Dps}" +
        $"\tsdps={s.Sdps}" +
        $"\tpctRaid={F(s.PercentOfRaid)}" +
        $"\tsecs={F(s.TotalSeconds)}" +
        $"\thits={s.Hits}" +
        $"\tmax={s.Max}" +
        $"\tmin={s.Min}" +
        $"\tbest={s.BestSec}" +
        $"\tavg={s.Avg}" +
        $"\tavgCrit={s.AvgCrit}" +
        $"\tavgLucky={s.AvgLucky}" +
        $"\tcritRate={F(s.CritRate)}" +
        $"\tluckRate={F(s.LuckRate)}" +
        $"\taccRate={F(s.MeleeAccRate)}" +
        $"\thitRate={F(s.MeleeHitRate)}" +
        $"\tbane={s.BaneHits}" +
        $"\tspecial={s.Special}" +
        // Not columns, but the arithmetic they are built from: a rate that silently stops summing is caught here
        // rather than by eyeballing a grid.
        $"\t|meleeHits={s.MeleeHits}\tspellHits={s.SpellHits}\tmeleeAttempts={s.MeleeAttempts}" +
        $"\tmiss={s.Misses}\tblock={s.Blocks}\tdodge={s.Dodges}\tparry={s.Parries}\triposte={s.RiposteHits}" +
        $"\tabsorb={s.Absorbs}\tinvm={s.Invulnerable}\tflurry={s.FlurryHits}\trampage={s.RampageHits}" +
        $"\tstrike={s.StrikethroughHits}\ttwin={s.TwincastHits}\tlucky={s.LuckyHits}\tbows={s.BowHits}" +
        $"\tmaxPot={s.MaxPotentialHit}";

    /*
     * Columns the DAMAGE grid does not bind but the other two do, kept out of the shared line so the damage golden's
     * bytes stay its bytes: % Rampage (tanking) and Extra (healing's "healed" column, and the amount a tanking row was
     * healed for). A board opts in by appending this to StatsLine.
     */
    internal static string ExtraColumns(PlayerStats s) =>
        $"\trampRate={F(s.RampageRate)}\textra={s.Extra}";

    /// <summary>A sub-stat line (spell / melee / healed-by breakdown behind a row).</summary>
    internal static string SubLine(string tag, string owner, PlayerSubStats sub) =>
        $"  {tag}\towner={owner}" +
        $"\tkey={sub.Key}" +
        $"\tname={sub.Name}" +
        $"\ttype={sub.Type}" +
        $"\ttotal={sub.Total}" +
        $"\thits={sub.Hits}" +
        $"\tcrit={sub.CritHits}" +
        $"\tdps={sub.Dps}" +
        $"\tbest={sub.BestSec}" +
        $"\tmax={sub.Max}" +
        $"\tmin={sub.Min}";

    internal static void SnapshotEvents(StringBuilder sb, List<string> states, List<string> dataPoints)
    {
        sb.Append("states\t").Append(string.Join(" ; ", states)).Append('\n');
        sb.Append("datapoints\t").Append(string.Join(" ; ", dataPoints)).Append('\n');
    }

    internal static void IsSortedByTotal(List<PlayerStats> list, string label)
    {
        for (var i = 1; i < list.Count; i++)
        {
            Assert.IsTrue(list[i - 1].Total >= list[i].Total, $"{label} is not ordered by Total at index {i}");
        }
    }

    /*
     * Golden compare with an explicit write mode:
     *   EQLP_GOLDEN_WRITE=1 dotnet test EQLogParser.Test --filter <TestName>
     * On a mismatch the message names the differing lines, because "360 lines differ" is not actionable and
     * "line 41: pctRaid=12.03 vs 11.98 on row Vael" is.
     */
    internal static void CompareOrWrite(string goldenName, string actualName, string label, string actual)
    {
        var goldenPath = FixturePath(goldenName);
        var actualPath = FixturePath(actualName);

        if (Environment.GetEnvironmentVariable("EQLP_GOLDEN_WRITE") == "1" || !File.Exists(goldenPath))
        {
            Assert.Inconclusive($"golden written to {actualPath} — copy it to {goldenPath} after reading the diff");
        }

        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        var got = actual.Replace("\r\n", "\n");
        if (expected == got) return;

        var a = expected.Split('\n');
        var b = got.Split('\n');
        var diffs = new List<string>();
        for (var i = 0; i < Math.Max(a.Length, b.Length) && diffs.Count < 12; i++)
        {
            var left = i < a.Length ? a[i] : "<missing>";
            var right = i < b.Length ? b[i] : "<missing>";
            if (left != right) diffs.Add($"line {i + 1}:\n  golden: {left}\n  actual: {right}");
        }

        Assert.Fail($"{label} board differs from the golden ({a.Length} vs {b.Length} lines)\n{string.Join("\n", diffs)}");
    }
}

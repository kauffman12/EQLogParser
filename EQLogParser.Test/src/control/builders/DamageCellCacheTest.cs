using System.Text;

namespace EQLogParser
{
    /*
     * The damage board's carried cells: a build over a capture it has already counted starts from each name's saved totals and
     * walks only what materialization appended since. The one law this file exists for is that a cached board and a cold board
     * are THE SAME BOARD - same rows, same totals, same sub-stat lines, same `+Pets` folding - so every test here compares a
     * cached build against a from-zero one rather than against hand-written numbers (a hand-written number would freeze whatever
     * the walk does today, including any bug in it; the goldens own correctness).
     *
     * HOW GROWTH IS MADE FAITHFULLY. One parsed `DamageFactTable`, and the same table handed more of its own ordinals: `_oracle`
     * is what a whole-capture pass fills, `_live` starts empty and receives those runs up to a time cut, then the rest. That is
     * production's shape - a live session has ONE fact table that grows - and it is the only arrangement where
     * `FightFactIndex.SummaryFightFor` continues a row instead of rebuilding it: re-parsing the file makes a NEW table, whose
     * ordinals mean something else, and a swapped table correctly rebuilds every row (which would refuse cells and prove nothing).
     * A continued row shares its closed blocks by reference and CLONES only its newest second (`ReopenTail`), so the one case
     * where a counted block legitimately changes identity is a same-second block whose counted prefix is reference-identical.
     *
     * `DamageCellStore.LastReusedActions` keeps this suite honest: with reuse at zero every equivalence test below would still
     * pass green on a cache that never ran, so one test asserts the number is positive and four assert it is zero where a refusal
     * is the correct answer. `DamageCellStore.LastDecision` names which refusal, so a failure reads as a reason.
     */
    [TestClass]
    [DoNotParallelize]
    public class DamageCellCacheTest
    {
        // The capture's own id for these tests; a different one means "another log", which no cell set may answer for.
        private const int Session = 123456789;

        // The six DamageValidator knobs are process-static and other suites flip them; one known setting here, restored after.
        private bool _ass, _bane, _ds, _fb, _hs, _su;
        private Func<string, bool> _originalIsValidClass = null!;

        private DamageFactTable _facts = null!;
        private List<DerivedFight> _rows = null!;
        private FightFactIndex _oracle = null!;
        private FightFactIndex _live = null!;
        private Dictionary<DerivedFight, (int Damage, int Tanking)> _replayed = null!;
        private long _stamp;

        [TestInitialize]
        public void Setup()
        {
            PipelineHarness.EnsureDataStore();
            _originalIsValidClass = CombatRecordLookup.IsValidClassName;
            CombatRecordLookup.IsValidClassName = name => EQDataStore.Instance.IsValidClassName(name);

            DamageLineParser.ResetProcessState();
            HealingLineParser.ClearCaches();
            RecordsStore.Instance.Clear(false);
            PlayerRegistry.Instance.Clear();
            HealRecordSource.Current = null;
            DamageCellStore.Clear();

            _ass = AppSettings.IsAssassinateDamageEnabled;
            _bane = AppSettings.IsBaneDamageEnabled;
            _ds = AppSettings.IsDamageShieldDamageEnabled;
            _fb = AppSettings.IsFinishingBlowDamageEnabled;
            _hs = AppSettings.IsHeadshotDamageEnabled;
            _su = AppSettings.IsSlayUndeadDamageEnabled;
            AppSettings.IsAssassinateDamageEnabled = true;
            AppSettings.IsBaneDamageEnabled = true;
            AppSettings.IsDamageShieldDamageEnabled = true;
            AppSettings.IsFinishingBlowDamageEnabled = true;
            AppSettings.IsHeadshotDamageEnabled = true;
            AppSettings.IsSlayUndeadDamageEnabled = true;

            // A pet folded under its raiders is the shape most at risk - a carried `+Pets` cell has to keep belonging to the same
            // person - so the fixture's group assignments are stated rather than inherited from whatever a previous test left.
            DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Akira", 1);
            DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Bryn", 2);

            Produce("board", "damage-board.txt");
        }

        [TestCleanup]
        public void Cleanup()
        {
            AppSettings.IsAssassinateDamageEnabled = _ass;
            AppSettings.IsBaneDamageEnabled = _bane;
            AppSettings.IsDamageShieldDamageEnabled = _ds;
            AppSettings.IsFinishingBlowDamageEnabled = _fb;
            AppSettings.IsHeadshotDamageEnabled = _hs;
            AppSettings.IsSlayUndeadDamageEnabled = _su;

            DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Akira", -1);
            DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Bryn", -1);
            CombatRecordLookup.IsValidClassName = _originalIsValidClass;
            RecordsStore.Instance.Clear(false);
            PlayerRegistry.Instance.Clear();
            DamageCellStore.Clear();
        }

        // ---- the laws ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void ASecondPassOverAGrownCaptureRecountsOnlyWhatArrived()
        {
            ReplayHalf();
            var firstFights = MaterializeAll();
            Build(firstFights, Proof);
            var first = Last();

            Assert.IsNotNull(DamageCellStore.Held, "a whole-capture build with a proof must publish cells");
            Assert.AreEqual(0L, DamageCellStore.LastReusedActions, "the first build of a capture has nothing to reuse");

            ReplayRest();
            var grownFights = MaterializeAll();
            Build(grownFights, Proof);
            var grown = Last();

            Assert.IsTrue(DamageCellStore.LastReusedActions > 0,
                "the second build walked every record, so cells are not being reused and this suite proves nothing. "
                + $"Refusal: {DamageCellStore.LastDecision ?? "(none recorded)"}");
            Assert.IsTrue(grown.RaidStats.Total > first.RaidStats.Total, "the fixture must actually have grown between the two builds");

            // The answer itself: row for row, sub-stat for sub-stat, the board a cold build prints over the same capture.
            AssertMatchesAColdBuild(grownFights, grown);
        }

        [TestMethod]
        public void ASelectionThatLostARowRecountsRatherThanKeepingItsDamage()
        {
            // A fixture with more than one fight row: this test is about a selection that loses one of them.
            Produce("derive", "mini-fight.txt");

            ReplayRest();
            var fights = MaterializeAll();
            Build(fights, Proof);
            var whole = Last();
            Assert.IsNotNull(DamageCellStore.Held);
            Assert.IsTrue(fights.Count > 1, "mini-fight fixture expected more than one materialized row");

            var fewer = new List<Fight>(fights);
            fewer.RemoveAt(fewer.Count - 1);

            Build(fewer, Proof);
            var narrowed = Last();
            Assert.AreEqual(0L, DamageCellStore.LastReusedActions,
                $"a selection that dropped a counted row must refuse the set, not carry it. {DamageCellStore.LastDecision}");
            Assert.IsTrue(whole.RaidStats.Total > narrowed.RaidStats.Total, "dropping a row must lower the board");
            AssertMatchesAColdBuild(fewer, narrowed);
        }

        [TestMethod]
        public void AChangedVerdictRefusesTheCellsAndStillPrintsTheSameBoard()
        {
            ReplayRest();
            var fights = MaterializeAll();
            Build(fights, Proof);

            // The stamp is the timeline's: a pass that moved a verdict re-routes records between a name and its `+Pets` row, so a
            // different stamp is a different question even though the rows in hand are identical.
            Build(fights, new DamageBoardProof(Session, _stamp + 7));
            var movedStamp = Last();

            Assert.AreEqual(0L, DamageCellStore.LastReusedActions, "a moved verdict must not read cells written under the old one");
            Assert.AreEqual(_stamp + 7, DamageCellStore.Held!.ClassificationStamp, "the recount must publish under the new stamp");
            AssertMatchesAColdBuild(fights, movedStamp, _stamp + 7);
        }

        [TestMethod]
        public void AFilterBoxChangeRefusesTheCellsAndStillPrintsTheSameBoard()
        {
            ReplayRest();
            var fights = MaterializeAll();
            Build(fights, Proof);
            Assert.IsNotNull(DamageCellStore.Held);

            // The six DamageValidator boxes filter INSIDE the walk: same records, same stamp, different answer.
            AppSettings.IsHeadshotDamageEnabled = false;
            Build(fights, Proof);
            var filtered = Last();

            Assert.AreEqual(0L, DamageCellStore.LastReusedActions,
                "cells counted with a different filter setting would report damage the boxes excluded");
            AssertMatchesAColdBuild(fights, filtered);
        }

        [TestMethod]
        public void AWindowedSliceNeitherReadsTheCellsNorOverwritesThem()
        {
            ReplayRest();
            var fights = MaterializeAll();
            Build(fights, Proof);
            var published = DamageCellStore.Held;
            Assert.IsNotNull(published);

            // The pane's own time dial re-slices groups that are partly JOINED objects (seconds merged across blocks), so no
            // resume point in them is trustworthy - and writing over the whole-capture set would make "show everything again"
            // read a slice's totals as the night's.
            Build(fights, Proof, minSeconds: 0, maxSeconds: 2);
            var sliced = Last();

            Assert.AreEqual(0L, DamageCellStore.LastReusedActions, "a slice must not read whole-capture cells");
            Assert.AreSame(published, DamageCellStore.Held, "a slice must not replace the whole-capture set");
            AssertMatchesAColdBuild(fights, sliced, minSeconds: 0, maxSeconds: 2);
        }

        [TestMethod]
        public void TwoCapturesNeverAnswerForOneAnother()
        {
            ReplayRest();
            var fights = MaterializeAll();
            Build(fights, Proof);
            Assert.IsNotNull(DamageCellStore.Held);

            // Same stamp (a second capture can classify to the same digest), different session: the store is process-wide while a
            // closed log's rows are not, so the session id is the term that keeps this from being one pile.
            Build(fights, new DamageBoardProof(Session + 1, _stamp));
            var otherSession = Last();

            Assert.AreEqual(0L, DamageCellStore.LastReusedActions);
            Assert.AreEqual(Session + 1, DamageCellStore.Held!.SessionId);
            AssertMatchesAColdBuild(fights, otherSession, otherSession: Session + 1);
        }

        /*
         * The same law over a capture big enough for the walk to cost seconds, since that is the only place the win exists:
         *
         *   EQLP_CELL_CACHE=local/logs/live/eqlog_Incogitable_xegony.txt dotnet test --filter DamageCellCache --logger "console;verbosity=detailed"
         *
         * Half the capture's timeline, built; then the rest of the same table, built; then a cold build for the comparison. The
         * printed line is the number to look for in the app: reused share high and cached milliseconds low.
         */
        [TestMethod]
        public void CachedBoardMatchesAColdOneOnARealCapture()
        {
            var path = ResolveGatePath(Environment.GetEnvironmentVariable("EQLP_CELL_CACHE"));
            if (path is null) Assert.Inconclusive("set EQLP_CELL_CACHE=<log>");

            ProduceFile(path);

            // A LIVE-shaped increment, not half the night: a running capture asks this question about seconds of new facts, and the
            // first minutes of a capture are exactly when pet mappings are still being learned - and a new mapping legitimately
            // refuses the set (cells counted before it would fold old records onto the wrong owner).
            ReplayTimeCut(0.9);
            var firstWatch = System.Diagnostics.Stopwatch.StartNew();
            Build(MaterializeAll(), Proof);
            firstWatch.Stop();

            ReplayRest();
            var grownFights = MaterializeAll();
            var grownWatch = System.Diagnostics.Stopwatch.StartNew();
            Build(grownFights, Proof);
            grownWatch.Stop();
            var grown = Last();

            var reused = DamageCellStore.LastReusedActions;
            var counted = DamageCellStore.LastCountedActions;
            Console.WriteLine($"[cells] {Path.GetFileName(path)}: rows {_rows.Count}, facts {_facts.Facts.Length:N0}, "
                + $"reused {reused:N0} of {reused + counted:N0} actions "
                + $"({100.0 * reused / Math.Max(1, reused + counted):0.#} %); "
                + $"first build {firstWatch.ElapsedMilliseconds} ms, cached build {grownWatch.ElapsedMilliseconds} ms; "
                + $"refusal: {DamageCellStore.LastDecision ?? "(none)"}, {StatsBuildTrace.LastFinishedLineOf("damage") ?? "(no trace)"}");

            Assert.IsTrue(reused > 0, $"no records were skipped on the real capture. Refusal: {DamageCellStore.LastDecision}");
            AssertMatchesAColdBuild(grownFights, grown);
        }

        // ---- the comparison ---------------------------------------------------------------------------------------

        /// <summary>Assert the board in hand equals a from-zero build over exactly the same fights and settings.</summary>
        private void AssertMatchesAColdBuild(List<Fight> fights, CombinedStats cached, long stamp = 0,
                                             int minSeconds = -1, int maxSeconds = -1, int otherSession = 0)
        {
            Assert.AreEqual(Board(Cold(fights, stamp == 0 ? _stamp : stamp, otherSession, minSeconds, maxSeconds)), Board(cached));
        }

        private CombinedStats Cold(List<Fight> fights, long stamp, int session, int minSeconds, int maxSeconds)
        {
            DamageCellStore.Clear();
            Build(fights, new DamageBoardProof(session == 0 ? 987654321 : session, stamp), minSeconds, maxSeconds);
            return Last();
        }

        /// <summary>What the damage tab prints for one build: every row, every child row, the raid line, and the board's shape.</summary>
        private static string Board(CombinedStats stats)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BoardGolden.StatsLine("raid", stats.RaidStats));

            foreach (var row in stats.StatsList) sb.AppendLine(BoardGolden.StatsLine("top", row));
            foreach (var row in stats.ExpandedStatsList) sb.AppendLine(BoardGolden.StatsLine("expanded", row));

            foreach (var parent in stats.Children)
                foreach (var child in stats.Children[parent.Key])
                    sb.AppendLine(BoardGolden.SubLine("child", parent.Key, child));

            // The shape too: a row can move between `top` and `child` while every printed column still matches.
            sb.AppendLine($"shape rows={stats.StatsList.Count} expanded={stats.ExpandedStatsList.Count} "
                + $"parents={stats.Children.Count} classes={stats.PlayerClasses.Count}");

            return sb.ToString();
        }

        // ---- production's own doors ------------------------------------------------------------------------------

        private DamageBoardProof Proof => new(Session, _stamp);

        private void Produce(string dir, string name)
            => ProduceFile(Path.Combine(AppContext.BaseDirectory, "mini-data", dir, name));

        private void ProduceFile(string path)
        {
            var run = PipelineHarness.RunFileDerived(path);
            _facts = run.Facts;

            var timeline = new EntityTimeline();
            var firstT = _facts.Facts.Length > 0 ? _facts.Facts[0].TimeS : 0;
            var lastT = _facts.Facts.Length > 0 ? _facts.Facts[^1].TimeS : 0;
            RegistrySeed.Apply(timeline, _facts, firstT, lastT);
            ClassificationRules.Apply(_facts, timeline, run.HealFacts);

            _stamp = timeline.StateStamp();
            _oracle = new FightFactIndex();
            _rows = FightProjection.Build(_facts, timeline, _oracle.OnFact);
            _live = new FightFactIndex();
            _replayed = new Dictionary<DerivedFight, (int Damage, int Tanking)>();

            Assert.IsTrue(_rows.Count > 0 && _facts.Facts.Length > 0, $"{Path.GetFileName(path)} produced nothing");
        }

        /// <summary>The capture as far as its midpoint in time: what a board build sees mid-load or mid-raid.</summary>
        private void ReplayHalf()
        {
            var all = _facts.Facts;
            Replay(all[0].TimeS + (all[^1].TimeS - all[0].TimeS) / 2);
        }

        /// <summary>Hand the SAME index the rest of its ordinals, so every row continues rather than being rebuilt.</summary>
        private void ReplayRest() => Replay(double.MaxValue);

        /// <summary>The capture up to a fraction of its own time span - a live tail rather than a mid-load half.</summary>
        private void ReplayTimeCut(double fraction)
        {
            var all = _facts.Facts;
            Replay(all[0].TimeS + (all[^1].TimeS - all[0].TimeS) * fraction);
        }

        /// <summary>Replay each row's own ordinal runs (ascending, as the table wrote them) up to `uptoTime` into `_live`.</summary>
        private void Replay(double uptoTime)
        {
            var all = _facts.Facts;

            foreach (var row in _rows)
            {
                var cursor = _replayed.TryGetValue(row, out var held) ? held : default;

                var damage = _oracle.DamageOrdinalsFor(row);
                for (; cursor.Damage < damage.Count; cursor.Damage++)
                {
                    var ordinal = damage[cursor.Damage];
                    if (all[ordinal].TimeS > uptoTime) break;
                    _live.OnFact(all[ordinal], ordinal, row, FightProjection.FactTarget.AtOwner);
                }

                var tanking = _oracle.TankingOrdinalsFor(row);
                for (; cursor.Tanking < tanking.Count; cursor.Tanking++)
                {
                    var ordinal = tanking[cursor.Tanking];
                    if (all[ordinal].TimeS > uptoTime) break;
                    _live.OnFact(all[ordinal], ordinal, row, FightProjection.FactTarget.RaidSide);
                }

                _replayed[row] = cursor;
            }
        }

        /// <summary>The fights a build answers for - the same call MainWindow makes through `DeriveEngine.BuildSummaryInput`.</summary>
        private List<Fight> MaterializeAll() => FightSummarySource.Build(_rows, _live, _facts).Fights.ToList();

        private static void Build(List<Fight> fights, DamageBoardProof proof, int minSeconds = -1, int maxSeconds = -1)
        {
            var options = new GenerateStatsOptions
            {
                Source = "test [cells]",
                MinSeconds = minSeconds,
                MaxSeconds = maxSeconds,
                CellProof = proof
            };
            options.Npcs.AddRange(fights);

            DamageStatsBuilder.Instance.BuildTotalStats(options);
        }

        /// <summary>The board this builder last produced (the same object the panes repaint from).</summary>
        private static CombinedStats Last() => DamageStatsBuilder.Instance.GetLastStats()!.CombinedStats!;

        /// <summary>A gated log path, found from the test assembly's directory upward (the harness runs from bin/, the logs live in the repo).</summary>
        private static string? ResolveGatePath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return path;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, path);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }
}

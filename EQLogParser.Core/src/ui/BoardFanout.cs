/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): a job list is always built by the caller
 * with three entries, and the interesting nulls here are the delegates' contracts rather than flow the compiler would prove.
 */
#nullable enable annotations

using System.Reflection;
using log4net;

namespace EQLogParser;

/*
 * How many workers ONE board request may use. Two values because there are two answers and they are decided by one measured number:
 * a five-minute selection is worth three threads for about as long as it takes to start them, and a whole farm night is not.
 */
internal enum BoardFanoutMode
{
  /// <summary>Today's exact path: damage, then tanking, then healing, on the request's own worker.</summary>
  Serial,

  /// <summary>The three boards run side by side and the request waits for all of them.</summary>
  Parallel,
}

/*
 * Three boards, three workers, one answer per board.
 *
 * The shape this exists for (docs/DesignNotes → "Where a board build's time actually goes", measured in
 * docs/improve-stat-building §0): `MainWindow.BuildBoards` materializes a selection and then builds damage, tanking and healing in
 * a LINE. The boards share no state — each has its own lock, its own traced handle, its own events, its own output rows — so the
 * line is not a dependency, it is an accident of the method body. Measured on 4 cores over the 998 MB farm night, the build wall
 * goes 3,899 ms → 2,518 ms (×1.55) and the end-to-end ask ×1.31; on Incogitable ×1.39 / ×1.09, because there materialize is the
 * bigger term and fan-out does not touch it. The win holds from 18 cores down to 2 (the walks are memory-latency bound), which is
 * why there is no core-count branch: three workers, always, above the threshold.
 *
 * Three laws, all of them here rather than in the app so they can be tested without a window:
 *
 *   1. **A worker asks whose capture this still is before it touches a builder.** The standing abandonment law says a build that
 *      outlived its own log must return before painting — `Dispose` drops the snapshot, and a materialize of a dropped snapshot
 *      reads as "clear every board", so a late worker would blank whatever capture opened next. Serially one check after materialize
 *      covers the whole build; in parallel each worker needs its own, because the other two can start after a capture change.
 *   2. **A board that throws costs that board and nothing else.** Serially, a damage throw suppresses tanking AND healing (the
 *      method's single catch). Fanned out, each worker carries its own try/catch and logs the same greppable anchor — so one bad
 *      record can no longer take three grids away. This is a deliberate change of behaviour, stated as such: "a partial board beats
 *      no board" now holds per board instead of stopping at the first throw.
 *   3. **Every worker is waited for.** The request does not complete while a board is still running: the gate's "one build in
 *      flight" rule ends when this returns, and a straggler would be a second build overlapping whatever starts next — with its own
 *      materialized input still on the heap.
 *
 * Why the inputs make this safe at all (audited against the three builders, not assumed): `FightSummarySource.Build` and
 * `HealSummarySource.Materialize` hand back FRESH legacy-shaped objects per request, and the builders only read them — they copy the
 * selection into their own `_selected`, sort their own copy of the blocks, and write per-builder rows/collections. The one place a
 * build touches a shared object at all is `TimeRange.Add(options.AllRanges.TimeSegments)`: that overload ADOPTS the segment objects
 * it is handed (the "merging copies, adding adopts" law in TimeRange), and welding happens inside `Add`, so the invariant fan-out
 * depends on is that no build ever adds to a range it adopted — true today (`_raidTotals.AllRanges` is filled once, from the
 * selection's disjoint-by-construction span list, and read after that). A future writer into an adopted range would be a data race,
 * not a bug in this file; `TimeRangeTest` owns the merge semantics.
 *
 * What is NOT changed by any of this: the gate (fan-out happens INSIDE one request's work action, so there is still never a second
 * materialization), `UnaskedRefresh` (it decides whether a build runs at all; a declined pass fans out nothing), the charts (each
 * board stamps its own `ChartDataGeneration` from its own trace handle, and the repeat-question drop sees the same questions), and
 * the deferred-busy law (each pane arms and cancels its own 150 ms tick on its own board's STARTED/COMPLETED).
 */
internal static class BoardFanout
{
  /*
   * The crossover, measured rather than guessed (docs/improve-stat-building §4): at 466,165 outcomes the fan-out saves 40 ms — under
   * the deferred-busy state's own 150 ms "worth telling the user" line and inside run-to-run jitter — while at 862,149 it saves
   * 253 ms and a whole-capture select-all saves 335-1,381 ms. The constant sits in the unmeasured middle of a measured ≤40 ms and a
   * measured ≥253 ms on purpose: below it nothing says three threads earn their keep, above it something does. Roughly a ninth of a
   * farm-night select-all.
   *
   * It deliberately shares its METER with the refresh budget (`UnaskedRefresh.OutcomeCount`) but none of its constants: "may this
   * build run at all" and "how many workers may it use" are different questions on different clocks, and a policy that merged them
   * would make every future threshold edit ambiguous about which one it moved.
   */
  internal const long MinOutcomes = 500_000;

  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  /// <summary>Production's spawner: plain pool work, no dedicated threads and no `Parallel.Invoke` magic — three starts against a multi-second build.</summary>
  private static readonly Func<Action, Task> PoolSpawn = work => Task.Run(work);

  /// <summary>How many workers the three boards of a selection this size may use. Exactly at the constant, they fan out.</summary>
  /// <param name="outcomes">`UnaskedRefresh.OutcomeCount(selection)` — hits dealt plus hits taken.</param>
  /// <param name="minOutcomes">Overridable so the suite can force `Parallel` on small fixtures without touching production's number.</param>
  internal static BoardFanoutMode Decide(long outcomes, long minOutcomes = MinOutcomes)
    => outcomes >= minOutcomes ? BoardFanoutMode.Parallel : BoardFanoutMode.Serial;

  /*
   * One board's work. `StillMine` is the abandonment law (law 1) and `Asked` is the request's own words for why it happened, so the
   * abandon line names the gesture that is no longer running rather than a bare board name.
   */
  internal readonly record struct BoardJob(string Board, string Asked, Func<bool> StillMine, Action Build);

  /*
   * Run the jobs on separate workers and wait for all of them (law 3).
   *
   * `spawn` is a parameter because the law here is about what each worker DOES, not about how the pool schedules it: production hands
   * work to `Task.Run`, and the suite runs the same jobs inline to assert abandonment and per-board isolation without racing a
   * thread pool — while the concurrency itself is pinned by its own test with the real spawner.
   */
  internal static void RunParallel(IReadOnlyList<BoardJob> boards, Func<Action, Task>? spawn = null)
  {
    var start = spawn ?? PoolSpawn;

    var workers = new Task[boards.Count];
    for (var i = 0; i < boards.Count; i++)
    {
      var job = boards[i];
      workers[i] = start(() => RunOne(job));
    }

    Task.WaitAll(workers);
  }

  /// <summary>Run one board's build under the two laws a worker owes: whose capture is this, and a failure that stays inside its own board.</summary>
  internal static void RunOne(in BoardJob job)
  {
    if (!job.StillMine())
    {
      Log.Debug($"board build abandoned [{job.Asked}]: capture closed before the {job.Board} board started");
      return;
    }

    try
    {
      job.Build();
    }
    catch (Exception ex)
    {
      // The same anchor the serial path logs, so `grep "Derived damage summary error"` finds a fanned-out failure too — the board
      // suffix says WHICH grid the operator is missing, which is the question the line gets asked.
      Log.Error($"Derived damage summary error ({job.Board} board)", ex);
    }
  }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Tests.Ui
{
  /*
   * Whether the three summary boards of ONE selection may run side by side, and what each of its workers owes (EQLogParser.Core/src/ui/BoardFanout.cs).
   *
   * The shape: `MainWindow.BuildBoards` materializes a selection and used to build damage, tanking and healing strictly in a line.
   * Measured on 4 cores over the 998 MB farm night that line costs 3,899 ms of build wall where three workers need 2,518 (§0 of
   * docs/improve-stat-building, re-measured from a throwaway probe now deleted); below a measured crossover the same three builds take
   * tens of milliseconds and three thread starts are the whole finding. So the class holds two things: a THRESHOLD on the one meter
   * the refresh budget already reads, and the worker policy (abandon before painting, one board's failure stays in that board, wait
   * for every board).
   *
   * The equivalence question — "do three parallel builds give the same ANSWERS as a serial one" — is NOT asserted here, because it is
   * already asserted better: the three board goldens (`DamageBoardGoldenTest` and siblings) freeze every grid column, child share and
   * sub-stat key over retained pools, and they run against the same builder code the workers call. What this file pins is the decision
   * and the three laws, all of which are invisible to a golden.
   *
   * Most tests pass an INLINE spawner (`work => { work(); return Task.CompletedTask; }`) so they assert what a worker DOES without
   * racing a thread pool; the one test that cares about real concurrency uses production's spawner and proves overlap with a countdown
   * rather than by sleeping.
   */
  [TestClass]
  public sealed class BoardFanoutTest
  {
    // A spawner that runs each job on the calling thread: same code path, no scheduling involved.
    private static Task Inline(Action work)
    {
      work();
      return Task.CompletedTask;
    }

    private static List<DerivedFight> Fights(long outcomes)
      => [new DerivedFight { Id = 1, DamageHits = (uint)Math.Min(outcomes, uint.MaxValue), TankHits = (uint)Math.Max(0, outcomes - Math.Min(outcomes, uint.MaxValue)) }];

    private static BoardFanout.BoardJob Job(string board, List<string> ran, bool mine = true)
      => new(board, "unit test", () => mine, () => ran.Add(board));

    // ── the threshold: one measured number, read off the budget's own meter ───────────────────────────────

    [TestMethod]
    public void ASelectionBelowTheCrossoverKeepsItsOwnWorker()
    {
      Assert.AreEqual(BoardFanoutMode.Serial, BoardFanout.Decide(0), "nothing to build is never worth three threads");
      Assert.AreEqual(BoardFanoutMode.Serial, BoardFanout.Decide(BoardFanout.MinOutcomes - 1));

      // The measured corner under the constant: 36,235 outcomes (five fights of a farm night) saves 18 ms — one frame — so it keeps
      // today's exact path instead of paying three schedules for something nobody can perceive.
      Assert.AreEqual(BoardFanoutMode.Serial, BoardFanout.Decide(36_235), "a selection worth one frame stays serial");
    }

    [TestMethod]
    public void ASelectionAboveTheCrossoverFansOut()
    {
      Assert.AreEqual(BoardFanoutMode.Parallel, BoardFanout.Decide(BoardFanout.MinOutcomes), "exactly at the constant, it fans out");

      // The measured corners above the constant: 389,552 outcomes saves 125 ms, 717,190 saves 194 ms, and a whole farm night
      // (4,580,874) saves 1,459 ms of build wall — x1.32 of the ask end to end.
      Assert.AreEqual(BoardFanoutMode.Parallel, BoardFanout.Decide(389_552));
      Assert.AreEqual(BoardFanoutMode.Parallel, BoardFanout.Decide(4_580_874));
    }

    [TestMethod]
    public void TheDecisionReadsTheSameMeterAsTheRefreshBudget()
    {
      /*
       * Both questions — "may this build run at all" and "how many workers may it use" — are priced from `UnaskedRefresh.OutcomeCount`
       * (hits dealt + hits taken), so they cannot drift into different ideas of how big a selection is. Two rows that each carry half
       * the crossover prove the count, not the row list, decides: 200 rows of nothing is still serial, two rows of work are not.
       */
      var manyEmpty = new List<DerivedFight>();
      for (var i = 0; i < 200; i++) manyEmpty.Add(new DerivedFight { Id = i + 1, DamageHits = 3, TankHits = 3 });

      Assert.AreEqual(1_200, UnaskedRefresh.OutcomeCount(manyEmpty), "rows are not the meter");
      Assert.AreEqual(BoardFanoutMode.Serial, BoardFanout.Decide(UnaskedRefresh.OutcomeCount(manyEmpty)),
          "two hundred quiet rows do not fan out however many of them there are");

      var half = BoardFanout.MinOutcomes / 2;
      var twoRows = new List<DerivedFight>
      {
        new() { Id = 1, DamageHits = (uint)half },
        new() { Id = 2, TankHits = (uint)half }
      };

      Assert.AreEqual(BoardFanout.MinOutcomes, UnaskedRefresh.OutcomeCount(twoRows), "both directions count, dealt and taken");
      Assert.AreEqual(BoardFanoutMode.Parallel, BoardFanout.Decide(UnaskedRefresh.OutcomeCount(twoRows)));
    }

    [TestMethod]
    public void TheThresholdIsOverridableSoFixturesCanForceAFanOut()
    {
      // The injectable dial exists so the suite can drive the parallel path over fixtures without moving production's constant.
      Assert.AreEqual(BoardFanoutMode.Parallel, BoardFanout.Decide(10, minOutcomes: 10));
      Assert.AreEqual(BoardFanoutMode.Serial, BoardFanout.Decide(9, minOutcomes: 10));
      Assert.AreEqual(BoardFanoutMode.Parallel, BoardFanout.Decide(BoardFanout.MinOutcomes), "the production default is still the measured one");
    }

    // ── law 2: a board that throws costs that board and nothing else ──────────────────────────────────────

    [TestMethod]
    public void AThrowingBoardDoesNotCostTheOtherTwo()
    {
      /*
       * The one deliberate behaviour change of the fan-out. Serially, the damage build threw into BuildBoards' single catch and tanking
       * plus healing never ran: three blank grids. Fanned out, each worker carries its own failure, so "a partial board beats no board"
       * holds PER board. (Production logs `Derived damage summary error (<board> board)`; nothing in this suite captures logs, so the
       * law is asserted as what still runs and what does not escape.)
       */
      /*
       * The throwing board records NOTHING rather than asserting inside its own body: a law asserted with Assert.Fail in there would be
       * swallowed by exactly the isolation this test is about, and would pass while doing nothing.
       */
      var ran = new List<string>();
      var threw = false;

      BoardFanout.RunParallel([
        new("damage", "unit test", static () => true,
            () => { threw = true; throw new InvalidOperationException("one bad record"); }),
        Job("tanking", ran),
        Job("healing", ran)
      ], Inline);

      Assert.IsTrue(threw, "the broken board really ran (and really threw)");
      CollectionAssert.AreEqual(new List<string> { "tanking", "healing" }, ran,
          "the two healthy boards still build when a sibling throws - and no exception escapes to take the gate's worker with it");
    }

    [TestMethod]
    public void EveryBoardIsAttemptedRatherThanUntilTheFirstOneFinishes()
    {
      var ran = new List<string>();
      BoardFanout.RunParallel([Job("damage", ran), Job("tanking", ran), Job("healing", ran)], Inline);

      CollectionAssert.AreEqual(new List<string> { "damage", "tanking", "healing" }, ran, "all three boards of the request run");
    }

    // ── law 1: a worker whose capture closed does no board work ───────────────────────────────────────────

    [TestMethod]
    public void AWorkerWhoseCaptureClosedBuildsNothing()
    {
      /*
       * `Dispose` drops the snapshot, and `BuildSummaryInput` reads a dropped snapshot as "clear every board" - so a worker scheduled
       * after a capture change must return BEFORE touching its builder, or it blanks whatever log just opened. The serial path checks
       * once after materialize; in parallel every worker needs its own, because the other two can start behind a session swap.
       */
      var ran = new List<string>();

      BoardFanout.RunParallel([
        new("damage", "unit test", static () => false, () => ran.Add("damage built")),
        Job("tanking", ran),
        new("healing", "unit test", static () => false, () => ran.Add("healing built"))
      ], Inline);

      CollectionAssert.AreEqual(new List<string> { "tanking" }, ran,
          "only the board whose capture is still open builds; the other two returned before their builder was touched");
    }

    [TestMethod]
    public void NothingAboutAJobIsDecidedBeforeItsWorkerRuns()
    {
      /*
       * The abandonment question is asked WHEN THE WORKER RUNS, not when the request submits: a capture change landing in between is
       * exactly the race this law exists for. A capturing spawner makes that assertable without racing anything - the job's body is held
       * back, the capture is taken away while it waits, and running it afterwards must still build nothing.
       */
      var mine = true;
      var asked = 0;
      var built = false;
      var held = new List<Action>();

      BoardFanout.RunParallel(
        [new("damage", "unit test", () => { asked++; return mine; }, () => built = true)],
        work => { held.Add(work); return Task.CompletedTask; });

      Assert.AreEqual(0, asked, "submitting a request asks nothing - the worker is what asks");
      Assert.IsFalse(built);

      // The capture closes here, between submission and the worker actually running.
      mine = false;
      foreach (var work in held) work();

      Assert.AreEqual(1, asked, "the worker asked exactly once when it ran");
      Assert.IsFalse(built, "and got the late answer: a capture closed after submission still builds nothing");
    }

    // ── law 3: the request waits for every board ──────────────────────────────────────────────────────────

    [TestMethod]
    public void TheRequestWaitsForTheSlowestBoard()
    {
      /*
       * The gate's "one build in flight" ends when this returns. A straggler would be a second build overlapping whatever starts next —
       * with its own materialized input still on the heap, which is the memory half of the same law.
       */
      var slowFinished = false;

      BoardFanout.RunParallel([
        new("damage", "unit test", static () => true, () => { Thread.Sleep(150); slowFinished = true; }),
        Job("tanking", []),
        Job("healing", [])
      ]);

      Assert.IsTrue(slowFinished, "RunParallel does not hand the request back while a board is still running");
    }

    [TestMethod]
    public void TheThreeBoardsReallyRunAtTheSameTime()
    {
      /*
       * Concurrency itself, asserted rather than assumed: three jobs that each refuse to finish until all three have STARTED. If the
       * spawner serialized them the countdown would expire and every job would report "alone" - so this fails by name on a regression
       * to a line, instead of merely getting slower. (A timeout is used rather than an infinite wait so a regression reads as a failed
       * assertion in the budget of the run rather than as a hung test host.)
       */
      var started = new CountdownEvent(3);
      // One slot per job: a List<bool> appended from three workers is a race inside the test that is supposed to prove concurrency.
      var overlapped = new bool[3];

      BoardFanout.RunParallel([
        new("damage", "unit test", static () => true, () => overlapped[0] = Overlaps(started)),
        new("tanking", "unit test", static () => true, () => overlapped[1] = Overlaps(started)),
        new("healing", "unit test", static () => true, () => overlapped[2] = Overlaps(started))
      ]);

      for (var i = 0; i < overlapped.Length; i++)
      {
        Assert.IsTrue(overlapped[i], $"board #{i} was not running while the other two were - if this fails, the fan-out became a line again");
      }

      started.Dispose();
    }

    // Signal that this board is up, then report whether the other two joined it within a generous budget.
    private static bool Overlaps(CountdownEvent started)
    {
      started.Signal();
      return started.Wait(TimeSpan.FromSeconds(10));
    }
  }
}

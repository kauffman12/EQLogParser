using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * The overlay's spawn path used to allocate ~5.4 KB per number it put on screen (docs → "What the FCT overlay costs per
   * number on screen"), most of it the eighteen row states `FctPlacement` cloned to score its lattice — seventeen discarded,
   * all of it on the render thread, during the one moment the program has to be smooth. The bench took that to zero; this is
   * what keeps it there, because "no allocations" is not something a geometry test can see and every readability change to
   * the placement search will want to reach for another convenient `Clone()`.
   *
   * Measured per SPAWNED number at a rate nobody crowds (one arrival every 20 frames, so nothing folds and nothing is
   * dropped — a fold rebuilds the "×N" text, a drop formats a worst-drop label; both are separate, allowed allocations).
   */
  [TestClass]
  public class FctPlacementAllocationTest
  {
    // What a spawned row may cost now. The pre-fix figure was ~5,400 B; what is left is the row itself plus its two strings.
    private const int MaxBytesPerSpawnedNumber = 900;

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();

    [TestMethod]
    public void SpawningANumberAllocatesLessThanACopyOfTheRowUsedToCost()
    {
      var ingest = new FctIngest(new Random(11)) { Style = FctMotionStyle.Fountain };
      var hits = new List<FctHitState>();
      var rnd = new Random(5);

      const int frames = 20_000;
      const int arriveEvery = 20;

      // Warm-up: the JIT, the first rows, and the bench's first two buffers are not the thing being measured.
      Run(ingest, hits, rnd, 3_000, 0, arriveEvery);

      var before = GC.GetAllocatedBytesForCurrentThread();
      var spawned = Run(ingest, hits, rnd, frames, 1_000_000, arriveEvery);
      var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

      Assert.IsTrue(spawned >= 900, $"the run spawned only {spawned} numbers — nothing was measured");
      TestContext.WriteLine($"[placement] {bytes / (double)spawned:N0} B per spawned number ({spawned} spawns)");

      Assert.IsTrue(bytes < (long)MaxBytesPerSpawnedNumber * spawned,
        $"a spawned number costs {bytes / (double)spawned:N0} B; the budget is {MaxBytesPerSpawnedNumber}. " +
        "Placement trials must run through FctTrialBench, not by cloning rows.");
    }

    [TestMethod]
    public void TheProbeCanSeeAnAllocation()
    {
      /*
       * An allocation assertion that cannot fail is worse than none: a runtime change that made the counter useless would
       * report a clean overlay forever. Same instrument, known cost — one row state per iteration, kept alive by the list so
       * nothing can be optimised away.
       */
      var keep = new List<FctHitState>();
      var before = GC.GetAllocatedBytesForCurrentThread();
      for (var i = 0; i < 2_000; i++) keep.Add(new FctHitState { Value = i });
      var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

      Assert.IsTrue(keep.Count == 2_000);
      Assert.IsTrue(bytes > 2_000 * 32,
        $"allocating 2,000 row states registered {bytes} B — the probe cannot see allocation and the test above means nothing");
    }

    /// <summary>Spawns one number every `arriveEvery` frames and ages the rest; returns how many rows were actually created.</summary>
    private static int Run(FctIngest ingest, List<FctHitState> hits, Random rnd, int frames, double startNow, int arriveEvery)
    {
      const double FrameMs = 1000.0 / 60;
      var now = startNow;
      var spawned = 0;

      for (var f = 0; f < frames; f++)
      {
        now += FrameMs;
        if (f % arriveEvery == 0)
        {
          // A fresh face value every time: identical values fold into the number already on screen, and folding is a
          // different allocation story (the odometer text changes) that this test must not bill to spawning.
          var hit = ingest.Accept(hits, FctLane.DamageDealt, 1 + rnd.Next(500_000), "Starfire", false, false, false,
            null, 1920, 1080, now, false, FctRow.SpellHits, FctSpecial.None);
          if (hit is not null) spawned++;
        }
        ingest.PruneExpired(hits, now, null);
      }

      return spawned;
    }

    public TestContext TestContext { get; set; } = null!;
  }
}

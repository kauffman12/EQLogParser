using EQLogParser.Audio;

namespace EQLogParser.Wpf.Test
{
  /*
   * The gate that answers "is there a speech engine yet" (EQLogParser.Audio/src/TtsReadiness.cs), pinned directly.
   *
   * It cannot be reached through AudioManager: constructing that would open this machine's audio devices and try to
   * build a real neural voice, which is why no test has ever touched the manager itself. What the manager relies on is
   * three properties of the gate, all of them cheap to state and all of them load-bearing for a callout:
   *
   *   - a release always comes, whether the build produced an engine or not, because the alternative is a callout
   *     parked for the life of the process on something that already gave up;
   *   - releasing is not the same as being able to speak, which is why the state is read after waiting rather than
   *     carried by the wait -- an engine can arrive later than the attempt that failed;
   *   - completing is safe from any thread and twice, because it is called from a finally that may race whatever
   *     cleanup follows it.
   */
  [TestClass]
  public class TtsReadinessTest
  {
    // Longer than any of these can legitimately take; a wait past it is the hang the gate exists to prevent.
    private const int ReleaseBudgetMs = 2000;

    [TestMethod]
    public void ANewGateSaysThereIsNoEngineAndHasNotAnsweredYet()
    {
      var ready = new TtsReadiness();

      Assert.IsFalse(ready.HasEngine, "an engine that has not been built is not an engine");
      Assert.IsFalse(ready.ReadyAsync.IsCompleted,
        "nobody may claim the build is over before it is: a reader that treated this as done would report silence as failure");
    }

    [TestMethod]
    public async Task ABuiltEngineReleasesWhoeverWaitedAndSaysSo()
    {
      var ready = new TtsReadiness();
      var waiter = ready.ReadyAsync;

      Assert.IsFalse(waiter.IsCompleted, "the wait is not over before the build is");

      ready.Complete(true);

      Assert.IsTrue(await ReleasedInTime(waiter), "whoever waited on the first engine gets its answer");
      Assert.IsTrue(ready.HasEngine, "and the answer includes that there is something to speak with");
    }

    [TestMethod]
    public async Task ABuildThatFailedStillReleasesThemRatherThanParkingThem()
    {
      var ready = new TtsReadiness();
      var waiter = ready.ReadyAsync;

      ready.Complete(false);

      Assert.IsTrue(await ReleasedInTime(waiter),
        "a failed build is still an ending: a callout left waiting here waits for an engine that is never coming");
      Assert.IsFalse(ready.HasEngine, "released is not the same as able to speak, and silence stays the answer");
    }

    [TestMethod]
    public void AnEngineThatArrivesLateRestoresSpeechAfterAFailedFirstAttempt()
    {
      /*
       * The shape this rules out: the automatic engine fails at startup, the operator notices the silence and picks a
       * runtime in the TTS window. A gate that took its first answer as final would keep every later callout silent on
       * a machine that can speak now, with no way back short of a restart.
       */
      var ready = new TtsReadiness();

      ready.Complete(false);
      ready.Complete(true);

      Assert.IsTrue(ready.HasEngine, "a build that succeeded is not un-said by the one that failed first");
      Assert.IsTrue(ready.ReadyAsync.IsCompleted,
        "the gate does not re-arm: whoever waits after this point is waiting on a release that already happened");
    }

    [TestMethod]
    public void EveryWaiterIsGivenTheSameTaskRatherThanOneEach()
    {
      var ready = new TtsReadiness();
      var first = ready.ReadyAsync;
      var second = ready.ReadyAsync;

      Assert.AreSame(first, second,
        "callouts ask this on the way to speaking; handing each of them a new completion source would put an allocation " +
        "in a callout and let one build release some waiters and not others");
    }

    [TestMethod]
    public async Task CompletingFromSeveralThreadsAtOnceNeitherThrowsNorReopens()
    {
      var ready = new TtsReadiness();
      var waiter = ready.ReadyAsync;

      // Half succeed, half fail, in whatever order the pool runs them. SetResult on a task somebody already completed
      // would throw inside cleanup code, which is the last place an audio failure wants to be reported from.
      var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() => ready.Complete(i % 2 == 0))).ToArray();

      await Task.WhenAll(writers);

      Assert.IsTrue(waiter.IsCompleted, "every completion path releases");
      Assert.IsTrue(ready.HasEngine, "and one engine among them is enough to say there is an engine");
    }

    /* Never a blocking Wait: a gate that hangs should fail this test, not take the run down with it. */
    private static async Task<bool> ReleasedInTime(Task waiter) =>
      await Task.WhenAny(waiter, Task.Delay(ReleaseBudgetMs)) == waiter;
  }
}

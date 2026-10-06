using System.Collections.Concurrent;

using EQLogParser;

namespace EQLogParser;

/*
 * LogProcessor hands PreLineParser three identity callbacks on every non-chat line, and one of them is a
 * method-group conversion the compiler cannot cache: `PlayerRegistry.Instance.AddMerc` allocates a fresh delegate
 * per call (measured 64 bytes a line on .NET 10 Release; a non-capturing lambda and a static method group, which
 * sit beside it on the same line, both cost 0 because the compiler caches them). So that one is bound once in the
 * constructor into a field — which means the wiring is now two steps from where it used to be written, and the
 * failure mode of a mistake there is silent: a merc callback that never fires leaves every mercenary in the log
 * reading Unknown instead of Merc, with nothing thrown.
 *
 * What is asserted here is therefore the WIRING, both callbacks at once (the cached field and the lambda), by
 * running lines through the real consumer loop the app uses.
 *
 * What is deliberately NOT asserted: the allocation itself. A line's legitimate work — the substring for the
 * action, Split(' '), interning — allocates on the order of a kilobyte, so a 64-byte delegate cannot be separated
 * from it by GC counters, and a threshold that happens to pass today would be a coin flip on another machine. The
 * number is a measurement written into the comment at LogProcessor._addMerc and into docs/DesignNotes.md →
 * "Allocation traffic is not retained memory: the delegate on every line", which is where this repo keeps costs that
 * are measured rather than asserted (including the sibling measurements in that table: a non-capturing lambda and a
 * static method group cost 0, so caching them would have been ceremony). Repeated conversions in a loop,
 * GC.GetTotalAllocatedBytes around each variant, Release build.
 */
[TestClass]
[DoNotParallelize]
public class LogProcessorIdentityCallbackTest
{
    // The processor needs both sinks; neither is what this test is about.
    private sealed class NullSinks : IChatSink, ITriggerHook
    {
        public void Init()
        {
        }

        public void Add(ChatType chat)
        {
        }

        public void CheckQuickShare(ChatType chat, string action, double beginTime)
        {
        }
    }

    [TestInitialize]
    public void Setup() => PlayerRegistry.Instance.Clear();

    [TestCleanup]
    public void Cleanup() => PlayerRegistry.Instance.Clear();

    /*
     * Feed lines through the consumer loop and drain it the way LogReader does. Returns nothing; the assertions
     * are about what the registry learned, which is the only visible edge of the callbacks.
     */
    private static void Process(params string[] lines)
    {
        using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>());
        using var processor = new LogProcessor("callbacks.txt", new NullSinks(), new NullSinks());
        processor.LinkTo(items);

        foreach (var line in lines)
        {
            var dt = DateUtil.ParseStandardDate(line);
            Assert.AreNotEqual(DateTime.MinValue, dt, $"fixture line has no standard timestamp: {line}");
            items.Add(new LogReaderItem(line, DateUtil.ToDotNetSeconds(dt), false));
        }

        items.CompleteAdding();

        /*
         * Drained before asserting, because the callbacks fire on the consumer thread and Dispose is allowed to race it
         * (that is what LogProcessor.Completion exists for — the headless harness waits for the same task).
         */
        var completion = processor.Completion;
        Assert.IsNotNull(completion);
        Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(60)), "consumer did not drain");
    }

    [TestMethod]
    public void AMercenaryJoinLineStillReachesTheRegistry()
    {
        /*
         * A group join whose name is NOT player-shaped is a mercenary — that is the branch, and it is reached only
         * through the delegate. "A loyal lion" fails IsPossiblePlayerName on its spaces (and its article), which is
         * exactly the shape the log writes for a pet-like hireling.
         */
        Process("[Sun Apr 26 18:41:01 2026] A loyal lion has joined the group.");

        Assert.IsTrue(PlayerRegistry.Instance.IsMerc("A loyal lion"),
          "the merc join line named its hireling but the processor's AddMerc callback never reached the registry");
    }

    [TestMethod]
    public void ARaidJoinLineStillVerifiesThePlayer()
    {
        // The sibling callback on the same call — a lambda over AddVerifiedPlayer — must still fire too, so the
        // pair is asserted together rather than one at a time.
        //
        // NO "has" IN THIS LINE, and that is the log's own shape, not a typo. Across all twelve local captures EQ writes
        // "Pickter joined the raid." 1,151 times and "has joined the raid." zero times (the GROUP line does carry it:
        // "Ammeren has joined the group."). That matters more than tidiness here: the raid branch tests the WHOLE prefix
        // — isPossiblePlayerName(action, action.Length - 17) — and the name test refuses anything containing a space, so a
        // "has" would enter the branch, fail the check and claim the line for nothing. Asserting the tidier sentence would
        // pin a shape the game never prints while leaving the real one untested.
        Process("[Sun Apr 26 18:41:02 2026] Bithika joined the raid.");

        Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Bithika"),
          "the raid join line named its raider but the processor's AddVerifiedPlayer callback never reached the registry");
    }

    [TestMethod]
    public void TheTwoCallbacksAreDecidedByTheNameNotByOrder()
    {
        // One consumer, both kinds, in one stream: a merc and a raider must not trade places. (The merc branch is
        // an else on the player-shape test, so a name that satisfies it can never be recorded as a hireling.)
        Process(
            "[Sun Apr 26 18:41:03 2026] Bithika joined the raid.",
            "[Sun Apr 26 18:41:04 2026] A loyal lion has joined the group.");

        Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Bithika"));
        Assert.IsFalse(PlayerRegistry.Instance.IsMerc("Bithika"), "a player-shaped name went down the merc branch");
        Assert.IsTrue(PlayerRegistry.Instance.IsMerc("A loyal lion"));
        Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("A loyal lion"), "the hireling was verified as a raider");
    }
}

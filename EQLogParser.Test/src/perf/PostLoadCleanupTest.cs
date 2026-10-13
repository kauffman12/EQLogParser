using EQLogParser;

namespace EQLogParser;

/*
 * The hold the fight list's loading band waits on (PostLoadCleanup). The window-side behaviour — a bar that stays up while a collection
 * runs, and comes down at once when none does — belongs to FightTable's WPF tests, which run only on Windows; what is worth asserting here
 * is the contract both windows share, since the two failure modes are a bar parked forever and a bar dropped mid-collection.
 *
 * Counted, not a flag, and that is the point of the overlap case: the UI stays live through a pass, so Clear All can mark while a load's
 * collection is still running, and a single bool let whichever `finally` ran first take the other pass's bar down under the stop-the-world.
 */
[TestClass]
public class PostLoadCleanupTest
{
  [TestInitialize]
  public void Init() => PostLoadCleanup.Reset();

  [TestCleanup]
  public void CleanUp() => PostLoadCleanup.Reset();

  [TestMethod]
  public void AMarkedCleanupIsHeldAndAClearedOneIsNot()
  {
    Assert.IsFalse(PostLoadCleanup.Owes, "a session nobody loaded does not hold anyone's band");

    PostLoadCleanup.Mark();
    Assert.IsTrue(PostLoadCleanup.Owes, "a collection in flight must keep the loading band up (the pause needs its bar)");

    // The finally MainWindow pays in. A hold that outlived it would park a progress bar over an answered grid.
    PostLoadCleanup.Clear();
    Assert.IsFalse(PostLoadCleanup.Owes, "an ended cleanup must not strand the band over an answered grid");
  }

  [TestMethod]
  public void TwoOverlappingPassesReleaseOnlyTheirOwnHold()
  {
    // A load's collector running while Clear All marks: two passes, one band, and neither pass may cancel the other's wait.
    PostLoadCleanup.Mark();
    PostLoadCleanup.Mark();
    Assert.IsTrue(PostLoadCleanup.Owes);

    PostLoadCleanup.Clear();
    Assert.IsTrue(PostLoadCleanup.Owes, "the first pass to finish must not take the bar down while the second still runs");

    PostLoadCleanup.Clear();
    Assert.IsFalse(PostLoadCleanup.Owes, "the last pass out releases the band");
  }

  [TestMethod]
  public void ACaptureDoorEndsEveryOutstandingHoldAndALateReleaseCannotDig()
  {
    PostLoadCleanup.Mark();
    PostLoadCleanup.Mark();

    // OpenLogFile/CloseLogFile: a debt never crosses a capture, including one held by a pass that cannot be cancelled.
    PostLoadCleanup.Reset();
    Assert.IsFalse(PostLoadCleanup.Owes, "the next session must not hold a band for the previous capture's cleanup");

    // The abandoned pass still runs its finally. Clamped, or a negative count would swallow the NEXT load's hold.
    PostLoadCleanup.Clear();
    PostLoadCleanup.Clear();
    Assert.IsFalse(PostLoadCleanup.Owes);

    PostLoadCleanup.Mark();
    Assert.IsTrue(PostLoadCleanup.Owes, "a fresh capture's cleanup still holds after earlier releases drove the count at the floor");
  }
}

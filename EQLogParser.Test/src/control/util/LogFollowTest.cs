using EQLogParser;

namespace EQLogParser;

/*
 * A rename does not shrink the file an open handle follows. Identity asks whether the NAME changed hands; it does not
 * say that the replacement's CONTENT is new. LogFollowPrefixTest checks where to resume after this decision.
 */
[TestClass]
public class LogFollowTest
{
  private static readonly long OldFileBorn = new DateTime(2026, 11, 7, 18, 30, 0).Ticks;
  private static readonly long NewFileBorn = new DateTime(2026, 11, 8, 0, 0, 0).Ticks;

  [TestMethod]
  public void MissingNameDoesNotTearDownAnOpenHandle()
  {
    Assert.AreEqual(LogFollowDecision.KeepFollowing, LogFollow.DecideFor(OldFileBorn, null, 5_000, 5_000));
  }

  [TestMethod]
  public void RenamedFileNeedsAReopenEvenWhenOldHandleGrows()
  {
    // The length is of the renamed-away file, NOT the replacement. If this were shorter than 5,000 the shrink rule
    // could answer instead and a test would pass with the identity rule removed.
    Assert.AreEqual(LogFollowDecision.Reopen, LogFollow.DecideFor(OldFileBorn, NewFileBorn, 5_000, 8_000));
  }

  [TestMethod]
  public void SameFileGrowingNeedsNoReopen()
  {
    Assert.AreEqual(LogFollowDecision.KeepFollowing, LogFollow.DecideFor(OldFileBorn, OldFileBorn, 5_000, 9_400));
  }

  [TestMethod]
  public void UnchangedFileAfterReadExceptionNeedsNoRotation()
  {
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
      LogFollow.DecideFor(OldFileBorn, OldFileBorn, 40_000_000, 40_000_000));
  }

  [TestMethod]
  public void ShorterFileRewrittenInPlaceNeedsAReopen()
  {
    Assert.AreEqual(LogFollowDecision.Reopen, LogFollow.DecideFor(OldFileBorn, OldFileBorn, 40_000_000, 1_200));
  }

  [TestMethod]
  public void UnknownIdentityBuysNoDecision()
  {
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
      LogFollow.DecideFor(OldFileBorn, LogFollow.UnknownIdentity, 5_000, 6_000));
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
      LogFollow.DecideFor(LogFollow.UnknownIdentity, NewFileBorn, 5_000, 6_000));
  }

  [TestMethod]
  public void AShortenedOpenHandleStillReopensWhenIdentityCannotBeRead()
  {
    Assert.AreEqual(LogFollowDecision.Reopen,
      LogFollow.DecideFor(LogFollow.UnknownIdentity, LogFollow.UnknownIdentity, 5_000, 200));
  }

  [TestMethod]
  public void UnrecordedIdentityBuysNoDecision()
  {
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
      LogFollow.DecideFor(LogFollow.NoIdentity, NewFileBorn, 0, 6_000));
  }

  [TestMethod]
  public void RolloverAdvancesFromOldHandleToNewNameInOnePoll()
  {
    var followedLength = 5_000L;
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
      LogFollow.DecideFor(OldFileBorn, OldFileBorn, followedLength, 6_000));

    followedLength = 6_000;
    Assert.AreEqual(LogFollowDecision.Reopen,
      LogFollow.DecideFor(OldFileBorn, NewFileBorn, followedLength, followedLength + 900));

    Assert.AreEqual(LogFollowDecision.KeepFollowing,
      LogFollow.DecideFor(NewFileBorn, NewFileBorn, 140, 2_300));
  }
}

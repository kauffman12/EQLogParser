using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;

namespace EQLogParser;

/*
 * Who owns the name a live tail is watching, in the one place that decides it
 * (EQLogParser.Core/src/control/util/LogFollow.cs).
 *
 * A reader monitoring `eqlog_Kizant_xegony.txt` holds a handle on a FILE. When that file is renamed away — EQ's own
 * midnight roll, or this app's Log Management moving the file out and recreating it — reading continues from the
 * renamed-away file, which nothing writes to any more, while the new day's lines accumulate behind the name unread. The
 * length test that used to be the only fallback cannot fire there: a complete file never shrinks. So the rollover rode
 * entirely on FileSystemWatcher delivering Renamed, and the watcher was configured to flood its default 8 KB buffer with
 * append notifications nobody reads — an overflowed watcher stops reporting, and the event it then eats is the one that
 * mattered. Reported as: play past midnight and chat freezes on yesterday until the app restarts.
 *
 * THE DISCIPLINE OF THESE TESTS: each case says whether the reader should read from line one or keep its place, because
 * the two failure modes are not symmetric. Restarting on a file already counted feeds the same damage into the fact
 * tables twice — visible as inflated boards — while not restarting on a genuinely new file loses a whole day and says
 * nothing. So "same content → never restart" is asserted as hard as "new content → always restart", and every case names
 * which of the two it is guarding.
 */
[TestClass]
public class LogFollowTest
{
  // Creation stamps standing in for three different file objects, as ticks would arrive from FileInfo.CreationTimeUtc.
  private static readonly DateTime OldFileBorn = new(2026, 11, 7, 18, 30, 0);
  private static readonly DateTime NewDayFileBorn = new(2026, 11, 8, 0, 0, 0);

  private static long Followed() => OldFileBorn.Ticks;

  [TestMethod]
  public void ANameHoldingNothingIsAWaitNotAReopen()
  {
    // Log Management moves the file out, then recreates it: in that gap there is nothing to read and nothing to lose.
    Assert.AreEqual(LogFollowDecision.WaitForFile, LogFollow.DecideFor(Followed(), null, 5_000, 0));
  }

  /*
   * The length arguments here are load-bearing. The poll hands DecideFor the length of the file it is FOLLOWING, and a
   * renamed-away file never shrinks — so this case is written with that length grown, which leaves the identity question as
   * the only rule that can answer. (Mutation-checked: with a small `currentLength` here the shorten rule answers instead and
   * deleting the identity rule entirely leaves this suite green.)
   */
  [TestMethod]
  public void ADifferentFileWearingTheNameIsReadFromTheStart()
  {
    var decision = LogFollow.DecideFor(Followed(), NewDayFileBorn.Ticks, 5_000, 8_000);

    Assert.AreEqual(LogFollowDecision.RestartFromStart, decision,
                    "the new day's file has never reached a processor: this is the rollover that used to be missed");
    Assert.AreEqual(0, LogFollow.ResumeOffsetFor(decision, 120), "the head of the new day must not be skipped");
  }

  [TestMethod]
  public void TheSameFileThatGrewIsLeftAlone()
  {
    var decision = LogFollow.DecideFor(Followed(), OldFileBorn.Ticks, 5_000, 9_400);

    Assert.AreEqual(LogFollowDecision.KeepFollowing, decision, "growth is the normal case, not a rollover");
    Assert.AreEqual(9_400, LogFollow.ResumeOffsetFor(decision, 9_400));
  }

  /*
   * The double-count guard. A recovery reopen after the stream threw, over a file that never changed, must go back to the
   * tail. Reading it from line one would re-ingest lines already in the fact tables — the kind of bug that shows up as
   * boards reading high with nothing wrong in the log.
   */
  [TestMethod]
  public void RecoveryOnAnUnchangedFileNeverReReadsTheCapture()
  {
    var decision = LogFollow.DecideFor(Followed(), OldFileBorn.Ticks, 40_000_000, 40_000_000);

    Assert.AreEqual(LogFollowDecision.KeepFollowing, decision);
    Assert.AreEqual(40_000_000, LogFollow.ResumeOffsetFor(decision, 40_000_000),
                    "a recovery resumes at the tail rather than replaying a capture");
  }

  [TestMethod]
  public void AFileRewrittenShorterIsReadFromTheStart()
  {
    // Same name, same creation stamp, shorter than the last look: truncated in place under us.
    var decision = LogFollow.DecideFor(Followed(), OldFileBorn.Ticks, 40_000_000, 1_200);

    Assert.AreEqual(LogFollowDecision.RestartFromStart, decision);
    Assert.AreEqual(0, LogFollow.ResumeOffsetFor(decision, 1_200));
  }

  /* No evidence either way must not move the read position: an unknown identity is a shrug, not a rollover. */
  [TestMethod]
  public void AnUnknownIdentityBuysNoDecision()
  {
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
                    LogFollow.DecideFor(Followed(), LogFollow.UnknownIdentity, 5_000, 6_000),
                    "the name could not be asked — keep tailing rather than assume a new file");
    Assert.AreEqual(LogFollowDecision.KeepFollowing,
                    LogFollow.DecideFor(LogFollow.UnknownIdentity, NewDayFileBorn.Ticks, 5_000, 6_000));
  }

  [TestMethod]
  public void AnUnrecordedIdentityNeverRestartsAnything()
  {
    // A reader that has not stamped the file it opened yet has no basis to declare the content unread.
    Assert.AreEqual(LogFollowDecision.KeepFollowing, LogFollow.DecideFor(LogFollow.NoIdentity, NewDayFileBorn.Ticks, 0, 6_000));
  }

  /*
   * The shape of a full rollover as the loop sees it, in order: tail the old file, watch the name change hands, reopen at
   * zero. Asserted as a sequence because the bug was a state that never advanced — a decision table read one row at a
   * time can look right while the loop keeps its stale handle forever.
   */
  [TestMethod]
  public void ARolloverAdvancesFromTailToNewFileInOnePass()
  {
    var followedBorn = Followed();
    long followedLength = 5_000;

    // Pass 1: same file, grown by the live game. Nothing to do, and the length sample moves up with it.
    Assert.AreEqual(LogFollowDecision.KeepFollowing, LogFollow.DecideFor(followedBorn, OldFileBorn.Ticks, followedLength, 6_000));
    followedLength = 6_000;

    // Pass 2: EQ has renamed the old file away and opened a fresh one under the name. The handle still reports the old
    // file's length — it only grew — so nothing but the name's identity says anything changed.
    var rollover = LogFollow.DecideFor(followedBorn, NewDayFileBorn.Ticks, followedLength, followedLength + 900);
    Assert.AreEqual(LogFollowDecision.RestartFromStart, rollover,
                    "a complete file that never shrinks is exactly why length cannot see a rename");

    // Reopen: the reader now belongs to the new file, and a further pass on it is quiet again.
    var reopenedLength = LogFollow.ResumeOffsetFor(rollover, 140);
    Assert.AreEqual(0, reopenedLength, "the head of the new day must not be skipped (a seek-to-end lost it)");

    followedBorn = NewDayFileBorn.Ticks;
    followedLength = 140;
    Assert.AreEqual(LogFollowDecision.KeepFollowing, LogFollow.DecideFor(followedBorn, NewDayFileBorn.Ticks, followedLength, 2_300));
  }
}

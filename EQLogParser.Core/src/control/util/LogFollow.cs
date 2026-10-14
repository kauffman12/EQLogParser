using System;

namespace EQLogParser
{
  /*
   * What a live tail does when the name it is watching stops being the file it was reading.
   */
  internal enum LogFollowDecision
  {
    /* The name holds nothing at this moment: wait for a file to appear before opening anything. */
    WaitForFile,

    /* The name now holds content this reader has never handed to a processor: read it from the first line. */
    RestartFromStart,

    /* Nothing to act on — keep tailing the stream that is already open (and, on a recovery reopen, resume at the tail). */
    KeepFollowing
  }

  /*
   * Following a monitored log across a rollover: EQ's own date roll, and this app's Log Management move-and-recreate,
   * both take the name out from under an open reader. The rules live here rather than inline in LogReader because every
   * one of them is a decision about which bytes are safe to read, and none of them can be reached by a fixture on this
   * machine — they need a live tail across midnight (EQLogParser.Test/src/control/util/LogFollowTest.cs).
   *
   * The hole this closes: a reader watching `eqlog_Kizant_xegony.txt` holds a handle on a FILE, not on a name. When the
   * file is renamed away and a fresh one takes its place, reading continues happily from the renamed-away file — which
   * nothing writes to any more — while the new day's lines pile up unread behind it. The length test that used to be the
   * only fallback (`_fs.Length < position`) cannot fire there: the followed file is complete and never shrinks. So the
   * rollover depended entirely on `FileSystemWatcher` delivering a Renamed event, and that is the notification most
   * likely to be missing: the watcher ran with the default filter — which queues a LastWrite notification for every
   * append, i.e. thousands of events a second in a raid log that nobody reads — into the default 8 KB buffer, and an
   * overflowed watcher stops reporting for the rest of its life. `Documents\EverQuest` being OneDrive-synced or on a
   * network share makes dropped events routine. The symptom is exactly what was reported: chat and boards freeze at
   * "yesterday" and only a restart clears them (docs/DesignNotes.md → "Following the live log across a midnight
   * rollover"). Asking the name who it is, once per poll, needs no event to be lucky.
   *
   * What each answer costs if it is wrong, which is why they are asymmetric:
   *
   * - Restarting on a file that really is new reads lines nobody has counted yet. That is the whole point, and it cannot
   *   double count: this reader never opened that file.
   * - Restarting on the SAME file re-feeds lines already in the fact tables, which counts damage twice. So a plain
   *   recovery — the stream threw while the file sat there unchanged — goes back to the tail and accepts the gap
   *   instead, which is what the shipped code has always done. The cheap creation-time id can lie only in the narrow
   *   FAT/reserved-timestamp case where a new file inherits the same second; that degrades to the tail (a few lines
   *   missing), never to a double count, and the watcher stays wired up for promptness on top of this.
   */
  internal static class LogFollow
  {
    /* We could not ask who owns the name (transient IO error). Not evidence of anything: keep tailing. */
    public const long UnknownIdentity = -1;

    /* An identity that has never been recorded behaves like "unknown" — nothing may be restarted against it. */
    public const long NoIdentity = 0;

    /*
     * `followedCreatedUtcTicks` — creation stamp of the file this reader has been reading.
     * `nameCreatedUtcTicks`  — creation stamp of whatever stands at that name now, or null when nothing does.
     * `followedLength`       — the followed file's length the last time the tail looked at it.
     * `currentLength`        — the length NOW of whatever file was opened at that name. It can only decide anything when
     *                          the identity matches (a truncate-in-place keeps the same stamp and leaves a shorter tail);
     *                          on a rename the poll hands in the RENAMED-AWAY file's length, which is complete and never
     *                          shrinks, and the identity answer has to carry the case by itself. That is the whole reason
     *                          the question is asked twice, and why a test that lets the length rule answer a rollover is
     *                          not testing this file.
     */
    internal static LogFollowDecision DecideFor(long followedCreatedUtcTicks, long? nameCreatedUtcTicks,
      long followedLength, long currentLength)
    {
      if (nameCreatedUtcTicks is null)
      {
        return LogFollowDecision.WaitForFile;
      }

      if (IsUnknown(followedCreatedUtcTicks) || IsUnknown(nameCreatedUtcTicks.Value))
      {
        return LogFollowDecision.KeepFollowing;
      }

      if (followedCreatedUtcTicks != nameCreatedUtcTicks.Value)
      {
        return LogFollowDecision.RestartFromStart;
      }

      // Same stamp, same name, shorter than the last look: something rewrote it under us (a truncate-to-restart keeps
      // the file object), and the head of what is there now has never been read.
      return currentLength < followedLength ? LogFollowDecision.RestartFromStart : LogFollowDecision.KeepFollowing;
    }

    /*
     * Where a reopened stream starts. From line one when `DecideFor` proved the content is new to this reader; at the
     * tail otherwise, because an exception recovery on an unchanged file must not re-ingest it.
     */
    internal static long ResumeOffsetFor(LogFollowDecision decision, long reopenedLength) =>
      decision is LogFollowDecision.RestartFromStart ? 0 : reopenedLength;

    private static bool IsUnknown(long createdUtcTicks) =>
      createdUtcTicks is NoIdentity or UnknownIdentity;
  }
}

namespace EQLogParser
{
  /* The name of a monitored file can change hands while the reader's old handle remains valid. */
  internal enum LogFollowDecision
  {
    Reopen,
    KeepFollowing
  }

  /*
   * A file's creation stamp is a reason to REOPEN, not a proof that its lines are new. A sync client can atomically
   * replace a log with a copy containing every line already ingested. The reader compares its drained old handle to the
   * new one (LogFollowPrefix) before deciding where to resume. Conversely, after a rename the old handle stays on the
   * renamed file and never shrinks, so comparing lengths alone cannot detect that the name now belongs to a different
   * file. See docs/DesignNotes.md → "Following the live log across a midnight rollover".
   *
   * A missing name or an unreadable creation stamp is not, by itself, evidence to tear down a working stream. A
   * shortened open handle still requires a reopen even if its creation stamp is unavailable. The watcher's
   * Deleted/Renamed notification can also ask for a reopen and wait for a file to reappear.
   */
  internal static class LogFollow
  {
    public const long UnknownIdentity = -1;
    public const long NoIdentity = 0;

    internal static LogFollowDecision DecideFor(long followedCreatedUtcTicks, long? nameCreatedUtcTicks,
      long followedLength, long currentLength)
    {
      if (nameCreatedUtcTicks is null)
      {
        return LogFollowDecision.KeepFollowing;
      }

      // Shortening is evidence from the open handle itself, even if the filesystem cannot answer creation-time
      // questions. Do not turn an unknown identity into a veto of the old truncate fallback.
      if (currentLength < followedLength)
      {
        return LogFollowDecision.Reopen;
      }

      if (followedCreatedUtcTicks is NoIdentity or UnknownIdentity ||
          nameCreatedUtcTicks.Value is NoIdentity or UnknownIdentity)
      {
        return LogFollowDecision.KeepFollowing;
      }

      if (followedCreatedUtcTicks != nameCreatedUtcTicks.Value)
      {
        return LogFollowDecision.Reopen;
      }

      return LogFollowDecision.KeepFollowing;
    }
  }
}

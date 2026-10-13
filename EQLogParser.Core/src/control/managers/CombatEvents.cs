using System;

namespace EQLogParser
{
  /*
   * The app-wide "what is on screen stopped describing the current log" signal: a new log opened, the server changed,
   * or the capture was cleared. Board and chart handlers blank their grids in answer to it.
   *
   * LifecycleManager.Clear is the sole raiser. Subscribers release their last answer rather than retaining data from
   * a closed capture. See docs/DesignNotes.md → "The legacy engine is deleted".
   */
  internal static class CombatEvents
  {
    /// <summary>True when the server changed as well, which is also when cached strings are dropped.</summary>
    internal static event Action<bool> ActiveDataCleared;

    internal static void FireActiveDataCleared(bool serverChanged) => ActiveDataCleared?.Invoke(serverChanged);
  }
}

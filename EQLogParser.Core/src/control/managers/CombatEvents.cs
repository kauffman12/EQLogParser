using System;

namespace EQLogParser
{
  /*
   * The app-wide "what is on screen stopped describing the current log" signal: a new log opened, the server changed,
   * or the capture was cleared. Board and chart handlers blank their grids in answer to it.
   *
   * It used to live on FightManager, which meant that seven surfaces — three summaries, four charts, the fight table —
   * depended on the legacy per-line pipeline for one piece of information that has nothing to do with how fights are
   * produced. Moving it here is the first cut in docs/legacy-replacement-map.md: after this those files no longer name
   * FightManager at all, so deleting that class becomes a change they cannot break and not a change they must be
   * rewritten for. The mirror will raise the same event when its capture resets (it does not yet — while both lists run
   * side by side, legacy's clear is the one that means "start over", and firing twice would blank a board that the other
   * pipeline is about to refill).
   */
  internal static class CombatEvents
  {
    /// <summary>True when the server changed as well, which is also when cached strings are dropped.</summary>
    internal static event Action<bool> ActiveDataCleared;

    internal static void FireActiveDataCleared(bool serverChanged) => ActiveDataCleared?.Invoke(serverChanged);
  }
}

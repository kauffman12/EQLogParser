using log4net;
using System;
using System.Diagnostics;
using System.Reflection;

#nullable enable annotations

namespace EQLogParser;

/*
 * Holds faults on timers, work items and context-less async continuations, including failures while reporting them.
 * The first fault per site and kind is logged; repeats are counted by RepeatCap. Parser and derive guards stay separate.
 * docs/DesignNotes.md → "Nothing escapes a thread that has no net, and a fault speaks once".
 */
internal static class ThreadBoundary
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  /// <summary>Run work without allowing its fault or a reporting failure to escape; site names the attempted operation.</summary>
  internal static void Guard(string site, Action work)
  {
    try
    {
      work();
    }
    catch (Exception ex)
    {
      Note(site, ex);
    }
  }

  /// <summary>Report an already-caught fault once per site and kind without allowing reporting failures to escape.</summary>
  internal static void Note(string site, Exception ex)
  {
    try
    {
      if (RepeatCap.SpeaksOnce($"boundary {site} {ex.GetType().Name}"))
        Log.Error($"{site}: faulted on a thread with no safety net and was held here "
          + $"(this kind of fault repeats silently): {ex.Message}", ex);
    }
    catch (Exception reporting)
    {
      try
      {
        Trace.TraceError($"Could not report {ex.GetType().Name} at {site}: {reporting}");
      }
      catch (Exception)
      {
        // Trace listeners are extensible too; a last-resort reporter cannot depend on another reporter.
      }
    }
  }
}

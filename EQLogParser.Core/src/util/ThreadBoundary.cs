using log4net;
using System;
using System.Reflection;

#nullable enable annotations

namespace EQLogParser;

/*
 * The last line of a thread that has no safety net.
 *
 * An exception on the dispatcher is survivable — App's DispatcherUnhandledException logs it and sets Handled — and one inside a Task
 * reaches TaskScheduler.UnobservedTaskException, eventually, whenever the garbage collector gets around to finalising that task,
 * which is not a diagnosis anyone reads while a log is still loading. A timer callback (System.Threading.Timer, or a
 * System.Timers.Timer Elapsed handler), a ThreadPool work item and an async void continuation have no net at all: the runtime takes the
 * exception to AppDomain.UnhandledException with IsTerminating = true and the process ends mid-sentence. For this application that means
 * a raid night's parse and every identity verdict that had not yet reached identity-priors.txt.
 *
 * So nothing may escape one of those bodies, and this is the one shape they all handle it with: log instead of propagating. The FIRST
 * fault at a site speaks at Error with its stack — a thing broke, and this tick's work is lost — and everything behind it is counted by
 * RepeatCap on site + exception type rather than printed, because a timer that fails once fails every interval for the rest of the night:
 * the save timer is on a period, the records store fires every 1.5 s, the follow poll every 200 ms, and an uncapped Error there is 40,000
 * copies of one sentence with the useful line buried at the top of them (see RepeatCap for why the cap never re-opens on a timer).
 *
 * Not for the parse path: the four line parsers each already own a per-line catch with their own capped voice, and the derive pass has its
 * retry ladder. This is for the bodies that had neither and would have taken the process with them.
 */
internal static class ThreadBoundary
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  /// <summary>
  /// Run work that must not leave this thread. `site` names what was attempted in the words an operator would recognise ("registry
  /// save timer", "records-updated subscriber"), because it is the only thing on the line that says which feature just lost a tick.
  /// </summary>
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

  /// <summary>
  /// The capped voice for a catch block that already exists. Use it instead of Log.Error in anything a timer or a poll can re-enter:
  /// one line per kind of fault, then silence with the count kept in RepeatCap (RepeatCap.Silenced is the session total).
  /// </summary>
  internal static void Note(string site, Exception ex)
  {
    if (RepeatCap.SpeaksOnce($"boundary {site} {ex.GetType().Name}"))
      Log.Error($"{site}: faulted on a thread with no safety net and was held here (this kind of fault repeats silently): {ex.Message}", ex);
  }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EQLogParser
{
  /*
   * Whether the UI thread was blocked or busy while a stall episode lasted, taken from what the operating system already tracks.
   *
   * A stall line can prove the thread did not answer for 46 seconds and that none of our measured passes were inside it, which leaves two
   * explanations pointing at completely different fixes. Either the thread was *running* — draining framework work nobody times here:
   * layout, binding refresh, rasterizing a window — or it was *waiting*, held by something outside itself: a lock another thread owns, the
   * render thread, a driver call. From inside the process those two look identical ("no code of ours executed"), and no span can separate
   * them because in the second case the span would have to be around code we do not have.
   *
   * The kernel does track it, per thread, and reading it costs microseconds: a thread is either Run or Wait, and a wait carries a reason.
   * So the watchdog samples it while an episode lasts, at its own 200 ms cadence, and the closing line says which of the two it was in
   * numbers a reader can weigh — 230/230 samples waiting with 40 ms of CPU is a block; 5/230 waiting with 45 s of CPU is work. That
   * distinction decides whether to go looking for a lock or for a slow pass, which is why it is worth a kernel call.
   *
   * Everything here runs on the watchdog's pool thread except CaptureThreadId, and nothing may throw: an exception while reporting on a
   * frozen interface would replace a symptom with a crash. If the thread cannot be found, or the process does not permit the read, the
   * episode reports "n/a" and says nothing more.
   */
  internal static class UiThreadProbe
  {
    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    private static readonly object _gate = new();

    /* The thread being watched during the current episode, and what has been seen of it. */
    private static Process _process;
    private static ProcessThread _thread;
    private static int _watchedId;
    private static long _cpuBaseMs;
    private static int _waitSamples;
    private static int _runSamples;

    /*
     * Why an episode could not be watched, when it could not. "n/a" on a stall line is two different complaints - the id was never
     * captured, or the thread list does not contain it - and they mean different things to whoever reads it, so the reason is kept rather
     * than flattened into the same three characters.
     */
    private static string _note;

    /* The reason most often seen while waiting: one wait dominates an episode, and a list of reasons would be read as uncertainty. */
    private static readonly Dictionary<string, int> _reasons = [];

    /*
     * The operating system's id for the calling thread, which is what ProcessThread is keyed by — Dispatcher.ManagedThreadId is not, and
     * an id from a pool thread would watch the wrong thread. Call once on the UI thread; returns 0 only where the call is unavailable.
     */
    internal static int CaptureThreadId()
    {
      try
      {
        return GetCurrentThreadId();
      }
      catch (Exception ex)
      {
        /* A build or platform without it: the episodes report n/a instead, and every other number stays. */
        _note = $"GetCurrentThreadId failed: {ex.GetType().Name}";
        return 0;
      }
    }

    /* Opens an episode on the given thread: tallies start empty and the CPU clock is read so the closing line can report a delta. */
    internal static void BeginEpisode(int threadId)
    {
      lock (_gate)
      {
        _note = null;
        _watchedId = threadId;
        _thread = FindThread(threadId);

        if (_thread is null)
        {
          _note = threadId <= 0 ? "no thread id was captured" : $"thread {threadId} is not in this process";
        }

        _cpuBaseMs = _thread is null ? 0 : ElapsedMs(_thread);
        _waitSamples = 0;
        _runSamples = 0;
        _reasons.Clear();
      }
    }

    /* One look, taken each time the watchdog notices a beat still missing. */
    internal static void SampleEpisode(int threadId)
    {
      lock (_gate)
      {
        try
        {
          if (threadId != _watchedId)
          {
            return;
          }

          /*
           * Enumerated again for every sample rather than cached: a ProcessThread's properties are a snapshot taken when it was read, so the
           * one held from the start of an episode would keep answering with the state the thread had back then — which is exactly not the
           * question. Walking this process's own thread list costs microseconds, and only during an episode.
           */
          _thread = FindThread(threadId);

          if (_thread is null)
          {
            return;
          }

          if (_thread.ThreadState == System.Diagnostics.ThreadState.Wait)
          {
            _waitSamples++;

            var reason = WaitReasonText(_thread);
            _reasons.TryGetValue(reason, out var seen);
            _reasons[reason] = seen + 1;
          }
          else
          {
            _runSamples++;
          }
        }
        catch (Exception ex)
        {
          /*
           * Never let this reach the watchdog's pool loop: its own handler stops the monitor when a poll throws, which would trade a
           * measured stall for no stall detection at all. A sample is a diagnostic; losing one is cheaper than that.
           */
          _note = $"sampling failed: {ex.GetType().Name}";
        }
      }
    }

    /*
     * The episode as it belongs on a stall line: which of the two it was, in samples out of samples, with the CPU the thread actually
     * burned. "n/a" when nothing could be read — an absence the reader should see rather than a guess.
     */
    internal static string EndEpisode()
    {
      lock (_gate)
      {
        var samples = _waitSamples + _runSamples;

        if (samples == 0)
        {
          return _note is null ? "ui thread: n/a" : $"ui thread: n/a ({_note})";
        }

        var builder = new StringBuilder();
        builder.Append("ui thread: ");

        if (_waitSamples == 0)
        {
          builder.Append("running");
        }
        else if (_runSamples == 0)
        {
          builder.Append("blocked");
        }
        else
        {
          builder.Append(_waitSamples > _runSamples ? "mostly waiting" : "mostly running");
        }

        builder.Append(' ').Append(_waitSamples).Append('/').Append(samples).Append(" samples waiting");

        if (_reasons.Count > 0)
        {
          builder.Append(" (").Append(DominantReason()).Append(')');
        }

        var cpuMs = _thread is null ? 0 : ElapsedMs(_thread) - _cpuBaseMs;

        return builder.Append(", cpu ").Append(cpuMs).Append(" ms").ToString();
      }
    }

    /* Wiping tallies when a watch stops keeps an interrupted episode from bleeding into the next one. */
    internal static void Reset()
    {
      lock (_gate)
      {
        _thread = null;
        _watchedId = 0;
        _waitSamples = 0;
        _runSamples = 0;
        _note = null;
        _reasons.Clear();
      }
    }

    /*
     * Whether this id names a thread of this process right now. Split out because a probe that reports nothing has two independent halves
     * that can fail — capturing the id, and finding it in the process's own thread list — and a test has to be able to say which one broke
     * rather than observe the same blank twice.
     */
    internal static bool IsWatchable(int threadId) => FindThread(threadId) is not null;

    private static string DominantReason()
    {
      string best = null;
      var seen = 0;

      foreach (var pair in _reasons)
      {
        if (pair.Value > seen)
        {
          seen = pair.Value;
          best = pair.Key;
        }
      }

      /* Ties and repeats are possible in a thread flapping between two waits; the count tells the reader how sure it is. */
      return _reasons.Count == 1 ? best : $"{best} x{seen}";
    }

    private static ProcessThread FindThread(int threadId)
    {
      if (threadId <= 0)
      {
        return null;
      }

      try
      {
        /*
         * Held rather than fetched per sample: a ProcessThread belongs to the Process it came from, so disposing that would leave the
         * cached thread unreadable, and walking the thread list every 200 ms of an episode is the cost we are trying not to pay.
         */
        _process ??= Process.GetCurrentProcess();

        foreach (ProcessThread candidate in _process.Threads)
        {
          if (candidate.Id == threadId)
          {
            return candidate;
          }
        }
      }
      catch (Exception)
      {
        /* No access to the thread list: every episode reports n/a rather than nothing at all. */
      }

      return null;
    }

    /*
     * The reason for a wait, or "unknown". Read separately from the state because `ProcessThread` fetches each property on demand rather
     * than from one snapshot: asking for `WaitReason` after the thread has woken throws InvalidOperationException — "only available if the
     * ThreadState is Wait" — which is a race between two reads of the same object, and the common case rather than the exotic one when a
     * thread is flapping. A sample that still says it waited is worth keeping; the name of the wait is not worth throwing over.
     */
    private static string WaitReasonText(ProcessThread thread)
    {
      try
      {
        return thread.WaitReason.ToString();
      }
      catch (Exception)
      {
        return "unknown";
      }
    }

    private static long ElapsedMs(ProcessThread thread)
    {
      try
      {
        return (long)thread.TotalProcessorTime.TotalMilliseconds;
      }
      catch (Exception)
      {
        return 0;
      }
    }
  }
}

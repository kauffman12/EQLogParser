using log4net;
using System;
using System.Reflection;
using System.Runtime;

namespace EQLogParser
{
  /*
   * "GC: throughput or responsiveness?" is not one answer for this app, because the app has two very different jobs.
   *
   * Streaming a file into a capture allocates the line's strings and the parse temporaries and throws almost all of them
   * away — docs/DesignNotes.md → "The reader hands the parser one line at a time…" counted the ledger for exactly that
   * workload (+200 B per line: 22 gen0 collections, nothing promoted, ~0.2 % of wall). Nothing about it needs a collector
   * that keeps the UI smooth; it needs one that spends the fewest total milliseconds. `GCLatencyMode.Batch` says precisely
   * that: stop running background collection concurrently with the work and take fewer, blocking sweeps instead. The pause
   * is not a cost during a load — the operator is looking at a loading band — while the reclaimed concurrency comes straight
   * off the two threads doing the reading and parsing.
   *
   * Which is why this is scoped instead of set once at startup. `SustainedLowLatency` (or plain `Interactive`) is the right
   * answer for live play, where a stutter lands on a raid; `Batch` left on after a load would quietly grow the working set
   * of an app that then sits idle for four hours. So the mode is entered for the bulk read and left when the file runs out,
   * by whoever is still holding it — more than one reader can be alive over different files (the open's own loop plus a
   * trigger reader per enabled character), so a naive set-and-restore would drop the mode out from under a load that is
   * still running.
   *
   * This does NOT decide which COLLECTOR runs. `GCSettings.IsServerGC` is read-only at runtime — Server vs workstation is
   * a startup choice in EQLogParser.csproj — and the open question recorded in docs/DesignNotes.md → "Parallel loading:
   * measured, shipped, and backed out" is whether that flag earns its memory now that reading is one stream. The gate below
   * exists so one build can answer that alongside this mechanism.
   */
  internal static class BulkGcMode
  {
    /*
     * Below this there is nothing to win: a read of a few megabytes finishes in a fraction of a second, and switching the
     * process-wide collector policy for it would be a side effect in search of a cause. Chosen as "clearly a bulk file"
     * rather than derived — an EverQuest night raid writes tens of megabytes an hour, so 32 MiB is a real capture and a
     * tail-follow of one is not.
     */
    internal const long BulkReadThresholdBytes = 32L * 1024 * 1024;

    /*
     * TEMPORARY — delete once the Server-GC × latency-mode matrix in docs/DesignNotes.md ("Parallel loading: measured,
     * shipped, and backed out") is settled. EQLP_BULKGC=0 turns this mechanism off entirely so ONE build can measure both
     * arms without a rebuild; =1 forces it on regardless of file size, which is how a small file gets measured as if big.
     * Unset (what every player runs) uses the size rule and nothing else.
     */
    private static readonly string _envOverride = Environment.GetEnvironmentVariable("EQLP_BULKGC");

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly object _gate = new();

    // How many bulk reads are in flight. The mode belongs to the FIRST one and is released by the LAST, because
    // GCSettings.LatencyMode is one process-global slot with no notion of ownership.
    private static int _depth;
    private static bool _weChanged;
    private static GCLatencyMode _saved;

    /// <summary>Bulk reads currently holding the throughput mode. A test seam, and the answer to "did someone forget to leave?".</summary>
    internal static int Depth
    {
      get
      {
        lock (_gate) return _depth;
      }
    }

    /*
     * Is this read big enough to be worth a process-wide policy change? `bytesToRead` is what THIS reader will walk, not
     * the file's size: a monitor open follows from end of file and reads nothing at all, which is the case most likely to
     * be mislabelled "big" (an operator can be tailing a 1.2 GB capture and reading zero bytes of it).
     */
    internal static bool ShouldUseBatchMode(long bytesToRead)
    {
      if (IsEnvironment("1") || string.Equals(_envOverride, "on", StringComparison.OrdinalIgnoreCase)) return true;
      if (IsEnvironment("0") || string.Equals(_envOverride, "off", StringComparison.OrdinalIgnoreCase)) return false;
      return bytesToRead >= BulkReadThresholdBytes;
    }

    /*
     * Enter throughput mode for a bulk read, if it is one. Always returns something disposable — the caller should not
     * have to know whether anything happened, and a load path should not be littered with conditionals that can be
     * mismatched. Nesting is refcounted; a refused or failed mode change is simply not held against the restore.
     */
    internal static IDisposable EnterForBulkRead(long bytesToRead)
    {
      if (!ShouldUseBatchMode(bytesToRead)) return NoScope.Instance;

      lock (_gate)
      {
        if (_depth == 0)
        {
          _saved = GCSettings.LatencyMode;
          _weChanged = _saved != GCLatencyMode.Batch;
          if (_weChanged)
          {
            try
            {
              GCSettings.LatencyMode = GCLatencyMode.Batch;
            }
            catch (Exception ex)
            {
              // A refused mode is not a failed load. Stay quiet-but-honest in the log and restore nothing we never set.
              _weChanged = false;
              Log.Debug($"gc.mode could not enter batch: {ex.Message}");
            }
          }

          if (_weChanged)
          {
            Log.Debug($"gc.mode batch ON for a bulk read ({bytesToRead / (1024 * 1024)} MB), from {_saved}");
          }
        }

        _depth++;
      }

      return new Scope();
    }

    private static bool IsEnvironment(string value) => string.Equals(_envOverride, value, StringComparison.Ordinal);

    private static void Release()
    {
      lock (_gate)
      {
        if (_depth == 0) return; // an unmatched release cannot be allowed to drive the count negative
        _depth--;
        if (_depth > 0) return;  // another reader is still streaming; it inherited this mode, not a new one

        if (_weChanged)
        {
          try
          {
            GCSettings.LatencyMode = _saved;
            Log.Debug($"gc.mode batch OFF, back to {_saved}");
          }
          catch (Exception ex)
          {
            Log.Debug($"gc.mode could not restore {_saved}: {ex.Message}");
          }

          _weChanged = false;
        }
      }
    }

    // Per-caller token, so a double Dispose cannot release a load that someone else is still holding.
    private sealed class Scope : IDisposable
    {
      private int _closed;

      public void Dispose()
      {
        if (System.Threading.Interlocked.Exchange(ref _closed, 1) == 0) Release();
      }
    }

    private sealed class NoScope : IDisposable
    {
      internal static readonly NoScope Instance = new();

      public void Dispose()
      {
      }
    }
  }
}

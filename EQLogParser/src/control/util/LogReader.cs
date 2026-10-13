using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /*
   * `origin` is the door that started this reader, in the words of whoever opened it. There is deliberately **no default**:
   * naming oneself is the whole point, and an optional parameter is how a fourth lane ends up printing an anonymous line.
   * It exists because "which session is this read loop" was unanswerable — a Windows run left three `load: read loop #N`
   * lines against two `capture: started` lines — and the word answered it on its first outing: the extra loop was not the
   * main window at all but **the trigger lane's own reader over the same file** (`TriggerManager` keeps a reader per
   * enabled character, or one over `AppSettings.CurrentLogFile` in basic mode). One word per construction, beside the file name.
   */
  /*
   * ONE reader over ONE file in file order — the whole design in a sentence, and it is load-bearing. A whole-file
   * open used to read on two lanes and merge (the numbers and the reasons for backing that out are in
   * docs/DesignNotes → "Parallel loading: measured, shipped, and backed out"). Nothing here asks for that back:
   * progress percent, HandedOverLines, the watch loop, the archive hook and the GC-tidy gate all describe a single
   * sequential read, because that is the only thing that happens.
   */
  internal class LogReader(ILogProcessor logProcessor, string fileName, string origin, int minBack = 0)
    : IDisposable
  {
    private const int BatchSize = 5000;
    /*
     * The bound is memory backpressure, and it is what a bulk open spends its life against: the reader runs far ahead of
     * the parser (a capture reads at hundreds of MB/s against a parse lane an order of magnitude slower), so within a
     * second this queue is full and every handoff parks the reader until the consumer drains. That behaviour is correct -
     * unbounded would buffer a whole night of strings behind the parser - but it is also why WHO runs this loop matters:
     * the thread that blocks in Add is the thread that cannot paint a window. See the note on StartAsync.
     *
     * The number itself is five batches deep, and it was 100,000 LINES until the memory pass asked what those slots
     * cost: the `LogReaderItem` is 24 bytes but the STRING it carries is what pays - at ~95 characters per EverQuest line
     * (a 973 MB capture over 10.2 M lines) that is ~200 bytes of UTF-16 per line, so 100,000 of them held ~22 MB of a bulk
     * load's peak working set against a parser that was the bottleneck either way. Lowering it changes no throughput (a
     * queue at its bound means the PARSE lane is the limit; a queue near empty means the reader is, and neither cares how
     * high the ceiling is) while taking ~17 MB out of the load. It also matters for steady raid tailing far less than for
     * an open: live traffic leaves this queue nearly empty by itself.
     *
     * It is stated in BATCHES now because that is the unit the queue holds: five of them is the same 25,000 lines
     * (~5 MB of strings) that were in flight before, so the reader runs exactly as far ahead and parks at exactly the
     * same point. What changed is only the price of getting there - one handshake per 5,000 lines instead of 5,000 of
     * them — the handoff was measured at roughly half the CPU of the ingest loop (see the section named above).
     */
    private const int QueueBoundBatches = 5;
    private readonly BlockingCollection<LogReaderItem[]> _lines = new(new ConcurrentQueue<LogReaderItem[]>(), QueueBoundBatches);
    private readonly List<LogReaderItem> _batch = new(BatchSize);
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static ReadOnlySpan<char> LoadingMsg => "LOADING, PLEASE WAIT...";
    private static ReadOnlySpan<char> WelcomeMsg => "Welcome to EverQuest!";
    private const int BufferSize = 147456;
    private CancellationTokenSource _cts = new();
    private StreamReader _reader;
    private FileStream _fs;

    /// <summary>How many read loops this process has started. Only ever used to number the load line, so two sessions' worth of them can
    /// be told apart (see StartAsync).</summary>
    private static int _readersStarted;

    private FileSystemWatcher _watcher;
    private long _initSize;
    private long _currentPos;

    private long _nextUpdateThreshold;
    private double _lastParsedTime;

    /* Lines handed to the parse lane. Ungated because it answers a PRODUCT question: MainWindow asks it (HandedOverLines) to
       decide the load-complete line, whether the fight list's loading band may show, and whether the post-load GC tidy runs.
       An increment per batch of 5,000 lines is not a cost worth thinking about. */
    private long _handedOver;
    private bool _fileDeleted;
    private bool _waiting = true;
    private bool _ready;
    private bool _invalid;

    public string FileName { get; } = fileName;
    public IDisposable GetProcessor() => logProcessor;
    public bool IsWaiting() => _waiting;

    /*
     * Lines this reader has handed to the parser. `lastMins: 0` follows from end of file, so a monitor open over an old
     * capture legitimately reads nothing and reaches "100 %" without a single line: that is the one case where this reads
     * zero, and the open path words its announcement accordingly.
     */
    internal long HandedOverLines => _handedOver;

    /*
     * "Is there load garbage to hand back?" — the law behind which 100 % asks the collector for memory, stated here because it is a fact
     * about what a reader did rather than about a status line. Two states reach 100 %: an open that read a file, and an open that followed
     * from end of file (lastMins 0) having handed over nothing. Only the first allocated gigabytes of split strings and per-line
     * temporaries — which is what makes a load expensive for the collector — so only the first is worth a blocking compacting pass.
     * Static with the count as a parameter so the rule can be asserted without opening a file (MainWindowTidyTriggerTest).
     */
    internal static bool LoadAllocatedGarbage(long handedOverLines) => handedOverLines > 0;

    public bool IsInValid() => _invalid;

    /*
     * File length for whoever sizes buffers before a session starts (DeriveEngine, via FactCapacity). One stat call,
     * and a file that vanished between the menu click and here answers 0 = "no hint" instead of throwing over a
     * memory estimate.
     */
    internal static long FileSizeOrZero(string fileName)
    {
      try
      {
        return File.Exists(fileName) ? new FileInfo(fileName).Length : 0;
      }
      catch (Exception ex)
      {
        Log.Debug("File length unavailable", ex);
        return 0;
      }
    }

    /*
     * Who runs this loop matters as much as what it does, so both halves of the rule live here.
     *
     * Every await in this file is ConfigureAwait(false). Without that, a caller who starts the task on the UI thread - and
     * MainWindow's open path IS inside a dispatcher callback - captures WPF's SynchronizationContext, and every
     * continuation comes back to the dispatcher: the read, the timestamp reuse, the batch append and FlushBatch's Add all
     * resume on the thread that paints the window. Reads only suspend once per ~144 KB buffer (about a thousand lines), so
     * almost every iteration continued inline there, and when one did suspend its continuation was posted back anyway. The
     * steady state of a big open was therefore: UI thread reading lines, then parked in Add against a full queue.
     *
     * Nothing in this class wants a thread affinity at all - no dispatcher, no WPF type, and FileSystemWatcher already
     * drives the same code on pool threads - so it takes none. Callers should still start it off the UI thread (MainWindow
     * uses Task.Run) because the FIRST segment runs before any await can move it.
     */
    public async Task StartAsync()
    {
      /*
       * Started off the UI thread now, so this can arrive after the pane moved on: opening another log disposes the previous
       * reader before its queued start runs, and by then the processor is gone and the queue is closed. Walk away quietly -
       * the alternative is an ObjectDisposedException/NullReference inside a fire-and-forget task that nobody observes.
       */
      if (_disposedValue || _cts.IsCancellationRequested)
      {
        Log.Debug($"load: start skipped - the reader for {Path.GetFileName(FileName)} was closed before it began");
        return;
      }

      if (await WhenFileExistsAsync().ConfigureAwait(false))
      {
        logProcessor.LinkTo(_lines);
        LogArchiveManager.QueueFileArchiveAsync(this);
      }

      try
      {
        await ReadFileAsync().ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        Log.Error($"Error Loading File: {FileName}. Re-open or toggle Triggers to try again.", ex);
      }
      finally
      {
        await CleanupStreamsAsync().ConfigureAwait(false);

        if (_watcher != null)
        {
          _watcher.EnableRaisingEvents = false;
          _watcher.Dispose();
          _watcher = null;
        }

        _cts?.Dispose();
        _cts = null;
        logProcessor?.Dispose();
        logProcessor = null;
        _invalid = true;
      }
    }

    /// <summary>
    /// Gets the current reading progress as a percentage.
    /// </summary>
    public double GetProgress()
    {
      if (!_ready)
      {
        return 0.0;
      }

      if (_initSize == 0)
      {
        return 100.0;
      }

      return _currentPos / (double)_initSize * 100;
    }

    private async Task ReadFileAsync()
    {
      string line;
      string previous = null;

      try
      {
        // Use FileInfo.Length (GetFileAttributesEx) instead of FileStream.Length (GetFileInformationByHandle)
        // which is significantly faster for large files
        _initSize = new FileInfo(FileName).Length;

        _fs = new FileStream(
          path: FileName,
          options: new FileStreamOptions
          {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
          });
        _nextUpdateThreshold = _initSize / 50;

        if (minBack == 0)
        {
          _fs.Seek(0, SeekOrigin.End);
        }

        var minDate = DateTime.MinValue;
        double beginTime = 0;

        if (minBack > 0)
        {
          minDate = DateTime.Now.AddMinutes(-minBack);
          beginTime = DateUtil.ToDotNetSeconds(minDate);
        }

        _reader = FileUtil.GetStreamReader(_fs, beginTime);
        SearchLinear(_reader, minDate);

        _ready = true;

        // One line per open, at Debug (a player's log never carries it; the session contract lines `capture: started` /
        // `derive: first pass` stay at Info), and it is the answer to "is the load running on my UI thread?". Before this
        // class was made context-free it named WPF's synchronization context here; the honest answer now is "none".
        //
        // It names WHICH reader too, because that was the other unanswerable question: closing one log and opening another leaves two of
        // these lines in one file, and "four lines for two sessions" reads as a reader that will not stop. An ordinal plus the file makes
        // each line belong to someone — which is also what tells a leftover reader from the one you just started.
        Log.Debug($"load: read loop #{Interlocked.Increment(ref _readersStarted)} on thread "
                 + $"{Environment.CurrentManagedThreadId}, sync context = "
                 + $"{SynchronizationContext.Current?.GetType().Name ?? "none"} | {Path.GetFileName(FileName)}"
                 + $" | {FactCapacity.ModeWord(minBack)} from {origin}");

        _currentPos = _fs.Position;
        var bytesRead = _fs.Position;

        /*
         * What THIS read will walk, which is not the file's size: a monitor open (minBack 0) sits at end of file and reads
         * nothing, so it stays out of throughput mode however big the capture on disk is.
         */
        var bulkBytes = Math.Max(0, _initSize - _fs.Position);

        /*
         * An explicit using BLOCK rather than `using var` for the method, because the monitor loop further down lives in
         * this same method: batch mode belongs to the bulk read and must be gone the moment the file runs out, not ride
         * along behind a live raid for the next four hours. Unwinding through a cancel or a failed read leaves it the same
         * way, which is the other half of why the scope ends here.
         */
        using (BulkGcMode.EnterForBulkRead(bulkBytes))
        {
          // date is now valid so read every line
          while ((line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)) != null)
          {
            if (_cts.IsCancellationRequested)
            {
              throw new TaskCanceledException();
            }

            // update progress during initial load
            bytesRead += Encoding.UTF8.GetByteCount(line) + 2;
            if (bytesRead >= _nextUpdateThreshold)
            {
              _currentPos = _fs.Position;
              _nextUpdateThreshold += _initSize / 50; // 2% of InitSize
            }
            else if (_initSize - bytesRead < 10000)
            {
              _currentPos = _fs.Position;
            }
            else if (_fs.Position >= _initSize)
            {
              _currentPos = _fs.Position;
            }

            HandleLine(line, ref previous);
          }
        }
      }
      catch (TaskCanceledException)
      {
        return;
      }
      catch (FileNotFoundException)
      {
        Log.Warn($"File Not Available: {FileName}");
      }
      catch (Exception ex)
      {
        Log.Error($"Error Loading File: {FileName}. Re-open or toggle Triggers to try again.", ex);
        return;
      }

      FlushBatch();

      if (Path.GetDirectoryName(FileName) is { } directory)
      {
        _watcher = new FileSystemWatcher(directory);
        _watcher.Deleted += (_, args) =>
        {
          if (args?.FullPath == FileName)
          {
            _fileDeleted = true;
          }
        };

        _watcher.Renamed += (_, args) =>
        {
          if (args?.OldFullPath == FileName || args?.FullPath == FileName)
          {
            _fileDeleted = true;
          }
        };

        _watcher.EnableRaisingEvents = true;
      }

      // continue reading for new updates
      while (_reader != null)
      {
        _waiting = false;

        try
        {
          // if deleted or truncated
          if (_fileDeleted || _fs.Length < _currentPos)
          {
            _fileDeleted = false;
            await ReOpenAsync().ConfigureAwait(false);
            continue;
          }

          if (_cts == null || _cts.IsCancellationRequested)
          {
            // stop
            break;
          }

          while ((line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)) != null)
          {
            HandleLine(line, ref previous, true);
          }

          FlushBatch();

          /*
           * This delay IS the tail latency of the whole app: the FileSystemWatcher above listens for Deleted and Renamed only, so
           * nothing wakes this loop when EQ appends a line — it comes back on this timer. A line's wait averages half of it, and that
           * is the only cost of the number being big; the drain above reads everything available before sleeping, so nothing queues up
           * behind a smaller one.
           *
           * It was dropped to 75 ms while chasing the meter's ~3.4 s refresh, and given back afterwards: with the engine now folding on
           * `DeriveCadence.FastFloorSeconds` (0.5 s) the wait is almost entirely inside the cadence, where this much of it overlaps
           * with a pass that was not due yet anyway, so polling thirteen times a second buys hundredths rather than tenths of a second
           * (docs/DesignNotes.md -> "How long a meter update takes"). If the tail ever needs to be prompter again, the order is this knob
           * first and `FastFloorSeconds` second — the floors, not the pump.
           *
           * Deliberately not a Changed-event wake-up: EQ's own write buffering coalesces those notifications unpredictably, so an event
           * would have to be backed by this same poll anyway.
           */
          await Task.Delay(200, _cts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
          // stop
          break;
        }
        catch (Exception)
        {
          FlushBatch();
          await ReOpenAsync().ConfigureAwait(false);
        }
      }
    }

    private void HandleLine(string theLine, ref string previous, bool monitor = false)
    {
      if (!TryStamp(theLine, ref previous, ref _lastParsedTime))
      {
        return;
      }

      if (_cts.Token.IsCancellationRequested)
      {
        throw new TaskCanceledException();
      }

      // if zoning during monitor try to archive
      if (monitor && theLine.Length > 27)
      {
        var rest = theLine.AsSpan(27);
        if (rest.StartsWith(LoadingMsg, StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith(WelcomeMsg, StringComparison.OrdinalIgnoreCase))
        {
          LogArchiveManager.QueueFileArchiveAsync(this);
        }
      }

      _batch.Add(new(theLine, _lastParsedTime, monitor));
      if (_batch.Count >= BatchSize)
      {
        FlushBatch();
      }

      previous = theLine;
    }

    /*
     * The line gate the read loop runs every candidate through: a line must be long enough to hold a standard timestamp
     * ("Tue Apr 14 09:00:00 2026 …") and within one second — logs are second-resolution — an identical date
     * prefix means the parse (the expensive part) can be reused. Returns false for anything that never
     * reaches a processor; `lastTs` moves only when the second actually changed, and `previous` is updated
     * by the CALLER only for accepted lines, exactly as HandleLine always did.
     */
    private static bool TryStamp(string theLine, ref string previous, ref double lastTs)
    {
      if (theLine.Length <= 28)
      {
        return false;
      }

      var lineSpan = theLine.AsSpan();
      if (previous == null || !lineSpan.Slice(1, 24).SequenceEqual(previous.AsSpan(1, 24)))
      {
        var dateTime = DateUtil.ParseStandardDate(theLine);
        if (dateTime == DateTime.MinValue)
        {
          return false;
        }

        lastTs = DateUtil.ToDotNetSeconds(dateTime);
      }

      return true;
    }

    private void FlushBatch()
    {
      if (_batch.Count == 0) return;

      var flushed = _batch.Count;

      /*
       * ONE handoff for the whole batch. A COPY crosses, not the accumulator: the consumer can still be reading what
       * it took when this list is cleared and refilled with the next few thousand lines, so whatever is in the queue
       * belongs to whoever receives it.
       *
       * Renting the buffer from ArrayPool (and having the consumer return it once drained) measured lower still -
       * 0.4 MB against 108 MB of transient arrays per capture - and was not taken: every path that abandons a batch
       * (dispose, cancel mid-block, a consumer fault) would then owe that return exactly once, and getting it wrong
       * is a double-return or a leaked buffer rather than a garbage number the GC reclaims for free.
       */
      _lines.Add(_batch.ToArray(), _cts.Token);  // Blocks if queue full, but consumer keeps processing
      _batch.Clear();

      // Counted here rather than at the batch site because this is the one place that knows how many lines crossed.
      _handedOver += flushed;
    }

    private void SearchLinear(StreamReader reader, DateTime minDate)
    {
      if (minDate != DateTime.MinValue)
      {
        while (reader.ReadLine() is { } line)
        {
          var dateTime = DateUtil.ParseStandardDate(line);
          if (dateTime == DateTime.MinValue)
          {
            continue;
          }

          if (dateTime >= minDate)
          {
            string previous = null;
            HandleLine(line, ref previous);
            break;
          }
        }
      }
    }

    private async Task<bool> WhenFileExistsAsync()
    {
      while (true)
      {
        _waiting = true;

        try
        {
          if (_cts.IsCancellationRequested)
          {
            return false;
          }

          if (File.Exists(FileName))
          {
            return true;
          }

          await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TaskCanceledException or ObjectDisposedException)
        {
          return false;
        }
      }
    }

    private async Task ReOpenAsync()
    {
      await CleanupStreamsAsync().ConfigureAwait(false);
      await Task.Delay(100).ConfigureAwait(false);

      if (await WhenFileExistsAsync().ConfigureAwait(false))
      {
        _fileDeleted = false;
        _fs = new FileStream(
          path: FileName,
          options: new FileStreamOptions
          {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
          });

        _fs.Seek(0, SeekOrigin.End);
        _currentPos = _fs.Position;
        _reader = FileUtil.GetStreamReader(_fs);
      }
    }

    private async Task CleanupStreamsAsync()
    {
      _reader?.Dispose();
      _reader = null;

      if (_fs != null)
      {
        await _fs.DisposeAsync().ConfigureAwait(false);
        _fs = null;
      }
    }

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        if (_watcher != null)
        {
          _watcher.EnableRaisingEvents = false;
          _watcher.Dispose();
          _watcher = null;
        }

        _cts?.Cancel();

        if (!_lines.IsCompleted)
        {
          _lines.CompleteAdding();
        }

        logProcessor?.Dispose();
        logProcessor = null;
        _invalid = true;
        _disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }

}

using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /*
   * `origin` is the door that started this reader, in the words of whoever opened it (see MainWindow.OpenLogFile). It
   * exists because "which session is this read loop" was unanswerable: a Windows run left THREE `load: read loop #N`
   * lines against TWO `capture: started` lines, so a leftover reader and a fresh one looked identical, and the ordinal
   * alone could not say who created it. One word per construction, printed beside the file name.
   */
  internal class LogReader(ILogProcessor logProcessor, string fileName, int minBack = 0, string origin = "open")
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
     * The number itself is five handoffs (BatchSize) deep, and it was 100,000 until the memory pass asked what those slots
     * cost: a `LogReaderItem` is 24 bytes unboxed in the queue's own segments, but the STRING it carries is what pays - at
     * ~95 characters per EverQuest line (a 973 MB capture over 10.2 M lines) that is ~200 bytes of UTF-16 per slot, so
     * 100,000 of them held ~22 MB of a bulk load's peak working set against an parser that was the bottleneck either way.
     * Lowering it changes no throughput (a queue at its bound means the PARSE lane is the limit; a queue near empty means
     * the reader is, and neither cares how high the ceiling is) while taking ~17 MB out of the load. It also matters for
     * steady raid tailing far less than for an open: live traffic leaves this queue nearly empty by itself.
     */
    private const int QueueBound = 25_000;
    private readonly BlockingCollection<LogReaderItem> _lines = new(new ConcurrentQueue<LogReaderItem>(), QueueBound);
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

    // Load diagnostics (PerfJournal.Enabled only): see NoteLoadProgress. The handed-over count itself is ungated, because
    // MainWindow asks it (see HandedOverLines) - an increment per batch of 5,000 lines is not a cost worth gating.
    private readonly Stopwatch _loadWatch = new();
    private long _handedOver;
    private long _handedOverLast;
    private double _diagSeconds;
    private int _diagGen0;
    private int _diagGen1;
    private int _diagGen2;
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

        // One line per open, and it is the answer to "is the load running on my UI thread?". Before this class was made
        // context-free it named WPF's synchronization context here; the honest answer now is "none".
        //
        // It names WHICH reader too, because that was the other unanswerable question: closing one log and opening another leaves two of
        // these lines in one file, and "four lines for two sessions" reads as a reader that will not stop. An ordinal plus the file makes
        // each line belong to someone — which is also what tells a leftover reader from the one you just started.
        Log.Info($"load: read loop #{Interlocked.Increment(ref _readersStarted)} on thread "
                 + $"{Environment.CurrentManagedThreadId}, sync context = "
                 + $"{SynchronizationContext.Current?.GetType().Name ?? "none"} | {Path.GetFileName(FileName)}"
                 + $" | {FactCapacity.ModeWord(minBack)} from {origin}");

        _currentPos = _fs.Position;
        var bytesRead = _fs.Position;

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
      if (theLine.Length > 28)
      {
        var lineSpan = theLine.AsSpan();
        if (previous == null || !lineSpan.Slice(1, 24).SequenceEqual(previous.AsSpan(1, 24)))
        {
          var dateTime = DateUtil.ParseStandardDate(theLine);
          if (dateTime == DateTime.MinValue)
          {
            return;
          }

          _lastParsedTime = DateUtil.ToDotNetSeconds(dateTime);
        }

        if (_cts.Token.IsCancellationRequested)
        {
          throw new TaskCanceledException();
        }

        // if zoning during monitor try to archive
        if (monitor && lineSpan.Length > 27)
        {
          var rest = lineSpan[27..];
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
    }

    private void FlushBatch()
    {
      if (_batch.Count == 0) return;

      var flushed = _batch.Count;
      foreach (var item in _batch)
      {
        _lines.Add(item, _cts.Token);  // Blocks if queue full, but consumer keeps processing
      }
      _batch.Clear();

      NoteLoadProgress(flushed);
    }

    /*
     * What a big load is actually doing, one line every ~2 s, and only when PerfJournal.Enabled (settings.txt
     * PerfReport=True) - a normal session writes nothing here. Chosen for the two questions that matter during an open:
     *
     *   queue 0/25000      - the reader is the slow half; nothing waits on the parse lane.
     *   queue 25000/...    - the parse lane is the bottleneck and this thread is parked in Add. That is what used to
     *                      freeze the window while a 400 MB capture loaded, and the queue depth is how you can see it
     *                      afterwards from eqlogparser.log alone.
     *
     * The generation deltas say whether collection work competes for cores during the load (the measured figures in
     * docs/DesignNotes.md say the garbage is small; if this line says otherwise, that is a finding). Lines/s here is READ
     * rate, not parsed rate - the reader runs ahead of the parser by design.
     *
     * The rate is THIS window's count over THIS window's seconds, with the running total beside it. The first version
     * divided the cumulative count by a constant window, and a field run over a 951 MB capture printed a beautiful
     * twelvefold acceleration from 359k to 4.59 M lines/s - pure arithmetic, since cumulative/window is a straight line.
     * Two independent checks caught it, and both are worth keeping: that capture (the local reference copy of
     * `eqlog_Kizant_xegony-09-03-26.txt`) holds 10,015,348 lines in 997,656,755 bytes - 99.6 bytes a line, so 4.59 M
     * lines/s would be a 458 MB/s handoff - and the same line's own `allocated` column ended at 16,432 MB over those
     * 10 M lines, which is **1,641 bytes per line**, the figure the headless measurements reach independently (docs
     * -> "what garbage a raid actually makes"). The honest number was ~0.5 M lines/s, ~48 MB/s, with the queue pinned:
     * the parse lane capping the reader on real hardware, which is the whole point of asking for the line.
     */
    private void NoteLoadProgress(int batchLines)
    {
      if (!PerfJournal.Enabled || batchLines == 0) return;

      _handedOver += batchLines;
      if (!_loadWatch.IsRunning) _loadWatch.Restart();
      var seconds = _loadWatch.Elapsed.TotalSeconds;
      var delta = seconds - _diagSeconds;
      if (delta < 2.0) return;

      var g0 = GC.CollectionCount(0);
      var g1 = GC.CollectionCount(1);
      var g2 = GC.CollectionCount(2);
      Log.Info($"load: {GetProgress():0}% | queue {_lines.Count}/{QueueBound}"
               + $" | read {(_handedOver - _handedOverLast) / delta:N0} lines/s ({_handedOver:N0} handed over)"
               + $" | gen +{g0 - _diagGen0}/{g1 - _diagGen1}/{g2 - _diagGen2}"
               + $" | allocated {GC.GetTotalAllocatedBytes(true) / 1_048_576:N0} MB");

      _handedOverLast = _handedOver;
      _diagSeconds = seconds;
      _diagGen0 = g0;
      _diagGen1 = g1;
      _diagGen2 = g2;
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

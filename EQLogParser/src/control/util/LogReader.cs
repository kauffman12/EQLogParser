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
   * `origin` is the door that started this reader, in the words of whoever opened it. There is deliberately **no default**:
   * naming oneself is the whole point, and an optional parameter is how a fourth lane ends up printing an anonymous line.
   * It exists because "which session is this read loop" was unanswerable — a Windows run left three `load: read loop #N`
   * lines against two `capture: started` lines — and the word answered it on its first outing: the extra loop was not the
   * main window at all but **the trigger lane's own reader over the same file** (`TriggerManager` keeps a reader per
   * enabled character, or one over `AppSettings.CurrentLogFile` in basic mode). One word per construction, beside the file name.
   */
  /*
   * `backfill` is the sharded-load collaborator (IShardedBackfill / docs/DesignNotes → the sharded-load chapter):
   * when a WHOLE-file open meets a file big enough to earn it, the bytes [0, EOF-at-open) are read and parsed on
   * TWO lanes with their own captures, and the merged result is poured into the session's real capture before the
   * first tail line moves. Every part of this class that matters downstream — progress percent, HandedOverLines,
   * the watch loop, the archive hook, the GC-tidy gate — sees one reader doing one job over one file, because the
   * backfill is a prelude INSIDE ReadFileAsync, not a separate lifecycle. Null (every small file, every seek-back,
   * every gz, every construction that predates this) reads exactly as before.
   */
  internal class LogReader(ILogProcessor logProcessor, string fileName, string origin, int minBack = 0, IShardedBackfill backfill = null)
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

    // Backfill progress state: while the lanes run, GetProgress answers from these instead of 0.
    // _shardConsumed[s] is bytes this range has decoded; windows are their byte sizes. The volatile flag
    // publishes "aggregate these arrays now" without a lock — one writer (the reader task), one reader (the pump).
    private long[] _shardConsumed;
    private long[] _backfillWindows;
    private int _backfillActive;

    // The lanes' captures, published while they run (null before the first and after Merge). Assigned by the
    // backfill task only; the UI pump reads it through Volatile to answer BackfilledFacts mid-load.
    private BackfillShard[] _liveShards;
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
     * What the open has CAPTURED so far, while a sharded backfill still runs: every parsed damage and heal row lives in
     * its lane's capture until the merge, so the honest mid-load fact count is the sum of those tables — which is what
     * the fight list's band quotes instead of "lines read" (operator 2026-10-13: thirteen million lines counted out loud
     * read as telemetry from the wrong feature; this pane counts captures). ZERO outside a running backfill on purpose:
     * once Merge returns, the session's own capture answers through its Capturing event, and summing both sources at
     * any point would double every merged row. The lane counters are plain longs written by each lane's parse thread;
     * a pump read is a snapshot in time, which is exactly what a display number may be.
     */
    internal long BackfilledFacts
    {
      get
      {
        if (Volatile.Read(ref _liveShards) is not { } shards) return 0L;
        var total = 0L;
        foreach (var shard in shards)
        {
          total += shard.Capture.Facts.FactCount + (shard.Capture.HealFacts?.HealCount ?? 0L);
        }

        return total;
      }
    }

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
      /*
       * During a sharded backfill no single _currentPos exists — the honest aggregate is the bytes every
       * range has decoded over the bytes every range owns, which is the same fraction of THE FILE the
       * sequential reader would have shown. Once the merge lands and the tail reader opens, the ordinary
       * path resumes at _initSize/total = 100 %.
       */
      if (Volatile.Read(ref _backfillActive) != 0 && _shardConsumed is { } consumed && _backfillWindows is { } windows)
      {
        long done = 0;
        long total = 0;
        for (var i = 0; i < windows.Length; i++)
        {
          done += Math.Min(Volatile.Read(ref consumed[i]), windows[i]);
          total += windows[i];
        }

        /*
         * CAPPED below 100 while the lanes are still owned by RunBackfillAsync. Consumed bytes are not a
         * finished load: after the last chunk every lane still drains its queue and the merge still pours —
         * and a pump that saw true 100 % there announced "Finished Loading", ran the compacting GC INSIDE the
         * merge and printed "Monitoring Log" while the tail reader had not even opened (2026-10-13 field log:
         * `Finished Loading Log File in 17 seconds` lands 280 ms BEFORE `backfill merged`). Percent is a
         * promise about the whole open, so the last point belongs to the merge, not to the disk.
         */
        return total > 0 ? Math.Min(99.0, done / (double)total * 100) : 0.0;
      }

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

        /*
         * The sharded prelude: two lanes read and parse [0, _initSize) side by side, then merge into the session's
         * capture before this thread opens the file for real. The conditions are the measured policy (LogShardPlan):
         * whole-file mode only — a seek-back is live-tail-shaped and already partial — plain text (a gzip stream has
         * no byte axis to cut), a collaborator that owns a capture, and a file big enough that each range earns its
         * lane. Anything else falls through to the sequential path below and nobody mentions it.
         */
        var ranges = 1;
        if (backfill is not null && minBack < 0 && _initSize >= 2 * LogShardPlan.MinWindowBytes &&
            !FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
          ranges = await RunBackfillAsync(LogShardPlan.Workers);
        }

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
        else if (ranges > 1)
        {
          // Everything before the open's EOF already reached the capture through the lanes; anything EQ
          // appended WHILE they ran is read here as ordinary bulk lines — same monitor state, quick-share and
          // archive behavior any other byte of a backfill gets.
          _fs.Seek(_initSize, SeekOrigin.Begin);
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
     * The line gate both readers share: a line must be long enough to hold a standard timestamp
     * ("Tue Apr 14 09:00:00 2026 …") and within one second — logs are second-resolution — an identical date
     * prefix means the parse (the expensive part) can be reused. Returns false for anything that never
     * reaches a processor; `lastTs` moves only when the second actually changed, and `previous` is updated
     * by the CALLER only for accepted lines, exactly as HandleLine always did. The shard workers keep both
     * in locals (see ReadAndFeedRange); the tail reader passes its fields.
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

    /*
     * ─── sharded backfill ─── the reader-side how of IShardedBackfill (the why and the numbers are in
     * docs/DesignNotes; the whether/how-many policy is LogShardPlan).
     *
     * plan → per range: engine-made shard + lane-bound processor → worker → WhenAll → Merge. A failed
     * attempt aborts WHOLE: there is no partial merge, because half a file's facts wearing whole-file
     * completeness is the one outcome worse than a load that fails — ReadFileAsync's outer catch surfaces
     * it like any other broken file. Cancellation rides the same _cts the tail loop and Dispose use.
     */
    private async Task<int> RunBackfillAsync(int workers)
    {
      var cuts = LogShardPlan.CutPoints(FileName, workers);
      var ranges = cuts.Length - 1;
      if (ranges < 2)
      {
        return 1;   // the file cannot supply line-aligned ranges; sequential takes it from here
      }

      var stopwatch = Stopwatch.StartNew();
      _backfillWindows = new long[ranges];
      _shardConsumed = new long[ranges];
      for (var i = 0; i < ranges; i++)
      {
        _backfillWindows[i] = cuts[i + 1] - cuts[i];
      }

      Volatile.Write(ref _backfillActive, 1);

      var shards = new List<BackfillShard>(ranges);
      try
      {
        Log.Debug($"load: sharded backfill '{Path.GetFileName(FileName)}' | ranges={ranges} | bytes={_initSize:N0}");
        var tasks = new Task[ranges];
        for (var i = 0; i < ranges; i++)
        {
          var slot = i;
          var shard = backfill.NewShard(_backfillWindows[slot]);
          shards.Add(shard);
          var processor = backfill.NewShardProcessor(FileName, shard);
          tasks[slot] = Task.Run(() => RunRangeAsync(slot, cuts[slot], cuts[slot + 1], shard, processor));
        }

        // Publish the lanes once every slot exists (BackfilledFacts iterates without locking); Merge clears it.
        Volatile.Write(ref _liveShards, shards.ToArray());

        await Task.WhenAll(tasks);
        backfill.Merge(shards);   // stops the lane captures and pours under the ingest gate; nothing tails before this returns
        Log.Debug($"load: backfill merged into the capture in {stopwatch.ElapsedMilliseconds:N0} ms (wall, all ranges + merge)");
        return ranges;
      }
      catch
      {
        backfill.Abort(shards);
        throw;   // ReadFileAsync's catch reports it; the stack rides on
      }
      finally
      {
        Volatile.Write(ref _liveShards, null);
        Volatile.Write(ref _backfillActive, 0);
      }
    }

    private async Task RunRangeAsync(int slot, long from, long to, BackfillShard shard, LogProcessor processor)
    {
      using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), QueueBound);
      processor.LinkTo(items);
      var stopwatch = Stopwatch.StartNew();
      long lines = 0;
      try
      {
        lines = ReadAndFeedRange(slot, from, to, items);
      }
      finally
      {
        if (!items.IsAddingCompleted)
        {
          items.CompleteAdding();
        }

        /*
         * The lane drains fully before this worker reports done — and Merge (which stops the shard captures)
         * only runs after every worker is done — so no parsed event can arrive at a stopped capture. The same
         * ordering law ShutdownAsync arrives at from the other side.
         */
        var completion = processor.Completion;
        if (completion is not null)
        {
          await completion.ConfigureAwait(false);
        }

        /*
         * One line per range at Debug, and it is the answer to "what did the lanes actually do": bytes, lines,
         * wall INCLUDING the final queue drain, and the resulting MB/s. A rate sitting at the machine's
         * sequential-disk ceiling names bandwidth as the sharding limit; a rate well above it says the parse was
         * the limit and both lanes did their job (docs/DesignNotes → "How loading is sharded in the app").
         */
        var ms = Math.Max(1, stopwatch.ElapsedMilliseconds);
        Log.Debug($"load: range {slot} [{from:N0}, {to:N0}) fed {lines:N0} lines in {ms:N0} ms "
                  + $"({(to - from) / 1048576.0 * 1000 / ms:F0} MB/s)");
        processor.Dispose();
      }
    }

    /*
     * One lane's feed loop: lines come from LogRangeReader (whose fidelity to sequential ReadLine is the
     * load-bearing property, asserted headlessly in LogRangeReaderTest), are stamped with the same gate the
     * tail uses, and cross a bounded queue to this shard's lane-bound processor. Runs synchronously on the
     * worker's pool thread; the queue blocks it while parsing catches up — the same backpressure shape the
     * sequential reader has always had.
     */
    // Returns the number of lines this range stamped and fed (the same set FlushShardBatch counts into
    // _handedOver), which is what the per-range Debug line reports. The body is HandleLine's bulk half: same
    // gate, no monitor/archive channels - a backfill line is a bulk line.
    private long ReadAndFeedRange(int slot, long from, long to, BlockingCollection<LogReaderItem> items)
    {
      var token = _cts?.Token ?? default;
      var batch = new List<LogReaderItem>(BatchSize);
      string previous = null;
      double lastTs = 0;
      long fed = 0;

      LogRangeReader.ForEachLine(FileName, from, to,
        line =>
        {
          if (!TryStamp(line, ref previous, ref lastTs))
          {
            return;
          }

          batch.Add(new LogReaderItem(line, lastTs, false));
          previous = line;
          fed++;
          if (batch.Count >= BatchSize)
          {
            FlushShardBatch(batch, items, token);
          }
        },
        consumed => Volatile.Write(ref _shardConsumed[slot], consumed), token);

      FlushShardBatch(batch, items, token);
      return fed;
    }

    private void FlushShardBatch(List<LogReaderItem> batch, BlockingCollection<LogReaderItem> items, CancellationToken token)
    {
      if (batch.Count == 0)
      {
        return;
      }

      foreach (var item in batch)
      {
        try
        {
          items.Add(item, token);
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
      }

      // Counted even when cancelled: the counter only answers "did this load move any bytes" for the GC
      // tidy-up gate, and FlushBatch has always counted it the same way.
      Interlocked.Add(ref _handedOver, batch.Count);
      batch.Clear();
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

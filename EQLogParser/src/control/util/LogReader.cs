using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /* One reader per file stream, in order; `origin` has no default because main and trigger readers need distinct load lines.
   * See docs/DesignNotes.md → "Parallel loading: measured, shipped, and backed out". */
  internal class LogReader(ILogProcessor logProcessor, string fileName, string origin, int minBack = 0)
    : IDisposable
  {
    private const int BatchSize = 5000;
    // Five batches cap the queue at 25,000 lines; see docs/DesignNotes.md → "Retiring probes that no longer protect production".
    private const int QueueBoundBatches = 5;
    private readonly BlockingCollection<LogReaderItem[]> _lines = new(new ConcurrentQueue<LogReaderItem[]>(), QueueBoundBatches);
    private readonly List<LogReaderItem> _batch = new(BatchSize);
    private readonly object _lifecycleGate = new();
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

     // Identity and last-seen length of THIS handle's file; the tail compares them against whatever stands at the name (LogFollow).
    private long _followedCreatedUtcTicks = LogFollow.NoIdentity;
    private long _followedLength;

    private long _nextUpdateThreshold;
    private double _lastParsedTime;

    // The handoff count gates the load announcement, fight-list band and post-load cleanup; never gate it on logging.
    private long _handedOver;
    private bool _fileDeleted;
    private bool _waiting = true;
    private bool _ready;
    private volatile bool _initialReadFinished;
    private long _initialLines;
    private volatile bool _invalid;

    public string FileName { get; } = fileName;
    public IDisposable GetProcessor() => logProcessor;
    public bool IsWaiting() => _waiting;

    /*
     * Lines this reader has handed to the parser. `lastMins: 0` follows from end of file, so a monitor open over an old
     * capture legitimately reads nothing and reaches "100 %" without a single line: that is the one case where this reads
     * zero, and the open path words its announcement accordingly.
     */
    internal long HandedOverLines => Interlocked.Read(ref _handedOver);

    // A file position can reach EOF while StreamReader still holds lines and the consumer still parses earlier batches.
    // Completion belongs to the initial read AND its parser, not the read-ahead position used for the percent dial.
    internal bool InitialLoadComplete => !_disposedValue && !_invalid && _initialReadFinished
      && (logProcessor is not LogProcessor parser || parser.ProcessedLines >= _initialLines);

    // A follow-from-end open reaches 100 % without reading lines; it owes no post-load collection.
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

    /* Starts on a pool thread: the first segment runs before any await, and continuations must not capture WPF's
     * dispatcher or a full batch can block it. Every await here uses ConfigureAwait(false). */
    public async Task StartAsync()
    {
      /*
       * Started off the UI thread now, so this can arrive after the pane moved on: opening another log disposes the previous
       * reader before its queued start runs, and by then the processor is gone and the queue is closed. Walk away quietly -
       * the alternative is an ObjectDisposedException/NullReference inside a fire-and-forget task that nobody observes.
       */
      lock (_lifecycleGate)
      {
        if (_disposedValue || _cts.IsCancellationRequested)
        {
          Log.Debug($"load: start skipped - the reader for {Path.GetFileName(FileName)} was closed before it began");
          return;
        }
      }

      try
      {
        if (!await WhenFileExistsAsync().ConfigureAwait(false)) return;

        // Dispose can run while the file-exists await is suspended. Link and close must agree on one owner.
        lock (_lifecycleGate)
        {
          if (_disposedValue || _cts.IsCancellationRequested) return;
          logProcessor.LinkTo(_lines);
        }

        LogArchiveManager.QueueFileArchiveAsync(this);
        await ReadFileAsync().ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        Log.Error($"Error Loading File: {FileName}. Re-open or toggle Triggers to try again.", ex);
      }
      finally
      {
        await CleanupStreamsAsync().ConfigureAwait(false);

        lock (_lifecycleGate)
        {
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

        // Record the identity of the file this handle opened, before the tail ever asks the name who it is now.
        _followedCreatedUtcTicks = HandleIdentityOf(_fs);
        _followedLength = _fs.Length;

        _reader = FileUtil.GetStreamReader(_fs, beginTime);
        SearchLinear(_reader, minDate);

        _ready = true;

        // Identify both the stream and its thread, so main and trigger readers over one file remain distinguishable.
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
          /*
           * A pacer for the 50-notch dial, not a byte count — the displayed number comes from _fs.Position. Counting UTF-8 here cost
           * a second pass over every line; a multi-byte log crosses a notch slightly late and is corrected at the next sample.
           */
          bytesRead += line.Length + 2;
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
      _initialLines = HandedOverLines;
      _initialReadFinished = true;

      if (Path.GetDirectoryName(FileName) is { } directory)
      {
        _watcher = new FileSystemWatcher(directory)
        {
          /*
           * Only Deleted/Renamed are read, so nothing else is requested: the default filter queues a LastWrite event per append and an
           * overflowed watcher stops reporting for good. The poll asks the name directly too (LogFollow).
           */
          NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
          InternalBufferSize = 64 * 1024
        };
        _watcher.Error += (_, args) => Log.Warn($"Directory watcher faulted for {directory}: "
                                               + $"{args.GetException()?.Message}. Rollover now rides on the poll alone.");
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
          /*
           * Deleted, rewritten, or no longer the file this handle opened — the midnight rollover, which neither the length test nor the
           * watcher can be trusted to see. Only Reopen acts here: a name that merely reports nothing must not close a working stream.
           */
          if (_fileDeleted || LogFollow.DecideFor(_followedCreatedUtcTicks, NameIdentityOf(FileName), _followedLength,
                _fs.Length) is LogFollowDecision.Reopen)
          {
            _fileDeleted = false;
            previous = await ReOpenAsync(previous).ConfigureAwait(false);
            continue;
          }

          // Sampled after the decision: a file that only ever grew must never look rewritten to the next pass.
          _followedLength = _fs.Length;

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

          /* Append events are not a reliable wake-up, so this poll sets tail latency; cadence dominates the meter's
           * refresh. See docs/DesignNotes.md -> "How long a meter update takes". */
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
          previous = await ReOpenAsync(previous).ConfigureAwait(false);
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
     * The gate every candidate line passes: long enough for a standard timestamp, reusing the parse of an identical date prefix.
     * False means the line never reaches a processor; the CALLER advances `previous` only for accepted lines.
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
       * ONE handoff per batch, and a COPY crosses rather than the accumulator: the consumer may still be reading what it took while
       * this list is refilled. ArrayPool was measured and refused (docs/DesignNotes.md → "Retiring probes that no longer protect
       * production").
       */
      _lines.Add(_batch.ToArray(), _cts.Token);  // Blocks if queue full, but consumer keeps processing
      _batch.Clear();

      // Counted here rather than at the batch site because this is the one place that knows how many lines crossed.
      Interlocked.Add(ref _handedOver, flushed);
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

    private async Task<string> ReOpenAsync(string previous)
    {
      // On a rename the old handle is still valid and may hold lines from this poll interval; drain it BEFORE the new
      // file reaches the parser so both sides arrive in file order. A truncate has nothing left to drain.
      var shortened = false;
      var oldAvailable = _fs is not null;
      try
      {
        if (_fs is not null)
        {
          shortened = _fs.Length < _followedLength;
        }
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException)
      {
        // This can itself be the I/O error that sent us here. Do not ask the broken handle twice: recover at the
        // replacement's tail rather than letting the reopen throw again and stop the monitor permanently.
        oldAvailable = false;
      }

      var drained = false;
      if (!shortened && oldAvailable && _reader is not null)
      {
        try
        {
          string line;
          while ((line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)) is not null)
          {
            HandleLine(line, ref previous, true);
          }

          FlushBatch();
          drained = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          // A broken old stream cannot prove which bytes were processed. Preserve the historical seek-to-end fallback
          // instead of using its buffered FileStream.Position to skip or replay a line in the replacement.
          Log.Warn($"Could not drain the old log before reopening {FileName}", ex);
          FlushBatch();
        }
      }

      await Task.Delay(100, _cts.Token).ConfigureAwait(false);
      if (!await WhenFileExistsAsync().ConfigureAwait(false))
      {
        return previous;
      }

      FileStream next = null;
      try
      {
        next = new FileStream(
          path: FileName,
          options: new FileStreamOptions
          {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
          });

        // A new file object can be an exact copy of the old one, so identity says WHEN to reopen and never that the content
        // is new. Cut on the last shared complete line; a fresh daily file differs on its first line and starts at zero.
        var shared = shortened ? 0L : drained && _fs is not null
          ? await LogFollowPrefix.SharedLineEndAsync(_fs, next, _fs.Position, _cts.Token).ConfigureAwait(false)
          : (long?)next.Length;
        if (shared is null)
        {
          // Identical so far with no complete shared line: the replacement's first line has not landed yet. Replay from zero
          // would double-count and seeking to its end could drop lines, so keep the old handle; the next poll asks again.
          if (RepeatCap.SpeaksOnce("follow.reopen.noSharedLine"))
          {
            Log.Debug($"Reopen of {FileName} deferred: no shared complete line with the drained file yet.");
          }

          return previous;
        }

        next.Seek(shared.Value, SeekOrigin.Begin);
        var reader = FileUtil.GetStreamReader(next);
        await CleanupStreamsAsync().ConfigureAwait(false);
        _fs = next;
        _reader = reader;
        next = null;
        _currentPos = _fs.Position;
        _followedCreatedUtcTicks = HandleIdentityOf(_fs);
        _followedLength = _fs.Length;
        _fileDeleted = false;
      }
      finally
      {
        if (next is not null)
        {
          await next.DisposeAsync().ConfigureAwait(false);
        }
      }

      return previous;
    }

    /* Stamp the actual opened handle, not the name: a rename between Open and stat must not label the old handle new. */
    private static long HandleIdentityOf(FileStream stream)
    {
      try
      {
        return File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks;
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
      {
        return LogFollow.UnknownIdentity;
      }
    }

     /* The cheap id of the file standing at a name. Null: nothing is there. UnknownIdentity: the question could not be asked, so it buys no decision. */
    private static long? NameIdentityOf(string path)
    {
      try
      {
        var info = new FileInfo(path);
        return info.Exists ? info.CreationTimeUtc.Ticks : (long?)null;
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
      {
        return LogFollow.UnknownIdentity;
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
    private volatile bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      lock (_lifecycleGate)
      {
        if (_disposedValue) return;
        _disposedValue = true;

        if (_watcher != null)
        {
          _watcher.EnableRaisingEvents = false;
          _watcher.Dispose();
          _watcher = null;
        }

        _cts?.Cancel();
        if (!_lines.IsCompleted) _lines.CompleteAdding();

        logProcessor?.Dispose();
        logProcessor = null;
        _invalid = true;
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

using log4net;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class LogReader(ILogProcessor logProcessor, string fileName, int minBack = 0)
    : IDisposable
  {
    private readonly BlockingCollection<Tuple<string, double, bool>> _lines = new(new ConcurrentQueue<Tuple<string, double, bool>>(), 100000);
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const int BufferSize = 147456;
    private CancellationTokenSource _cts = new();
    private StreamReader _reader;
    private FileStream _fs;
    private FileSystemWatcher _watcher;
    private long _initSize;
    private long _currentPos;
    private long _nextUpdateThreshold;
    private double _lastParsedTime;
    private bool _fileDeleted;
    private bool _waiting = true;
    private bool _ready;
    private bool _invalid;

    public string FileName { get; } = fileName;
    public IDisposable GetProcessor() => logProcessor;
    public bool IsWaiting() => _waiting;
    public bool IsInValid() => _invalid;

    public async void Start()
    {
      if (await WhenFileExists())
      {
        logProcessor.LinkTo(_lines);
        FileUtil.ArchiveFile(this);
      }

      try
      {
        await ReadFile();
      }
      catch (Exception)
      {
        Log.Warn($"Error Loading File: ${FileName}. Re-open or toggle Triggers to try again.");
      }
      finally
      {
        CleanupStreams();

        if (_watcher != null)
        {
          _watcher.EnableRaisingEvents = false;
          _watcher.Dispose();
          _watcher = null;
        }

        _cts?.Dispose();
        _cts = null;

        if (!_lines.IsCompleted)
        {
          _lines.CompleteAdding();
        }

        logProcessor?.Dispose();
        logProcessor = null;
        _invalid = true;
      }
    }

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

    private async Task ReadFile()
    {
      string line;
      string previous = null;

      try
      {
        _fs = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        _initSize = _fs.Length;
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
          beginTime = DateUtil.ToDouble(minDate);
        }

        _reader = FileUtil.GetStreamReader(_fs, beginTime);
        SearchLinear(_reader, minDate);

        _ready = true;
        _currentPos = _fs.Position;
        var bytesRead = _fs.Position;

        // date is now valid so read every line
        while ((line = await _reader.ReadLineAsync(_cts.Token)) != null)
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
          else if ((_initSize - bytesRead) < 10000)
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
      catch (Exception)
      {
        Log.Warn($"Error Loading File: ${FileName}. Re-open or toggle Triggers to try again.");
        return;
      }

      if (Path.GetDirectoryName(FileName) is { } directory)
      {
        _watcher = new FileSystemWatcher(directory);
        _watcher.Deleted += (_, fileArgs) =>
        {
          if (fileArgs?.FullPath == FileName)
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
          if (_fileDeleted)
          {
            await ReOpen();
          }

          if (_cts == null || _cts.IsCancellationRequested)
          {
            // stop
            break;
          }

          while ((line = await _reader.ReadLineAsync(_cts.Token)) != null)
          {
            HandleLine(line, ref previous, true);
          }

          await Task.Delay(200, _cts.Token);
        }
        catch (TaskCanceledException)
        {
          // stop
          break;
        }
        catch (Exception)
        {
          await ReOpen();
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

          _lastParsedTime = DateUtil.ToDouble(dateTime);
        }

        if (_cts.Token.IsCancellationRequested)
        {
          throw new TaskCanceledException();
        }

        // if zoning during monitor try to archive
        if (monitor && lineSpan[27..].StartsWith("LOADING, PLEASE WAIT..."))
        {
          FileUtil.ArchiveFile(this);
        }

        _lines.Add(Tuple.Create(theLine, _lastParsedTime, monitor), _cts.Token);
        previous = theLine;
      }
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

    private async Task<bool> WhenFileExists()
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

          await Task.Delay(200, _cts.Token);
        }
        catch (Exception ex) when (ex is TaskCanceledException or ObjectDisposedException)
        {
          return false;
        }
      }
    }

    private async Task ReOpen()
    {
      CleanupStreams();
      var exists = await WhenFileExists();
      if (exists)
      {
        _fileDeleted = false;
        _fs = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        _fs.Seek(0, SeekOrigin.End);
        _reader = FileUtil.GetStreamReader(_fs);
      }
    }

    private async void CleanupStreams()
    {
      _reader?.Dispose();
      _reader = null;

      if (_fs != null)
      {
        await _fs.DisposeAsync();
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

  internal interface ILogProcessor : IDisposable
  {
    public void LinkTo(BlockingCollection<Tuple<string, double, bool>> collection);
  }
}

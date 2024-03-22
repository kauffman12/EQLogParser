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
  internal class LogReader : IDisposable
  {
    private readonly BlockingCollection<Tuple<string, double, bool>> _lines = new(new ConcurrentQueue<Tuple<string, double, bool>>(), 100000);
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly FileSystemWatcher _fileWatcher;
    private int _minBack;
    private CancellationTokenSource _cts;
    private readonly ILogProcessor _logProcessor;
    private Task _readFileTask;
    private long _initSize;
    private long _currentPos;
    private long _nextUpdateThreshold;
    private bool _ready;

    public LogReader(ILogProcessor logProcessor, string fileName, int minBack = 0)
    {
      _logProcessor = logProcessor;
      FileName = fileName;
      _minBack = minBack;

      if (Path.GetDirectoryName(fileName) is { } directory)
      {
        logProcessor.LinkTo(_lines);

        _fileWatcher = new FileSystemWatcher(directory, Path.GetFileName(fileName))
        {
          NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.LastAccess
        };

        _fileWatcher.Created += OnFileCreated;
        _fileWatcher.Deleted += OnFileMoved;
        _fileWatcher.Renamed += OnFileMoved;
        _fileWatcher.EnableRaisingEvents = true;
        FileUtil.ArchiveFile(this);
        StartReadingFile();
      }
      else
      {
        Log.Error($"Can not open file: {fileName}.");
      }
    }

    public string FileName { get; }
    public IDisposable GetProcessor() => _logProcessor;

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

    private void OnFileCreated(object sender, FileSystemEventArgs e) => StartReadingFile();
    private void OnFileMoved(object sender, FileSystemEventArgs e)
    {
      _cts?.Cancel();
      _readFileTask?.Wait();
      _minBack = 0;
    }

    private void StartReadingFile()
    {
      _cts = new CancellationTokenSource();
      _readFileTask = Task.Run(() => ReadFile(FileName, _minBack, _cts.Token), _cts.Token);
    }

    private async Task ReadFile(string fileName, int minBack, CancellationToken cancelToken)
    {
      const int bufferSize = 147456;

      try
      {
        var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
          bufferSize);

        _initSize = fs.Length;
        _nextUpdateThreshold = _initSize / 50;

        if (minBack == 0)
        {
          fs.Seek(0, SeekOrigin.End);
        }

        var minDate = DateTime.MinValue;
        double beginTime = 0;

        if (minBack > 0)
        {
          minDate = DateTime.Now.AddMinutes(-minBack);
          beginTime = DateUtil.ToDouble(minDate);
        }

        var reader = FileUtil.GetStreamReader(fs, beginTime);
        SearchLinear(reader, minDate, cancelToken);

        _ready = true;
        _currentPos = fs.Position;
        await using (fs)
        using (reader)
        {
          string line;
          string previous = null;
          double doubleValue = 0;
          var bytesRead = fs.Position;

          // date is now valid so read every line
          while ((line = await reader.ReadLineAsync(cancelToken)) != null && !cancelToken.IsCancellationRequested)
          {
            if (cancelToken.IsCancellationRequested) break;

            // update progress during initial load
            bytesRead += Encoding.UTF8.GetByteCount(line) + 2;
            if (bytesRead >= _nextUpdateThreshold)
            {
              _currentPos = fs.Position;
              _nextUpdateThreshold += _initSize / 50; // 2% of InitSize
            }
            else if ((_initSize - bytesRead) < 10000)
            {
              _currentPos = fs.Position;
            }

            HandleLine(line, ref previous, ref doubleValue, cancelToken);
          }

          // continue reading for new updates
          while (!cancelToken.IsCancellationRequested)
          {
            while ((line = await reader.ReadLineAsync(cancelToken)) != null)
            {
              HandleLine(line, ref previous, ref doubleValue, cancelToken, true);
            }

            if (cancelToken.IsCancellationRequested) break;
            await Task.Delay(200, cancelToken);
          }
        }
      }
      catch (TaskCanceledException)
      {
        // ignore
      }
      catch (IOException e)
      {
        Log.Debug(e);
      }
    }

    private void HandleLine(string theLine, ref string previous, ref double doubleValue, CancellationToken cancelToken, bool monitor = false)
    {
      if (theLine.Length > 28)
      {
        if (previous == null || !theLine.AsSpan(1, 24).SequenceEqual(previous.AsSpan(1, 24)))
        {
          var dateTime = DateUtil.ParseStandardDate(theLine);
          if (dateTime == DateTime.MinValue)
          {
            return;
          }

          doubleValue = DateUtil.ToDouble(dateTime);
        }

        if (cancelToken.IsCancellationRequested) return;
        _lines.Add(Tuple.Create(theLine, doubleValue, monitor), cancelToken);
        previous = theLine;
      }
    }

    private void SearchLinear(StreamReader reader, DateTime minDate, CancellationToken cancelToken)
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
            if (!cancelToken.IsCancellationRequested)
            {
              string previous = null;
              double doubleValue = 0;
              HandleLine(line, ref previous, ref doubleValue, cancelToken);
            }
            break;
          }
        }
      }
    }

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        _cts?.Cancel();
        _cts?.Dispose();
        _fileWatcher?.Dispose();
        _lines.CompleteAdding();
        _logProcessor?.Dispose();
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

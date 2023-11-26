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
    private readonly BlockingCollection<Tuple<string, double, bool>> Lines = new(new ConcurrentQueue<Tuple<string, double, bool>>(), 100000);
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly FileSystemWatcher FileWatcher;
    private readonly string FileName;
    private int MinBack;
    private CancellationTokenSource Cts;
    private readonly ManualResetEvent NewDataAvailable = new(false);
    private readonly ILogProcessor LogProcessor;
    private Task ReadFileTask;
    private long InitSize;
    private long CurrentPos;
    private long NextUpdateThreshold;

    public LogReader(ILogProcessor logProcessor, string fileName, int minBack = 0)
    {
      LogProcessor = logProcessor;
      FileName = fileName;
      MinBack = minBack;

      if (Path.GetDirectoryName(fileName) is { } directory)
      {
        logProcessor.LinkTo(Lines);

        FileWatcher = new FileSystemWatcher(directory, Path.GetFileName(fileName))
        {
          NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };

        FileWatcher.Created += OnFileCreated;
        FileWatcher.Deleted += OnFileMoved;
        FileWatcher.Renamed += OnFileMoved;
        FileWatcher.Changed += OnFileChanged;
        FileWatcher.EnableRaisingEvents = true;
        StartReadingFile();
      }
      else
      {
        Log.Error($"Can not open file: {fileName}.");
      }
    }

    public double Progress => CurrentPos / (double)InitSize * 100;
    public IDisposable GetProcessor() => LogProcessor;

    private void OnFileCreated(object sender, FileSystemEventArgs e) => StartReadingFile();
    private void OnFileChanged(object sender, FileSystemEventArgs e) => NewDataAvailable.Set();

    private void OnFileMoved(object sender, FileSystemEventArgs e)
    {
      Cts?.Cancel();
      ReadFileTask?.Wait();
      MinBack = 0;
    }

    private void StartReadingFile()
    {
      Cts = new CancellationTokenSource();
      ReadFileTask = Task.Run(() => ReadFile(FileName, MinBack, Cts.Token), Cts.Token);
    }

    private async Task ReadFile(string fileName, int minBack, CancellationToken cancelToken)
    {
      var bufferSize = 147456;

      try
      {
        var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize);
        InitSize = fs.Length;
        NextUpdateThreshold = InitSize / 50;

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
        SearchLinear(reader, minDate, out var firstLine);

        CurrentPos = fs.Position;
        await using (fs)
        using (reader)
        {
          string line;
          string previous = null;
          double doubleValue = 0;
          var bytesRead = fs.Position;

          // date is now valid so read every line
          while ((line = await reader.ReadLineAsync()) != null && !cancelToken.IsCancellationRequested)
          {
            if (cancelToken.IsCancellationRequested) break;

            // update progress during initial load
            bytesRead += Encoding.UTF8.GetByteCount(line) + 2;
            if (bytesRead >= NextUpdateThreshold)
            {
              CurrentPos = fs.Position;
              NextUpdateThreshold += InitSize / 50; // 2% of InitSize
            }
            else if ((InitSize - bytesRead) < 10000)
            {
              CurrentPos = fs.Position;
            }

            if (firstLine != null)
            {
              HandleLine(firstLine, ref previous, ref doubleValue, cancelToken);
              firstLine = null;
            }

            HandleLine(line, ref previous, ref doubleValue, cancelToken);
          }

          // continue reading for new updates
          while (!cancelToken.IsCancellationRequested)
          {
            while ((line = await reader.ReadLineAsync()) != null)
            {
              HandleLine(line, ref previous, ref doubleValue, cancelToken, true);
            }

            if (cancelToken.IsCancellationRequested) break;
            WaitHandle.WaitAny(new[] { NewDataAvailable, cancelToken.WaitHandle });

            if (!cancelToken.IsCancellationRequested)
            {
              try
              {
                NewDataAvailable.Reset();
              }
              catch (Exception)
              {
                // ignore already disposed
              }
            }
          }
        }
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
        Lines.Add(Tuple.Create(theLine, doubleValue, monitor), cancelToken);
        previous = theLine;
      }
    }

    private static void SearchLinear(StreamReader reader, DateTime minDate, out string firstLine)
    {
      firstLine = null;

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
            firstLine = line;
            break;
          }
        }
      }
    }

    #region IDisposable Support
    private bool DisposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!DisposedValue)
      {
        FileWatcher?.Dispose();
        Lines.CompleteAdding();
        Cts?.Cancel();
        Cts?.Dispose();
        LogProcessor?.Dispose();
        DisposedValue = true;
        NewDataAvailable.Close();
        NewDataAvailable.Dispose();
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

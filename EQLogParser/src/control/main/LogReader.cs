using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EQLogParser
{
  class LogReader : IDisposable
  {
    private BufferBlock<Tuple<string, double, bool>> Lines { get; } =
      new BufferBlock<Tuple<string, double, bool>>(new DataflowBlockOptions { BoundedCapacity = 25000 });
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private readonly FileSystemWatcher FileWatcher;
    private readonly string FileName;
    private int MinBack;
    private CancellationTokenSource Cts;
    private readonly ManualResetEvent NewDataAvailable = new ManualResetEvent(false);
    private readonly IDisposable LogProcessor;
    private Task ReadFileTask;
    private long InitSize;
    private long CurrentPos;
    private long NextUpdateThreshold;

    public LogReader(IDisposable logProcessor, string fileName, int minBack = 0)
    {
      LogProcessor = logProcessor;
      FileName = fileName;
      MinBack = minBack;

      dynamic processor = logProcessor;
      processor.LinkTo(Lines);

      FileWatcher = new FileSystemWatcher(Path.GetDirectoryName(fileName) ?? string.Empty, Path.GetFileName(fileName))
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
      ReadFileTask = Task.Run(() => ReadFile(FileName, Cts.Token, MinBack), Cts.Token);
    }

    private async Task ReadFile(string fileName, CancellationToken cancelToken, int? minBack)
    {
      var bufferSize = 147456;
      var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize);
      InitSize = fs.Length;
      NextUpdateThreshold = InitSize / 50;

      if (minBack == 0)
      {
        fs.Seek(0, SeekOrigin.End);
        CurrentPos = fs.Position;
      }

      StreamReader reader;
      if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
      {
        var gzipStream = new GZipStream(fs, CompressionMode.Decompress);
        reader = new StreamReader(gzipStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize);

        if (minBack > 0)
        {
          SearchCompressed(reader, minBack);
        }
      }
      else
      {
        if (minBack > 0)
        {
          Search(fs, minBack);
        }

        reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize);
      }

      using (fs)
      using (reader)
      {
        string line;
        string previous = null;
        var dateTime = DateTime.MinValue;
        double doubleValue = 0;
        var bytesRead = fs.Position;

        // date is now valid so read every line
        while ((line = await reader.ReadLineAsync()) != null && !cancelToken.IsCancellationRequested)
        {
          if (cancelToken.IsCancellationRequested) break;

          // update progress during intitial load
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

          await HandleLine();
        }

        // continue reading for new updates
        while (!cancelToken.IsCancellationRequested)
        {
          while ((line = await reader.ReadLineAsync()) != null)
          {
            await HandleLine(true);
          }

          if (cancelToken.IsCancellationRequested) break;
          WaitHandle.WaitAny(new[] { NewDataAvailable, cancelToken.WaitHandle });
          NewDataAvailable.Reset();
        }

        async Task HandleLine(bool monitor = false)
        {
          if (line.Length > 28)
          {
            if (previous == null || !line.AsSpan(1, 24).SequenceEqual(previous.AsSpan(1, 24)))
            {
              dateTime = DateUtil.ParseStandardDate(line);
              doubleValue = DateUtil.ToDouble(dateTime);
            }

            if (cancelToken.IsCancellationRequested) return;
            await Lines.SendAsync(Tuple.Create(line, doubleValue, monitor), cancelToken);
            previous = line;
          }
        }
      }
    }

    private void Search(FileStream fs, int? minBack)
    {
      long min = 0;
      var max = fs.Length;
      long? closestGreaterPosition = null;
      var minimumDate = DateTime.Now.AddMinutes(-minBack.Value);

      while (min < max)
      {
        var mid = (min + max) / 2;
        fs.Seek(mid, SeekOrigin.Begin);

        using var tempReader = new StreamReader(fs, Encoding.UTF8, true, 1024, leaveOpen: true);
        if (mid != 0)
        {
          // Discard partial line, if not at the start
          tempReader.ReadLine();
        }

        var positionBeforeReadLine = fs.Position;
        var line = tempReader.ReadLine();
        if (line == null) break;

        var dateTime = DateUtil.ParseStandardDate(line);
        if (dateTime >= minimumDate)
        {
          closestGreaterPosition = positionBeforeReadLine;
          max = mid;
        }
        else
        {
          min = fs.Position;
        }
      }

      if (closestGreaterPosition.HasValue)
        fs.Seek(closestGreaterPosition.Value, SeekOrigin.Begin);
    }

    private void SearchCompressed(StreamReader reader, int? minBack)
    {
      var minimumDate = DateTime.Now.AddMinutes(-minBack.Value);

      string line;
      while ((line = reader.ReadLine()) != null)
      {
        var dateTime = DateUtil.ParseStandardDate(line);
        if (dateTime >= minimumDate)
        {
          break;
        }
      }
    }

    public void Dispose()
    {
      Cts?.Cancel();

      try
      {
        ReadFileTask?.Wait();
      }
      catch (AggregateException ex)
      {
        LOG.Warn(ex);
      }

      Cts?.Dispose();
      FileWatcher?.Dispose();
      NewDataAvailable?.Dispose();
      Lines.Complete();
      LogProcessor?.Dispose();
    }
  }
}

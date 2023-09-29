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
      new BufferBlock<Tuple<string, double, bool>>(new DataflowBlockOptions { BoundedCapacity = 20000 });
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private readonly FileSystemWatcher FileWatcher;
    private readonly string FileName;
    private readonly int MinBack;
    private CancellationTokenSource Cts;
    private ManualResetEvent NewDataAvailable = new ManualResetEvent(false);
    private IDisposable LogProcessor;
    private Task ReadFileTask;
    private long InitSize;
    private long CurrentPos;
    private long NextUpdateThreshold;

    public LogReader(IDisposable logProcessor, string fileName, int minBack)
    {
      LogProcessor = logProcessor;
      FileName = fileName;
      MinBack = minBack;

      dynamic processor = logProcessor;
      processor.LinkTo(Lines);

      FileWatcher = new FileSystemWatcher(Path.GetDirectoryName(fileName), Path.GetFileName(fileName))
      {
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
      };

      FileWatcher.Deleted += OnFileMoved;
      FileWatcher.Renamed += OnFileMoved;
      FileWatcher.Changed += OnFileChanged;
      FileWatcher.EnableRaisingEvents = true;
      StartReadingFile();
    }

    public double Progress => CurrentPos / (double)InitSize * 100;

    private void OnFileMoved(object sender, FileSystemEventArgs e)
    {
      Cts?.Cancel();
      ReadFileTask?.Wait();
      StartReadingFile();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
      NewDataAvailable.Set();
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
        reader = new StreamReader(gzipStream, bufferSize: bufferSize);
      }
      else
      {
        reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize);
      }

      using (fs)
      using (reader)
      {
        string line = null;
        string previous = null;
        var dateTime = DateTime.MinValue;
        double doubleValue = 0;
        var validTimeframe = false;
        var minimumDate = minBack > -1 ? DateTime.Now.AddMinutes(-minBack.Value) : DateTime.MinValue;
        long bytesRead = 0;

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
              dateTime = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", line, 5);
              doubleValue = DateUtil.ToDouble(dateTime);
            }
            else if (!validTimeframe)
            {
              return;
            }

            if (dateTime != DateTime.MinValue && (validTimeframe || minimumDate == DateTime.MinValue || dateTime >= minimumDate))
            {
              validTimeframe = true;
              await Lines.SendAsync(Tuple.Create(line, doubleValue, monitor), cancelToken);
            }
            previous = line;
          }
        }
      }

      Lines.Complete();
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

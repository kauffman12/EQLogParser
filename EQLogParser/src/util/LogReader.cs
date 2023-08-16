using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace EQLogParser
{
  class LogReader
  {
    public delegate void ParseLineCallback(string line, long position, double dateTime);

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public bool FileLoadComplete = false;
    public long FileSize;
    private readonly string FileName;
    private readonly ParseLineCallback LoadingCallback;
    private bool Running = false;
    private readonly int LastMins;
    private readonly DateUtil DateUtil = new DateUtil();

    public LogReader(string fileName, ParseLineCallback loadingCallback, int lastMins)
    {
      FileName = fileName;
      LoadingCallback = loadingCallback;
      LastMins = lastMins;
    }

    public void Start()
    {
      Running = true;
      new Thread(() =>
      {
        try
        {
          var logFilePath = FileName.Substring(0, FileName.LastIndexOf("\\", StringComparison.Ordinal)) + "\\";
          var logFileName = FileName.Substring(FileName.LastIndexOf("\\", StringComparison.Ordinal) + 1);
          var isGzip = logFileName.EndsWith(".gz", StringComparison.Ordinal);

          Stream gs;
          Stream fs = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
          StreamReader reader;
          FileSize = fs.Length;
          var dateTime = double.NaN;

          if (!isGzip) // fs.Length works and we can seek properly
          {
            reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096);
            if (LastMins > -1 && fs.Length > 0)
            {
              double now = DateTime.Now.Ticks / TimeSpan.FromSeconds(1).Ticks;
              var position = fs.Length / 2;
              long lastPos = 0;
              long value = -1;

              fs.Seek(position, SeekOrigin.Begin);
              reader.ReadLine();

              while (!reader.EndOfStream && value != 0)
              {
                var line = reader.ReadLine();
                var inRange = DateUtil.HasTimeInRange(now, line, LastMins, out dateTime);
                value = Math.Abs(lastPos - position) / 2;

                lastPos = position;
                position += inRange ? -value : value;

                fs.Seek(position, SeekOrigin.Begin);
                reader.DiscardBufferedData();
                reader.ReadLine(); // seek will lead to partial line
              }

              fs.Seek(lastPos, SeekOrigin.Begin);
              reader.DiscardBufferedData();
              reader.ReadLine(); // seek will lead to partial line
            }
          }
          else
          {
            gs = new GZipStream(fs, CompressionMode.Decompress);
            reader = new StreamReader(gs, System.Text.Encoding.UTF8, true, 4096);

            if (LastMins > -1 && fs.Length > 0)
            {
              double now = DateTime.Now.Ticks / TimeSpan.FromSeconds(1).Ticks;
              while (!reader.EndOfStream)
              {
                // seek the slow way since we can't jump around a zip stream
                var line = reader.ReadLine();
                if (DateUtil.HasTimeInRange(now, line, LastMins, out dateTime))
                {
                  LoadingCallback(line, fs.Position, dateTime);
                  break;
                }
              }

              // complete
              LoadingCallback(null, fs.Position, dateTime);
            }
          }

          while (!reader.EndOfStream && Running)
          {
            var line = reader.ReadLine();
            LoadingCallback(line, fs.Position, DateUtil.ParseDate(line));
          }

          FileLoadComplete = true;

          // setup watcher
          var fsw = new FileSystemWatcher
          {
            Path = logFilePath,
            Filter = logFileName
          };

          // events to notify for changes
          //fsw.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.CreationTime;
          fsw.EnableRaisingEvents = true;

          while (Running)
          {
            var result = fsw.WaitForChanged(WatcherChangeTypes.Renamed | WatcherChangeTypes.Deleted | WatcherChangeTypes.Changed, 2000);

            if (reader == null && File.Exists(FileName))
            {
              fs = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
              reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096);
            }

            switch (result.ChangeType)
            {
              case WatcherChangeTypes.Renamed:
              case WatcherChangeTypes.Deleted:
                if (reader != null)
                {
                  reader.Close();
                  reader = null;
                }
                break;
              case WatcherChangeTypes.Changed:
                if (reader != null)
                {
                  while (Running && !reader.EndOfStream)
                  {
                    var line = reader.ReadLine();
                    LoadingCallback(line, fs.Length, DateUtil.ParseDate(line));
                  }
                }
                break;
            }
          }

          if (reader != null)
          {
            reader.Close();
          }

          fsw.Dispose();
        }
        catch (IOException e)
        {
          LOG.Error(e);
        }
      }).Start();
    }

    public void Stop()
    {
      Running = false;
    }
  }
}

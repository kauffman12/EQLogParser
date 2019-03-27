using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace EQLogParser
{
  class LogReader
  {
    public delegate void ParseLineCallback(List<string> lines, long position);

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int BUFFER_SIZE = 50000;

    public bool FileLoadComplete = false;
    public long FileSize;
    private string FileName;
    private readonly ParseLineCallback LoadingCallback;
    private bool Running = false;
    private readonly bool MonitorOnly;
    private readonly int LastMins;
    private DateUtil DateUtil = new DateUtil();

    public LogReader(string fileName, ParseLineCallback loadingCallback, bool monitorOnly, int lastMins)
    {
      FileName = fileName;
      LoadingCallback = loadingCallback;
      MonitorOnly = monitorOnly;
      LastMins = lastMins;
    }

    public void Start()
    {
      Running = true;
      new Thread(() =>
      {
        try
        {
          string logFilePath = FileName.Substring(0, FileName.LastIndexOf("\\")) + "\\";
          string logFileName = FileName.Substring(FileName.LastIndexOf("\\") + 1);
          bool isGzip = logFileName.EndsWith(".gz");

          Stream gs;
          Stream fs = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
          StreamReader reader;
          FileSize = fs.Length;

          List<string> buffer = new List<string>(BUFFER_SIZE);
         
          if (!isGzip) // fs.Length works and we can seek properly
          {
            reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096);
            if (!MonitorOnly && LastMins > -1 && fs.Length > 0)
            {
              double now = DateTime.Now.Ticks / TimeSpan.FromSeconds(1).Ticks;
              long position = fs.Length / 2;
              long lastPos = 0;
              long value = -1;

              fs.Seek(position, SeekOrigin.Begin);
              reader.ReadLine();

              while (!reader.EndOfStream && value != 0)
              {
                string line = reader.ReadLine();
                bool inRange = DateUtil.HasTimeInRange(now, line, LastMins);
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

            if (MonitorOnly)
            {
              fs.Seek(0, SeekOrigin.End);
              FileLoadComplete = true;
              LoadingCallback(null, fs.Position);
            }
          }
          else
          {
            gs = new GZipStream(fs, CompressionMode.Decompress);
            reader = new StreamReader(gs, System.Text.Encoding.UTF8, true, 4096);

            if (!MonitorOnly && LastMins > -1 && fs.Length > 0)
            {
              double now = DateTime.Now.Ticks / TimeSpan.FromSeconds(1).Ticks;
              while (!reader.EndOfStream)
              {
                // seek the slow way since we can't jump around a zip stream
                string line = reader.ReadLine();
                if (DateUtil.HasTimeInRange(now, line, LastMins))
                {
                  buffer.Add(line);
                  if (buffer.Count >= BUFFER_SIZE)
                  {
                    LoadingCallback(buffer, fs.Position);
                    buffer.Clear();
                  }
                  break;
                }
              }

              // complete
              LoadingCallback(null, fs.Position);
            }

            if (MonitorOnly)
            {
              char[] block = new char[16384];
              while (!reader.EndOfStream)
              {
                reader.ReadBlock(block, 0, block.Length);
              }

              FileLoadComplete = true;
              LoadingCallback(null, fs.Position);
            }
          }

          while (!reader.EndOfStream && Running)
          {
            buffer.Add(reader.ReadLine());
            if (buffer.Count >= BUFFER_SIZE)
            {
              LoadingCallback(buffer, fs.Position);
              buffer.Clear();
            }
          }

          if (buffer.Count > 0)
          {
            LoadingCallback(buffer, fs.Position);
            buffer.Clear();
          }

          buffer.Add(null);
          FileLoadComplete = true;

          // setup watcher
          FileSystemWatcher fsw = new FileSystemWatcher
          {
            Path = logFilePath,
            Filter = logFileName
          };

          // events to notify for changes
          fsw.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.CreationTime;
          fsw.EnableRaisingEvents = true;

          bool exitOnError = false;

          while (Running && !exitOnError)
          {
            WaitForChangedResult result = fsw.WaitForChanged(WatcherChangeTypes.Deleted | WatcherChangeTypes.Changed, 2000);

            switch (result.ChangeType)
            {
              case WatcherChangeTypes.Deleted:
                // file gone
                exitOnError = true;
                break;
              case WatcherChangeTypes.Changed:
                if (reader != null)
                {
                  while (Running && !reader.EndOfStream)
                  {
                    buffer[0] = reader.ReadLine();
                    LoadingCallback(buffer, fs.Length);
                  }
                }
                break;
            }
          }

          reader.Close();
          fsw.Dispose();
        }
        catch (Exception e)
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

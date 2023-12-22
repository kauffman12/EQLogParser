using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;

namespace EQLogManager
{
  public partial class EQLogService : ServiceBase
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Regex FilenameRegex = new(@"^eqlog_([A-Za-z]+)(_[A-Za-z]+)?(_[A-Za-z]+)?\.txt$");
    private const long FiveMegabytes = 5 * 1024 * 1024;
    private readonly string _configFile;
    private readonly List<string> _sourceFiles = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private FileSystemWatcher _configWatcher;
    private bool _serviceStopped;
    private Thread _workerThread;
    private string _archiveDir;
    private bool _compress;
    private long _maxSize = -1;
    private int _scheduleDay = -1;
    private int _scheduleHour = -1;
    private int _scheduleMin = -1;
    private object _locker = new();

    public EQLogService()
    {
      InitializeComponent();

      var appDataRoamingPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
      var configFolderPath = Path.Combine(appDataRoamingPath, "EQLogParser", "config");
      _configFile = Path.Combine(configFolderPath, "logManager.txt");
    }

    // for debug
    public void StartDebug()
    {
      OnStart(Array.Empty<string>());
    }

    public void StopDebug()
    {
      OnStop();
    }

    protected override void OnStart(string[] args)
    {
      _workerThread = new Thread(WorkerThread)
      {
        IsBackground = true
      };

      _workerThread.Start();
    }

    protected override void OnStop()
    {
      _serviceStopped = true;

      lock (_locker)
      {
        if (_configWatcher != null)
        {
          _configWatcher.EnableRaisingEvents = false;
          _configWatcher.Dispose();
          _configWatcher = null;
        }
      }

      Reset();
      _workerThread?.Join();
    }

    private void WorkerThread()
    {
      while (!_serviceStopped)
      {
        lock (_locker)
        {
          if (_configWatcher == null && !_serviceStopped)
          {
            try
            {
              _configWatcher = new FileSystemWatcher
              {
                Path = Path.GetDirectoryName(_configFile),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                Filter = Path.GetFileName(_configFile)
              };

              // initial read
              ReadConfig(true);

              _configWatcher.Changed += ConfigFileChanged;
              _configWatcher.Created += ConfigFileChanged;
              _configWatcher.EnableRaisingEvents = true;
            }
            catch (Exception)
            {
              // no config specified yet
            }
          }
        }

        // if not archiving based on max size
        if (_maxSize == -1)
        {
          var now = DateTime.Now;
          if ((int)now.DayOfWeek == _scheduleDay && now.Hour == _scheduleHour && now.Minute == _scheduleMin)
          {
            ArchiveOnSchedule(now);
          }
        }

        // Wait for 60 seconds
        Thread.Sleep(60000);
      }
    }

    private void ConfigFileChanged(object sender, FileSystemEventArgs e) => ReadConfig();

    private void ReadConfig(bool init = false)
    {
      Reset();

      try
      {
        var fileInfo = new FileInfo(_configFile);
        if (fileInfo.Exists)
        {
          if (fileInfo.Length > FiveMegabytes)
          {
            throw new Exception("Error reading config file. It looks to be corrupt.");
          }

          foreach (var line in File.ReadAllLines(_configFile))
          {
            if (!string.IsNullOrEmpty(line) && line.Split('=') is { Length: > 1 } split)
            {
              if (split[0] == "Enabled" && bool.TryParse(split[1], out var enabled) && !enabled)
              {
                Reset();
                if (!init)
                {
                  Log.Info("Configuration Updated. LogManager Inactive.");
                }
                return;
              }

              if (split[0] == "ArchiveDir" && Directory.Exists(split[1]))
              {
                _archiveDir = Path.GetFullPath(split[1]);
              }
              else if (split[0] == "SourceFile" && IsPathFullyQualified(split[1]))
              {
                var fixPath = Path.GetFullPath(split[1]);
                if (!_sourceFiles.Contains(fixPath))
                {
                  _sourceFiles.Add(fixPath);
                }
              }
              else if (split[0] == "Compress" && bool.TryParse(split[1], out var compress))
              {
                _compress = compress;
              }
              else if (split[0] == "MaxSize" && long.TryParse(split[1], out var maxSize))
              {
                _maxSize = maxSize;
              }
              else if (split[0] == "ScheduleDay" && int.TryParse(split[1], out var day) &&
                       day is >= (int)DayOfWeek.Sunday and <= (int)DayOfWeek.Saturday)
              {
                _scheduleDay = day;
              }
              else if (split[0] == "ScheduleHour" && int.TryParse(split[1], out var hour) && hour is >= 0 and <= 23)
              {
                _scheduleHour = hour;
              }
              else if (split[0] == "ScheduleMin" && int.TryParse(split[1], out var min) && min is >= 0 and <= 59)
              {
                _scheduleMin = min;
              }
            }
          }

          if (_archiveDir == null)
          {
            throw new Exception("Archive Directory does not exist or was not configured.");
          }

          if (_sourceFiles.Count == 0)
          {
            throw new Exception("No Source Files were configured.");
          }

          if (_maxSize == -1 && (_scheduleDay == -1 || _scheduleHour == -1 || _scheduleMin == -1))
          {
            throw new Exception("Invalid Schedule/Max Size configuration. Neither option appears valid.");
          }

          if (_maxSize != -1 && _scheduleDay != -1 && _scheduleHour != -1 && _scheduleMin != -1)
          {
            throw new Exception("Invalid Schedule/Max Size configuration. Both options can not be configured.");
          }

          // if size configuration then start watching for changes
          if (_maxSize > -1)
          {
            StartWatchers();
          }

          if (!init)
          {
            Log.Info("Configuration Updated. LogManager Active.");
          }
        }
        else
        {
          throw new Exception("No configuration file found.");
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex);
      }
    }

    private void StartWatchers()
    {
      var handled = new List<string>();
      foreach (var file in _sourceFiles)
      {
        var dir = Path.GetDirectoryName(file);
        if (dir != null && !handled.Contains(dir))
        {
          handled.Add(dir);
          WatchDirectory(dir);
        }
      }
    }

    private void WatchDirectory(string directoryPath)
    {
      var watcher = new FileSystemWatcher
      {
        Path = directoryPath,
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        Filter = "*.txt"
      };

      watcher.Changed += OnChanged;
      watcher.EnableRaisingEvents = true;
      _watchers.Add(watcher);
    }

    private void OnChanged(object source, FileSystemEventArgs e)
    {
      if (e.Name == null)
      {
        return;
      }

      var fixPath = Path.GetFullPath(e.FullPath);
      var match = FilenameRegex.Match(e.Name);
      if (match.Success && (_sourceFiles.Count == 0 || _sourceFiles.Contains(fixPath)))
      {
        var fileInfo = new FileInfo(fixPath);
        if (fileInfo.Length >= _maxSize)
        {
          ArchiveFile(DateTime.Now, fixPath);
        }
      }
    }

    private void ArchiveOnSchedule(DateTime dateTime)
    {
      var handled = new List<string>();
      foreach (var file in _sourceFiles)
      {
        var fixPath = Path.GetFullPath(file);
        if (!handled.Contains(fixPath))
        {
          handled.Add(fixPath);
          ArchiveFile(dateTime, fixPath);
        }
      }
    }

    private void ArchiveFile(DateTime dateTime, string file)
    {
      if (_archiveDir == null || !Directory.Exists(_archiveDir))
      {
        throw new Exception("Can not archive log files. Archive Directory does not exist.");
      }

      var fileName = Path.GetFileName(file);
      var match = FilenameRegex.Match(fileName);

      if (match.Success)
      {
        var updatedFileName = GenFileName(dateTime, match);
        if (Directory.Exists(_archiveDir))
        {
          var destPath = Path.Combine(_archiveDir, updatedFileName);
          File.Move(file, destPath);

          if (_compress)
          {
            CompressFile(destPath);
          }
        }
      }
    }

    private static async void CompressFile(string filePath)
    {
      var compressedFilePath = $"{filePath}.gz";

      try
      {
        using var originalFileStream = File.OpenRead(filePath);
        using var compressedFileStream = File.Create(compressedFilePath);
        using var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress);
        await originalFileStream.CopyToAsync(compressionStream);
      }
      catch (Exception ex)
      {
        Log.Error(ex);
      }

      try
      {
        File.Delete(filePath);
      }
      catch (Exception)
      {
        // do nothing -- shouldn't happen
      }
    }

    private void Reset()
    {
      // reset
      foreach (var watcher in _watchers)
      {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
      }

      _watchers.Clear();
      _maxSize = _scheduleDay = _scheduleHour = _scheduleMin = -1;
      _archiveDir = null;
      _sourceFiles.Clear();
      _compress = false;
    }

    private static string GenFileName(DateTime dateTime, Capture match)
    {
      var dateSuffix = dateTime.ToString("yyyyMMdd");
      var baseName = match.Value.Substring(0, match.Value.Length - 4);
      return $"{baseName}_{dateSuffix}_{dateTime.Millisecond}.txt";
    }

    private static bool IsPathFullyQualified(string path)
    {
      // For Windows paths
      if (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
      {
        return true;
      }

      // For Unix-like paths
      if (path.StartsWith("/"))
      {
        return true;
      }

      return false;
    }

  }
}

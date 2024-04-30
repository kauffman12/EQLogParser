using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal static partial class FileUtil
  {
    private const long M = 1000000;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Regex FileNameRegex = TheFileNameRegex();
    private static readonly object LockObject = new();
    private static readonly HashSet<LogReader> ArchiveQueue = [];

    private static readonly Dictionary<string, long> ArchiveFileSizes = new()
    {
      { "any size", 0 },
      { "25m", 25 * M },
      { "50m", 50 * M },
      { "75m", 75 * M },
      { "100m", 100 * M },
      { "250m", 250 * M },
      { "500m", 500 * M },
      { "750m", 750 * M },
      { "1g", 1000 * M },
      { "1.5g", 1500 * M },
      { "2g", 2000 * M }
    };

    private static readonly Dictionary<string, int> ArchiveFileAges = new()
    {
      { "any age", 0 },
      { "1 day", 1 },
      { "3 days", 3 },
      { "1 week", 7 },
      { "2 weeks", 14 },
      { "3 weeks", 21 },
      { "1 month", 28 }
    };

    internal static void ArchiveFile(LogReader logReader)
    {
      var file = Path.GetFileName(logReader.FileName);
      if (string.IsNullOrEmpty(file) || !FileNameRegex.IsMatch(file) || !ConfigUtil.IfSet("LogManagementEnabled"))
      {
        return;
      }

      lock (LockObject)
      {
        ArchiveQueue.Add(logReader);
        if (ArchiveQueue.Count == 1)
        {
          Task.Delay(2500).ContinueWith(_ => ArchiveProcess());
        }
      }
    }

    private static void ArchiveProcess()
    {
      lock (LockObject)
      {
        var notReady = ArchiveQueue.Where(reader => reader.GetProgress() < 100.0).Select(reader => reader.FileName).ToArray();
        foreach (var reader in ArchiveQueue.ToArray())
        {
          if (notReady.Contains(reader.FileName))
          {
            continue;
          }

          ArchiveQueue.Remove(reader);
          var path = reader.FileName;

          if (!File.Exists(path))
          {
            // was archived by another process?
            continue;
          }

          // can remove defaults in the future. fixing issue where config was enabled but settings were never chosen/saved
          var savedFileSize = ConfigUtil.GetSetting("LogManagementMinFileSize", "500M");
          if (string.IsNullOrEmpty(savedFileSize))
          {
            continue;
          }

          savedFileSize = savedFileSize.ToLower();
          if (!ArchiveFileSizes.ContainsKey(savedFileSize))
          {
            continue;
          }

          var savedFileAge = ConfigUtil.GetSetting("LogManagementMinFileAge", "1 Week");
          if (string.IsNullOrEmpty(savedFileAge))
          {
            continue;
          }

          savedFileAge = savedFileAge.ToLower();
          if (!ArchiveFileAges.ContainsKey(savedFileAge))
          {
            continue;
          }

          var archiveFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
          if (string.IsNullOrEmpty(archiveFolder) || Path.GetDirectoryName(archiveFolder) == null)
          {
            continue;
          }

          var fileInfo = new FileInfo(path);
          if (fileInfo.CreationTime > DateTime.Now.Subtract(TimeSpan.FromDays(ArchiveFileAges[savedFileAge])))
          {
            continue;
          }

          if (fileInfo.Length < ArchiveFileSizes[savedFileSize])
          {
            continue;
          }

          try
          {
            var formatted = DateTime.Now.ToString("_yyyyMMddHHmm_ssfff") + ".txt";
            var destination = archiveFolder + Path.DirectorySeparatorChar + fileInfo.Name.Replace(".txt", formatted);
            File.Move(path, destination);
            CompressFile(destination);
            Log.Info($"Archived File: {path}");
          }
          catch (Exception e)
          {
            Log.Error(e);
          }
        }

        if (ArchiveQueue.Count > 0)
        {
          Task.Delay(5000).ContinueWith(_ => ArchiveProcess());
        }
      }
    }

    private static async void CompressFile(string filePath)
    {
      var compressedFilePath = $"{filePath}.gz";
      var originalFileStream = File.OpenRead(filePath);
      var compressedFileStream = File.Create(compressedFilePath);
      var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress);
      await originalFileStream.CopyToAsync(compressionStream);
      await compressionStream.DisposeAsync();
      await compressedFileStream.DisposeAsync();
      await originalFileStream.DisposeAsync();

      try
      {
        File.Delete(filePath);
      }
      catch (Exception)
      {
        // ignore delete errors
      }
    }

    internal static void ParseFileName(string theFile, ref string name, ref string server)
    {
      var file = Path.GetFileName(theFile);
      var matches = FileNameRegex.Matches(file);

      if (matches.Count == 1)
      {
        if (matches[0].Groups.Count > 1)
        {
          name = matches[0].Groups[1].Value;
        }

        if (matches[0].Groups.Count > 2)
        {
          server = matches[0].Groups[2].Value;
        }
      }
    }

    internal static StreamReader GetStreamReader(FileStream f, double start = 0)
    {
      StreamReader s;
      if (!f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
      {
        if (f.Length > 100000000 && start > 0)
        {
          SetStartingPosition(f, start);
        }

        s = new StreamReader(f);
      }
      else
      {
        var gs = new GZipStream(f, CompressionMode.Decompress);
        s = new StreamReader(gs, Encoding.UTF8, true, 4096);
      }

      return s;
    }

    internal static void SetStartingPosition(FileStream f, double time, long left = 0, long right = 0, long good = 0, int count = 0)
    {
      if (count <= 8)
      {
        if (f.Position == 0)
        {
          right = f.Length;
          f.Seek(f.Length / 2, SeekOrigin.Begin);
        }

        try
        {
          var s = new StreamReader(f);
          s.ReadLine();
          var check = TimeRange.TimeCheck(s.ReadLine(), time);
          s.DiscardBufferedData();

          long pos;
          if (check)
          {
            pos = left + ((f.Position - left) / 2);
            right = f.Position;
          }
          else
          {
            pos = right - ((right - f.Position) / 2);
            good = left = f.Position;
          }

          f.Seek(pos, SeekOrigin.Begin);
          SetStartingPosition(f, time, left, right, good, count + 1);
        }
        catch (IOException ioe)
        {
          Log.Error("Problem searching log file", ioe);
        }
        catch (OutOfMemoryException ome)
        {
          Log.Debug("Out of memory", ome);
        }
      }
      else if (f.Position != good)
      {
        f.Seek(good, SeekOrigin.Begin);
      }
    }

    [GeneratedRegex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.txt", RegexOptions.Singleline)]
    private static partial Regex TheFileNameRegex();
  }
}

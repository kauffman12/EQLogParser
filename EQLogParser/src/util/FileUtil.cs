using log4net;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  public static partial class FileUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Regex ArchivedFileNameRegex = TheArchivedFileNameRegex();
    private static readonly Regex ServerFileNameRegex = TheServerFileNameRegex();

    // public so backup util can use
    public static string BuildBackupFilename()
    {
      // get file name
      var version = typeof(FileUtil).Assembly.GetName().Version?.ToString();
      var dateTime = DateTime.Now.ToString("yyyyMMdd-ssfff", CultureInfo.InvariantCulture);
      version = string.IsNullOrEmpty(version) ? "unknown" : version[..^2];
      return $"EQLogParser_backup_{version}_{dateTime}.zip";
    }

    internal static string GetDirFromPath(string path)
    {
      if (!string.IsNullOrEmpty(path))
      {
        try
        {
          var name = Path.GetDirectoryName(path);
          if (Directory.Exists(name))
          {
            return name;
          }
        }
        catch (Exception)
        {
          // ignore
        }
      }

      return null;
    }

    internal static List<string> FindArchivedLogFiles(string player, string server, double start)
    {
      var matchingFiles = new List<(string filePath, DateTime date)>();

      var archiveFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (string.IsNullOrEmpty(archiveFolder) || Path.GetDirectoryName(archiveFolder) == null)
      {
        return [];
      }

      var formats = new[] { "yyyyMMddHHmm", "yyyyMMdd" };

      foreach (var file in Directory.EnumerateFiles(archiveFolder, "*.*", SearchOption.AllDirectories))
      {
        var fileName = Path.GetFileName(file);
        var match = ArchivedFileNameRegex.Match(fileName);

        if (match.Success &&
            string.Equals(match.Groups["player"].Value, player, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(match.Groups["server"].Value, server, StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(match.Groups["datetime"].Value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fileDate))
        {
          if (start == 0 || DateUtil.ToDouble(fileDate) >= start)
          {
            matchingFiles.Add((file, fileDate));
          }
        }
      }

      return [.. matchingFiles.OrderByDescending(f => f.date).Select(f => f.filePath)];
    }

    internal static bool ParseFileName(string theFile, out string name, out string server)
    {
      name = "You";
      server = "Unknown";
      var found = 0;
      var file = Path.GetFileName(theFile);
      var matches = ServerFileNameRegex.Matches(file);

      if (matches.Count == 1)
      {
        if (matches[0].Groups.Count > 1)
        {
          name = matches[0].Groups[1].Value;
          found++;
        }

        if (matches[0].Groups.Count > 2)
        {
          server = matches[0].Groups[2].Value;
          found++;
        }
      }

      return found == 2;
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

    internal static void SafeWriteAllLines(string path, IEnumerable<string> lines)
    {
      try
      {
        File.WriteAllLines(path, lines);
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException ex)
      {
        Log.Error(ex);
      }
      catch (SecurityException ex)
      {
        Log.Error(ex);
      }
      catch (ArgumentNullException ex)
      {
        Log.Error(ex);
      }
    }

   [GeneratedRegex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.(txt|log)(?:\.gz)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TheServerFileNameRegex();

    [GeneratedRegex(@"^eqlog_(?<player>[^_]+)_(?<server>[^_]+)_(?<datetime>\d{8}(?:\d{4})?)_\d+\.txt(?:\.gz)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TheArchivedFileNameRegex();
  }
}

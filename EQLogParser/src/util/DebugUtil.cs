using System.Collections.Generic;
using System.IO;

namespace EQLogParser
{
  class DebugUtil
  {
    private static Dictionary<long, LineInfo> UnprocessedLines = new Dictionary<long, LineInfo>();
    private static object LockObject = new object();
    private static string UNPROCESSED_LINES_FILE = "UnprocessedLines.log";
    private static StreamWriter Output;

    public static void Reset()
    {
      if (ConfigUtil.Debug)
      {
        if (Output != null)
        {
          Output.Close();
        }

        ConfigUtil.RemoveFileIfExists(ConfigUtil.LogsDir + UNPROCESSED_LINES_FILE);
        Output = File.CreateText(ConfigUtil.LogsDir + UNPROCESSED_LINES_FILE);
      }
    }

    public static void RegisterLine(long id, string line, int count)
    {
      if (ConfigUtil.Debug)
      {
        lock (LockObject)
        {
          UnprocessedLines[id] = new LineInfo { Line = line, Count = count };
        }
      }
    }

    public static void UnregisterLine(long id, bool handled)
    {
      if (ConfigUtil.Debug)
      {
        lock (LockObject)
        {
          if (handled)
          {
            UnprocessedLines.Remove(id);
          }
          else
          {
            if (UnprocessedLines.TryGetValue(id, out LineInfo info))
            {
              info.Count -= 1;

              if (info.Count == 0)
              {
                UnprocessedLines.Remove(id);
                Output.WriteLine(info.Line);
              }
            }
          }
        }
      }
    }

    private class LineInfo
    {
      public int Count { get; set; }
      public string Line { get; set; }
    }
  }
}

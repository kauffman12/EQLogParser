using System.Collections.Concurrent;
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
    private static ConcurrentDictionary<long, string> LineTypes= new ConcurrentDictionary<long, string>();

    private DebugUtil()
    {

    }

    public static string GetLineType(long lineNum)
    {
      LineTypes.TryGetValue(lineNum, out string value);
      return value;
    }

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

    public static void RegisterLine(long lineNum, string line, int count)
    {
      if (ConfigUtil.Debug)
      {
        lock (LockObject)
        {
          UnprocessedLines[lineNum] = new LineInfo { Line = line, Count = count };
        }
      }
    }

    public static void UnregisterLine(long lineNum, bool handled, string type = null)
    {
      if (handled && type != null)
      {
        LineTypes[lineNum] = type;
      }

      if (ConfigUtil.Debug)
      {
        lock (LockObject)
        {
          if (handled)
          {           
            UnprocessedLines.Remove(lineNum);
          }
          else
          {
            if (UnprocessedLines.TryGetValue(lineNum, out LineInfo info))
            {
              info.Count -= 1;

              if (info.Count == 0)
              {
                UnprocessedLines.Remove(lineNum);
                Output.WriteLine(info.Line);
              }
            }
          }
        }
      }
    }

    public static void WriteLine(string line)
    {
      if (ConfigUtil.Debug)
      {
        lock(LockObject)
        {
          Output.WriteLine(line);
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

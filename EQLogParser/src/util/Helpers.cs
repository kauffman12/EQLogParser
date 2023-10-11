using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace EQLogParser
{
  static class Helpers
  {
    internal static DictionaryAddHelper<long, int> LongIntAddHelper = new();
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public static void AddAction(List<ActionGroup> blockList, IAction action, double beginTime)
    {
      if (blockList.LastOrDefault() is { } last && last.BeginTime == beginTime)
      {
        last.Actions.Add(action);
      }
      else
      {
        var newSegment = new ActionGroup { BeginTime = beginTime };
        newSegment.Actions.Add(action);
        blockList.Add(newSegment);
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
        s = new StreamReader(gs, System.Text.Encoding.UTF8, true, 4096);
      }

      return s;
    }

    internal static void SetStartingPosition(FileStream f, double time, long left = 0, long right = 0, long good = 0, int count = 0)
    {
      if (count <= 5)
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

          long pos = 0;
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
          LOG.Error("Problem searching log file", ioe);
        }
        catch (OutOfMemoryException ome)
        {
          LOG.Debug("Out of memory", ome);
        }
      }
      else if (f.Position != good)
      {
        f.Seek(good, SeekOrigin.Begin);
      }
    }
  }

  internal class DictionaryUniqueListHelper<T1, T2>
  {
    internal int AddToList(Dictionary<T1, List<T2>> dict, T1 key, T2 value)
    {
      var size = 0;
      lock (dict)
      {
        if (!dict.ContainsKey(key))
        {
          dict[key] = new List<T2>();
        }

        if (!dict[key].Contains(value))
        {
          dict[key].Add(value);
          size = dict[key].Count;
        }
      }
      return size;
    }
  }

  internal class DictionaryAddHelper<T1, T2>
  {
    internal void Add(Dictionary<T1, T2> dict, T1 key, T2 value)
    {
      lock (dict)
      {
        dict.TryAdd(key, default);

        dynamic temp = dict[key];
        temp += value;
        dict[key] = temp;
      }
    }
  }
}

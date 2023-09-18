using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Xml;

namespace EQLogParser
{
  static class Helpers
  {
    internal static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();
    private static readonly DateUtil DateUtil = new DateUtil();
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public static void AddAction(List<ActionBlock> blockList, IAction action, double beginTime)
    {
      if (blockList.LastOrDefault() is ActionBlock last && last.BeginTime == beginTime)
      {
        last.Actions.Add(action);
      }
      else
      {
        var newSegment = new ActionBlock { BeginTime = beginTime };
        newSegment.Actions.Add(action);
        blockList.Add(newSegment);
      }
    }

    internal static void AddToCollection(ObservableCollection<string> props, params string[] values) => values.ToList().ForEach(value => props.Add(value));

    internal static string GetText(XmlNode node, string value)
    {
      if (node.SelectSingleNode(value) is XmlNode selected)
      {
        return selected.InnerText?.Trim();
      }

      return "";
    }

    internal static bool SCompare(string s, int start, int count, string test) => s.AsSpan().Slice(start, count).SequenceEqual(test);

    internal static void LoadDictionary(string path)
    {
      var dict = new ResourceDictionary
      {
        Source = new Uri(path, UriKind.RelativeOrAbsolute)
      };

      foreach (var key in dict.Keys)
      {
        Application.Current.Resources[key] = dict[key];
      }
    }

    internal static void OpenFileWithDefault(string fileName)
    {
      try
      {
        Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    internal static string CreateRecordKey(string type, string subType)
    {
      var key = subType;

      if (type == Labels.DD || type == Labels.DOT)
      {
        key = type + "=" + key;
      }

      return key;
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
          var check = TimeCheck(s.ReadLine(), time);
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

    internal static bool TimeCheck(string line, double start, double end = -1)
    {
      var pass = false;
      if (!string.IsNullOrEmpty(line) && line.Length > 24)
      {
        var logTime = DateUtil.ParseDate(line);
        if (!double.IsNaN(logTime))
        {
          if (end > -1)
          {
            pass = logTime >= start && logTime <= end;
          }
          else
          {
            pass = (start > 0) ? logTime >= start : false;
          }
        }
      }

      return pass;
    }

    internal static bool TimeCheck(string line, double start, TimeRange range, out bool exceeds)
    {
      var pass = false;
      exceeds = false;
      if (!string.IsNullOrEmpty(line) && line.Length > 24)
      {
        var logTime = DateUtil.ParseDate(line);
        if (!double.IsNaN(logTime))
        {
          if (range == null)
          {
            pass = (start > -1) ? logTime >= start : false;
          }
          else
          {
            if (logTime > range.TimeSegments.Last().EndTime)
            {
              exceeds = true;
            }
            else
            {
              foreach (var segment in range.TimeSegments)
              {
                if (logTime >= segment.BeginTime && logTime <= segment.EndTime)
                {
                  pass = true;
                  break;
                }
              }
            }
          }
        }
      }

      return pass;
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
        if (!dict.ContainsKey(key))
        {
          dict[key] = default;
        }

        dynamic temp = dict[key];
        temp += value;
        dict[key] = temp;
      }
    }
  }
}

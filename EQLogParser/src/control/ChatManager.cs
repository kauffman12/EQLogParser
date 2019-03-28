using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace EQLogParser
{
  class ChatManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int ACTION_PART_INDEX = 27;
    private const int TIMEOUT = 5000;

    private static DateUtil DateUtil = new DateUtil();
    private static readonly object LockObject = new object();
    private static string ArchiveDir;

    private Dictionary<string, StreamWriter> OpenWriters = new Dictionary<string, StreamWriter>();
    private Dictionary<string, ZipArchive> OpenArchives = new Dictionary<string, ZipArchive>();
    private List<string> Lines = new List<string>();
    private List<string> Types = new List<string>();
    private List<double> BeginTimes = new List<double>();
    private List<double> LastTimes = new List<double>();
    private double BeginTime = double.NaN;
    private double LastTime = double.NaN;
    private readonly string ArchiveTimeFile;
    private bool Running = false;

    internal static List<ChatLine> GetRecent()
    {
      List<ChatLine> chat = new List<ChatLine>();

      try
      {
        string year = DateTime.Now.ToString("yyyy");
        string month = DateTime.Now.ToString("MM");
        string fileName = GetFileName(year, month);
        if (File.Exists(fileName))
        {
          using (var archive = ZipFile.Open(fileName, ZipArchiveMode.Read))
          {
            var entry = archive.Entries.OrderByDescending(item => item.Name).FirstOrDefault();
            if (entry != null)
            {
              StreamReader reader = new StreamReader(entry.Open());
              while (!reader.EndOfStream)
              {
                var chatLine = CreateLine(reader.ReadLine());
                chat.Add(chatLine);
              }

              reader.Close();
            }
          }

          chat = chat.AsParallel().OrderByDescending(chatLine => chatLine.BeginTime).Take(200).Reverse().ToList();
        }
      }
      catch(Exception ex)
      {
        LOG.Error(ex);
      }

      return chat;
    }

    private static ChatLine CreateLine(string line)
    {
      ChatLine chatLine = null;

      try
      {
        string dateString = line.Substring(1, 24);
        double dt = DateUtil.ParseDate(dateString);
        chatLine = new ChatLine { Line = line, BeginTime = dt };
      }
      catch(Exception)
      {
        // ignore
      }

      return chatLine;
    }

    internal ChatManager(string player)
    {
      try
      {
        ArchiveDir = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser\archive\" + player);
        ArchiveTimeFile = ArchiveDir + @"\timeindex";

        // create config dir if it doesn't exist
        Directory.CreateDirectory(ArchiveDir);

        LoadTimeIntervals(BeginTimes, LastTimes);
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    internal void Add(string type, string line)
    {
      lock (LockObject)
      {
        Types.Add(type);
        Lines.Add(line);

        if (!Running)
        {
          Running = true;
          new Timer(new TimerCallback(ArchiveChat)).Change(TIMEOUT, Timeout.Infinite);
        }
      }
    }

    private void ArchiveChat(object state)
    {
      try
      {
        List<string> workingLines = null;
        List<string> workingTypes = null;

        lock(LockObject)
        {
          workingLines = Lines;
          workingTypes = Types;
          Lines = new List<string>();
          Types = new List<string>();
        }

        for (int i=0; i<workingLines.Count; i++)
        {
          string line = workingLines[i];
          string type = workingTypes[i];
          string dateString = line.Substring(1, 24);
          double dt = DateUtil.ParseDate(dateString);

          if (NeedsSaving(dt))
          {
            string actionPart = line.Substring(ACTION_PART_INDEX);
            int endSpot = actionPart.IndexOf(type, StringComparison.Ordinal);
            if (endSpot > -1 && (endSpot == 0 || Helpers.IsPossiblePlayerName(actionPart, endSpot)))
            {
              DateTime dateTime = DateTime.MinValue.AddSeconds(dt);
              string year = dateTime.ToString("yyyy");
              string month = dateTime.ToString("MM");
              string day = dateTime.ToString("dd");
              AddToArchive(year, month, day, line, dt);
            }
          }
        }

        lock(LockObject)
        {
          if (Lines.Count > 0)
          {
            new Timer(new TimerCallback(ArchiveChat)).Change(TIMEOUT, Timeout.Infinite);
          }
          else
          {
            OpenWriters.Values.ToList().ForEach(writer => writer.Close());
            OpenArchives.Values.ToList().ForEach(archive => archive.Dispose());
            OpenWriters.Clear();
            OpenArchives.Clear();

            int found = BeginTimes.BinarySearch(BeginTime);
            if (found < 0)
            {
              found = Math.Abs(found) - 1;
            }

            BeginTimes.Insert(found, BeginTime);
            LastTimes.Insert(found, LastTime);

            List<double> updatedBeginTimes = new List<double>();
            List<double> updatedLastTimes = new List<double>();
            for (int i=0; i <BeginTimes.Count; i++)
            {
              UpdateTimeIntervals(updatedBeginTimes, updatedLastTimes, BeginTimes[i], LastTimes[i]);
            }

            SaveTimeIntervals(updatedBeginTimes, updatedLastTimes);
            BeginTimes = updatedBeginTimes;
            LastTimes = updatedLastTimes;

            Running = false;
          }
        }
      }
      catch (Exception ex)
      {
        LOG.Debug(ex);
      }
      finally
      {
        (state as Timer)?.Dispose();
      }
    }

    private static string GetFileName(string year, string month)
    {
      string folder = ArchiveDir + @"\" + year;
      Directory.CreateDirectory(folder);
      return folder + @"\Chat-" + month + @".zip";
    }

    private void AddToArchive(string year, string month, string day, string line, double currentTime)
    {
      string streamKey = year + "-" + month + "-" + day;
      string archiveKey = year + "-" + month;
      if (!OpenWriters.TryGetValue(streamKey, out StreamWriter writer))
      {
        string fileName = GetFileName(year, month);
        if (File.Exists(fileName))
        {
          if (OpenArchives.TryGetValue(archiveKey, out ZipArchive archive))
          {
            OpenWriters.Values.ToList().ForEach(done => done.Close());
            OpenWriters.Clear();
          }
          else
          {
            archive = ZipFile.Open(fileName, ZipArchiveMode.Update);
          }

          var entry = archive.Mode == ZipArchiveMode.Create ? archive.CreateEntry(day) : archive.GetEntry(day);
          if (entry == null) // if in update mode but entry does not exist
          {
            entry = archive.CreateEntry(day);
          }

          writer = new StreamWriter(entry.Open());
          AdvanceStream(writer.BaseStream);
          OpenWriters[streamKey] = writer;
          OpenArchives[archiveKey] = archive;
        }
        else
        {
          var archive = ZipFile.Open(fileName, ZipArchiveMode.Create);
          var entry = archive.CreateEntry(day);
          writer = new StreamWriter(entry.Open());
          OpenWriters[streamKey] = writer;
          OpenArchives[archiveKey] = archive;
        }
      }

      if (writer != null)
      {
        writer.WriteLine(line);

        if (double.IsNaN(BeginTime))
        {
          BeginTime = currentTime;
        }

        LastTime = currentTime;
      }
    }

    private void AdvanceStream(Stream stream)
    {
      if (stream?.CanRead == true)
      {
        char[] buffer = new char[4096];
        var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, true);

        while (!reader.EndOfStream)
        {
          reader.ReadBlock(buffer, 0, buffer.Length);
        }
      }
    }

    private bool NeedsSaving(double currentTime)
    {
      bool needs = true;

      for (int i=0; i<BeginTimes.Count; i++)
      {
        if (BeginTimes[i] < currentTime && LastTimes[i] > currentTime)
        {
          needs = false;
        }
      }

      return needs;
    }

    private void UpdateTimeIntervals(List<double> beginTimes, List<double> lastTimes, double newBeginTime, double newLastTime)
    {
      int currentIndex = beginTimes.Count - 1;
      if (currentIndex == -1)
      {
        beginTimes.Add(newBeginTime);
        lastTimes.Add(newLastTime);
        currentIndex = 0;
      }
      else if (lastTimes[currentIndex] >= newBeginTime)
      {
        if (newLastTime > lastTimes[currentIndex])
        {
          lastTimes[currentIndex] = newLastTime;
        }
      }
      else
      {
        beginTimes.Add(newBeginTime);
        lastTimes.Add(newLastTime);
        currentIndex++;
      }
    }

    private void LoadTimeIntervals(List<double> beginTimes, List<double> lastTimes)
    {
      if (File.Exists(ArchiveTimeFile))
      {
        try
        {
          string[] lines = File.ReadAllLines(ArchiveTimeFile);
          foreach (string line in lines)
          {
            string[] items = line.Split(',');
            if (items.Length > 0)
            {
              if (double.TryParse(items[0], out double beginTime) && double.TryParse(items[1], out double lastTime))
              {
                beginTimes.Add(beginTime);
                lastTimes.Add(lastTime);
              }
            }
          }
        }
        catch(Exception ex)
        {
          LOG.Error(ex);
        }
      }
    }

    private void SaveTimeIntervals(List<double> beginTimes, List<double> lastTimes)
    {
      List<string> lines = new List<string>();

      for (int i = 0; i < beginTimes.Count && i < lastTimes.Count; i++)
      {
        lines.Add(beginTimes[i] + "," + lastTimes[i]);
      }

      File.WriteAllLines(ArchiveTimeFile, lines);
    }
  }
}

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
    private static readonly object LockObject = new object();
    private static string PLAYER_DIR;

    private List<string> Lines = new List<string>();
    private List<string> Types = new List<string>();
    private List<ChatLine> CurrentList = null;
    private ZipArchive CurrentArchive = null;
    private string CurrentArchiveKey = null;
    private string CurrentEntryKey = null;
    private bool CurrentListModified = false;

    private readonly string ArchiveTimeFile;
    private bool Running = false;

    internal static ChatLine CreateLine(DateUtil dateUtil, string line)
    {
      ChatLine chatLine = null;

      try
      {
        string dateString = line.Substring(1, 24);
        dateUtil.ParseDate(dateString, out double precise);
        chatLine = new ChatLine { Line = line, BeginTime = precise };
      }
      catch(Exception)
      {
        // ignore
      }

      return chatLine;
    }

    internal static List<string> GetArchivedPlayers()
    {
      var result = new List<string>();

      if (Directory.Exists(DataManager.ARCHIVE_DIR))
      {
        foreach (var dir in Directory.GetDirectories(DataManager.ARCHIVE_DIR))
        {
          string name = Path.GetFileName(dir);
          if (Helpers.IsPossiblePlayerName(name))
          {
            bool found = false;
            foreach (var sub in Directory.GetDirectories(dir))
            {
              if (int.TryParse(Path.GetFileName(sub), out int year))
              {
                found = true;
                break;
              }
            }

            if (found)
            {
              result.Add(name);
            }
          }
        }
      }

      return result.OrderBy(name => name).ToList();
    }

    internal ChatManager(string player)
    {
      try
      {

        PLAYER_DIR = DataManager.ARCHIVE_DIR + player;
        ArchiveTimeFile = PLAYER_DIR + @"\timeindex";

        // create config dir if it doesn't exist
        Directory.CreateDirectory(PLAYER_DIR);
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
        DateUtil dateUtil = new DateUtil();
        DateUtil dateUtilSavedLines = new DateUtil();

        lock (LockObject)
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

          string actionPart = line.Substring(ACTION_PART_INDEX);
          int endSpot = actionPart.IndexOf(type, StringComparison.Ordinal);
          if (endSpot > -1 && (endSpot == 0 || Helpers.IsPossiblePlayerName(actionPart, endSpot)))
          {
            var chatLine = CreateLine(dateUtil, line);
            DateTime dateTime = DateTime.MinValue.AddSeconds(chatLine.BeginTime);
            string year = dateTime.ToString("yyyy");
            string month = dateTime.ToString("MM");
            string day = dateTime.ToString("dd");
            AddToArchive(year, month, day, chatLine, dateUtilSavedLines);
          }
        }

        lock(LockObject)
        {
          if (Lines.Count > 0)
          {
            new Timer(new TimerCallback(ArchiveChat)).Change(0, Timeout.Infinite);
          }
          else
          {
            SaveCurrent(true);
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

    private void AddToArchive(string year, string month, string day, ChatLine chatLine, DateUtil dateUtil)
    {
      string entryKey = day;
      string archiveKey = year + "-" + month;

      if (CurrentArchiveKey != archiveKey)
      {
        SaveCurrent(true);
        string fileName = GetFileName(year, month);
        var mode = File.Exists(fileName) ? ZipArchiveMode.Update : ZipArchiveMode.Create;
        CurrentArchive = ZipFile.Open(fileName, mode);
        CurrentArchiveKey = archiveKey;
      }

      if (entryKey != CurrentEntryKey)
      {
        SaveCurrent(false);
        CurrentEntryKey = entryKey;
        CurrentList = new List<ChatLine>();

        var entry = CurrentArchive.Mode != ZipArchiveMode.Create ? CurrentArchive.GetEntry(CurrentEntryKey) : null;
        if (entry != null)
        {
          using (var reader = new StreamReader(entry.Open()))
          {
            while (reader.BaseStream.CanRead && !reader.EndOfStream)
            {
              var existingLine = CreateLine(dateUtil, reader.ReadLine());
              if (existingLine != null)
              {
                CurrentList.Add(existingLine);
              }
            }
          }
        }
      }

      int index = CurrentList.BinarySearch(chatLine, Helpers.ReverseTimedActionComparer);
      if (index < 0)
      {
        index = Math.Abs(index) - 1;
        CurrentList.Insert(index, chatLine);
        CurrentListModified = true;
      }
      else if (chatLine.Line != CurrentList[index].Line)
      {
        CurrentList.Insert(index, chatLine);
        CurrentListModified = true;
      }
    }

    private void SaveCurrent(bool closeArchive)
    {
      if (CurrentList != null && CurrentArchive != null && CurrentEntryKey != null)
      {
        try
        {
          if (CurrentList.Count > 0 && CurrentListModified)
          {
            var entry = CurrentArchive.Mode == ZipArchiveMode.Create ? CurrentArchive.CreateEntry(CurrentEntryKey) :
              CurrentArchive.GetEntry(CurrentEntryKey) ?? CurrentArchive.CreateEntry(CurrentEntryKey);
            using (var writer = new StreamWriter(entry.Open()))
            {
              CurrentList.ForEach(chatLine => writer.WriteLine(chatLine.Line));
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Debug(ex);
        }
      }

      if (closeArchive && CurrentArchive != null)
      {
        CurrentArchive.Dispose();
        CurrentArchive = null;
        CurrentArchiveKey = null;
      }

      CurrentList = null;
      CurrentEntryKey = null;
      CurrentListModified = false;
    }

    private static string GetFileName(string year, string month)
    {
      string folder = PLAYER_DIR + @"\" + year;
      Directory.CreateDirectory(folder);
      return folder + @"\Chat-" + month + @".zip";
    }
  }
}

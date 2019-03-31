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

    internal const string INDEX = "index";

    private const int ACTION_PART_INDEX = 27;
    private const int TIMEOUT = 3000;
    private static readonly object LockObject = new object();
    private static string PLAYER_DIR;

    private Dictionary<string, Dictionary<string, byte>> ChannelEntryCache = null;
    private List<ChatType> ChatTypes = new List<ChatType>();
    private List<ChatLine> CurrentList = null;
    private ZipArchive CurrentArchive = null;
    private string CurrentArchiveKey = null;
    private string CurrentEntryKey = null;
    private bool ChannelEntryCacheUpdated = false;
    private bool CurrentListModified = false;
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

        // create config dir if it doesn't exist
        Directory.CreateDirectory(PLAYER_DIR);
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    internal void Add(ChatType chatType)
    {
      lock (LockObject)
      {
        ChatTypes.Add(chatType);

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
        List<ChatType> working = null;
        DateUtil dateUtil = new DateUtil();
        DateUtil dateUtilSavedLines = new DateUtil();

        lock (LockObject)
        {
          working = ChatTypes;
          ChatTypes = new List<ChatType>();
        }

        for (int i=0; i<working.Count; i++)
        {
          var chatType = working[i];
          var chatLine = CreateLine(dateUtil, chatType.Line);
          DateTime dateTime = DateTime.MinValue.AddSeconds(chatLine.BeginTime);
          string year = dateTime.ToString("yyyy");
          string month = dateTime.ToString("MM");
          string day = dateTime.ToString("dd");
          AddToArchive(year, month, day, chatLine, chatType, dateUtilSavedLines);
        }

        lock(LockObject)
        {
          if (ChatTypes.Count > 0)
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

    private void AddToArchive(string year, string month, string day, ChatLine chatLine, ChatType chatType, DateUtil dateUtil)
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
        LoadCache();
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
            List<string> temp = new List<string>();
            while (reader.BaseStream.CanRead && !reader.EndOfStream)
            {
              temp.Insert(0, reader.ReadLine()); // reverse
            }

            // this is so the date precision numbers are calculated in the same order
            // as the new lines being added
            temp.ForEach(line =>
            {
              var existingLine = CreateLine(dateUtil, line);
              if (existingLine != null)
              {
                CurrentList.Insert(0, existingLine); // reverse back
              }
            });
          }
        }
      }

      int index = CurrentList.BinarySearch(chatLine, Helpers.ReverseTimedActionComparer);
      if (index < 0)
      {
        index = Math.Abs(index) - 1;
        CurrentList.Insert(index, chatLine);
        UpdateCache(entryKey, chatType);
        CurrentListModified = true;
      }
      else if (chatLine.Line != CurrentList[index].Line)
      {
        CurrentList.Insert(index, chatLine);
        UpdateCache(entryKey, chatType);
        CurrentListModified = true;
      }
    }

    private void LoadCache()
    {
      ChannelEntryCache = new Dictionary<string, Dictionary<string, byte>>();
      ChannelEntryCacheUpdated = false;

      if (CurrentArchive != null && CurrentArchive.Mode != ZipArchiveMode.Create)
      {
        try
        {
          var indexEntry = CurrentArchive.GetEntry(INDEX);
          if (indexEntry != null)
          {
            using (var reader = new StreamReader(indexEntry.Open()))
            {
              while (!reader.EndOfStream)
              {
                var temp = reader.ReadLine().Split('|');
                if (temp != null && temp.Length == 2)
                {
                  ChannelEntryCache[temp[0]] = new Dictionary<string, byte>();
                  foreach (var channel in temp[1].Split(','))
                  {
                    ChannelEntryCache[temp[0]][channel] = 1;
                  }
                }
              }
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Debug(ex);
        }
      }
    }

    private void SaveCache()
    {
      var indexList = new List<string>();
      foreach (var keyvalue in ChannelEntryCache)
      {
        var channels = new List<string>();
        foreach (var channel in (keyvalue.Value as Dictionary<string, byte>).Keys)
        {
          channels.Add(channel);
        }

        if (channels.Count > 0)
        {
          var list= string.Join(",", channels);
          var temp = keyvalue.Key + "|" + list;
          indexList.Add(temp);
        }
      }

      if (indexList.Count > 0)
      {
        var indexEntry = GetEntry(INDEX);
        using (var writer = new StreamWriter(indexEntry.Open()))
        {
          indexList.ForEach(item => writer.WriteLine(item));
          writer.Close();
        }
      }
    }

    private void UpdateCache(string entryKey, ChatType chatType)
    {
      if (chatType.Channel != null)
      {
        if (!ChannelEntryCache.ContainsKey(entryKey))
        {
          ChannelEntryCache.Add(entryKey, new Dictionary<string, byte>());
        }

        var channels = ChannelEntryCache[entryKey];
        if (!channels.ContainsKey(chatType.Channel))
        {
          channels[chatType.Channel] = 1;
          ChannelEntryCacheUpdated = true;
          DataManager.Instance.AddChannel(chatType.Channel);
        }
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
            var entry = GetEntry(CurrentEntryKey);
            using (var writer = new StreamWriter(entry.Open()))
            {
              CurrentList.ForEach(chatLine => writer.WriteLine(chatLine.Line));
              writer.Close();
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
        if (ChannelEntryCacheUpdated)
        {
          SaveCache();
        }

        CurrentArchive.Dispose();
        CurrentArchive = null;
        CurrentArchiveKey = null;
        ChannelEntryCacheUpdated = false;
        ChannelEntryCache = null;

      }

      CurrentList = null;
      CurrentEntryKey = null;
      CurrentListModified = false;
    }

    private ZipArchiveEntry GetEntry(string key)
    {
      return CurrentArchive.Mode == ZipArchiveMode.Create ? CurrentArchive.CreateEntry(key) : CurrentArchive.GetEntry(key) ?? CurrentArchive.CreateEntry(key);
    }

    private string GetFileName(string year, string month)
    {
      string folder = PLAYER_DIR + @"\" + year;
      Directory.CreateDirectory(folder);
      return folder + @"\Chat-" + month + @".zip";
    }
  }
}

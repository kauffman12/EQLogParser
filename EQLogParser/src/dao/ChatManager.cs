using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace EQLogParser
{
  class ChatManager : IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static event EventHandler<string> EventsUpdatePlayer;
    internal static event EventHandler<List<string>> EventsNewChannels;

    internal const string INDEX = "index";

    private const int TIMEOUT = 2000;
    private const string SELECTED_CHANNELS_FILE = "channels-selected.txt";

    private static readonly object LockObject = new object();
    private static string PLAYER_DIR;

    private static readonly ReverseTimedActionComparer RTAComparer = new ReverseTimedActionComparer();
    private readonly Dictionary<string, byte> ChannelCache = new Dictionary<string, byte>();
    private readonly Dictionary<string, byte> PlayerCache = new Dictionary<string, byte>();
    private bool ChannelCacheUpdated = false;
    private bool PlayerCacheUpdated = false;

    private Dictionary<string, Dictionary<string, byte>> ChannelIndex = null;
    private List<ChatType> ChatTypes = new List<ChatType>();
    private List<ChatLine> CurrentList = null;
    private ZipArchive CurrentArchive = null;
    private string CurrentArchiveKey = null;
    private string CurrentEntryKey = null;
    private readonly string CurrentPlayer = null;
    private bool ChannelIndexUpdated = false;
    private bool CurrentListModified = false;
    private bool Running = false;
    private Timer ArchiveTimer;

    internal ChatManager()
    {
      try
      {
        CurrentPlayer = ConfigUtil.PlayerName + "." + ConfigUtil.ServerName;
        PLAYER_DIR = ConfigUtil.GetArchiveDir() + CurrentPlayer;

        if (!Directory.Exists(PLAYER_DIR))
        {
          // create config dir if it doesn't exist
          Directory.CreateDirectory(PLAYER_DIR);
        }

        GetSavedChannels(CurrentPlayer).ForEach(channel => ChannelCache[channel] = 1);
        GetPlayers(CurrentPlayer).ForEach(channel => PlayerCache[channel] = 1);
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
    }

    internal string GetCurrentPlayer()
    {
      return CurrentPlayer;
    }

    internal void Reset()
    {
      try
      {
        PlayerCache.Clear();
        ChannelCache.Clear();

        if (!Directory.Exists(PLAYER_DIR))
        {
          // create config dir if it doesn't exist
          Directory.CreateDirectory(PLAYER_DIR);
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
    }

    internal static bool DeleteArchivedPlayer(string player)
    {
      bool deleted = false;

      var dir = ConfigUtil.GetArchiveDir() + @"/" + player;
      if (Directory.Exists(dir))
      {
        try
        {
          Directory.Delete(dir, true);
          deleted = true;
        }
        catch (IOException ex)
        {
          LOG.Error(ex);
        }
        catch (UnauthorizedAccessException uax)
        {
          LOG.Error(uax);
        }
      }

      return deleted;
    }

    internal static List<string> GetArchivedPlayers()
    {
      var result = new List<string>();

      if (Directory.Exists(ConfigUtil.GetArchiveDir()))
      {
        foreach (var dir in Directory.GetDirectories(ConfigUtil.GetArchiveDir()))
        {
          string name = Path.GetFileName(dir);
          var split = name.Split('.');

          if (split.Length > 1 && split[1].Length > 3)
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

    internal static void SaveSelectedChannels(string playerAndServer, List<string> channels)
    {
      try
      {
        // create config dir if it doesn't exist
        Directory.CreateDirectory(ConfigUtil.GetArchiveDir() + playerAndServer);
        ConfigUtil.SaveList(ConfigUtil.GetArchiveDir() + playerAndServer + @"\" + SELECTED_CHANNELS_FILE, channels);
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
    }

    internal static ZipArchive OpenArchive(string fileName, ZipArchiveMode mode)
    {
      ZipArchive result = null;

      int tries = 10;
      while (result == null && tries-- > 0)
      {
        try
        {
          result = ZipFile.Open(fileName, mode);
        }
        catch (IOException)
        {
          // wait for file to be freed
          Thread.Sleep(1000);
        }
        catch (InvalidDataException)
        {
          LOG.Error("Could not open " + fileName + ", deleting and trying again");
          ConfigUtil.RemoveFileIfExists(fileName);
        }
      }

      return result;
    }

    internal static List<ComboBoxItemDetails> GetChannels(string playerAndServer)
    {
      var selected = GetSelectedChannels(playerAndServer);
      List<ComboBoxItemDetails> channelList = new List<ComboBoxItemDetails>();

      foreach (string line in GetSavedChannels(playerAndServer))
      {
        var isChecked = selected == null || selected.Contains(line);
        ComboBoxItemDetails details = new ComboBoxItemDetails { Text = line, IsChecked = isChecked };
        channelList.Add(details);
      }

      return channelList.OrderBy(key => key.Text).ToList();
    }

    internal static List<string> GetPlayers(string playerAndServer)
    {
      string playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var file = playerDir + @"\players.txt";
      return ConfigUtil.ReadList(file);
    }

    internal void Add(ChatType chatType)
    {
      lock (LockObject)
      {
        ChatTypes.Add(chatType);

        if (!Running)
        {
          Running = true;
          ArchiveTimer?.Dispose();
          ArchiveTimer = new Timer(new TimerCallback(ArchiveChat));
          ArchiveTimer.Change(TIMEOUT, Timeout.Infinite);
        }
      }
    }

    private static List<string> GetSavedChannels(string playerAndServer)
    {
      string playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var file = playerDir + @"\channels.txt";
      return ConfigUtil.ReadList(file);
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

        for (int i = 0; i < working.Count; i++)
        {
          var chatType = working[i];
          var chatLine = CreateLine(dateUtil, chatType.Line);
          DateTime dateTime = DateTime.MinValue.AddSeconds(chatLine.BeginTime);
          string year = dateTime.ToString("yyyy", CultureInfo.CurrentCulture);
          string month = dateTime.ToString("MM", CultureInfo.CurrentCulture);
          string day = dateTime.ToString("dd", CultureInfo.CurrentCulture);
          AddToArchive(year, month, day, chatLine, chatType, dateUtilSavedLines);
        }

        lock (LockObject)
        {
          if (ChatTypes.Count > 0)
          {
            ArchiveTimer?.Dispose();
            ArchiveTimer = new Timer(new TimerCallback(ArchiveChat));
            ArchiveTimer.Change(0, Timeout.Infinite);
          }
          else
          {
            SaveCurrent(true);

            if (ChannelCacheUpdated)
            {
              var current = ChannelCache.Keys.ToList();
              ConfigUtil.SaveList(PLAYER_DIR + @"\channels.txt", current);
              ChannelCacheUpdated = false;
              EventsNewChannels?.Invoke(this, current);
            }

            if (PlayerCacheUpdated)
            {
              ConfigUtil.SaveList(PLAYER_DIR + @"\players.txt", PlayerCache.Keys.OrderBy(player => player)
                .Where(player => !PlayerManager.Instance.IsVerifiedPet(player)).ToList());
              PlayerCacheUpdated = false;
            }

            EventsUpdatePlayer?.Invoke(this, CurrentPlayer);
            Running = false;
          }
        }
      }
      catch (ObjectDisposedException ex)
      {
        LOG.Error(ex);
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

        CurrentArchive = OpenArchive(fileName, mode);
        CurrentArchiveKey = archiveKey;
        LoadCache();
      }

      if (entryKey != CurrentEntryKey && CurrentArchive != null)
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

      if (CurrentList != null)
      {
        int index = CurrentList.BinarySearch(chatLine, RTAComparer);
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
    }

    private void LoadCache()
    {
      ChannelIndex = new Dictionary<string, Dictionary<string, byte>>();
      ChannelIndexUpdated = false;

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
                  ChannelIndex[temp[0]] = new Dictionary<string, byte>();
                  foreach (var channel in temp[1].Split(','))
                  {
                    ChannelIndex[temp[0]][channel] = 1;
                    UpdateChannelCache(channel); // incase main cache is out of sync with archive
                  }
                }
              }
            }
          }
        }
        catch (IOException ex)
        {
          LOG.Error(ex);
        }
        catch (InvalidDataException ide)
        {
          LOG.Error(ide);
        }
      }
    }

    private void SaveCache()
    {
      var indexList = new List<string>();
      foreach (var keyvalue in ChannelIndex)
      {
        var channels = new List<string>();
        foreach (var channel in keyvalue.Value.Keys)
        {
          channels.Add(channel);
        }

        if (channels.Count > 0)
        {
          var list = string.Join(",", channels);
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
        if (!ChannelIndex.ContainsKey(entryKey))
        {
          ChannelIndex.Add(entryKey, new Dictionary<string, byte>());
        }

        var channels = ChannelIndex[entryKey];
        if (!channels.ContainsKey(chatType.Channel))
        {
          channels[chatType.Channel] = 1;
          ChannelIndexUpdated = true;
        }

        UpdateChannelCache(chatType.Channel);
      }

      AddPlayer(chatType.Sender);
      AddPlayer(chatType.Receiver);
    }

    private void UpdateChannelCache(string channel)
    {
      if (!ChannelCache.ContainsKey(channel))
      {
        ChannelCache[channel] = 1;
        ChannelCacheUpdated = true;
      }
    }

    private void SaveCurrent(bool closeArchive)
    {
      if (CurrentList != null && CurrentArchive != null && CurrentEntryKey != null)
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

      if (closeArchive && CurrentArchive != null)
      {
        if (ChannelIndexUpdated)
        {
          SaveCache();
        }

        CurrentArchive.Dispose();
        CurrentArchive = null;
        CurrentArchiveKey = null;
        ChannelIndexUpdated = false;
        ChannelIndex = null;

      }

      CurrentList = null;
      CurrentEntryKey = null;
      CurrentListModified = false;
    }

    private static List<string> GetSelectedChannels(string playerAndServer)
    {
      List<string> result = null; // throw null to check case where file has never existed vs empty content
      var playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      string fileName = playerDir + @"\" + SELECTED_CHANNELS_FILE;
      if (File.Exists(fileName))
      {
        result = ConfigUtil.ReadList(fileName);
      }
      return result;
    }

    private void AddPlayer(string value)
    {
      if (!string.IsNullOrEmpty(value))
      {
        string player = value.ToLower(CultureInfo.CurrentCulture);
        if (!PlayerCache.ContainsKey(player) && !PlayerManager.Instance.IsVerifiedPet(player) && PlayerManager.Instance.IsPossiblePlayerName(player))
        {
          PlayerCache[player] = 1;
          PlayerCacheUpdated = true;
        }
      }
    }

    private ZipArchiveEntry GetEntry(string key)
    {
      return CurrentArchive.Mode == ZipArchiveMode.Create ? CurrentArchive.CreateEntry(key) : CurrentArchive.GetEntry(key) ?? CurrentArchive.CreateEntry(key);
    }

    private static ChatLine CreateLine(DateUtil dateUtil, string line)
    {
      string dateString = line.Substring(1, 24);
      double precise = dateUtil.ParsePreciseDate(dateString);
      return new ChatLine { Line = line, BeginTime = precise };
    }

    private static string GetFileName(string year, string month)
    {
      string folder = PLAYER_DIR + @"\" + year;
      Directory.CreateDirectory(folder);
      return folder + @"\Chat-" + month + @".zip";
    }

    private class ReverseTimedActionComparer : IComparer<TimedAction>
    {
      public int Compare(TimedAction x, TimedAction y) => x != null && y != null ? y.BeginTime.CompareTo(x.BeginTime) : 0;
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
          ArchiveTimer?.Dispose();
          CurrentArchive?.Dispose();
        }

        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }

  internal class ChatLine : TimedAction
  {
    public string Line { get; set; }
  }
}

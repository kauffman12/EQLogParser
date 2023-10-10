using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace EQLogParser
{
  class ChatManager
  {
    private const int TIMEOUT = 2000;
    private const string CHANNELS_FILE = "channels.txt";
    private const string SELECTED_CHANNELS_FILE = "channels-selected.txt";
    internal const string INDEX = "index";
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<ChatManager> _lazy = new Lazy<ChatManager>(() => new ChatManager());
    private static readonly object LockObject = new object();
    private static readonly ReverseTimedActionComparer RTAComparer = new ReverseTimedActionComparer();
    internal static ChatManager Instance => _lazy.Value;
    internal event EventHandler<string> EventsUpdatePlayer;
    internal event EventHandler<List<string>> EventsNewChannels;
    private readonly Dictionary<string, byte> ChannelCache = new Dictionary<string, byte>();
    private readonly Dictionary<string, byte> PlayerCache = new Dictionary<string, byte>();
    private DateUtil DateUtil = null;
    private string CurrentPlayer = null;
    private string PlayerDir = null;
    private Timer ArchiveTimer;
    private Dictionary<string, Dictionary<string, byte>> ChannelIndex = null;
    private List<ChatType> ChatTypes = new List<ChatType>();
    private List<ChatLine> CurrentList = null;
    private ZipArchive CurrentArchive = null;
    private string CurrentArchiveKey = null;
    private string CurrentEntryKey = null;
    private bool ChannelCacheUpdated = false;
    private bool PlayerCacheUpdated = false;
    private bool ChannelIndexUpdated = false;
    private bool CurrentListModified = false;
    private bool Running = false;

    private ChatManager()
    {

    }

    internal void Init()
    {
      lock (LockObject)
      {
        try
        {
          ArchiveTimer?.Dispose();
          Running = false;
          CurrentArchive?.Dispose();
          CurrentList?.Clear();
          CurrentArchive = null;
          CurrentList = null;
          CurrentArchiveKey = null;
          CurrentEntryKey = null;
          ChannelCacheUpdated = false;
          PlayerCacheUpdated = false;
          ChannelIndexUpdated = false;
          CurrentListModified = false;
          ChannelCache.Clear();
          PlayerCache.Clear();
          ChatTypes.Clear();
          CurrentPlayer = ConfigUtil.PlayerName + "." + ConfigUtil.ServerName;
          PlayerDir = ConfigUtil.GetArchiveDir() + CurrentPlayer;
          DateUtil = new DateUtil();

          if (!Directory.Exists(PlayerDir))
          {
            // create config dir if it doesn't exist
            Directory.CreateDirectory(PlayerDir);
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
    }

    internal void Stop()
    {
      ArchiveTimer?.Dispose();
      CurrentArchive?.Dispose();
    }

    internal bool DeleteArchivedPlayer(string player)
    {
      var isCurrent = false;
      var dir = ConfigUtil.GetArchiveDir() + @"/" + player;
      if (Directory.Exists(dir))
      {
        try
        {
          Directory.Delete(dir, true);
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

      if (string.Compare(player, CurrentPlayer, true) == 0)
      {
        try
        {
          isCurrent = true;
          PlayerCache.Clear();
          ChannelCache.Clear();

          if (!Directory.Exists(PlayerDir))
          {
            // create config dir if it doesn't exist
            Directory.CreateDirectory(PlayerDir);
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

      return isCurrent;
    }

    internal List<string> GetArchivedPlayers()
    {
      var result = new List<string>();
      if (Directory.Exists(ConfigUtil.GetArchiveDir()))
      {
        foreach (var dir in Directory.GetDirectories(ConfigUtil.GetArchiveDir()))
        {
          var name = Path.GetFileName(dir);
          var split = name.Split('.');

          if (split.Length > 1 && split[1].Length > 3)
          {
            var found = false;
            foreach (var sub in Directory.GetDirectories(dir))
            {
              if (int.TryParse(Path.GetFileName(sub), out var year))
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

    internal void SaveSelectedChannels(string playerAndServer, List<string> channels)
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

    internal ZipArchive OpenArchive(string fileName, ZipArchiveMode mode)
    {
      ZipArchive result = null;
      var tries = 10;
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

    internal List<ComboBoxItemDetails> GetChannels(string playerAndServer)
    {
      var selected = GetSelectedChannels(playerAndServer);
      var channelList = new List<ComboBoxItemDetails>();
      foreach (var line in GetSavedChannels(playerAndServer))
      {
        var isChecked = selected == null || selected.Contains(line);
        var details = new ComboBoxItemDetails { Text = line, IsChecked = isChecked };
        channelList.Add(details);
      }
      return channelList;
    }

    internal List<string> GetPlayers(string playerAndServer)
    {
      var playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var file = playerDir + @"\players.txt";
      return ConfigUtil.ReadList(file);
    }

    internal void Add(ChatType chatType)
    {
      if (chatType != null)
      {
        lock (LockObject)
        {
          if (chatType.SenderIsYou == false && chatType.Sender != null)
          {
            if (chatType.Channel == ChatChannels.Guild || chatType.Channel == ChatChannels.Raid || chatType.Channel == ChatChannels.Fellowship)
            {
              PlayerManager.Instance.AddVerifiedPlayer(chatType.Sender, chatType.BeginTime);
            }
          }

          ChatTypes.Add(chatType);
        }

        if (!Running)
        {
          Running = true;
          ArchiveTimer?.Dispose();
          ArchiveTimer = new Timer(ArchiveChat);
          ArchiveTimer.Change(TIMEOUT, Timeout.Infinite);
        }
      }
    }

    private List<string> GetSavedChannels(string playerAndServer)
    {
      var playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var file = playerDir + @"\" + CHANNELS_FILE;
      var list = ConfigUtil.ReadList(file);
      return list.ConvertAll(item => item.ToLower()).Distinct().OrderBy(item => item.ToLower()).ToList();
    }

    private void ArchiveChat(object state)
    {
      try
      {
        List<ChatType> working = null;

        lock (LockObject)
        {
          working = ChatTypes;
          ChatTypes = new List<ChatType>();
        }

        var lastTime = double.NaN;
        var increment = 0.0;
        for (var i = 0; i < working.Count; i++)
        {
          if (working[i] != null)
          {
            var chatType = working[i];
            if (lastTime == chatType.BeginTime)
            {
              increment += 0.001;
            }
            else
            {
              increment = 0.0;
            }

            var chatLine = new ChatLine { Line = chatType.Text, BeginTime = chatType.BeginTime + increment };
            var dateTime = DateTime.MinValue.AddSeconds(chatLine.BeginTime);
            var year = dateTime.ToString("yyyy", CultureInfo.CurrentCulture);
            var month = dateTime.ToString("MM", CultureInfo.CurrentCulture);
            var day = dateTime.ToString("dd", CultureInfo.CurrentCulture);
            AddToArchive(year, month, day, chatLine, chatType);
            lastTime = chatType.BeginTime;
          }
        }

        lock (LockObject)
        {
          if (ChatTypes.Count > 0)
          {
            ArchiveTimer?.Dispose();
            ArchiveTimer = new Timer(ArchiveChat);
            ArchiveTimer.Change(0, Timeout.Infinite);
          }
          else
          {
            SaveCurrent(true);

            if (ChannelCacheUpdated)
            {
              var current = ChannelCache.Keys.ToList();
              ConfigUtil.SaveList(PlayerDir + @"\" + CHANNELS_FILE, current);
              ChannelCacheUpdated = false;
              EventsNewChannels?.Invoke(this, current);
            }

            if (PlayerCacheUpdated)
            {
              ConfigUtil.SaveList(PlayerDir + @"\players.txt", PlayerCache.Keys.OrderBy(player => player)
                .Where(player => !PlayerManager.Instance.IsVerifiedPet(player)).ToList());
              PlayerCacheUpdated = false;
            }

            EventsUpdatePlayer?.Invoke(this, CurrentPlayer);
            Running = false;
          }
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    private void AddToArchive(string year, string month, string day, ChatLine chatLine, ChatType chatType)
    {
      var entryKey = day;
      var archiveKey = year + "-" + month;

      if (CurrentArchiveKey != archiveKey)
      {
        SaveCurrent(true);
        var fileName = GetFileName(year, month);
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
          using var reader = new StreamReader(entry.Open());
          var temp = new List<string>();
          while (reader.BaseStream.CanRead && !reader.EndOfStream)
          {
            temp.Insert(0, reader.ReadLine()); // reverse
          }

          // this is so the date precision numbers are calculated in the same order
          // as the new lines being added
          var lastTime = double.NaN;
          var increment = 0.0;
          temp.ForEach(line =>
          {
            var beginTime = DateUtil.ParseDate(line);
            if (lastTime == beginTime)
            {
              increment += 0.001;
            }
            else
            {
              increment = 0.0;
            }

            var existingLine = new ChatLine { Line = line, BeginTime = beginTime + increment };
            if (existingLine != null)
            {
              CurrentList.Insert(0, existingLine); // reverse back
            }

            lastTime = beginTime;
          });
        }
      }

      if (CurrentList != null)
      {
        var index = CurrentList.BinarySearch(chatLine, RTAComparer);

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
            using var reader = new StreamReader(indexEntry.Open());
            while (!reader.EndOfStream)
            {
              var temp = reader.ReadLine().Split('|');
              if (temp != null && temp.Length == 2)
              {
                ChannelIndex[temp[0]] = new Dictionary<string, byte>();
                foreach (var channel in temp[1].Split(','))
                {
                  ChannelIndex[temp[0]][channel.ToLower()] = 1;
                  UpdateChannelCache(channel); // incase main cache is out of sync with archive
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
        using var writer = new StreamWriter(indexEntry.Open());
        indexList.ForEach(item => writer.WriteLine(item));
        writer.Close();
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
          using var writer = new StreamWriter(entry.Open());
          CurrentList.ForEach(chatLine => writer.WriteLine(chatLine.Line));
          writer.Close();
        }
      }

      if (closeArchive && CurrentArchive != null)
      {
        if (ChannelIndexUpdated)
        {
          SaveCache();
        }

        CurrentArchive?.Dispose();
        CurrentArchive = null;
        CurrentArchiveKey = null;
        ChannelIndexUpdated = false;
        ChannelIndex = null;

      }

      CurrentList?.Clear();
      CurrentList = null;
      CurrentEntryKey = null;
      CurrentListModified = false;
    }

    private List<string> GetSelectedChannels(string playerAndServer)
    {
      List<string> result = null; // throw null to check case where file has never existed vs empty content
      var playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var fileName = playerDir + @"\" + SELECTED_CHANNELS_FILE;
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
        var player = value.ToLower(CultureInfo.CurrentCulture);
        if (!PlayerCache.ContainsKey(player) && !PlayerManager.Instance.IsVerifiedPet(player) && PlayerManager.IsPossiblePlayerName(player))
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

    private string GetFileName(string year, string month)
    {
      var folder = PlayerDir + @"\" + year;
      Directory.CreateDirectory(folder);
      return folder + @"\Chat-" + month + @".zip";
    }

    private class ReverseTimedActionComparer : IComparer<TimedAction>
    {
      public int Compare(TimedAction x, TimedAction y) => x != null && y != null ? y.BeginTime.CompareTo(x.BeginTime) : 0;
    }
  }

  internal class ChatLine : TimedAction
  {
    public string Line { get; set; }
  }
}

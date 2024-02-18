using log4net;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace EQLogParser
{
  class ChatManager
  {
    private const int Timeout = 2000;
    private const string ChannelsFile = "channels.txt";
    private const string SelectedChannelsFile = "channels-selected.txt";
    internal const string Index = "index";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<ChatManager> Lazy = new(() => new ChatManager());
    private static readonly object LockObject = new();
    private static readonly ReverseTimedActionComparer RtaComparer = new();
    internal static ChatManager Instance => Lazy.Value;
    internal event Action<string> EventsUpdatePlayer;
    internal event Action<List<string>> EventsNewChannels;
    private readonly Dictionary<string, byte> _channelCache = new();
    private readonly Dictionary<string, byte> _playerCache = new();
    private DateUtil _dateUtil;
    private string _currentPlayer;
    private string _playerDir;
    private Timer _archiveTimer;
    private Dictionary<string, Dictionary<string, byte>> _channelIndex;
    private List<ChatType> _chatTypes = new();
    private List<ChatLine> _currentList;
    private ZipArchive _currentArchive;
    private string _currentArchiveKey;
    private string _currentEntryKey;
    private bool _channelCacheUpdated;
    private bool _playerCacheUpdated;
    private bool _channelIndexUpdated;
    private bool _currentListModified;
    private bool _running;

    private ChatManager()
    {

    }

    internal void Init()
    {
      lock (LockObject)
      {
        try
        {
          _archiveTimer?.Dispose();
          _running = false;
          _currentArchive?.Dispose();
          _currentList?.Clear();
          _currentArchive = null;
          _currentList = null;
          _currentArchiveKey = null;
          _currentEntryKey = null;
          _channelCacheUpdated = false;
          _playerCacheUpdated = false;
          _channelIndexUpdated = false;
          _currentListModified = false;
          _channelCache.Clear();
          _playerCache.Clear();
          _chatTypes.Clear();
          _currentPlayer = ConfigUtil.PlayerName + "." + ConfigUtil.ServerName;
          _playerDir = ConfigUtil.GetArchiveDir() + _currentPlayer;
          _dateUtil = new DateUtil();

          if (!Directory.Exists(_playerDir))
          {
            // create config dir if it doesn't exist
            Directory.CreateDirectory(_playerDir);
          }

          GetSavedChannels(_currentPlayer).ForEach(channel => _channelCache[channel] = 1);
          GetPlayers(_currentPlayer).ForEach(channel => _playerCache[channel] = 1);
        }
        catch (IOException ex)
        {
          Log.Error(ex);
        }
        catch (UnauthorizedAccessException uax)
        {
          Log.Error(uax);
        }
      }
    }

    internal void Stop()
    {
      _archiveTimer?.Dispose();
      _currentArchive?.Dispose();
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
          Log.Error(ex);
        }
        catch (UnauthorizedAccessException uax)
        {
          Log.Error(uax);
        }
      }

      if (String.Compare(player, _currentPlayer, StringComparison.OrdinalIgnoreCase) == 0)
      {
        try
        {
          isCurrent = true;
          _playerCache.Clear();
          _channelCache.Clear();

          if (!Directory.Exists(_playerDir))
          {
            // create config dir if it doesn't exist
            Directory.CreateDirectory(_playerDir);
          }
        }
        catch (IOException ex)
        {
          Log.Error(ex);
        }
        catch (UnauthorizedAccessException uax)
        {
          Log.Error(uax);
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
              if (int.TryParse(Path.GetFileName(sub), out _))
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
        ConfigUtil.SaveList(ConfigUtil.GetArchiveDir() + playerAndServer + @"\" + SelectedChannelsFile, channels);
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        Log.Error(uax);
      }
    }

    internal static ZipArchive OpenArchive(string fileName, ZipArchiveMode mode)
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
          Log.Error("Could not open " + fileName + ", deleting and trying again");
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
            if (chatType.Channel is ChatChannels.Guild or ChatChannels.Raid or ChatChannels.Fellowship)
            {
              PlayerManager.Instance.AddVerifiedPlayer(chatType.Sender, chatType.BeginTime);
            }
            else if (chatType.Channel is ChatChannels.Say)
            {
              var span = chatType.Text.AsSpan()[chatType.TextStart..];
              if (span.StartsWith("My leader is "))
              {
                span = span["My leader is ".Length..];
                if (span.IndexOf(".") is var found and > 2)
                {
                  var player = span[..found].ToString();
                  PlayerManager.Instance.AddVerifiedPlayer(player, chatType.BeginTime);
                  PlayerManager.Instance.AddPetToPlayer(chatType.Sender, player); // also adds verified pet
                }
                else
                {
                  var player = span.ToString();
                  PlayerManager.Instance.AddVerifiedPlayer(player, chatType.BeginTime);
                  PlayerManager.Instance.AddPetToPlayer(chatType.Sender, player); // also adds verified pet
                }
              }
            }
          }

          _chatTypes.Add(chatType);
        }

        if (!_running)
        {
          _running = true;
          _archiveTimer?.Dispose();
          _archiveTimer = new Timer(ArchiveChat);
          _archiveTimer.Change(Timeout, System.Threading.Timeout.Infinite);
        }
      }
    }

    private static List<string> GetSavedChannels(string playerAndServer)
    {
      var playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var file = playerDir + @"\" + ChannelsFile;
      var list = ConfigUtil.ReadList(file);
      return list.ConvertAll(item => item.ToLower()).Distinct().OrderBy(item => item.ToLower()).ToList();
    }

    private void ArchiveChat(object state)
    {
      try
      {
        List<ChatType> working;

        lock (LockObject)
        {
          working = _chatTypes;
          _chatTypes = new List<ChatType>();
        }

        var lastTime = double.NaN;
        var increment = 0.0;
        foreach (var t in working)
        {
          if (t != null)
          {
            var chatType = t;
            if (!double.IsNaN(lastTime) && lastTime.Equals(chatType.BeginTime))
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
          if (_chatTypes.Count > 0)
          {
            _archiveTimer?.Dispose();
            _archiveTimer = new Timer(ArchiveChat);
            _archiveTimer.Change(0, System.Threading.Timeout.Infinite);
          }
          else
          {
            SaveCurrent(true);

            if (_channelCacheUpdated)
            {
              var current = _channelCache.Keys.ToList();
              ConfigUtil.SaveList(_playerDir + @"\" + ChannelsFile, current);
              _channelCacheUpdated = false;
              EventsNewChannels?.Invoke(current);
            }

            if (_playerCacheUpdated)
            {
              ConfigUtil.SaveList(_playerDir + @"\players.txt", _playerCache.Keys.OrderBy(player => player)
                .Where(player => !PlayerManager.Instance.IsVerifiedPet(player)).ToList());
              _playerCacheUpdated = false;
            }

            EventsUpdatePlayer?.Invoke(_currentPlayer);
            _running = false;
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex);
      }
    }

    private void AddToArchive(string year, string month, string day, ChatLine chatLine, ChatType chatType)
    {
      var entryKey = day;
      var archiveKey = year + "-" + month;

      if (_currentArchiveKey != archiveKey)
      {
        SaveCurrent(true);
        var fileName = GetFileName(year, month);
        var mode = File.Exists(fileName) ? ZipArchiveMode.Update : ZipArchiveMode.Create;

        _currentArchive = OpenArchive(fileName, mode);
        _currentArchiveKey = archiveKey;
        LoadCache();
      }

      if (entryKey != _currentEntryKey && _currentArchive != null)
      {
        SaveCurrent(false);
        _currentEntryKey = entryKey;
        _currentList = new List<ChatLine>();

        var entry = _currentArchive.Mode != ZipArchiveMode.Create ? _currentArchive.GetEntry(_currentEntryKey) : null;
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
            var beginTime = _dateUtil.ParseDate(line);
            if (!double.IsNaN(lastTime) && lastTime.Equals(beginTime))
            {
              increment += 0.001;
            }
            else
            {
              increment = 0.0;
            }

            var existingLine = new ChatLine { Line = line, BeginTime = beginTime + increment };
            _currentList.Insert(0, existingLine); // reverse back
            lastTime = beginTime;
          });
        }
      }

      if (_currentList != null)
      {
        var index = _currentList.BinarySearch(chatLine, RtaComparer);

        if (index < 0)
        {
          index = Math.Abs(index) - 1;
          _currentList.Insert(index, chatLine);
          UpdateCache(entryKey, chatType);
          _currentListModified = true;
        }
        else if (chatLine.Line != _currentList[index].Line)
        {
          _currentList.Insert(index, chatLine);
          UpdateCache(entryKey, chatType);
          _currentListModified = true;
        }
      }
    }

    private void LoadCache()
    {
      _channelIndex = new Dictionary<string, Dictionary<string, byte>>();
      _channelIndexUpdated = false;

      if (_currentArchive != null && _currentArchive.Mode != ZipArchiveMode.Create)
      {
        try
        {
          var indexEntry = _currentArchive.GetEntry(Index);
          if (indexEntry != null)
          {
            using var reader = new StreamReader(indexEntry.Open());
            while (!reader.EndOfStream)
            {
              var temp = reader.ReadLine()?.Split('|');

              if (temp?.Length != 2)
              {
                continue;
              }

              _channelIndex[temp[0]] = new Dictionary<string, byte>();
              foreach (var channel in temp[1].Split(','))
              {
                _channelIndex[temp[0]][channel.ToLower()] = 1;
                UpdateChannelCache(channel); // in case main cache is out of sync with archive
              }
            }
          }
        }
        catch (IOException ex)
        {
          Log.Error(ex);
        }
        catch (InvalidDataException ide)
        {
          Log.Error(ide);
        }
      }
    }

    private void SaveCache()
    {
      var indexList = new List<string>();
      foreach (var keyvalue in _channelIndex)
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
        var indexEntry = GetEntry(Index);
        using var writer = new StreamWriter(indexEntry.Open());
        indexList.ForEach(item => writer.WriteLine(item));
        writer.Close();
      }
    }

    private void UpdateCache(string entryKey, ChatType chatType)
    {
      if (chatType.Channel != null)
      {
        if (!_channelIndex.ContainsKey(entryKey))
        {
          _channelIndex.Add(entryKey, new Dictionary<string, byte>());
        }

        var channels = _channelIndex[entryKey];
        if (!channels.ContainsKey(chatType.Channel))
        {
          channels[chatType.Channel] = 1;
          _channelIndexUpdated = true;
        }

        UpdateChannelCache(chatType.Channel);
      }

      AddPlayer(chatType.Sender);
      AddPlayer(chatType.Receiver);
    }

    private void UpdateChannelCache(string channel)
    {
      if (!_channelCache.ContainsKey(channel))
      {
        _channelCache[channel] = 1;
        _channelCacheUpdated = true;
      }
    }

    private void SaveCurrent(bool closeArchive)
    {
      if (_currentList != null && _currentArchive != null && _currentEntryKey != null)
      {
        if (_currentList.Count > 0 && _currentListModified)
        {
          var entry = GetEntry(_currentEntryKey);
          using var writer = new StreamWriter(entry.Open());
          _currentList.ForEach(chatLine => writer.WriteLine(chatLine.Line));
          writer.Close();
        }
      }

      if (closeArchive && _currentArchive != null)
      {
        if (_channelIndexUpdated)
        {
          SaveCache();
        }

        _currentArchive?.Dispose();
        _currentArchive = null;
        _currentArchiveKey = null;
        _channelIndexUpdated = false;
        _channelIndex = null;

      }

      _currentList?.Clear();
      _currentList = null;
      _currentEntryKey = null;
      _currentListModified = false;
    }

    private List<string> GetSelectedChannels(string playerAndServer)
    {
      List<string> result = null; // throw null to check case where file has never existed vs empty content
      var playerDir = ConfigUtil.GetArchiveDir() + playerAndServer;
      var fileName = playerDir + @"\" + SelectedChannelsFile;
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
        if (!_playerCache.ContainsKey(player) && !PlayerManager.Instance.IsVerifiedPet(player) && PlayerManager.IsPossiblePlayerName(player))
        {
          _playerCache[player] = 1;
          _playerCacheUpdated = true;
        }
      }
    }

    private ZipArchiveEntry GetEntry(string key)
    {
      return _currentArchive.Mode == ZipArchiveMode.Create ? _currentArchive.CreateEntry(key) : _currentArchive.GetEntry(key) ?? _currentArchive.CreateEntry(key);
    }

    private string GetFileName(string year, string month)
    {
      var folder = _playerDir + @"\" + year;
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

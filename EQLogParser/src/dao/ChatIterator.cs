using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace EQLogParser
{
  class ChatIterator : IEnumerable<ChatType>
  {
    private static readonly ChatType EndResult = new();

    private string _currentArchive;
    private readonly ChatFilter _currentChatFilter;
    private StringReader _currentReader;
    private readonly List<string> _directories;
    private List<string> _months;
    private List<string> _entries;
    private int _currentDirectory = -1;
    private int _currentMonth = -1;
    private int _currentEntry = -1;
    private readonly DateUtil _dateUtil;

    internal ChatIterator(string playerAndServer, ChatFilter chatFilter)
    {
      var home = ConfigUtil.GetArchiveDir() + playerAndServer;
      _currentChatFilter = chatFilter;
      _dateUtil = new DateUtil();

      if (Directory.Exists(home))
      {
        var years = Directory.GetDirectories(home);
        if (years.Length > 0)
        {
          _directories = years.ToList().OrderByDescending(year => year).ToList();
          GetNextYear();
        }
      }
    }

    internal void Close()
    {
      _currentReader?.Close();
      _currentReader = null;
      _currentArchive = null;
    }

    public IEnumerator<ChatType> GetEnumerator()
    {
      while (GetNextChat() is { } line)
      {
        yield return line;
      }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }

    private ChatType GetNextChat()
    {
      ChatType result = null;

      if (_currentArchive == null && _months != null && _currentMonth < _months.Count)
      {
        _currentArchive = GetArchive();
      }
      else if (_currentArchive == null && _months != null && _currentMonth >= _months.Count)
      {
        if (GetNextYear())
        {
          return GetNextChat();
        }
      }

      if (_currentArchive != null && _currentReader == null)
      {
        _currentReader = GetNextReader();
        if (_currentReader == null)
        {
          _currentMonth++;
          _currentArchive = null;
          return GetNextChat();
        }
      }

      if (_currentReader != null)
      {
        result = EndResult;
        while (result == EndResult && _currentReader.ReadLine() is { } nextLine)
        {
          var timeString = nextLine[..(MainWindow.ActionIndex - 1)];
          var action = nextLine[MainWindow.ActionIndex..];
          var chatType = ChatLineParser.ParseChatType(action);
          if (chatType != null)
          {
            // fix  % chars
            // workaround to set full line text
            chatType.Text = nextLine.Replace("PCT;", "%");
            chatType.BeginTime = _dateUtil.ParsePreciseDate(timeString);

            if (_currentChatFilter.PassFilter(chatType))
            {
              result = chatType;
            }
          }
        }

        if (result == EndResult)
        {
          _currentReader.Close();
          _currentReader = null;
          return GetNextChat();
        }
      }

      return result;
    }

    private bool GetNextYear()
    {
      var success = false;

      _currentDirectory++;
      _currentMonth = -1;

      if (_currentDirectory < _directories.Count && Directory.Exists(_directories[_currentDirectory]))
      {
        var dir = Path.GetFileName(_directories[_currentDirectory]);
        if (DateTime.TryParseExact(dir, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) && _currentChatFilter.DuringYear(parsed))
        {
          var months = Directory.GetFiles(_directories[_currentDirectory]);
          if (months.Length > 0)
          {
            _months = months.ToList().OrderByDescending(month => month).ToList();
            _currentMonth = 0;
            success = true;
          }
        }

        if (!success)
        {
          return GetNextYear();
        }
      }

      return success;
    }

    private StringReader GetNextReader()
    {
      StringReader result = null;

      _currentEntry++;
      if (_currentEntry < _entries.Count)
      {
        var archive = ChatManager.OpenArchive(_months[_currentMonth], ZipArchiveMode.Read);
        if (archive != null)
        {
          var reader = new StreamReader(archive.GetEntry(_entries[_currentEntry]).Open());
          result = new StringReader(reader.ReadToEnd());
          reader.Close();
          archive.Dispose();
        }
      }

      return result;
    }

    private string GetArchive()
    {
      string result = null;

      if (_currentDirectory > -1 && _currentDirectory < _directories.Count && _currentMonth > -1 && _months != null && _currentMonth < _months.Count)
      {
        var dir = Path.GetFileName(_directories[_currentDirectory]);
        var fileName = Path.GetFileName(_months[_currentMonth]);
        if (dir != null && fileName != null)
        {
          var monthString = dir + "-" + fileName.Substring(5, 2);
          if (DateTime.TryParseExact(monthString, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) && _currentChatFilter.DuringMonth(parsed))
          {
            var archive = ChatManager.OpenArchive(_months[_currentMonth], ZipArchiveMode.Read);
            if (archive != null)
            {
              _entries = archive.Entries.Where(entry =>
              {
                var found = false;
                if (entry.Name != ChatManager.Index)
                {
                  var dayString = monthString + "-" + entry.Name;
                  if (DateTime.TryParseExact(dayString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day) && _currentChatFilter.DuringDay(day))
                  {
                    found = true;
                  }
                }

                return found;
              }).OrderByDescending(entry => entry.Name).Select(entry => entry.Name).ToList();

              _currentEntry = -1;
              result = _months[_currentMonth];
              archive.Dispose();
            }
          }
        }

        if (result == null)
        {
          _currentMonth++;
          return GetArchive();
        }
      }

      return result;
    }
  }
}

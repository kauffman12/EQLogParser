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
    private static readonly ChatType END_RESULT = new();

    private readonly string Home;

    private string CurrentArchive;
    private readonly ChatFilter CurrentChatFilter;
    private StringReader CurrentReader;
    private readonly List<string> Directories;
    private List<string> Months;
    private List<string> Entries;
    private int CurrentDirectory = -1;
    private int CurrentMonth = -1;
    private int CurrentEntry = -1;
    private readonly DateUtil DateUtil;

    internal ChatIterator(string playerAndServer, ChatFilter ChatFilter)
    {
      Home = ConfigUtil.GetArchiveDir() + playerAndServer;
      CurrentChatFilter = ChatFilter;
      DateUtil = new DateUtil();

      if (Directory.Exists(Home))
      {
        var years = Directory.GetDirectories(Home);
        if (years.Length > 0)
        {
          Directories = years.ToList().OrderByDescending(year => year).ToList();
          GetNextYear();
        }
      }
    }

    internal void Close()
    {
      CurrentReader?.Close();
      CurrentReader = null;
      CurrentArchive = null;
    }

    public IEnumerator<ChatType> GetEnumerator()
    {
      while (GetNextChat() is { } line)
      {
        yield return line;
      }

      yield break;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }

    private ChatType GetNextChat()
    {
      ChatType result = null;

      if (CurrentArchive == null && Months != null && CurrentMonth < Months.Count)
      {
        CurrentArchive = GetArchive();
      }
      else if (CurrentArchive == null && Months != null && CurrentMonth >= Months.Count)
      {
        if (GetNextYear())
        {
          return GetNextChat();
        }
      }

      if (CurrentArchive != null && CurrentReader == null)
      {
        CurrentReader = GetNextReader();
        if (CurrentReader == null)
        {
          CurrentMonth++;
          CurrentArchive = null;
          return GetNextChat();
        }
      }

      if (CurrentReader != null)
      {
        result = END_RESULT;
        while (result == END_RESULT && CurrentReader.ReadLine() is { } nextLine)
        {
          var timeString = nextLine[..(MainWindow.ACTION_INDEX - 1)];
          var action = nextLine[MainWindow.ACTION_INDEX..];
          var chatType = ChatLineParser.ParseChatType(action);
          if (chatType != null)
          {
            // fix  % chars
            // workaround to set full line text
            chatType.Text = nextLine.Replace("PCT;", "%");
            chatType.BeginTime = DateUtil.ParsePreciseDate(timeString);

            if (CurrentChatFilter.PassFilter(chatType))
            {
              result = chatType;
            }
          }
        }

        if (result == END_RESULT)
        {
          CurrentReader.Close();
          CurrentReader = null;
          return GetNextChat();
        }
      }

      return result;
    }

    private bool GetNextYear()
    {
      var success = false;

      CurrentDirectory++;
      CurrentMonth = -1;

      if (CurrentDirectory < Directories.Count && Directory.Exists(Directories[CurrentDirectory]))
      {
        var dir = Path.GetFileName(Directories[CurrentDirectory]);
        if (DateTime.TryParseExact(dir, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) && CurrentChatFilter.DuringYear(parsed))
        {
          var months = Directory.GetFiles(Directories[CurrentDirectory]);
          if (months.Length > 0)
          {
            Months = months.ToList().OrderByDescending(month => month).ToList();
            CurrentMonth = 0;
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

      CurrentEntry++;
      if (CurrentEntry < Entries.Count)
      {
        var archive = ChatManager.OpenArchive(Months[CurrentMonth], ZipArchiveMode.Read);
        if (archive != null)
        {
          var reader = new StreamReader(archive.GetEntry(Entries[CurrentEntry]).Open());
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

      if (CurrentDirectory > -1 && CurrentDirectory < Directories.Count && CurrentMonth > -1 && Months != null && CurrentMonth < Months.Count)
      {
        var dir = Path.GetFileName(Directories[CurrentDirectory]);
        var fileName = Path.GetFileName(Months[CurrentMonth]);
        if (dir != null && fileName != null)
        {
          var monthString = dir + "-" + fileName.Substring(5, 2);
          if (DateTime.TryParseExact(monthString, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) && CurrentChatFilter.DuringMonth(parsed))
          {
            var archive = ChatManager.OpenArchive(Months[CurrentMonth], ZipArchiveMode.Read);
            if (archive != null)
            {
              Entries = archive.Entries.Where(entry =>
              {
                var found = false;
                if (entry.Name != ChatManager.INDEX)
                {
                  var dayString = monthString + "-" + entry.Name;
                  if (DateTime.TryParseExact(dayString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day) && CurrentChatFilter.DuringDay(day))
                  {
                    found = true;
                  }
                }

                return found;
              }).OrderByDescending(entry => entry.Name).Select(entry => entry.Name).ToList();

              CurrentEntry = -1;
              result = Months[CurrentMonth];
              archive.Dispose();
            }
          }
        }

        if (result == null)
        {
          CurrentMonth++;
          return GetArchive();
        }
      }

      return result;
    }
  }
}

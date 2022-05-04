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
    private static readonly ChatType END_RESULT = new ChatType();

    private readonly string Home;

    private string CurrentArchive = null;
    private readonly ChatFilter CurrentChatFilter = null;
    private StringReader CurrentReader = null;
    private readonly List<string> Directories;
    private List<string> Months;
    private List<string> Entries;
    private int CurrentDirectory = -1;
    private int CurrentMonth = -1;
    private int CurrentEntry = -1;

    internal ChatIterator(string playerAndServer, ChatFilter ChatFilter)
    {
      Home = ConfigUtil.GetArchiveDir() + playerAndServer;
      CurrentChatFilter = ChatFilter;

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
      ChatType line;
      while ((line = GetNextChat()) != null)
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
        string nextLine;
        while (result == END_RESULT && (nextLine = CurrentReader.ReadLine()) != null)
        {
          var chatType = ChatLineParser.ParseChatType(nextLine.Substring(LineParsing.ActionIndex));
          if (chatType != null)
          {
            // fix  % chars
            // workaround to set full line text
            chatType.Text = nextLine.Replace("PCT;", "%");
          }

          result = CurrentChatFilter.PassFilter(chatType) ? chatType : result;
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
      bool success = false;

      CurrentDirectory++;
      CurrentMonth = -1;

      if (CurrentDirectory < Directories.Count && Directory.Exists(Directories[CurrentDirectory]))
      {
        string dir = Path.GetFileName(Directories[CurrentDirectory]);
        if (DateTime.TryParseExact(dir, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed) && CurrentChatFilter.DuringYear(parsed))
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
        string dir = Path.GetFileName(Directories[CurrentDirectory]);
        string fileName = Path.GetFileName(Months[CurrentMonth]);
        if (dir != null && fileName != null)
        {
          string monthString = dir + "-" + fileName.Substring(5, 2);
          if (DateTime.TryParseExact(monthString, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed) && CurrentChatFilter.DuringMonth(parsed))
          {
            var archive = ChatManager.OpenArchive(Months[CurrentMonth], ZipArchiveMode.Read);
            if (archive != null)
            {
              Entries = archive.Entries.Where(entry =>
              {
                bool found = false;
                if (entry.Name != ChatManager.INDEX)
                {
                  string dayString = monthString + "-" + entry.Name;
                  if (DateTime.TryParseExact(dayString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime day) && CurrentChatFilter.DuringDay(day))
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

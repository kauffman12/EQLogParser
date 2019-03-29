using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace EQLogParser
{
  class ChatIterator : IEnumerable<ChatLine>
  {
    private readonly string Home;
    private readonly DateUtil DateUtil = new DateUtil();
    private ZipArchive CurrentArchive = null;
    private StreamReader CurrentReader = null;
    private List<string> Directories;
    private List<string> Months;
    private List<string> Entries;
    private int CurrentDirectory = -1;
    private int CurrentMonth = -1;
    private int CurrentEntry = -1;

    internal ChatIterator(string player)
    {
      Home = ChatManager.ArchiveDir + player;

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

    public IEnumerator<ChatLine> GetEnumerator()
    {
      ChatLine chatLine;
      while ((chatLine = GetNextChat()) != null)
      {
        yield return chatLine;
      }

      yield break;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }

    private ChatLine GetNextChat()
    {
      ChatLine result = null;

      if (CurrentArchive == null && CurrentMonth < Months.Count)
      {
        CurrentArchive = GetArchive();
      }
      else if (CurrentArchive == null && CurrentMonth >= Months.Count)
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
          CurrentArchive?.Dispose();
          CurrentArchive = null;
          return GetNextChat();
        }
      }

      if (CurrentReader != null)
      {
        if (!CurrentReader.EndOfStream)
        {
          result = ChatManager.CreateLine(DateUtil, CurrentReader.ReadLine());
        }
        else
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
        var months = Directory.GetFiles(Directories[CurrentDirectory]);
        if (months.Length > 0)
        {
          Months = months.ToList().OrderByDescending(month => month).ToList();
          CurrentMonth = 0;
          success = true;
        }
        else
        {
          return GetNextYear();
        }
      }

      return success;
    }

    private StreamReader GetNextReader()
    {
      StreamReader result = null;

      CurrentEntry++;
      if (CurrentEntry < Entries.Count)
      {
        result = new StreamReader(CurrentArchive.GetEntry(Entries[CurrentEntry]).Open());
      }

      return result;
    }

    private ZipArchive GetArchive()
    {
      ZipArchive result = null;

      if (CurrentDirectory > -1 && CurrentDirectory < Directories.Count && CurrentMonth > -1 && CurrentMonth < Months.Count)
      {
        result = ZipFile.OpenRead(Months[CurrentMonth]);
        Entries = result.Entries.OrderByDescending(entry => entry.Name).Select(entry => entry.Name).ToList();
        CurrentEntry = -1;
      }

      return result;
    }
  }
}

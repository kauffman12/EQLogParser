
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class MiscLineParser
  {
    public static event EventHandler<LootProcessedEvent> EventsLootProcessed;

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly DateUtil DateUtil = new DateUtil();
    private static readonly List<string> Currency = new List<string> { "Platinum", "Gold", "Silver", "Copper" };
    private static readonly Dictionary<char, uint> Rates = new Dictionary<char, uint>() { { 'p', 1000 }, { 'g', 100 }, { 's', 10 }, { 'c', 1 } };
    private const string MasterLooterText = "The master looter, ";
    private const string YouReceiveText = "You receive ";
    private const string YourSplitText = " as your split ";

    public static void Process(string source, string line)
    {
      bool handled = false;

      try
      {
        if (line.Length >= 47)
        {
          string name = null;
          string item = null;
          string npc = null;
          uint count = 0;
          bool isCurrency = false;

          if (line[Parsing.ACTIONINDEX] == '-' && line[Parsing.ACTIONINDEX + 1] == '-')
          {
            if (line.Substring(Parsing.ACTIONINDEX + 2).Split(' ') is string[] pieces && pieces.Length >= 7)
            {
              name = pieces[0] == "You" ? ConfigUtil.PlayerName : pieces[0];

              if (pieces[2] == "looted" && Array.FindIndex(pieces, piece => piece == "from") is int fromIndex && fromIndex > 4)
              {
                count = pieces[3][0] == 'a' ? 1 : StatsUtil.ParseUInt(pieces[3]);
                item = string.Join(" ", pieces, 4, fromIndex - 4);

                if (Array.FindLastIndex(pieces, piece => piece.EndsWith(".--", StringComparison.Ordinal)) is int endIndex && endIndex > fromIndex)
                {
                  var tmp = string.Join(" ", pieces, fromIndex + 1, endIndex - fromIndex);
                  npc = tmp.Replace(".--", "").Replace("'s corpse", "");
                }
              }
            }
          }
          else if (line.Substring(Parsing.ACTIONINDEX, MasterLooterText.Length) == MasterLooterText)
          {
            if (line.Substring(Parsing.ACTIONINDEX + MasterLooterText.Length).Split(' ') is string[] pieces && pieces.Length >= 7)
            {
              name = pieces[0].Substring(0, pieces[0].Length - 1);

              if (pieces[1] == "looted" && Array.FindIndex(pieces, piece => piece == "from") is int fromIndex && fromIndex > 3)
              {
                ParseCurrency(pieces, 2, fromIndex, out item, out count);
                isCurrency = true;
              }
            }
          }
          else if (line.Substring(Parsing.ACTIONINDEX, YouReceiveText.Length) == YouReceiveText && line.IndexOf(YourSplitText, Parsing.ACTIONINDEX + 15, StringComparison.Ordinal) > -1)
          {
            name = ConfigUtil.PlayerName;

            if (line.Substring(Parsing.ACTIONINDEX + YouReceiveText.Length).Split(' ') is string[] pieces && pieces.Length >= 2 && Array.FindIndex(pieces, end => end == "as") is int splitIndex && splitIndex > -1)
            {
              ParseCurrency(pieces, 0, splitIndex, out item, out count);
              isCurrency = true;
            }
          }

          if (count > 0 && !string.IsNullOrEmpty(item) && !string.IsNullOrEmpty(name))
          {
            double currentTime = DateUtil.ParseDate(line.Substring(1, 24), out double precise);
            LootRecord record = new LootRecord() { Item = item, Player = name, Quantity = count, IsCurrency = isCurrency, Npc = npc };
            EventsLootProcessed?.Invoke(record, new LootProcessedEvent() { Record = record, BeginTime = currentTime });
            handled = true;
          }
        }
      }
      catch (ArgumentNullException ne)
      {
        LOG.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        LOG.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        LOG.Error(aor);
      }
      catch (ArgumentException ae)
      {
        LOG.Error(ae);
      }

      if (!handled)
      {
        DataManager.Instance.AddUnhandledLine(source, line);
      }
    }

    private static void ParseCurrency(string[] pieces, int startIndex, int toIndex, out string item, out uint count)
    {
      bool parsed = true;
      item = null;
      count = 0;

      List<string> tmp = new List<string>();
      for (int i = startIndex; i < toIndex; i += 2)
      {
        if (pieces[i] == "and")
        {
          i -= 1;
          continue;
        }

        if (StatsUtil.ParseUInt(pieces[i]) is uint value && Currency.FirstOrDefault(curr => pieces[i + 1].StartsWith(curr, StringComparison.OrdinalIgnoreCase)) is string type)
        {
          tmp.Add(pieces[i] + " " + type);
          count += value * Rates[pieces[i + 1][0]];
        }
        else
        {
          parsed = false;
          break;
        }
      }

      if (parsed && tmp.Count > 0)
      {
        item = string.Join(", ", tmp);
      }
    }
  }
}

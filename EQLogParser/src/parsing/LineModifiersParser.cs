
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EQLogParser
{
  class LineModifiersParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static Dictionary<string, byte> ALL_MODIFIERS = new Dictionary<string, byte>()
    {
      { "Assassinate", 1 }, { "Crippling Blow", 1 }, { "Critical", 1 }, { "Deadly Strike", 1 }, { "Double Bow Shot", 1 }, { "Finishing Blow", 1 },
      { "Flurry", 1 }, { "Headshot", 1 }, { "Lucky", 1 }, { "Rampage", 1 }, { "Riposte", 1 }, { "Slay Undead", 1 }, { "Strikethrough", 1 },
      { "Twincast", 1 }, { "Wild Rampage", 1 },
    };

    private static Dictionary<string, byte> CRIT_MODIFIERS = new Dictionary<string, byte>()
    {
      { "Assassinate", 1 }, { "Crippling Blow", 1 }, { "Critical", 1 }, { "Deadly Strike", 1 }, { "Finishing Blow", 1 }, { "Headshot", 1 }
    };

    private static int TWINCAST = 1;
    private static int CRIT = 2;
    private static int LUCKY = 4;

    private static ConcurrentDictionary<string, int> MaskCache = new ConcurrentDictionary<string, int>();

    internal static void Parse(HitRecord record, Hit playerStats, Hit theHit = null)
    {
      if (record.ModifiersMask > -1)
      {
        if ((record.ModifiersMask & TWINCAST) != 0)
        {
          playerStats.TwincastHits++;

          if (theHit != null)
          {
            theHit.TwincastHits++;
          }
        }

        if ((record.ModifiersMask & CRIT) != 0)
        {
          playerStats.CritHits++;

          if (theHit != null)
          {
            theHit.CritHits++;
          }

          if ((record.ModifiersMask & LUCKY) == 0)
          {
            playerStats.TotalCrit += record.Total;

            if (theHit != null)
            {
              theHit.TotalCrit += record.Total;
            }
          }
        }

        if ((record.ModifiersMask & LUCKY) != 0)
        {
          playerStats.LuckyHits++;
          playerStats.TotalLucky += record.Total;

          if (theHit != null)
          {
            theHit.LuckyHits++;
            theHit.TotalLucky += record.Total;
          }
        }
      }
    }

    internal static int Parse(string modifiers)
    {
      int result = -1;

      if (modifiers != null && modifiers != "")
      {
        if (!MaskCache.TryGetValue(modifiers, out result))
        {
          result = BuildVector(modifiers);
          MaskCache[modifiers] = result;
        }
      }

      return result;
    }

    private static int BuildVector(string modifiers)
    {
      int result = 0;

      bool lucky = false;
      bool critical = false;

      string temp = "";
      foreach (string modifier in modifiers.Split(' '))
      {
        temp += modifier;
        if (ALL_MODIFIERS.ContainsKey(temp))
        {
          if (!critical && CRIT_MODIFIERS.ContainsKey(temp))
          {
            result = result | CRIT;
          }

          if (!lucky && "Lucky" == temp)
          {
            result = result | LUCKY;
          }

          switch (temp)
          {
            case "Twincast":
              result = result | TWINCAST;
              break;
          }

          temp = ""; // reset
        }
        else
        {
          temp += " ";
        }
      }

      if (temp != "")
      {
        LOG.Debug("Unknown Modifiers: " + modifiers);
      }

      return result;
    }
  }
}


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

    public static void Parse(HitRecord record, Hit playerStats, Hit theHit = null)
    {
      if (record.Modifiers != null && record.Modifiers != "")
      {
        bool lucky = false;
        bool critical = false;

        string temp = "";
        foreach (string modifier in record.Modifiers.Split(' '))
        {
          temp += modifier;
          if (ALL_MODIFIERS.ContainsKey(temp))
          {
            if (!critical && CRIT_MODIFIERS.ContainsKey(temp))
            {
              critical = true;
            }

            if (!lucky && "Lucky" == temp)
            {
              lucky = true;
            }

            switch (temp)
            {
              case "Twincast":
                playerStats.TwincastHits++;

                if (theHit != null)
                {
                  theHit.TwincastHits++;
                }
                break;
            }

            temp = ""; // reset
          }
          else
          {
            temp += " ";
          }
        }

        if (critical)
        {
          playerStats.CritHits++;

          if (theHit != null)
          {
            theHit.CritHits++;
          }

          if (!lucky)
          {
            playerStats.TotalCrit += record.Total;

            if (theHit != null)
            {
              theHit.TotalCrit += record.Total;
            }
          }
        }

        if (lucky)
        {
          playerStats.LuckyHits++;
          playerStats.TotalLucky += record.Total;

          if (theHit != null)
          {
            theHit.LuckyHits++;
            theHit.TotalLucky += record.Total;
          }
        }

        if (temp != "")
        {
          LOG.Debug("Unknown Modifiers: " + record.Modifiers);
        }
      }
    }
  }
}
